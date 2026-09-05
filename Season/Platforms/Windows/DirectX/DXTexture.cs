// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;

namespace Season.Platforms.Windows.DirectX;

public readonly record struct TextureUploadRect(int X, int Y, int Width, int Height);

public unsafe class DXTexture : IDisposable
{
    public string Name;

    public bool Ready;

    /// <summary>
    /// Records the Copy Queue fence value when this texture finishes uploading.
    /// Before the Direct Queue uses this texture for the first time, it must
    /// wait on this fence and issue the Common -> PixelShaderResource barrier.
    /// A value of 0 means no wait is needed because the texture was already
    /// transitioned or has never been uploaded.
    /// </summary>
    public ulong UploadFenceValue;

    public ID3D12Resource* _textureUploadHeap = null;

    public ID3D12Resource* _textureResource = null;

    uint _srvDescriptorSize;

    public uint Width;
    public uint Height;

    Format _format = Format.FormatR8G8B8A8Unorm;
    ulong _offsetInSharedHeap;
    byte[] _imageData;

    /// <summary>
    /// 2-6 clause 4: subresource count of this texture, which for a non-array 2D texture is exactly the mip level
    /// count. Stays 1 unless <see cref="MipChain.ShouldGenerate"/> approved a chain for the requested policy, so
    /// every existing caller keeps the pre-2-6 single-level layout untouched.
    /// </summary>
    uint _mipLevels = 1;

    /// <summary>
    /// Geometry of each level inside <see cref="_imageData"/>. This is the *source* layout - tightly packed with no
    /// row padding - and must not be confused with <see cref="_footprints"/>, which is the D3D12 upload-heap layout
    /// where every row is padded to 256 bytes and every subresource starts on a 512-byte boundary. Keeping both is
    /// what makes <see cref="CopyDataToSharedHeap"/> a straight re-pitch rather than a second layout computation.
    /// </summary>
    MipLevelInfo[] _mipInfos;

    /// <summary>Upload-heap footprint per subresource, offsets relative to the start of this texture's own region.</summary>
    PlacedSubresourceFootprint[] _footprints;

    /// <summary>Row count per subresource, as reported by GetCopyableFootprints.</summary>
    uint[] _numRows;

    /// <summary>
    /// The policy this texture was created with, retained so that in-place replacement through
    /// <see cref="UploadPixels"/> can regenerate the chain instead of leaving levels 1..N holding stale content.
    /// </summary>
    TextureMipPolicy _mipPolicy = TextureMipPolicy.None;

    ulong _totalBytes;
    ulong _textureUploadHeapCapacity;

    ResourceStates _currentState = ResourceStates.Common;

    public ResourceStates CurrentState
    {
        get => _currentState;
        set => _currentState = value;
    }

    private object _stateLock = new object();

    public int DescriptorID;

    public CpuDescriptorHandle CpuDescriptorHandle;

    public GpuDescriptorHandle GpuDescriptorHandle;

    // 1-6 Compute: dual views for storage textures
    // (SRV for sampling + UAV for compute writes)
    // Allocated only by CreateComputeStorage factory products.
    // Regular uploaded textures always keep this at -1.

    public int UavDescriptorID = -1;

    public GpuDescriptorHandle UavGpuDescriptorHandle;

    private int _refCount = 1;

    public int RefCount => _refCount;

    public void AddRef() => System.Threading.Interlocked.Increment(ref _refCount);

    public void Release()
    {
        if (System.Threading.Interlocked.Decrement(ref _refCount) == 0)
            Dispose();
    }

    DXTexture()
    {
    }

    internal DXTexture(INativeImageDecoder decoder, TextureMipPolicy mipPolicy = TextureMipPolicy.None)
    {
        ProcessDecoder(decoder, mipPolicy);
    }

    internal DXTexture(string name, SharpGLTF.Schema2.Image image, TextureMipPolicy mipPolicy = TextureMipPolicy.None)
    {
        INativeImageDecoder decoder = null;

        if (name is "White")
        {
            // 1×1 white RGBA8 (direct decoder, no image pipeline).
            decoder = CreateWhiteDecoder();
        }
        else if (image != null)
        {
            var stream = image.Content.Open();
            try
            {
                decoder = new WindowsImageDecoder(stream);
            }
            catch (Exception ex)
            {

            }
            finally
            {
                stream.Dispose();
            }
        }
        else
        {
            var stream = File.Open(name, FileMode.Open);
            decoder = new WindowsImageDecoder(stream);
            stream.Dispose();
            //decoder = new ImageResultDecoder(ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha));
        }

        if (decoder != null)
        {
            ProcessDecoder(decoder, mipPolicy);
        }
    }

