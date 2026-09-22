// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using NativeImage = global::Season.Rendering.Image2D;

namespace Microsoft.Xna.Framework.Graphics;

/// <summary>Read-only, straight-alpha image handle. Create through SeasonXNA.Interop.SeasonResources.</summary>
public sealed class Texture2D : IDisposable
{
    private NativeImage? _image;
    private readonly bool _ownsImage;
    private readonly Dictionary<Rectangle, global::Season.Rendering.ImageRegion2D> _regions = new();

    internal Texture2D(NativeImage image, bool ownsImage)
    {
        ArgumentNullException.ThrowIfNull(image);
        ObjectDisposedException.ThrowIf(image.IsDisposed, image);
        Width = PixelDimension(image.PixelSize.X);
        Height = PixelDimension(image.PixelSize.Y);
        _image = image;
        _ownsImage = ownsImage;
    }

    public int Width { get; }
    public int Height { get; }
    public Rectangle Bounds => new(0, 0, Width, Height);
    public bool IsDisposed
    {
        get
        {
            var image = _image;
            return image is null || image.IsDisposed;
        }
    }

    internal NativeImage Image
    {
        get
        {
            var image = _image;
            ObjectDisposedException.ThrowIf(image is null || image.IsDisposed, this);
            return image!;
        }
    }

    public void Dispose()
    {
        var image = Interlocked.Exchange(ref _image, null);
        _regions.Clear();
        if (_ownsImage) image?.Dispose();
    }

    internal global::Season.Rendering.ImageRegion2D GetRegion(Rectangle source)
    {
        var image = Image;
        if (!_regions.TryGetValue(source, out var region))
        {
            region = new(image, new(source.X, source.Y, source.Width, source.Height), new(source.Width, source.Height));
            // Bound retention for callers animating arbitrary crop rectangles.
            if (_regions.Count == 256) _regions.Clear();
            _regions.Add(source, region);
        }
        return region;
    }

    private static int PixelDimension(float value)
    {
        if (!float.IsFinite(value) || value <= 0 || (double)value > int.MaxValue || value != MathF.Truncate(value))
            throw new ArgumentException("Image pixel dimensions must be positive integers representable by XNA.");
        return (int)value;
    }
}
