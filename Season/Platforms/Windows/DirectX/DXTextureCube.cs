// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Silk.NET.Direct3D12;
using Silk.NET.DXGI;

namespace Season.Platforms.Windows.DirectX;

/// <summary>
/// 1-7 cubemap on the D3D12 backend: a single-mip resource with
/// DepthOrArraySize=6 and SrvDimension.TextureCube.
/// It intentionally stays separate from <see cref="DXTexture"/> rather than
/// extending it, because DXTexture assumes a Texture2D single-subresource
/// pipeline everywhere, including SRV dimension, footprint, copy destination,
/// UAV, and sub-rect updates. Mixing cube support into it would add branches
/// to every path.
///
/// Lifetime: CreateFromDecoders synchronously performs
/// "create resource -> create SRV -> upload six faces -> publish Ready".
/// Once it returns, the cubemap is usable. It is then registered in the static
/// dictionary of this class under <see cref="Name"/>, following the
/// name-as-handle convention used by 1-6 storage textures, and later 2-4 DDGI
/// resolves sky radiance by name through <see cref="Find"/>.
/// On the render thread, each draw uses <see cref="EnsureReadyForRendering"/>
/// to wait on the CopyFence and transition into the sample state, matching the
/// DXTexture convention and the reasoning documented there
/// (EXECUTION ERROR #1047).
/// </summary>
internal unsafe sealed class DXTextureCube : IDisposable
{
    /// <summary>Number of cube faces. Always 6. Face order follows
    /// Season.Rendering.CubeFace.</summary>
    public const int FaceCount = 6;

    public string Name = string.Empty;

    /// <summary>The native resource exists and all six faces have finished
    /// uploading. Published with Volatile and set last.</summary>
    public bool Ready;

    /// <summary>Copy Queue fence value recorded when upload finishes.
    /// `0` means no wait is needed. Semantics match DXTexture.UploadFenceValue.</summary>
    public ulong UploadFenceValue;

    /// <summary>Edge length of one face. All six faces are equal-sized squares.</summary>
    public uint Size;

    public int DescriptorID = -1;

    public CpuDescriptorHandle CpuDescriptorHandle;

    public GpuDescriptorHandle GpuDescriptorHandle;

    Format _format = Format.FormatR8G8B8A8Unorm;

    ID3D12Resource* _resource = null;

    ResourceStates _currentState = ResourceStates.Common;

    readonly object _stateLock = new object();

    // Six-face upload layout: fetched in one GetCopyableFootprints(0, 6) call.
    // Each face offset is already 512-byte aligned by the API.
    readonly PlacedSubresourceFootprint[] _footprints = new PlacedSubresourceFootprint[FaceCount];
    readonly uint[] _numRows = new uint[FaceCount];
    byte[][] _faceData;
    ulong _totalBytes;

    /// <summary>Name-based registry following the name-as-handle convention.
    /// All accesses hold this lock.</summary>
    static readonly Dictionary<string, DXTextureCube> _registry = new();

    /// <summary>
    /// Environment radiance cubemap active for this frame.
    /// Resolved once per frame by DXPrimitiveGroup.SetLighting.
    /// Null means there is no environment map, so DrawPrimitive binds
    /// <see cref="DummyBlack"/>.
    /// </summary>
    internal static DXTextureCube Active;

    static DXTextureCube _dummyBlack;

    /// <summary>
    /// 1x1 all-black fallback cubemap.
    /// The root signature's t11 table always needs a valid descriptor because
    /// the shader statically references envCube. Bind this object when no
    /// environment map is available.
    /// It intentionally bypasses the upload path because D3D12 committed
    /// resources are zero-initialized by default unless CreateNotZeroed is
    /// requested. Zero is black, so this saves one Copy Queue round trip.
    /// </summary>
    internal static DXTextureCube DummyBlack
    {
        get
        {
            if (_dummyBlack == null)
            {
                var cube = new DXTextureCube { Name = "__EnvCubeDummyBlack", Size = 1 };
                cube.AllocateDescriptor();
                cube.CreateResource();
                cube.CreateSRV();
                cube.Ready = true;
                _dummyBlack = cube;
            }
            return _dummyBlack;
        }
    }

    /// <summary>Looks up by name. Returns null when not registered.</summary>
    internal static DXTextureCube Find(string name)
    {
        if (string.IsNullOrEmpty(name))
            return null;

        lock (_registry)
        {
            return _registry.TryGetValue(name, out var cube) ? cube : null;
        }
    }

