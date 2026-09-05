// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Silk.NET.Direct3D12;
using Silk.NET.DXGI;

namespace Season.Platforms.Windows.DirectX;

/// <summary>
/// Texture upload batcher with a dual-path design:
/// Path A (loading): accumulate textures and flush once via ExecuteFullUploads.
/// Path B (runtime): upload sub-rects immediately via ExecuteSubRectUpload.
/// Both paths share _executeLock for serialization and never use CopyGraphicsCommandList concurrently.
/// </summary>
internal unsafe class TextureUploadBatch
{
    readonly List<DXTexture> _pendingUploads = new();
    readonly ID3D12Device* _device;
    readonly object _executeLock = new();

    ID3D12Resource* _sharedUploadHeap = null;
    byte* _mappedSharedUploadHeap = null;
    bool _isDisposed;

    public TextureUploadBatch(ID3D12Device* device)
    {
        _device = device;
    }

    // ════════════════════════════════════════════════════════════════
    // Path A: full texture uploads, used by the loading path in batches
    // ════════════════════════════════════════════════════════════════

    /// <summary>Accumulate one full texture upload request. Called by DXTexture.ProcessDecoder.</summary>
    public void AddTextureUpload(DXTexture dxTexture)
    {
        lock (_executeLock)
        {
            _pendingUploads.Add(dxTexture);
        }
    }