    static INativeImageDecoder CreateWhiteDecoder()
        => new WhiteImageDecoder();

    /// <summary>1×1 white RGBA8 decoder (bypasses WinRT image pipeline).</summary>
    sealed class WhiteImageDecoder : INativeImageDecoder
    {
        static readonly byte[] _white = [0xFF, 0xFF, 0xFF, 0xFF];

        public int Width => 1;
        public int Height => 1;
        public int Stride => 4;
        public ReadOnlySpan<byte> PixelSpan => _white;
        public void Dispose() { }
    }

    void ProcessDecoder(INativeImageDecoder decoder, TextureMipPolicy mipPolicy)
    {
        DescriptorID = Device.DescriptorAllocator.Allocate();
        CpuDescriptorHandle = Device.SrvHeapManager.GetCpuHandle(DescriptorID);
        GpuDescriptorHandle = Device.SrvHeapManager.GetGpuHandle(DescriptorID);

        PrepareForBatchUpload(decoder, mipPolicy);

        // Only accumulate into the batch-upload queue here, without submitting
        // immediately. Graphics.ExecuteUpload performs the centralized flush.
        Device.textureUploadBatch.AddTextureUpload(this);

        decoder.Dispose();
    }

    internal static DXTexture GetOrCreate(string name, SharpGLTF.Schema2.Image image, TextureMipPolicy mipPolicy = TextureMipPolicy.None)
    {
        DXTexture texture;

        // 2-6 clause 4: the policy is part of the cache identity. The same image file can legitimately be bound as
        // base colour in one material and as a normal map in another, and those two need different chains (one box
        // filtered, one renormalized). Keying on the name alone would silently hand the second caller whatever the
        // first one happened to ask for. The suffix is only appended for non-default policies so that every
        // pre-2-6 cache key stays byte-identical.
        string key = mipPolicy == TextureMipPolicy.None ? name : $"{name}#mip{mipPolicy}";

        if (DirectX.Device.DictionaryDXTexture.TryGetValue(key, out texture))
        {
            texture.AddRef();
        }
        else
        {
            texture = new DXTexture(name, image, mipPolicy);

            DirectX.Device.DictionaryDXTexture.Add(key, texture);
        }

        return texture;
    }

    /// <summary>
    /// Creates a new texture directly from already decoded pixels.
    /// It is not inserted into the global cache, so the caller owns its
    /// lifetime. Used by the "create new texture for material replacement" path.
    /// </summary>
    internal static DXTexture CreateFromDecoder(INativeImageDecoder decoder, TextureMipPolicy mipPolicy = TextureMipPolicy.None)
    {
        var tex = new DXTexture(decoder, mipPolicy);
        return tex;
    }

    internal static DXTexture CreateEmpty(int width, int height, string name = null)
    {
        var tex = new DXTexture
        {
            Name = name ?? string.Empty
        };

        tex.DescriptorID = Device.DescriptorAllocator.Allocate();
        tex.CpuDescriptorHandle = Device.SrvHeapManager.GetCpuHandle(tex.DescriptorID);
        tex.GpuDescriptorHandle = Device.SrvHeapManager.GetGpuHandle(tex.DescriptorID);
        tex.Width = (uint)width;
        tex.Height = (uint)height;
        tex.PrepareTextureLayout();
        tex.CreateSRV();
        tex.Ready = true;
        return tex;
    }

    /// <summary>
    /// 1-8 Format intent -> concrete D3D12 format.
    /// The mapping table is documented in the
    /// Season.Rendering.ComputeStorageFormat summary.
    /// D3D12 supports typed UAV access for all five cases, so no fallback chain
    /// is needed. Both 2D and 3D creation paths share this function as the
    /// single source of truth.
    /// </summary>
    internal static Format MapComputeFormat(Season.Rendering.ComputeStorageFormat format) => format switch
    {
        Season.Rendering.ComputeStorageFormat.Rgba16Float => Format.FormatR16G16B16A16Float,
        Season.Rendering.ComputeStorageFormat.R16Float => Format.FormatR16Float,
        Season.Rendering.ComputeStorageFormat.R8Unorm => Format.FormatR8Unorm,
        Season.Rendering.ComputeStorageFormat.Rg16Float => Format.FormatR16G16Float,
        _ => Format.FormatR8G8B8A8Unorm,
    };

