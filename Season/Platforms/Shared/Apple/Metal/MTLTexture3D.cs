// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Metal;

namespace Season.Platforms.Shared.Apple.Metal;

/// <summary>
/// Compute 3D storage texture for render-quality 1-8 on Metal, aligned with D3D12 <c>DXTexture3D</c> and Vulkan <c>VKTexture3D</c>.
/// It uses a single-mip <c>MTLTextureType.k3D</c> texture with <c>ShaderRead | ShaderWrite</c>,
/// satisfying both MSL sampling through <c>texture3d&lt;float&gt;</c> and writing through <c>texture3d&lt;float, access::write&gt;</c>.
/// Trilinear filtering plus edge clamping comes from <c>Pipeline.StaticSampler</c>,
/// where all S, T, and R axes use ClampToEdge and both MinFilter and MagFilter use Linear,
/// so 1-8 does not need a new sampler.
/// There is no upload path because all contents are kernel-generated, such as Global SDF or voxel albedo data,
/// and the texture is ready immediately after creation.
///
/// This type stays intentionally separate from <see cref="Texture"/> instead of extending it,
/// for the same reason as <see cref="MTLTextureCube"/>.
/// That class assumes a Type2D single-slice workflow everywhere,
/// including CreateTexture2D, the bytesPerRow semantics of UploadPixels and UploadSubRects, and Recreate taking only width and height.
/// Even more importantly, the registry must remain separate:
/// entries in Graphics.DictionaryMtlTexture are consumed by Sprite2D, LoadSprite2D, and material lookup by name,
/// and mixing 3D textures into that registry would hand those paths dimensions that cannot be sampled as 2D.
/// Cubemaps in 1-7 already follow the same separation.
/// Because of that, 3D textures cannot be shown directly through Sprite2D,
/// and visualization must go through a 3D-to-2D slice kernel.
///
/// Lifetime:
/// Graphics.CreateComputeTexture3D resolves by name through <see cref="CreateOrUpdate"/>,
/// rebuilding in place when dimensions or format mismatch so the C# object identity stays stable.
/// DispatchCompute looks up the texture object by name through <see cref="Find"/>.
/// Metal naturally removes two mechanisms required on the other backends, and this is a platform difference rather than an omission:
/// 1. There is no layout or state transition path.
///    The driver performs hazard tracking automatically under Device rule 2,
///    so there is no D3D12 UnorderedAccess to NonPixelShaderResource transition
///    and no Vulkan General to ShaderReadOnlyOptimal barrier.
/// 2. There is no deferred-release queue.
///    In-flight command buffers keep retained references to textures under rule 5,
///    so immediate Dispose is safe.
///
/// Dimension contract:
/// the shared contract requires capability queries when any dimension exceeds 256.
/// Vulkan guarantees only 256 as the lower bound through maxImageDimension3D,
/// while Metal GPU families support at least 2048 for 3D textures,
/// so Metal is not the limiting factor here.
/// A single SDF cascade is recommended to stay at or below 128 cubed.
/// </summary>
internal sealed class MTLTexture3D : IDisposable
{
    public string Name = string.Empty;

    public uint Width;

    public uint Height;

    public uint Depth;

    /// <summary>The native texture is ready. Creation sets this immediately because there is no upload wait.</summary>
    public bool Ready;

    public IMTLTexture Image = null!;

    public MTLPixelFormat Format = MTLPixelFormat.RGBA8Unorm;

    /// <summary>Name-keyed registry using name-as-handle, matching the 1-6 and 1-7 convention. All access is protected by this lock.</summary>
    static readonly Dictionary<string, MTLTexture3D> _registry = new();

    /// <summary>Finds a texture by name. Returns null when it is not registered.</summary>
    internal static MTLTexture3D? Find(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return null;

        lock (_registry)
        {
            return _registry.TryGetValue(name, out var tex) ? tex : null;
        }
    }