    /// <summary>Submit all accumulated full texture uploads in one batch. Called by Graphics.ExecuteUpload.</summary>
    public void ExecuteFullUploads(
        ID3D12GraphicsCommandList* commandList,
        ID3D12CommandQueue* commandQueue)
    {
        lock (_executeLock)
        {
            if (_pendingUploads.Count == 0)
                return;

            if (commandList == null)
                throw new InvalidOperationException(
                    "TextureUploadBatch requires a valid copy command list before executing uploads.");
            if (commandQueue == null)
                throw new InvalidOperationException(
                    "TextureUploadBatch requires a valid copy command queue before executing uploads.");

            bool commandListClosed = false;
            try
            {
                // 1. Compute the total upload-heap size, aligned to 512 bytes.
                ulong totalSize = 0;
                for (int i = 0; i < _pendingUploads.Count; i++)
                    totalSize += _pendingUploads[i].GetTotalBytes();

                CreateSharedUploadHeap(totalSize);

                // 2. Ensure all textures are in a state usable by the Copy Queue.
                for (int i = 0; i < _pendingUploads.Count; i++)
                    _pendingUploads[i].EnsureCommonForCopyQueue();

                // 3. Copy pixel data on the CPU, then record GPU CopyTextureRegion commands.
                ulong offset = 0;
                for (int i = 0; i < _pendingUploads.Count; i++)
                {
                    var tex = _pendingUploads[i];

                    // Defensive check: make sure the texture resource was not unexpectedly released,
                    // preventing a CopyTextureRegion SEHException.
                    if (tex._textureResource == null)
                        throw new InvalidOperationException(
                            $"Texture '{tex.Name}' has null GPU resource at upload time. " +
                            "The texture may have failed to decode or was prematurely disposed.");
                    if (_sharedUploadHeap == null)
                        throw new InvalidOperationException(
                            "Shared upload heap was released before upload completed.");

                    tex.CopyDataToSharedHeap(_mappedSharedUploadHeap, offset);
                    RecordFullTextureCopy(commandList, tex, _sharedUploadHeap, offset);
                    offset += tex.GetTotalBytes();
                }

                // 4. Close the command list and submit it to the Copy Queue.
                commandList->Close();
                commandListClosed = true;

                ID3D12CommandList* commandListPtr = (ID3D12CommandList*)commandList;
                commandQueue->ExecuteCommandLists(1, &commandListPtr);

                // 5. Fence synchronization: Copy Queue -> Direct Queue.
                ulong fenceValue = Device.CopySignal();

                for (int i = 0; i < _pendingUploads.Count; i++)
                {
                    var tex = _pendingUploads[i];
                    tex.CurrentState = ResourceStates.Common;
                    tex.CreateSRV();
                    tex.UploadFenceValue = fenceValue;
                    // Ready must be published last, with Volatile preserving order. Once the render thread
                    // observes Ready==true, it must also observe UploadFenceValue and wait for CopyFence on
                    // the Direct Queue. Otherwise it may submit a transition barrier while Copy Queue writes
                    // are still in flight, triggering Debug Layer EXECUTION ERROR #1047.
                    System.Threading.Volatile.Write(ref tex.Ready, true);
                }

                // 6. Wait for copy completion on the CPU, then safely release the upload heap.
                Device.CopyWaitForCpu();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"TextureUploadBatch ExecuteFullUploads error: {ex.Message}");
                throw;
            }
            finally
            {
                if (!commandListClosed)
                    commandList->Close();
                Device.CopyCommandAllocator->Reset();
                var result = Device.CopyGraphicsCommandList->Reset(
                    Device.CopyCommandAllocator, null);
                Device.CheckResult(result);

                _pendingUploads.Clear();
                ReleaseSharedUploadHeap();
            }
        }
    }

    static void RecordFullTextureCopy(
        ID3D12GraphicsCommandList* commandList,
        DXTexture dxTexture,
        ID3D12Resource* sharedUploadHeap,
        ulong offset)
    {
        // When using a PlacedFootprint as the CopyTextureRegion source on the Copy Queue,
        // D3D12 requires the destination texture to be in CopyDest or Common state.
        // The callers ExecuteFullUploads and ExecuteSubRectUpload already guarantee that
        // precondition via EnsureCommonForCopyQueue.

        // 2-6 clause 4: one CopyTextureRegion per subresource. CopyTextureRegion has no notion of "copy the whole
        // chain", so a texture with N mip levels needs N commands - and this loop degenerates to the pre-2-6 single
        // command whenever MipLevels is 1.
        for (uint level = 0; level < dxTexture.MipLevels; level++)
        {
            var srcLocation = dxTexture.GetCopySourceLocation(level);
            srcLocation.PResource = sharedUploadHeap;
            // The per-level footprint offset is relative to this texture's region, so the batch offset is added to
            // it rather than replacing it. This matches how CopyDataToSharedHeap placed the bytes.
            srcLocation.Anonymous.PlacedFootprint.Offset += offset;

            var dstLocation = dxTexture.GetCopyDestLocation(level);

            // The D3D12 Debug Layer triggers an SEHException, effectively a memory access violation,
            // when PResource is null.
            if (dstLocation.PResource == null)
                throw new InvalidOperationException(
                    $"Texture '{dxTexture.Name}' has null destination resource. " +
                    "The texture may not have been properly initialized.");
            if (srcLocation.PResource == null)
                throw new InvalidOperationException(
                    "Source upload heap is null for CopyTextureRegion.");

            commandList->CopyTextureRegion(&dstLocation, 0, 0, 0, &srcLocation, null);
        }
    }

    // ════════════════════════════════════════════════════════════════
    // Path B: sub-rect texture updates, used by the runtime path and executed immediately
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Immediately execute dirty-rectangle updates for an already uploaded texture.
    /// Called by DXTexture.UploadSubRects and GlyphAtlasManager.FlushPendingUploadsOnRenderThread.
    /// This method is self-contained for the full lifecycle: plan -> upload -> submit -> clean,
    /// and does not depend on _pendingUploads.
    /// </summary>
    public void ExecuteSubRectUpload(
        DXTexture texture,
        byte[] rgbaPixels,
        int sourceWidth,
        int sourceHeight,
        TextureUploadRect[] dirtyRects,
        ID3D12GraphicsCommandList* commandList,
        ID3D12CommandQueue* commandQueue)
    {
        if (dirtyRects.Length == 0)
            return;

        if (commandList == null)
            throw new InvalidOperationException(
                "TextureUploadBatch requires a valid copy command list before executing uploads.");
        if (commandQueue == null)
            throw new InvalidOperationException(
                "TextureUploadBatch requires a valid copy command queue before executing uploads.");

        lock (_executeLock)
        {
            bool commandListClosed = false;
            try
            {
                // 1. Validate and plan the footprints for all rects.
                int expectedSize = sourceWidth * sourceHeight * 4;
                if (rgbaPixels.Length != expectedSize)
                    throw new ArgumentException(
                        $"Pixel data size mismatch. Expected {expectedSize} bytes " +
                        $"for {sourceWidth}×{sourceHeight}, got {rgbaPixels.Length}.");

                var uploadInfos = new (TextureUploadRect Rect, PlacedSubresourceFootprint Footprint)[dirtyRects.Length];
                ulong totalUploadBytes = 0;

                for (int i = 0; i < dirtyRects.Length; i++)
                {
                    var rect = dirtyRects[i];
                    texture.ValidateUploadRect(rect, sourceWidth, sourceHeight);

                    ulong alignedOffset = AlignUp(totalUploadBytes, 512);
                    texture.GetUploadFootprint(
                        rect, alignedOffset,
                        out var footprint, out var rectTotalBytes);
                    uploadInfos[i] = (rect, footprint);
                    totalUploadBytes = alignedOffset + rectTotalBytes;
                }

                // 2. Ensure the texture is in a state usable by the Copy Queue.
                texture.EnsureCommonForCopyQueue();

                // 3. Create the upload heap.
                CreateSharedUploadHeap(totalUploadBytes);

                // 4. Copy pixel data on the CPU, row by row for each rect.
                fixed (byte* srcPixels = rgbaPixels)
                {
                    for (int i = 0; i < uploadInfos.Length; i++)
                    {
                        var rect = uploadInfos[i].Rect;
                        var footprint = uploadInfos[i].Footprint;
                        byte* dstRect = _mappedSharedUploadHeap + (nint)footprint.Offset;
                        uint copyRowBytes = (uint)(rect.Width * 4);

                        for (int row = 0; row < rect.Height; row++)
                        {
                            int srcOffset = ((rect.Y + row) * sourceWidth + rect.X) * 4;
                            Unsafe.CopyBlock(
                                dstRect + row * footprint.Footprint.RowPitch,
                                srcPixels + srcOffset,
                                copyRowBytes);
                        }
                    }
                }

                // 5. Record GPU CopyTextureRegion commands.
                var dstLocation = texture.GetCopyDestLocation();
                for (int i = 0; i < uploadInfos.Length; i++)
                {
                    var rect = uploadInfos[i].Rect;
                    var srcLocation = new TextureCopyLocation
                    {
                        Type = TextureCopyType.PlacedFootprint,
                        Anonymous = { PlacedFootprint = uploadInfos[i].Footprint },
                        PResource = _sharedUploadHeap
                    };

                    commandList->CopyTextureRegion(
                        &dstLocation, (uint)rect.X, (uint)rect.Y, 0,
                        &srcLocation, null);
                }

                // 6. Submit.
                commandList->Close();
                commandListClosed = true;

                ID3D12CommandList* commandListPtr = (ID3D12CommandList*)commandList;
                commandQueue->ExecuteCommandLists(1, &commandListPtr);

                ulong fenceValue = Device.CopySignal();

                texture.CurrentState = ResourceStates.Common;
                texture.UploadFenceValue = fenceValue;
                // Publish Ready last for the same reason as ExecuteFullUploads:
                // to prevent the render thread from skipping the CopyFence wait and causing
                // a cross-queue race, which would trigger EXECUTION ERROR #1047.
                System.Threading.Volatile.Write(ref texture.Ready, true);

                Device.CopyWaitForCpu();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"TextureUploadBatch ExecuteSubRectUpload error: {ex.Message}");
                throw;
            }
            finally
            {
                if (!commandListClosed)
                    commandList->Close();
                Device.CopyCommandAllocator->Reset();
                var result = Device.CopyGraphicsCommandList->Reset(
                    Device.CopyCommandAllocator, null);
                Device.CheckResult(result);

                ReleaseSharedUploadHeap();
            }
        }
    }

    // ════════════════════════════════════════════════════════════════
    // Path C: upload all six faces of a cube map, executed immediately during loading (1-7)
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Immediately upload all six faces of one cube map. Called by DXTextureCube.CreateFromDecoders.
    /// Its structure follows ExecuteSubRectUpload and is fully self-contained:
    /// create heap -> copy data -> record -> submit -> clean up.
    /// It is not merged into _pendingUploads because that path assumes a DXTexture with a single subresource.
    /// It shares _executeLock with the other two paths, so CopyGraphicsCommandList is never used concurrently.
    /// </summary>
    public void ExecuteCubeUpload(
        DXTextureCube cube,
        ID3D12GraphicsCommandList* commandList,
        ID3D12CommandQueue* commandQueue)
    {
        if (commandList == null)
            throw new InvalidOperationException(
                "TextureUploadBatch requires a valid copy command list before executing uploads.");
        if (commandQueue == null)
            throw new InvalidOperationException(
                "TextureUploadBatch requires a valid copy command queue before executing uploads.");

        lock (_executeLock)
        {
            bool commandListClosed = false;
            try
            {
                // 1. Create the upload heap, with six-face footprint offsets already aligned by GetCopyableFootprints.
                CreateSharedUploadHeap(cube.GetTotalBytes());

                // 2. Copy data into the mapped region on the CPU, face by face and row by row.
                cube.CopyFacesToUploadHeap(_mappedSharedUploadHeap);

                // 3. Record six CopyTextureRegion calls. The destination stays in Common because the resource is newly created and has not left that state.
                cube.RecordFaceCopies(commandList, _sharedUploadHeap);

                // 4. Submit.
                commandList->Close();
                commandListClosed = true;

                ID3D12CommandList* commandListPtr = (ID3D12CommandList*)commandList;
                commandQueue->ExecuteCommandLists(1, &commandListPtr);

                ulong fenceValue = Device.CopySignal();

                // Publish Ready last for the same reason as ExecuteFullUploads, to avoid EXECUTION ERROR #1047.
                cube.OnUploadSubmitted(fenceValue);

                Device.CopyWaitForCpu();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"TextureUploadBatch ExecuteCubeUpload error: {ex.Message}");
                throw;
            }
            finally
            {
                if (!commandListClosed)
                    commandList->Close();
                Device.CopyCommandAllocator->Reset();
                var result = Device.CopyGraphicsCommandList->Reset(
                    Device.CopyCommandAllocator, null);
                Device.CheckResult(result);

                ReleaseSharedUploadHeap();
            }
        }
    }

    // ════════════════════════════════════════════════════════════════
    // Shared infrastructure
    // ════════════════════════════════════════════════════════════════

    void CreateSharedUploadHeap(ulong size)
    {
        ReleaseSharedUploadHeap();

        var heapProps = new HeapProperties(HeapType.Upload);
        var bufferDesc = new ResourceDesc
        {
            Dimension = ResourceDimension.Buffer,
            Width = size,
            Height = 1,
            DepthOrArraySize = 1,
            MipLevels = 1,
            Format = Format.FormatUnknown,
            SampleDesc = new SampleDesc(1, 0),
            Layout = TextureLayout.LayoutRowMajor,
            Flags = ResourceFlags.None
        };

        ID3D12Resource* uploadHeap;
        var iid = ID3D12Resource.Guid;
        var result = _device->CreateCommittedResource(
            &heapProps,
            HeapFlags.None,
            &bufferDesc,
            ResourceStates.GenericRead,
            null,
            &iid,
            (void**)&uploadHeap
        );
        var ex = Marshal.GetExceptionForHR(result);
        if (ex != null) throw ex;

        _sharedUploadHeap = uploadHeap;

        void* mappedPtr;
        result = _sharedUploadHeap->Map(0, null, &mappedPtr);
        ex = Marshal.GetExceptionForHR(result);
        if (ex != null) throw ex;

        _mappedSharedUploadHeap = (byte*)mappedPtr;
    }

    void ReleaseSharedUploadHeap()
    {
        if (_sharedUploadHeap != null)
        {
            if (_mappedSharedUploadHeap != null)
            {
                _sharedUploadHeap->Unmap(0, null);
                _mappedSharedUploadHeap = null;
            }

            _sharedUploadHeap->Release();
            _sharedUploadHeap = null;
        }
    }

    public void Clear()
    {
        ReleaseSharedUploadHeap();
        _pendingUploads.Clear();
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_isDisposed)
        {
            if (disposing)
            {
                // Release managed resources.
            }

            // Release unmanaged resources.
            Clear();

            _isDisposed = true;
        }
    }

    ~TextureUploadBatch()
    {
        Dispose(false);
    }

    static ulong AlignUp(ulong value, ulong alignment)
        => (value + alignment - 1) & ~(alignment - 1);
}