    /// <summary>
    /// 1-6 Compute: creates a storage texture with AllowUnorderedAccess and no
    /// upload path. Format comes from ComputeStorageFormat. Support started with
    /// rgba16float in 2-1 Step A and added r16f / r8 / rg16f in 1-8.
    /// Resource, SRV, and UAV all share the same source of truth in _format.
    /// Both SRV and UAV descriptors are allocated in the shared shader-visible
    /// heap. The SRV lets Sprite2D sample the texture by name without code-path
    /// changes, while the UAV lets DispatchCompute bind it to a `u` register.
    /// Initial state is Common and all subsequent transitions are centralized in
    /// DispatchCompute.
    /// </summary>
    internal static DXTexture CreateComputeStorage(string name, uint width, uint height,
        Season.Rendering.ComputeStorageFormat format = Season.Rendering.ComputeStorageFormat.Rgba8Unorm)
    {
        var nativeFormat = MapComputeFormat(format);

        var tex = new DXTexture
        {
            Name = name,
            Width = width,
            Height = height,
            _format = nativeFormat,
        };

        tex.DescriptorID = Device.DescriptorAllocator.Allocate();
        tex.CpuDescriptorHandle = Device.SrvHeapManager.GetCpuHandle(tex.DescriptorID);
        tex.GpuDescriptorHandle = Device.SrvHeapManager.GetGpuHandle(tex.DescriptorID);

        tex.CreateTextureResource(ResourceFlags.AllowUnorderedAccess);
        tex.CreateSRV();

        tex.UavDescriptorID = Device.DescriptorAllocator.Allocate();
        tex.UavGpuDescriptorHandle = Device.SrvHeapManager.GetGpuHandle(tex.UavDescriptorID);

        var uavDesc = new UnorderedAccessViewDesc
        {
            Format = nativeFormat,
            ViewDimension = UavDimension.Texture2D,
            Texture2D = new Tex2DUav { MipSlice = 0 },
        };
        Device.D3dDevice->CreateUnorderedAccessView(
            tex._textureResource, null, &uavDesc, Device.SrvHeapManager.GetCpuHandle(tex.UavDescriptorID));

        tex.Ready = true;
        return tex;
    }

    /// <summary>
    /// Recreates the native resource of a storage texture in place to match a
    /// new size while reusing the already allocated SRV/UAV descriptor slots.
    /// This preserves the C# object identity, so Sprite2D AddRef references and
    /// DictionaryDXTexture keys remain unchanged.
    /// The caller must guarantee the GPU is idle. On resize, this is driven by
    /// BaseApp.Resize after HandleResize[WaitForGpu].
    /// </summary>
    internal void RecreateComputeStorage(uint width, uint height)
    {
        // Release the old ID3D12Resource via COM Release.
        // Descriptor slots are reused in place, without Free or re-Allocate.
        if (_textureResource != null)
        {
            _textureResource->Release();
            _textureResource = null;
        }

        Width = width;
        Height = height;

        // Recreate the committed resource with the same _format and rebind the
        // SRV to the same CpuDescriptorHandle.
        CreateTextureResource(ResourceFlags.AllowUnorderedAccess);
        CreateSRV();

        // Rebind the UAV to the CPU handle addressed by the same UavDescriptorID.
        var uavDesc = new UnorderedAccessViewDesc
        {
            Format = _format,
            ViewDimension = UavDimension.Texture2D,
            Texture2D = new Tex2DUav { MipSlice = 0 },
        };
        Device.D3dDevice->CreateUnorderedAccessView(
            _textureResource, null, &uavDesc, Device.SrvHeapManager.GetCpuHandle(UavDescriptorID));

        Ready = true;
    }