    /// <summary>
    /// Creates and registers a cubemap from six decoded RGBA8 face images.
    /// Face order follows Season.Rendering.CubeFace declaration order.
    /// If the name already exists, it is reused directly because 1-7 does not
    /// support runtime cubemap replacement. See the simplified boundary in
    /// EnvironmentMap.
    /// The shared layer has already validated that all six faces are same-sized
    /// squares. This method only keeps a defensive assertion.
    /// </summary>
    internal static DXTextureCube CreateFromDecoders(string name, int size,
        Season.Rendering.TextureCubeFormat format, INativeImageDecoder[] faces)
    {
        lock (_registry)
        {
            if (_registry.TryGetValue(name, out var existing))
                return existing;
        }

        if (format != Season.Rendering.TextureCubeFormat.Rgba8Unorm)
            throw new NotSupportedException(
                $"[DXTextureCube] '{name}': 1-7 currently supports only Rgba8Unorm (got {format}).");

        if (faces == null || faces.Length != FaceCount)
            throw new ArgumentException($"[DXTextureCube] '{name}': exactly {FaceCount} face images are required.", nameof(faces));

        var cube = new DXTextureCube
        {
            Name = name,
            Size = (uint)size,
        };

        cube.AllocateDescriptor();
        cube.CreateResource();
        cube.CreateSRV();
        cube.StageFaces(faces);

        Device.textureUploadBatch.ExecuteCubeUpload(
            cube, Device.CopyGraphicsCommandList, Device.CopyCommandQueue);

        lock (_registry)
        {
            if (_registry.TryGetValue(name, out var raced))
            {
                cube.Dispose();
                return raced;
            }
            _registry.Add(name, cube);
        }

        return cube;
    }

    void AllocateDescriptor()
    {
        DescriptorID = Device.DescriptorAllocator.Allocate();
        CpuDescriptorHandle = Device.SrvHeapManager.GetCpuHandle(DescriptorID);
        GpuDescriptorHandle = Device.SrvHeapManager.GetGpuHandle(DescriptorID);
    }

    ResourceDesc BuildResourceDesc() => new ResourceDesc
    {
        Dimension = ResourceDimension.Texture2D,
        Alignment = 0,
        Width = Size,
        Height = Size,
        DepthOrArraySize = FaceCount,
        MipLevels = 1,
        Format = _format,
        SampleDesc = new SampleDesc(1, 0),
        Layout = TextureLayout.LayoutUnknown,
        Flags = ResourceFlags.None,
    };

    void CreateResource()
    {
        var heapProps = new HeapProperties(HeapType.Default);
        var desc = BuildResourceDesc();

        ID3D12Resource* resource;
        var iid = ID3D12Resource.Guid;
        var result = Device.D3dDevice->CreateCommittedResource(
            &heapProps, HeapFlags.None, &desc, ResourceStates.Common, null, &iid, (void**)&resource);

        var ex = Marshal.GetExceptionForHR(result);
        if (ex != null) throw ex;

        _resource = resource;
        _currentState = ResourceStates.Common;
    }

    void CreateSRV()
    {
        const uint DefaultShader4ComponentMapping = 0x00001688;

        var srvDesc = new ShaderResourceViewDesc
        {
            Format = _format,
            ViewDimension = SrvDimension.Texturecube,
            Shader4ComponentMapping = DefaultShader4ComponentMapping,
            TextureCube = new TexcubeSrv
            {
                MipLevels = 1,
                MostDetailedMip = 0,
                ResourceMinLODClamp = 0f,
            },
        };

        Device.D3dDevice->CreateShaderResourceView(_resource, &srvDesc, CpuDescriptorHandle);
    }

    /// <summary>
    /// Stages six faces into tightly packed RGBA8 temporary storage.
    /// Decoder stride may contain padding, so copying is performed row by row.
    /// Also fetches all six subresource footprints in a single call with
    /// BaseOffset=0, because the upload heap is exclusively owned by
    /// ExecuteCubeUpload.
    /// </summary>
    void StageFaces(INativeImageDecoder[] faces)
    {
        int dstStride = (int)Size * 4;
        _faceData = new byte[FaceCount][];

        for (int f = 0; f < FaceCount; f++)
        {
            var decoder = faces[f];
            if (decoder == null || decoder.Width != Size || decoder.Height != Size)
                throw new ArgumentException(
                    $"[DXTextureCube] '{Name}': face {(Season.Rendering.CubeFace)f} has the wrong size (expected {Size}x{Size}).");

            var data = new byte[(int)Size * dstStride];
            var src = decoder.PixelSpan;
            int srcStride = decoder.Stride;

            for (int y = 0; y < Size; y++)
                src.Slice(y * srcStride, dstStride).CopyTo(new Span<byte>(data, y * dstStride, dstStride));

            _faceData[f] = data;
        }

        var desc = BuildResourceDesc();
        var footprints = stackalloc PlacedSubresourceFootprint[FaceCount];
        var numRows = stackalloc uint[FaceCount];
        var rowSizes = stackalloc ulong[FaceCount];
        ulong totalBytes;

        Device.D3dDevice->GetCopyableFootprints(
            &desc, 0, FaceCount, 0, footprints, numRows, rowSizes, &totalBytes);

        for (int f = 0; f < FaceCount; f++)
        {
            _footprints[f] = footprints[f];
            _numRows[f] = numRows[f];
        }

        _totalBytes = totalBytes;
    }

