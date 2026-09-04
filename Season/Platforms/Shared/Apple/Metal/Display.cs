// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Metal;

namespace Season.Platforms.Shared.Apple.Metal;

/// <summary>
/// Aligns with the DX12 and Vulkan Display implementations by tracking the current backbuffer size plus viewport and scissor.
/// On the Metal path, both color and depth targets are managed directly by MTKView,
/// with DepthStencilPixelFormat set to Depth32Float,
/// so this class no longer owns a depth texture or framebuffer and only refreshes the viewport on resize.
/// </summary>
internal sealed class Display
{
    public int Width { get; private set; }

    public int Height { get; private set; }

    public MTLViewport Viewport { get; private set; }

    public MTLScissorRect ScissorRect { get; private set; }

    Vector4 _clearColor = new(1f, 1f, 1f, 1f);

    public Vector4 ClearColor => _clearColor;

    public void SetClearColor(Vector4 color) => _clearColor = color;

    public void Initialize(int width, int height) => Resize(width, height);

    public void Resize(int width, int height)
    {
        if (width <= 0 || height <= 0) return;
        Width = width;
        Height = height;

        Viewport = new MTLViewport
        {
            OriginX = 0,
            OriginY = 0,
            Width = width,
            Height = height,
            ZNear = 0.0,
            ZFar = 1.0
        };

        ScissorRect = new MTLScissorRect
        {
            X = 0,
            Y = 0,
            Width = (nuint)width,
            Height = (nuint)height
        };
    }
}