    /// <summary>
    /// Updates texture pixel content in place. The size must match the current
    /// GPU texture.
    /// Reuses the same ID3D12Resource, allocates no new GPU memory, and keeps
    /// the SRV handle unchanged.
    /// This is the optimal path for video frames or material replacement at the
    /// same size.
    /// </summary>
    public void UploadPixels(ReadOnlySpan<byte> rgbaPixels)
    {
        int expectedSize = (int)(Width * Height * 4);
        if (rgbaPixels.Length != expectedSize)
            throw new ArgumentException(
                $"Pixel data size mismatch. Expected {expectedSize} bytes for {Width}×{Height}, got {rgbaPixels.Length}.");

        ID3D12Resource* uploadBuf = GetOrCreateUploadBuffer(_totalBytes);

        bool closed = false;

        // 2-6 clause 4: in-place replacement has to refresh the whole chain. The incoming span only describes level
        // 0, so if this texture owns a chain the lower levels are regenerated here - otherwise they would keep
        // showing the previous content at distance, which is far more confusing to diagnose than no mipmaps at all.
        byte[] chain = null;
        MipLevelInfo[] chainInfos = null;
        if (_mipLevels > 1)
            chain = MipChain.Build(rgbaPixels, (int)Width, (int)Height, _mipPolicy, out chainInfos);

        try
        {
            void* mappedData = null;
            uploadBuf->Map(0, null, &mappedData);
            byte* mapped = (byte*)mappedData;

            if (chain != null)
            {
                fixed (byte* src = chain)
                {
                    for (uint level = 0; level < _mipLevels; level++)
                        RePitchLevel(mapped, src + chainInfos[level].ByteOffset, (uint)chainInfos[level].Width, level);
                }
            }
            else
            {
                fixed (byte* src = rgbaPixels)
                {
                    RePitchLevel(mapped, src, Width, 0);
                }
            }

            uploadBuf->Unmap(0, null);

            var cmdList = Device.UploadCommandList;

            // 3. Barrier: current state -> CopyDest
            {
                ResourceStates currentState;
                lock (_stateLock)
                {
                    currentState = _currentState;
                }
                var barrier = Device.InitTransition(
                    _textureResource, currentState, ResourceStates.CopyDest);
                cmdList->ResourceBarrier(1, &barrier);
            }

            // 4. CopyTextureRegion, once per subresource.
            for (uint level = 0; level < _mipLevels; level++)
            {
                var dst = new TextureCopyLocation
                {
                    Type = TextureCopyType.SubresourceIndex,
                    Anonymous = { SubresourceIndex = level },
                    PResource = _textureResource
                };

                var srcLoc = new TextureCopyLocation
                {
                    Type = TextureCopyType.PlacedFootprint,
                    Anonymous = { PlacedFootprint = _footprints[level] },
                    PResource = uploadBuf
                };

                cmdList->CopyTextureRegion(&dst, 0, 0, 0, &srcLoc, null);
            }

            // 5. Barrier: CopyDest -> Common
            // Finish the full state transition within the same queue.
            {
                var barrier = Device.InitTransition(
                    _textureResource, ResourceStates.CopyDest, ResourceStates.Common);
                cmdList->ResourceBarrier(1, &barrier);
            }

            // 6. Submit to the Graphics queue and wait for completion
            cmdList->Close();
            closed = true;
            Device.CommandQueue->ExecuteCommandLists(
                1, (ID3D12CommandList**)&cmdList);

            // Use the dedicated UploadFence with a monotonically increasing
            // value. Never signal arbitrary values on the ring fence
            // (Device.Fence), or MoveToNextFrame / PumpDeferredReleases would
            // see corrupted completed values, breaking frame synchronization and
            // causing premature resource release.
            ulong fenceValue = Device.NextUploadFenceValue();
            Device.CommandQueue->Signal(Device.UploadFence, fenceValue);
            while (Device.UploadFence->GetCompletedValue() < fenceValue)
            {
                Device.UploadFence->SetEventOnCompletion(fenceValue, Device.UploadFenceEvent.ToPointer());
                SilkMarshal.WaitWindowsObjects(Device.UploadFenceEvent);
            }

            UploadFenceValue = 0;

            lock (_stateLock)
            {
                _currentState = ResourceStates.Common;
            }
        }
        catch (Exception ex)
        {
            throw ex;
        }
        finally
        {
            // uploadBuf is reused across frames; lifetime managed by _textureUploadHeap / Dispose().
            // D3D12 requires every associated list to be closed before
            // CommandAllocator::Reset.
            // The normal path in the try block already closes the list. If an
            // exception is thrown before Close(), the list remains in recording
            // state and must be closed here. This avoids a double-Close
            // SEHException.
            if (!closed)
            {
                Device.UploadCommandList->Close();
            }
            // Reset the allocator to release its internal references to texture
            // resources. Otherwise the Debug Layer may later detect that the
            // resource is still referenced during Dispose and throw SEHException.
            Device.UploadCommandAllocator->Reset();
            Device.UploadCommandList->Reset(Device.UploadCommandAllocator, null);
        }
    }

