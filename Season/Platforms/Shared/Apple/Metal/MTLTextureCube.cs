// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Metal;

namespace Season.Platforms.Shared.Apple.Metal;

/// <summary>
/// Cubemap texture for render-quality 1-7 on Metal, aligned with D3D12 <c>DXTextureCube</c> and Vulkan <c>VKTextureCube</c>.
/// It uses a single-mip <c>MTLTextureType.Cube</c> texture where the six faces map to slices 0 through 5.
/// Face order follows the declaration order of <c>Season.Rendering.CubeFace</c>,
/// which naturally matches D3D12 subresources and Vulkan array layers, per contract clause 3.
/// It stays intentionally separate from <see cref="Texture"/> instead of extending it,
/// because that class assumes a Type2D single-slice workflow everywhere,
/// including CreateTexture2D, sub-rect updates, and named consumption by Sprite2D and materials.
/// Mixing cube textures into that path would force branching through the entire pipeline.
///
/// Lifetime:
/// CreateFromDecoders completes creation, six-face upload, and Ready publication synchronously,
/// so the result is usable immediately on return.
/// It registers itself by <see cref="Name"/> in this class's static dictionary using the same name-as-handle convention
/// used by 1-6 storage textures and by the D3D12 and Vulkan backends,
/// and later 2-4 DDGI sky-radiance lookup resolves it by name through <see cref="Find"/>.
///
/// Metal naturally removes two mechanisms required on the other backends, and this is a platform difference rather than an omission:
/// 1. There is no layout or barrier path.
///    After upload through a BlitEncoder into StorageMode.Private memory,
///    the texture can be sampled by the fragment stage immediately,
///    matching the rules documented by <see cref="Texture"/>.
///    That means there is no Vulkan TransitionAllLayers equivalent and no D3D12 EnsureReadyForRendering step.
/// 2. There is no ViewVersion concept.
///    Contract clause (b) requires cache invalidation through a monotonic version instead of a handle,
///    but that only matters when descriptor or framebuffer caches exist.
///    Metal binds texture objects directly through SetFragmentTexture on each pass,
///    so there is no baked descriptor cache to invalidate and therefore no corresponding object on this backend.
/// </summary>
internal sealed class MTLTextureCube : IDisposable
{
    /// <summary>Number of cubemap faces, always 6, with face order defined by Season.Rendering.CubeFace.</summary>
    public const int FaceCount = 6;

    /// <summary>
    /// Fragment-shader texture slot for the 1-7 environment radiance cube.
    /// texture(5) is already used by the 1-5 shadow atlas, so this path uses slot 6.
    /// Sampling reuses the static <c>Pipeline.StaticSampler</c> from texture(0..4), namely sampler(0) with Linear plus ClampToEdge.
    /// </summary>
    internal const nuint EnvCubeTextureSlot = 6;

    public string Name = string.Empty;

    /// <summary>The native texture is created and all six faces have finished uploading. Upload waits for completion, so Ready is true immediately on return.</summary>
    public bool Ready;

    /// <summary>Edge length of one face. All six faces are square and use the same size.</summary>
    public uint Size;

    public IMTLTexture Image = null!;

    MTLPixelFormat _format = MTLPixelFormat.RGBA8Unorm;

    /// <summary>Name-keyed registry using name-as-handle. All access is protected by this lock.</summary>
    static readonly Dictionary<string, MTLTextureCube> _registry = new();

    /// <summary>
    /// Environment radiance cube active for the current frame, resolved once per frame by MTLPrimitiveGroup.SetLighting.
    /// Null means no environment map is available, so texture(6) binds <see cref="DummyBlack"/>.
    /// </summary>
    internal static MTLTextureCube? Active;

    static MTLTextureCube? _dummyBlack;

