// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Metal;

namespace Season.Platforms.Shared.Apple.Metal;

/// <summary>
/// Offscreen Metal RenderTarget for render-quality 1-1 step 2 and 3, with four shapes selected by Desc:
/// - BackbufferCompatible color in BGRA8Unorm, used both as attachment and sampled texture, plus matching depth.
///   That depth uses Depth32Float, matching existing PSO bake formats and avoiding pipeline-versus-pass attachment mismatch.
/// - Rgba16Float color for render-quality 1-4 HDR, also with matching depth.
/// - Rg16Float color for the 2-3 velocity RT, used both as attachment and sampled texture, with no companion depth,
///   because velocity relies on the explicit Scene-pass depth.
/// - Depth-only shape for the 1-5 shadow map in Depth32Float, using one depth texture as both attachment and sampled texture.
///   Metal depth textures can be sampled directly, so there is no DX-style typeless indirection,
///   and fixed-size targets do not resize.
///
/// Lifetime contract, aligned with VK and WebGPU:
/// - MatchBackbufferSize targets are rebuilt lazily when Device.BeginPass resolves them.
///   EnsureSize compares against Display dimensions and swaps the texture in place inside the same wrapper object,
///   so external references remain valid, matching the WebGPU lazy model without resize callbacks.
/// - Old textures can be released immediately during rebuild or Dispose.
///   Metal command buffers keep retained references, so in-flight frames can continue using old textures
///   with no Vulkan-style deferred-release queue.
/// </summary>
internal sealed class MTLRenderTarget : Season.Rendering.RenderTarget
{
    public IMTLTexture? ColorTexture;

    public IMTLTexture? DepthTexture;

    public int Width { get; private set; }

    public int Height { get; private set; }

    public MTLRenderTarget(in Season.Rendering.RenderTargetDesc desc, int width, int height)
    {
        Desc = desc;
        EnsureSize(width, height);
    }

    /// <summary>Depth-only shape used by the shadow map. There is no color target, and the depth texture includes ShaderRead so 1-5 can sample it.</summary>
    public bool IsDepthOnly => Desc.ColorFormat == Season.Rendering.RtFormat.None;

    /// <summary>Velocity RT shape for 2-3 in Rg16Float. It has no companion depth because it relies on the explicit Scene-pass depth.</summary>
    public bool IsVelocity => Desc.ColorFormat == Season.Rendering.RtFormat.Rg16Float;

    /// <summary>Rebuilds textures in place when size changes. The first call also creates them.</summary>
    public void EnsureSize(int width, int height)
    {
        if (width <= 0 || height <= 0) return;
        if ((ColorTexture != null || DepthTexture != null) && Width == width && Height == height) return;

        IMTLTexture? color = null, depth = null;
        try
        {
            if (!IsDepthOnly)
            {
                var colorFormat = Desc.ColorFormat switch
                {
                    Season.Rendering.RtFormat.Rgba16Float => MTLPixelFormat.RGBA16Float,
                    Season.Rendering.RtFormat.Rg16Float => MTLPixelFormat.RG16Float,
                    _ => Device.BackBufferFormat,
                };
                using var colorDesc = MTLTextureDescriptor.CreateTexture2DDescriptor(
                    colorFormat, (nuint)width, (nuint)height, false);
                colorDesc.Usage = MTLTextureUsage.RenderTarget | MTLTextureUsage.ShaderRead;
                colorDesc.StorageMode = MTLStorageMode.Private;
                color = Device.MtlDevice.CreateTexture(colorDesc)
                    ?? throw new Exception("MTLRenderTarget: CreateTexture(color) failed");
                color.Label = IsVelocity ? "SceneVelocity" : "OffscreenColor";
            }

            // The velocity RT has no companion depth because it relies on the explicit SceneDepth from the Scene pass.
            if (IsDepthOnly || (!IsVelocity && !Desc.NoImplicitDepth))
            {
                using var depthDesc = MTLTextureDescriptor.CreateTexture2DDescriptor(
                    Device.DepthBufferFormat, (nuint)width, (nuint)height, false);
                // The depth-only shadow-map shape must be sampled by 1-5,
                // while companion depth for color targets is attachment-only.
                depthDesc.Usage = IsDepthOnly
                    ? MTLTextureUsage.RenderTarget | MTLTextureUsage.ShaderRead
                    : MTLTextureUsage.RenderTarget;
                depthDesc.StorageMode = MTLStorageMode.Private;
                depth = Device.MtlDevice.CreateTexture(depthDesc)
                    ?? throw new Exception("MTLRenderTarget: CreateTexture(depth) failed");
                depth.Label = IsDepthOnly ? "ShadowMap" : "OffscreenDepth";
            }
        }
        catch
        {
            color?.Dispose();
            depth?.Dispose();
            throw;
        }

        // 全部创建成功后才替换，失败时旧附件与尺寸保持一致。
        ColorTexture?.Dispose();
        DepthTexture?.Dispose();
        ColorTexture = color;
        DepthTexture = depth;
        Width = width;
        Height = height;
        // Publish the resolved size into Desc, the only size the shared layer can read back. The descriptor
        // arrives with Width/Height = 0 for MatchBackbufferSize targets, and the fog pass sizes its froxel grid
        // from Desc and declines to register at grid 0 (see DX Device.CreateRenderTarget). This lazy path is
        // also the resize path, so the published size follows every rebuild.
        Desc.Width = (uint)width;
        Desc.Height = (uint)height;
    }

    public override void Dispose()
    {
        ColorTexture?.Dispose();
        DepthTexture?.Dispose();
        ColorTexture = null;
        DepthTexture = null;
        Width = 0;
        Height = 0;
    }
}