    public void UploadSubRects(ReadOnlySpan<byte> rgbaPixels, int sourceWidth, int sourceHeight, ReadOnlySpan<TextureUploadRect> dirtyRects)
    {
        if (dirtyRects.Length == 0)
        {
            return;
        }

        int expectedSize = sourceWidth * sourceHeight * 4;
        if (rgbaPixels.Length != expectedSize)
            throw new ArgumentException(
                $"Pixel data size mismatch. Expected {expectedSize} bytes for {sourceWidth}×{sourceHeight}, got {rgbaPixels.Length}.");

        Device.textureUploadBatch.ExecuteSubRectUpload(
            this, rgbaPixels.ToArray(), sourceWidth, sourceHeight,
            dirtyRects.ToArray(),
            Device.CopyGraphicsCommandList, Device.CopyCommandQueue);
    }

    // ──────── State management ────────

    public void TransitionTo(ID3D12GraphicsCommandList* commandList, ResourceStates newState)
    {
        if (_textureResource == null)
            return;

        lock (_stateLock)
        {
            if (_currentState != newState)
            {
                var barrier = Device.InitTransition(
                    _textureResource, _currentState, newState);
                commandList->ResourceBarrier(1, &barrier);
                _currentState = newState;
            }
        }
    }

    public void EnsureState(ID3D12GraphicsCommandList* commandList, ResourceStates desiredState)
    {
        TransitionTo(commandList, desiredState);
    }

    public void ValidateState(ResourceStates expectedState)
    {
        if (_currentState != expectedState)
        {
            throw new InvalidOperationException(
                $"Texture resource state mismatch. Expected: {expectedState}, Actual: {_currentState}");
        }
    }

    public void EnsureReadyForRendering(ID3D12GraphicsCommandList* commandList)
    {
        // This Volatile read pairs with the upload thread's Volatile.Write(Ready).
        // It guarantees that once Ready==true is observed, UploadFenceValue is
        // also visible. Without that guarantee, CopyFence waiting could be
        // skipped and a transition barrier might be submitted while Copy Queue
        // writes are still in flight, triggering Debug Layer EXECUTION ERROR #1047.
        if (!System.Threading.Volatile.Read(ref Ready))
            return;

        if (UploadFenceValue > 0)
        {
            Device.DirectQueueWaitCopyFence(UploadFenceValue);
            UploadFenceValue = 0;
        }

        if (_currentState == ResourceStates.Common)
        {
            TransitionTo(commandList, ResourceStates.PixelShaderResource);
        }
    }

    // ──────── Upload preparation ────────

    void PrepareForBatchUpload(INativeImageDecoder decoder, TextureMipPolicy mipPolicy)
    {
        Width = (uint)decoder.Width;
        Height = (uint)decoder.Height;

        // All decoders guarantee RGBA8. Copy row by row using Stride
        // to handle platforms where stride > Width*4 (e.g. Pixbuf padding).
        int srcStride = decoder.Stride;
        int dstStride = (int)Width * 4;
        _imageData = new byte[Height * dstStride];

        var srcSpan = decoder.PixelSpan;
        for (int y = 0; y < Height; y++)
        {
            var srcRow = srcSpan.Slice(y * srcStride, dstStride);
            var dstRow = new Span<byte>(_imageData, y * dstStride, dstStride);
            srcRow.CopyTo(dstRow);
        }

        // 2-6 clause 3: the de-strided buffer above is exactly the tightly packed input MipChain.Build requires,
        // which is why generation belongs here and not earlier - this is the first point where the pixels are in a
        // known-dense layout, and the last point before the D3D12-specific upload layout is derived.
        if (MipChain.ShouldGenerate(mipPolicy, (int)Width, (int)Height))
        {
            _imageData = MipChain.Build(_imageData, (int)Width, (int)Height, mipPolicy, out _mipInfos);
            _mipLevels = (uint)_mipInfos.Length;
            _mipPolicy = mipPolicy;
        }
        else
        {
            _mipInfos = [new MipLevelInfo((int)Width, (int)Height, 0)];
            _mipLevels = 1;
        }

        PrepareTextureLayout();
    }