    /// <summary>
    /// 1x1 all-black fallback cubemap.
    /// MSL statically declares <c>texturecube envCube</c>, and the fragment shader samples the specular path unconditionally
    /// before multiplying by the <c>step(0.5, envParams.w)</c> gate.
    /// The same formula is used literally across all four backends under contract clause 6.
    /// That means the texture is sampled even when the feature is logically disabled,
    /// so texture(6) must always hold a valid binding or Metal API Validation will fail and sampling becomes undefined.
    /// This fallback is bound whenever there is no environment map.
    /// Unlike the D3D12 path, Metal textures in StorageMode.Private do not guarantee zero initialization,
    /// just like Vulkan device memory,
    /// so all six faces must be uploaded explicitly with black pixels instead of assuming allocation starts at zero.
    /// </summary>
    internal static MTLTextureCube DummyBlack
    {
        get
        {
            if (_dummyBlack == null)
            {
                var faces = new byte[FaceCount][];
                for (int f = 0; f < FaceCount; f++)
                    faces[f] = new byte[] { 0, 0, 0, 255 };
                _dummyBlack = CreateAndUpload("__EnvCubeDummyBlack", 1, faces, register: false);
            }
            return _dummyBlack;
        }
    }

    /// <summary>The cubemap that should be bound to texture(6) for the current frame. Active takes priority, otherwise the all-black fallback is used. This is never null.</summary>
    internal static MTLTextureCube Bound => Active ?? DummyBlack;

    /// <summary>Finds a cubemap by name. Returns null when it is not registered.</summary>
    internal static MTLTextureCube? Find(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return null;

        lock (_registry)
        {
            return _registry.TryGetValue(name, out var cube) ? cube : null;
        }
    }

    /// <summary>
    /// Creates and registers a cubemap from six decoded RGBA8 face images using the declaration order of Season.Rendering.CubeFace.
    /// If a cubemap with the same name already exists it is reused directly,
    /// because 1-7 does not support runtime cubemap replacement. See the EnvironmentMap simplification boundary.
    /// Shared code already validates that all faces are same-size squares, so this method keeps only defensive assertions.
    /// </summary>
    internal static MTLTextureCube CreateFromDecoders(string name, int size,
        Season.Rendering.TextureCubeFormat format, INativeImageDecoder[] faces)
    {
        lock (_registry)
        {
            if (_registry.TryGetValue(name, out var existing))
                return existing;
        }

        if (format != Season.Rendering.TextureCubeFormat.Rgba8Unorm)
            throw new NotSupportedException(
                $"[MTLTextureCube] '{name}': 1-7 currently supports only Rgba8Unorm (got {format}).");

        if (faces == null || faces.Length != FaceCount)
            throw new ArgumentException($"[MTLTextureCube] '{name}': exactly {FaceCount} face images are required.", nameof(faces));

        // The decoder contract is tightly packed RGBA8, while allowing row-end padding when Stride > size * 4,
        // so copy row by row into a tightly packed layout here.
        // Note that this handles padding only and does not expand RGB into RGBA.
        // Channel normalization is the decoder's own responsibility.
        // LinuxImageDecoder previously exposed raw 3-channel Gdk.Pixbuf data and caused an out-of-bounds issue on the VK path,
        // which has since been fixed internally.
        // If another decoder violates the contract, the explicit checks below identify it directly
        // instead of surfacing an ambiguous out-of-range failure.
        int dstStride = size * 4;
        var faceData = new byte[FaceCount][];
        for (int f = 0; f < FaceCount; f++)
        {
            var decoder = faces[f];
            if (decoder == null || decoder.Width != size || decoder.Height != size)
                throw new ArgumentException(
                    $"[MTLTextureCube] '{name}': face {(Season.Rendering.CubeFace)f} has the wrong size (expected {size}x{size}).");

            if (decoder.Stride < dstStride)
                throw new ArgumentException(
                    $"[MTLTextureCube] '{name}': decoder for face {(Season.Rendering.CubeFace)f} violates " +
                    $"the INativeImageDecoder RGBA8 contract (Stride={decoder.Stride} < {dstStride}, " +
                    $"likely unexpanded 3-channel RGB data).");

            var data = new byte[size * dstStride];
            var src = decoder.PixelSpan;
            int srcStride = decoder.Stride;
            for (int y = 0; y < size; y++)
                src.Slice(y * srcStride, dstStride).CopyTo(new Span<byte>(data, y * dstStride, dstStride));
            faceData[f] = data;
        }

        return CreateAndUpload(name, (uint)size, faceData, register: true);
    }

