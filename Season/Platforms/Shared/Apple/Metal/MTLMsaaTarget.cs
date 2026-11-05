// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Metal;

namespace Season.Platforms.Shared.Apple.Metal;

/// <summary>
/// Backend-private multisample pair used by the Msaa4x tier, mirroring Display.MsaaRenderTarget on D3D12 and
/// the MSAA images on Vulkan: one color texture plus the matching depth texture, both created with
/// <see cref="Device.MsaaSampleCount"/> samples and sized to the SceneColor target they resolve into.
///
/// It is deliberately not an <see cref="Season.Rendering.RenderTarget"/>: the shared layer never sees a
/// multisampled target, exactly as on the other three backends. BeginPass swaps it in as the attachment and
/// points the color attachment's ResolveTexture at the single-sampled SceneColor, so the rest of the frame keeps
/// sampling an ordinary 1x target.
///
/// Lifetime follows <see cref="MTLRenderTarget"/>: EnsureSize rebuilds in place when the backbuffer size or the
/// format changes, and old textures can be released immediately because command buffers retain what they use.
/// </summary>
internal sealed class MTLMsaaTarget : IDisposable
{
    IMTLTexture? _color;

    IMTLTexture? _depth;

    MTLPixelFormat _colorFormat;

    MTLPixelFormat _depthFormat;

    nuint _sampleCount;

    public IMTLTexture? ColorTexture => _color;

    public IMTLTexture? DepthTexture => _depth;

    public int Width { get; private set; }

    public int Height { get; private set; }

    /// <summary>Rebuilds the pair when the size, formats or sample count change. The first call also creates them.</summary>
    public void EnsureSize(int width, int height, MTLPixelFormat colorFormat, MTLPixelFormat depthFormat, nuint sampleCount)
    {
        if (width <= 0 || height <= 0 || sampleCount <= 1) return;
        if (_color != null && _depth != null && Width == width && Height == height
            && _colorFormat == colorFormat && _depthFormat == depthFormat && _sampleCount == sampleCount) return;

        IMTLTexture? color = null, depth = null;
        try
        {
            color = CreateMultisample(colorFormat, (nuint)width, (nuint)height, sampleCount, "MsaaSceneColor");
            depth = CreateMultisample(depthFormat, (nuint)width, (nuint)height, sampleCount, "MsaaSceneDepth");
        }
        catch
        {
            color?.Dispose();
            depth?.Dispose();
            throw;
        }

        // Replace only after both creations succeed; on failure the old pair stays consistent.
        _color?.Dispose();
        _depth?.Dispose();
        _color = color;
        _depth = depth;
        _colorFormat = colorFormat;
        _depthFormat = depthFormat;
        _sampleCount = sampleCount;
        Width = width;
        Height = height;
    }

    static IMTLTexture CreateMultisample(MTLPixelFormat format, nuint width, nuint height, nuint sampleCount,
        string label)
    {
        using var desc = MTLTextureDescriptor.CreateTexture2DDescriptor(format, width, height, false);
        // Metal requires the 2D-multisample texture type for anything above one sample; a resolve target stays 2D.
        desc.TextureType = MTLTextureType.k2DMultisample;
        desc.SampleCount = sampleCount;
        desc.Usage = MTLTextureUsage.RenderTarget;
        desc.StorageMode = MTLStorageMode.Private;
        var texture = Device.MtlDevice.CreateTexture(desc)
            ?? throw new Exception($"MTLMsaaTarget: CreateTexture({label}) failed");
        texture.Label = label;
        return texture;
    }

    public void Dispose()
    {
        _color?.Dispose();
        _depth?.Dispose();
        _color = null;
        _depth = null;
        Width = 0;
        Height = 0;
        _sampleCount = 0;
    }
}