    void PrepareTextureLayout()
    {
        CreateTextureResource();

        var textureDesc = new ResourceDesc
        {
            Dimension = ResourceDimension.Texture2D,
            Alignment = 0,
            Width = Width,
            Height = Height,
            DepthOrArraySize = 1,
            MipLevels = (ushort)_mipLevels,
            Format = _format,
            SampleDesc = new SampleDesc(1, 0),
            Layout = TextureLayout.LayoutUnknown,
            Flags = ResourceFlags.None
        };

        _footprints = new PlacedSubresourceFootprint[_mipLevels];
        _numRows = new uint[_mipLevels];
        var rowSizes = new ulong[_mipLevels];
        ulong totalBytes;

        // Asking for all subresources in one call is what makes the per-level offsets correct: D3D12 pads each row
        // to 256 bytes and starts each subresource on a 512-byte boundary, and letting the runtime lay that out is
        // the only way to be sure the CPU-side re-pitch below agrees with what CopyTextureRegion will read.
        fixed (PlacedSubresourceFootprint* pFootprints = _footprints)
        fixed (uint* pNumRows = _numRows)
        fixed (ulong* pRowSizes = rowSizes)
        {
            Device.D3dDevice->GetCopyableFootprints(
                &textureDesc,
                0,
                _mipLevels,
                0,
                pFootprints,
                pNumRows,
                pRowSizes,
                &totalBytes
            );
        }

        _totalBytes = totalBytes;
    }

    public ulong GetTotalBytes()
    {
        const ulong placementAlignment = 512;
        return (_totalBytes + placementAlignment - 1) & ~(placementAlignment - 1);
    }

    /// <summary>Subresource count to iterate when copying this texture; 1 for every single-level texture.</summary>
    public uint MipLevels => _mipLevels;

    public TextureCopyLocation GetCopySourceLocation(uint mipLevel = 0)
    {
        return new TextureCopyLocation
        {
            Type = TextureCopyType.PlacedFootprint,
            Anonymous = { PlacedFootprint = _footprints[mipLevel] },
            PResource = null
        };
    }

    public TextureCopyLocation GetCopyDestLocation(uint mipLevel = 0)
    {
        return new TextureCopyLocation
        {
            Type = TextureCopyType.SubresourceIndex,
            Anonymous = { SubresourceIndex = mipLevel },
            PResource = _textureResource
        };
    }

    public void CopyDataToSharedHeap(byte* sharedUploadPtr, ulong offset)
    {
        if (_imageData == null || _imageData.Length == 0)
            return;

        fixed (byte* pSrcData = _imageData)
        {
            for (uint level = 0; level < _mipLevels; level++)
            {
                var info = _mipInfos[level];
                // The footprint offsets are relative to this texture's own region, so the batch offset shifts the
                // whole layout rather than being folded into each level.
                RePitchLevel(sharedUploadPtr + offset, pSrcData + info.ByteOffset, (uint)info.Width, level);
            }
        }

        _offsetInSharedHeap = offset;
    }

    /// <summary>
    /// Copies one densely packed level into its slot in a mapped upload buffer, converting the source stride to the
    /// 256-byte-aligned destination row pitch that D3D12 reported for that subresource.
    /// </summary>
    void RePitchLevel(byte* mappedBase, byte* src, uint levelWidth, uint level)
    {
        var footprint = _footprints[level];
        uint dstRowPitch = footprint.Footprint.RowPitch;
        uint srcRowPitch = levelWidth * 4;
        uint copySize = Math.Min(srcRowPitch, dstRowPitch);
        byte* dst = mappedBase + footprint.Offset;

        for (uint row = 0; row < _numRows[level]; row++)
        {
            Unsafe.CopyBlock(dst + row * dstRowPitch, src + row * srcRowPitch, copySize);
        }
    }

    static ulong AlignUp(ulong value, ulong alignment)
        => (value + alignment - 1) & ~(alignment - 1);