    static MTLTextureCube CreateAndUpload(string name, uint size, byte[][] faceData, bool register)
    {
        var cube = new MTLTextureCube { Name = name, Size = size };
        cube.CreateTextureResource();
        cube.UploadFaces(faceData);
        cube.Ready = true;

        if (register)
        {
            lock (_registry)
            {
                if (_registry.TryGetValue(name, out var raced))
                {
                    cube.Dispose();
                    return raced;
                }
                _registry.Add(name, cube);
            }
        }

        return cube;
    }

    void CreateTextureResource()
    {
        // CreateTextureCubeDescriptor creates an MTLTextureType.Cube texture
        // with six face slices, square dimensions, and ArrayLength = 1.
        // mipmapped:false makes MipmapLevelCount = 1, matching the single-mip contract in clause 2.
        var desc = MTLTextureDescriptor.CreateTextureCubeDescriptor(_format, (nuint)Size, false);
        desc.Usage = MTLTextureUsage.ShaderRead;
        desc.StorageMode = MTLStorageMode.Private;   // GPU-only, uploaded through BlitEncoder just like Texture.

        Image = Device.MtlDevice.CreateTexture(desc)
            ?? throw new Exception($"MTLDevice.CreateTexture failed for cube '{Name}'");
    }

    /// <summary>
    /// Uploads all six faces in one pass using one staging buffer with tightly packed face data and six CopyFromBuffer calls,
    /// where destinationSlice = f is the face index.
    /// After submission it waits for completion, so the texture can be sampled by the fragment stage immediately on return.
    /// Metal has no layout transitions and no cross-queue visibility issue here,
    /// because the driver performs hazard tracking automatically, matching the Texture class rules.
    /// </summary>
    void UploadFaces(byte[][] faceData)
    {
        int faceBytes = (int)(Size * Size * 4);
        int totalBytes = faceBytes * FaceCount;

        var staging = Device.MtlDevice.CreateBuffer((nuint)totalBytes, MTLResourceOptions.StorageModeShared)
            ?? throw new Exception($"staging IMTLBuffer.CreateBuffer failed for cube '{Name}'");

        try
        {
            for (int f = 0; f < FaceCount; f++)
            {
                var data = faceData[f];
                if (data == null || data.Length != faceBytes)
                    throw new ArgumentException(
                        $"[MTLTextureCube] '{Name}': face {(Season.Rendering.CubeFace)f} has the wrong pixel byte count " +
                        $"(expected {faceBytes}, got {data?.Length ?? 0}).");

                System.Runtime.InteropServices.Marshal.Copy(
                    data, 0, staging.Contents + f * faceBytes, faceBytes);
            }

            var cmd = Device.GraphicsQueue.CreateCommandBuffer()
                ?? throw new Exception($"CreateCommandBuffer failed for cube '{Name}'");
            var blit = cmd.CreateBlitCommandEncoder(new MTLBlitPassDescriptor())
                ?? throw new Exception($"CreateBlitCommandEncoder failed for cube '{Name}'");

            for (int f = 0; f < FaceCount; f++)
            {
                blit.CopyFromBuffer(
                    staging,
                    (nuint)(f * faceBytes),         // Faces are packed tightly inside the staging buffer.
                    (nuint)(Size * 4),              // sourceBytesPerRow
                    (nuint)faceBytes,               // sourceBytesPerImage for one face.
                    new MTLSize((nint)Size, (nint)Size, 1),
                    Image,
                    (nuint)f,                       // destinationSlice equals the face index, with cube faces mapped to slices 0 through 5.
                    0,                              // destinationLevel for the single mip.
                    new MTLOrigin(0, 0, 0));
            }

            blit.EndEncoding();
            cmd.Commit();
            cmd.WaitUntilCompleted();
        }
        finally
        {
            staging.Dispose();
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

        // Metal retained references keep the texture object alive until any in-flight command buffers finish,
        // so immediate Dispose is safe and no VK or D3D12 style deferred-release queue is needed,
        // for the same reason documented on Texture.Recreate.
        if (Image != null)
        {
            Image.Dispose();
            Image = null!;
        }

        Ready = false;
    }
}
