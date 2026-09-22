// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

/// <summary>A rectangle with the origin in the upper left corner and pointing downwards to the right. Zero size indicates an empty area, and negative size is not used to represent mirroring.</summary>
public readonly record struct Rect2D(float X, float Y, float Width, float Height)
{
    public Vector2 Position => new(X, Y);
    public Vector2 Size => new(Width, Height);
    public float Right => X + Width;
    public float Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public bool Contains(Vector2 point) => point.X >= X && point.Y >= Y && point.X < Right && point.Y < Bottom;

    internal void Validate()
    {
        if (!float.IsFinite(X) || !float.IsFinite(Y) || !float.IsFinite(Right) || !float.IsFinite(Bottom)
            || !float.IsFinite(Width) || !float.IsFinite(Height) || Width < 0 || Height < 0)
            throw new ArgumentOutOfRangeException(nameof(Rect2D), "Rectangle must be finite and have non-negative dimensions.");
    }

    internal static void ValidateSize(Vector2 size)
    {
        if (!float.IsFinite(size.X) || !float.IsFinite(size.Y) || size.X <= 0 || size.Y <= 0)
            throw new ArgumentOutOfRangeException(nameof(size), "Size must be finite and positive.");
    }

    internal static Rect2D Intersect(Rect2D a, Rect2D b)
    {
        float x = Math.Max(a.X, b.X), y = Math.Max(a.Y, b.Y);
        return new(x, y, Math.Max(0, Math.Min(a.Right, b.Right) - x), Math.Max(0, Math.Min(a.Bottom, b.Bottom) - y));
    }
}

/// <summary>
/// Immutable image resource. Design size is independent of actual pixel size; loading resources with the same name reuses the engine's texture cache.
/// Dispose prevents new drawing references, but frames that have already recorded will still hold the resource, which will be collected by the backend according to GPU fence.
/// </summary>
public sealed class Image2D : IDisposable
{
    readonly object _sync = new();
    readonly Image2DResource _resource;
    int _references = 1;
    bool _disposed;
    public string Name { get; }
    public Vector2 DesignSize { get; }
    public Vector2 PixelSize { get; }
    public bool IsDisposed { get { lock (_sync) return _disposed; } }
    public ImageRegion2D Region => new(this, new(0, 0, PixelSize.X, PixelSize.Y), DesignSize);

    internal Image2D(string name, Vector2 designSize, Image2DResource resource)
    {
        Rect2D.ValidateSize(designSize);
        Name = name;
        DesignSize = designSize;
        PixelSize = resource.PixelSize;
        _resource = resource;
    }

    /// <summary>Preload the resource; do not call within a rendering pass. Asynchronous loading is recommended using LoadAsync.</summary>
    public static Image2D Load(string name, Vector2 designSize)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Rect2D.ValidateSize(designSize);
        var graphics = Season.Basic.Graphics.Instance ?? throw new InvalidOperationException("Graphics device not initialized.");
        return graphics.Immediate2D?.LoadImage(name, designSize)
            ?? throw new PlatformNotSupportedException("The current platform does not implement the immediate 2D backend.");
    }

    public static Task<Image2D> LoadAsync(string name, Vector2 designSize)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Rect2D.ValidateSize(designSize);
        var backend = Season.Basic.Graphics.Instance?.Immediate2D
            ?? throw new PlatformNotSupportedException("The current platform does not implement the immediate 2D backend.");
        return backend.LoadImageAsync(name, designSize);
    }

    /// <summary>Mapping the source area from design coordinates is only applicable for overall high-definition without changing the layout of the atlas.</summary>
    public ImageRegion2D FromDesignRect(Rect2D source, string? frameId = null)
    {
        source.Validate();
        if (source.IsEmpty || source.X < 0 || source.Y < 0 || source.Right > DesignSize.X || source.Bottom > DesignSize.Y)
            throw new ArgumentOutOfRangeException(nameof(source));
        var ratio = PixelSize / DesignSize;
        return new(this, new(source.X * ratio.X, source.Y * ratio.Y, source.Width * ratio.X, source.Height * ratio.Y), source.Size, frameId);
    }

    internal Image2DResource Acquire()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _references++;
            return _resource;
        }
    }

    internal void Release()
    {
        lock (_sync)
        {
            if (--_references == 0) _resource.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            Release();
        }
    }
}

/// <summary>Stable frame identifier, actual pixel clipping area, and design size; when repacking atlases, new pixel areas must be explicitly provided.</summary>
public sealed class ImageRegion2D
{
    public Image2D Image { get; }
    public Rect2D SourcePixels { get; }
    public Vector2 DesignSize { get; }
    public string? FrameId { get; }

    public ImageRegion2D(Image2D image, Rect2D sourcePixels, Vector2 designSize, string? frameId = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        sourcePixels.Validate();
        Rect2D.ValidateSize(designSize);
        if (sourcePixels.IsEmpty || sourcePixels.X < 0 || sourcePixels.Y < 0
            || sourcePixels.Right > image.PixelSize.X || sourcePixels.Bottom > image.PixelSize.Y)
            throw new ArgumentOutOfRangeException(nameof(sourcePixels));
        Image = image;
        SourcePixels = sourcePixels;
        DesignSize = designSize;
        FrameId = frameId;
    }

    /// <summary>Local scaling multiplies the design size without reading the actual pixel size of the image.</summary>
    public Rect2D At(Vector2 position, float scale = 1)
    {
        if (!float.IsFinite(scale) || scale < 0) throw new ArgumentOutOfRangeException(nameof(scale));
        return new(position.X, position.Y, DesignSize.X * scale, DesignSize.Y * scale);
    }
}

internal abstract class Image2DResource : IDisposable
{
    public abstract Vector2 PixelSize { get; }
    public abstract void Dispose();
}