    internal void ValidateUploadRect(TextureUploadRect rect, int sourceWidth, int sourceHeight)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(rect), "Upload rect must have positive width and height.");

        if (rect.X < 0 || rect.Y < 0)
            throw new ArgumentOutOfRangeException(nameof(rect), "Upload rect origin cannot be negative.");

        if (rect.X + rect.Width > sourceWidth || rect.Y + rect.Height > sourceHeight)
            throw new ArgumentOutOfRangeException(nameof(rect), "Upload rect exceeds source bounds.");

        if (rect.X + rect.Width > Width || rect.Y + rect.Height > Height)
            throw new ArgumentOutOfRangeException(nameof(rect), "Upload rect exceeds destination texture bounds.");
    }

    internal void GetUploadFootprint(TextureUploadRect rect, ulong alignedOffset, out PlacedSubresourceFootprint footprint, out ulong rectTotalBytes)
    {
        var rectDesc = new ResourceDesc
        {
            Dimension = ResourceDimension.Texture2D,
            Alignment = 0,
            Width = (ulong)rect.Width,
            Height = (uint)rect.Height,
            DepthOrArraySize = 1,
            MipLevels = 1,
            Format = _format,
            SampleDesc = new SampleDesc(1, 0),
            Layout = TextureLayout.LayoutUnknown,
            Flags = ResourceFlags.None
        };

        PlacedSubresourceFootprint localFootprint;
        uint numRows;
        ulong rowSizeInBytes;
        ulong localRectTotalBytes;
        Device.D3dDevice->GetCopyableFootprints(
            &rectDesc,
            0,
            1,
            alignedOffset,
            &localFootprint,
            &numRows,
            &rowSizeInBytes,
            &localRectTotalBytes);

        footprint = localFootprint;
        rectTotalBytes = localRectTotalBytes;
    }

    internal void EnsureCommonForCopyQueue()
    {
        if (_textureResource == null)
        {
            return;
        }

        ResourceStates currentState;
        lock (_stateLock)
        {
            currentState = _currentState;
            if (currentState == ResourceStates.Common)
            {
                return;
            }
        }

        Device.ExecuteImmediateDirectTransition(_textureResource, currentState, ResourceStates.Common);

        lock (_stateLock)
        {
            _currentState = ResourceStates.Common;
        }
    }

    ID3D12Resource* GetOrCreateUploadBuffer(ulong requiredBytes)
    {
        if (_textureUploadHeap != null && _textureUploadHeapCapacity >= requiredBytes)
        {
            return _textureUploadHeap;
        }

        if (_textureUploadHeap != null)
        {
            _textureUploadHeap->Release();
            _textureUploadHeap = null;
            _textureUploadHeapCapacity = 0;
        }

        var heapProps = new HeapProperties(HeapType.Upload);
        var bufferDesc = new ResourceDesc
        {
            Dimension = ResourceDimension.Buffer,
            Width = requiredBytes,
            Height = 1,
            DepthOrArraySize = 1,
            MipLevels = 1,
            Format = Format.FormatUnknown,
            SampleDesc = new SampleDesc(1, 0),
            Layout = TextureLayout.LayoutRowMajor,
            Flags = ResourceFlags.None
        };
        var iid = ID3D12Resource.Guid;
        ID3D12Resource* uploadBuf;
        var hr = Device.D3dDevice->CreateCommittedResource(
            &heapProps, HeapFlags.None, &bufferDesc,
            ResourceStates.GenericRead, null, &iid, (void**)&uploadBuf);
        if (hr < 0) throw Marshal.GetExceptionForHR(hr)!;

        _textureUploadHeap = uploadBuf;
        _textureUploadHeapCapacity = requiredBytes;
        return uploadBuf;
    }

    private void CreateTextureResource(ResourceFlags flags = ResourceFlags.None)
    {
        var heapProps = new HeapProperties(HeapType.Default);
        var textureDesc = new ResourceDesc
        {
            Dimension = ResourceDimension.Texture2D,
            Alignment = 0,
            Width = Width,
            Height = Height,
            DepthOrArraySize = 1,
            MipLevels = (ushort)_mipLevels,
            Format = _format,
            SampleDesc = new SampleDesc(1, 0),
            Layout = TextureLayout.LayoutUnknown,
            Flags = flags
        };

        ID3D12Resource* textureResource;
        var iid = ID3D12Resource.Guid;

        var result = Device.D3dDevice->CreateCommittedResource(
            &heapProps,
            HeapFlags.None,
            &textureDesc,
            ResourceStates.Common,
            null,
            &iid,
            (void**)&textureResource
        );

        var ex = Marshal.GetExceptionForHR(result);
        if (ex != null) throw ex;

        _textureResource = textureResource;
        _currentState = ResourceStates.Common;
    }

    public void CreateSRV()
    {
        const uint DefaultShader4ComponentMapping = 0x00001688;

        var srvDesc = new ShaderResourceViewDesc
        {
            // SRV format follows the resource format.
            // Regular uploaded textures always use default rgba8, while storage
            // textures may use rgba16f starting from 2-1.
            Format = _format,
            ViewDimension = SrvDimension.Texture2D,
            Shader4ComponentMapping = DefaultShader4ComponentMapping,
            Texture2D = new Tex2DSrv
            {
                MipLevels = _mipLevels,
                MostDetailedMip = 0
            }
        };

        Device.D3dDevice->CreateShaderResourceView(_textureResource, &srvDesc, CpuDescriptorHandle);

        var deviceRemovedReason = Device.D3dDevice->GetDeviceRemovedReason();
        if (deviceRemovedReason != 0)
        {
            var ex2 = Marshal.GetExceptionForHR(deviceRemovedReason);
            throw ex2;
        }
    }

    public static unsafe void WaitForGpuComplete(ID3D12CommandQueue* queue, ID3D12Fence* fence, IntPtr FenceEvent, ref ulong fenceValue)
    {
        fenceValue++;
        queue->Signal(fence, fenceValue);
        fence->SetEventOnCompletion(fenceValue, FenceEvent.ToPointer());
        SilkMarshal.WaitWindowsObjects(FenceEvent);
    }

    public void Dispose()
    {
        if (_textureResource != null)
        {
            // Overwrite the SRV descriptor with a null descriptor first, so the
            // D3D12 Debug Layer does not detect a live descriptor still pointing
            // to the resource during Release and throw SEHException.
            const uint DefaultShader4ComponentMapping = 0x00001688;
            var nullSrvDesc = new ShaderResourceViewDesc
            {
                Format = Format.FormatR8G8B8A8Unorm,
                ViewDimension = SrvDimension.Texture2D,
                Shader4ComponentMapping = DefaultShader4ComponentMapping,
                Texture2D = new Tex2DSrv { MipLevels = 1, MostDetailedMip = 0 }
            };
            Device.D3dDevice->CreateShaderResourceView(null, &nullSrvDesc, CpuDescriptorHandle);

            // For compute storage textures, overwrite the UAV descriptor with a
            // null descriptor as well before returning the slot.
            if (UavDescriptorID >= 0)
            {
                var nullUavDesc = new UnorderedAccessViewDesc
                {
                    Format = Format.FormatR8G8B8A8Unorm,
                    ViewDimension = UavDimension.Texture2D,
                    Texture2D = new Tex2DUav { MipSlice = 0 },
                };
                Device.D3dDevice->CreateUnorderedAccessView(null, null, &nullUavDesc, Device.SrvHeapManager.GetCpuHandle(UavDescriptorID));
                Device.DescriptorAllocator.Free(UavDescriptorID);
                UavDescriptorID = -1;
            }

            _textureResource->Release();
            _textureResource = null;
        }

        if (_textureUploadHeap != null)
        {
            _textureUploadHeap->Release();
            _textureUploadHeap = null;
        }

        Device.DescriptorAllocator.Free(DescriptorID);
    }

    // ──────── Adapter: ImageResult → INativeImageDecoder (compatibility) ────────

    /// <summary>
    /// Temporary adapter wrapping StbImageSharp ImageResult as INativeImageDecoder,
    /// so existing callers in Graphics.cs continue to work while we migrate.
    /// ImageResult.Data from StbImageSharp (ColorComponents.RedGreenBlueAlpha) is RGBA8.
    /// </summary>
    //sealed unsafe class ImageResultDecoder : INativeImageDecoder
    //{
    //    readonly ImageResult _image;
    //    GCHandle _handle;
    //    byte* _ptr;

    //    internal ImageResultDecoder(ImageResult image)
    //    {
    //        _image = image ?? throw new ArgumentNullException(nameof(image));
    //        _handle = GCHandle.Alloc(_image.Data, GCHandleType.Pinned);
    //        _ptr = (byte*)_handle.AddrOfPinnedObject();
    //    }

    //    public int Width => _image.Width;
    //    public int Height => _image.Height;
    //    public int Stride => _image.Width * 4;

    //    public ReadOnlySpan<byte> PixelSpan
    //        => new ReadOnlySpan<byte>(_ptr, Height * Stride);

    //    public void Dispose()
    //    {
    //        if (_handle.IsAllocated)
    //            _handle.Free();
    //    }
    //}
}