    /// <summary>Fallback for step E of 2-5: a 1x1x1 RGBA8Unorm texture bound by Device.BeginPass at texture(10) when apLut is not ready.
    /// Undefined contents are acceptable because apParams0.x, representing far distance in kilometers, gates sampling to zero when the feature is off,
    /// and a real LUT must exist when it is on.
    /// It is created lazily, does not enter the registry because Name is empty and Dispose naturally skips registry removal,
    /// and remains alive for the process lifetime, mirroring VKTexture3D.DummyBlack one to one.</summary>
    internal static MTLTexture3D DummyBlack => _dummyBlack ??= CreateDummyBlack();

    static MTLTexture3D? _dummyBlack;

    static MTLTexture3D CreateDummyBlack()
    {
        var tex = new MTLTexture3D
        {
            Name = string.Empty,
            Width = 1,
            Height = 1,
            Depth = 1,
            Format = MTLPixelFormat.RGBA8Unorm,
        };
        tex.CreateTextureResource();
        tex.Ready = true;
        return tex;
    }

    /// <summary>
    /// Creates or updates a 3D storage texture by name.
    /// When an entry with the same name already exists and dimensions plus format match, it is reused directly.
    /// Otherwise the native texture is rebuilt in place so the same C# object identity stays valid for callers holding references.
    /// </summary>
    internal static MTLTexture3D CreateOrUpdate(string name, uint width, uint height, uint depth,
        MTLPixelFormat format)
    {
        lock (_registry)
        {
            if (_registry.TryGetValue(name, out var existing))
            {
                if (existing.Width == width && existing.Height == height
                    && existing.Depth == depth && existing.Format == format)
                    return existing;

                existing.Recreate(width, height, depth, format);
                return existing;
            }

            var tex = new MTLTexture3D
            {
                Name = name,
                Width = width,
                Height = height,
                Depth = depth,
                Format = format,
            };
            tex.CreateTextureResource();
            tex.Ready = true;

            _registry.Add(name, tex);
            return tex;
        }
    }

    /// <summary>
    /// Maps the 1-8 format intent into the concrete Metal pixel format by delegating to <see cref="Texture.MapComputeFormat"/>.
    /// That method is the single source of truth shared by both 2D and 3D creation paths,
    /// preventing the same logical intent from mapping to different concrete formats in different places.
    /// </summary>
    internal static MTLPixelFormat MapComputeFormat(Season.Rendering.ComputeStorageFormat format)
        => Texture.MapComputeFormat(format);

    /// <summary>Rebuilds the texture in place.
    /// Retained references keep the old object alive for in-flight command buffers,
    /// so immediate Dispose is safe, matching <see cref="Texture.Recreate"/>.</summary>
    void Recreate(uint width, uint height, uint depth, MTLPixelFormat format)
    {
        Image?.Dispose();
        Width = width;
        Height = height;
        Depth = depth;
        Format = format;
        CreateTextureResource();
        Ready = true;
    }

    void CreateTextureResource()
    {
        // There is no CreateTexture3DDescriptor factory, unlike the 2D and Cube cases,
        // so fill the descriptor manually.
        // MipmapLevelCount = 1 matches the single-mip contract,
        // and ArrayLength is always 1 for k3D.
        var desc = new MTLTextureDescriptor
        {
            TextureType = MTLTextureType.k3D,
            PixelFormat = Format,
            Width = (nuint)Width,
            Height = (nuint)Height,
            Depth = (nuint)Depth,
            MipmapLevelCount = 1,
            ArrayLength = 1,
            SampleCount = 1,
            Usage = MTLTextureUsage.ShaderRead | MTLTextureUsage.ShaderWrite,
            StorageMode = MTLStorageMode.Private,
        };

        Image = Device.MtlDevice.CreateTexture(desc)
            ?? throw new Exception(
                $"MTLDevice.CreateTexture failed for 3D '{Name}' {Width}×{Height}×{Depth} {Format}");
    }

    public void Dispose()
    {
        lock (_registry)
        {
            if (!string.IsNullOrEmpty(Name) && _registry.TryGetValue(Name, out var registered)
                && ReferenceEquals(registered, this))
                _registry.Remove(Name);
        }

        Ready = false;
        Image?.Dispose();
        Image = null!;
    }
}