    /// <summary>Total bytes required by the upload heap, including API-aligned
    /// gaps between the six faces.</summary>
    internal ulong GetTotalBytes()
    {
        const ulong placementAlignment = 512;
        return (_totalBytes + placementAlignment - 1) & ~(placementAlignment - 1);
    }

    /// <summary>Copies staged face pixels into the mapped upload heap using each
    /// face footprint's row pitch.</summary>
    internal void CopyFacesToUploadHeap(byte* mappedUploadPtr)
    {
        if (_faceData == null)
            return;

        uint srcRowPitch = Size * 4;

        for (int f = 0; f < FaceCount; f++)
        {
            var footprint = _footprints[f];
            uint dstRowPitch = footprint.Footprint.RowPitch;
            byte* dst = mappedUploadPtr + (nint)footprint.Offset;

            fixed (byte* src = _faceData[f])
            {
                for (uint row = 0; row < _numRows[f]; row++)
                    Unsafe.CopyBlock(dst + row * dstRowPitch, src + row * srcRowPitch, srcRowPitch);
            }
        }
    }

    /// <summary>Records six CopyTextureRegion calls.
    /// The subresource index is the face index, because on a single-mip cubemap
    /// the array slice is the subresource.</summary>
    internal void RecordFaceCopies(ID3D12GraphicsCommandList* commandList, ID3D12Resource* uploadHeap)
    {
        if (_resource == null)
            throw new InvalidOperationException($"[DXTextureCube] '{Name}': native resource is null and cannot be uploaded.");
        if (uploadHeap == null)
            throw new InvalidOperationException($"[DXTextureCube] '{Name}': upload heap is null.");

        for (int f = 0; f < FaceCount; f++)
        {
            var dstLocation = new TextureCopyLocation
            {
                Type = TextureCopyType.SubresourceIndex,
                Anonymous = { SubresourceIndex = (uint)f },
                PResource = _resource,
            };

            var srcLocation = new TextureCopyLocation
            {
                Type = TextureCopyType.PlacedFootprint,
                Anonymous = { PlacedFootprint = _footprints[f] },
                PResource = uploadHeap,
            };

            commandList->CopyTextureRegion(&dstLocation, 0, 0, 0, &srcLocation, null);
        }
    }

    /// <summary>Finalizes state after upload submission:
    /// reset state, record the fence, and publish Ready in the same order as
    /// ExecuteFullUploads.</summary>
    internal void OnUploadSubmitted(ulong fenceValue)
    {
        lock (_stateLock)
        {
            _currentState = ResourceStates.Common;
        }

        UploadFenceValue = fenceValue;
        _faceData = null;
        System.Threading.Volatile.Write(ref Ready, true);
    }

    /// <summary>Called before binding on the render thread:
    /// wait for the Copy Queue fence, then transition
    /// Common -> PixelShaderResource. Semantics match DXTexture.</summary>
    internal void EnsureReadyForRendering(ID3D12GraphicsCommandList* commandList)
    {
        if (!System.Threading.Volatile.Read(ref Ready))
            return;

        if (UploadFenceValue > 0)
        {
            Device.DirectQueueWaitCopyFence(UploadFenceValue);
            UploadFenceValue = 0;
        }

        lock (_stateLock)
        {
            if (_currentState != ResourceStates.PixelShaderResource)
            {
                var barrier = Device.InitTransition(
                    _resource, _currentState, ResourceStates.PixelShaderResource);
                commandList->ResourceBarrier(1, &barrier);
                _currentState = ResourceStates.PixelShaderResource;
            }
        }
    }

    public void Dispose()
    {
        lock (_registry)
        {
            if (!string.IsNullOrEmpty(Name) && _registry.TryGetValue(Name, out var registered) && registered == this)
                _registry.Remove(Name);
        }

        if (Active == this)
            Active = null;

        if (_resource != null)
        {
            // Overwrite the SRV with a null descriptor before Release, for the
            // same reason as DXTexture.Dispose. The Debug Layer throws an
            // SEHException if a live descriptor still points to a released resource.
            const uint DefaultShader4ComponentMapping = 0x00001688;
            var nullSrvDesc = new ShaderResourceViewDesc
            {
                Format = Format.FormatR8G8B8A8Unorm,
                ViewDimension = SrvDimension.Texturecube,
                Shader4ComponentMapping = DefaultShader4ComponentMapping,
                TextureCube = new TexcubeSrv { MipLevels = 1, MostDetailedMip = 0, ResourceMinLODClamp = 0f },
            };
            Device.D3dDevice->CreateShaderResourceView(null, &nullSrvDesc, CpuDescriptorHandle);

            _resource->Release();
            _resource = null;
        }

        if (DescriptorID >= 0)
        {
            Device.DescriptorAllocator.Free(DescriptorID);
            DescriptorID = -1;
        }

        _faceData = null;
        Ready = false;
    }
}
