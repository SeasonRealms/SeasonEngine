// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using FontFace = Season.Fonts.Font;

namespace Season.Rendering;

public enum Sampling2D { Linear, Point }
[Flags]
public enum ImageFlip2D { None = 0, Horizontal = 1, Vertical = 2 }

/// <summary>Positioned Unicode code points and their baseline origin. The engine no longer typesets these positions.</summary>
public readonly record struct Glyph2D(int CodePoint, Vector2 Baseline);

/// <summary>
/// Instant 2D platform extension points. Prepare resources before all passes; Submit only within Overlay.
/// CompleteFrame must be called after successful completion or exception handling, without responsible for Present. Unimplemented platforms return null.
/// Except for LoadImage, all methods are called by the frame thread; Dispose is executed after stopping frame submission.
/// </summary>
public interface IImmediate2DBackend : IDisposable
{
    Vector2 OutputSize { get; }
    Image2D LoadImage(string name, Vector2 designSize);
    Task<Image2D> LoadImageAsync(string name, Vector2 designSize)
        => Task.Run(() => LoadImage(name, designSize));
    void Prepare(Draw2D frame);
    void Submit(Draw2D frame);
    void CompleteFrame();
}

internal enum Draw2DKind { Image, Rectangle, Glyph }
internal readonly record struct Draw2DCommand(
    Draw2DKind Kind, Rect2D Destination, Rect2D Source, Vector4 Color,
    Matrix3x2 Transform, Rect2D Clip, Image2DResource? Image = null,
    FontFace? Font = null, int FontSize = 0, int CodePoint = 0, float GlyphScale = 1,
    Sampling2D Sampling = Sampling2D.Linear, ImageFlip2D Flip = ImageFlip2D.None);

/// <summary>
/// Reusable frame command recorder; Only used in frame threads. Strictly preserve the submission order of images, rectangles, and fonts.
/// The host provides this object in BaseApp. Draw2D without creating a Control or directly operating the platform drawing API.
/// Coordinates are based on DesignSize as the logical canvas, centered and scaled to fit the physical output; DPI or BaseApp.Scale are no longer applied.
/// </summary>
public sealed class Draw2D
{
    readonly List<Draw2DCommand> _commands = new(256);
    readonly Dictionary<Image2D, Image2DResource> _images = new();
    readonly Stack<Matrix3x2> _transforms = new();
    readonly Stack<Rect2D> _clips = new();
    Matrix3x2 _transform;
    Rect2D _clip;
    int _thread;
    bool _recording;
    internal bool IsSealed { get; private set; }
    internal IReadOnlyList<Draw2DCommand> Commands => _commands;
    public int CommandCount => _commands.Count;
    public Vector2 DesignSize { get; private set; }
    public Vector2 OutputSize { get; private set; }
    public Matrix3x2 CanvasToOutput { get; private set; } = Matrix3x2.Identity;
    public Matrix3x2 OutputToCanvas { get; private set; } = Matrix3x2.Identity;

    /// <summary>The input must be a physical pixel; The coordinates of Windows TouchService need to be restored by multiplying BaseApp. Scale first, without multiplying DPI.</summary>
    public Vector2 ToCanvas(Vector2 outputPixels) => Vector2.Transform(outputPixels, OutputToCanvas);
    public Vector2 ToOutput(Vector2 canvasPoint) => Vector2.Transform(canvasPoint, CanvasToOutput);

    public void BeginFrame(Vector2 designSize, Vector2 outputSize)
    {
        if (_recording || IsSealed) throw new InvalidOperationException("The previous frame is not yet completed.");
        Rect2D.ValidateSize(designSize);
        Rect2D.ValidateSize(outputSize);
        float scale = Math.Min(outputSize.X / designSize.X, outputSize.Y / designSize.Y);
        if (!float.IsFinite(scale) || scale <= 0)
            throw new ArgumentOutOfRangeException(nameof(outputSize), "The size ratio of canvas to output exceeds the valid range.");
        var offset = (outputSize - designSize * scale) * 0.5f;
        var transform = Matrix3x2.CreateScale(scale) * Matrix3x2.CreateTranslation(offset);
        if (!Matrix3x2.Invert(transform, out var inverse) || !IsFinite(inverse) || inverse.M11 <= 0 || inverse.M22 <= 0)
            throw new ArgumentOutOfRangeException(nameof(outputSize), "The canvas transform cannot be inverted.");
        _thread = Environment.CurrentManagedThreadId;
        DesignSize = designSize;
        OutputSize = outputSize;
        CanvasToOutput = transform;
        OutputToCanvas = inverse;
        _transform = CanvasToOutput;
        _clip = new(offset.X, offset.Y, designSize.X * scale, designSize.Y * scale);
        _recording = true;
    }

    public void EndFrame()
    {
        CheckRecording();
        if (_transforms.Count != 0 || _clips.Count != 0)
            throw new InvalidOperationException("PushTransform/PushClip must be paired with Pop.");
        _recording = false;
        IsSealed = true;
    }

    /// <summary>Clear the command and return the resource reference after submission or exception, while preserving the container capacity.</summary>
    public void Clear()
    {
        if ((_recording || IsSealed) && _thread != Environment.CurrentManagedThreadId)
            throw new InvalidOperationException("Frame commands can only be cleared by the owning frame thread.");
        _commands.Clear();
        foreach (var image in _images.Keys) image.Release();
        _images.Clear();
        _transforms.Clear();
        _clips.Clear();
        _recording = IsSealed = false;
    }

    public void PushTransform(Matrix3x2 local)
    {
        CheckRecording();
        var combined = local * _transform;
        if (!IsFinite(combined)) throw new ArgumentOutOfRangeException(nameof(local));
        _transforms.Push(_transform);
        _transform = combined;
    }

    public void PopTransform()
    {
        CheckRecording();
        if (_transforms.Count == 0) throw new InvalidOperationException("The transform stack is empty.");
        _transform = _transforms.Pop();
    }

    /// <summary>
    /// Clips are converted to output pixels when pushed and intersected with the parent clip, unaffected by subsequent transformations.
    /// The initial version only accepts axis-aligned local coordinate systems; after rotation/skewing, already pushed outer clips can still be used.
    /// </summary>
    public void PushClip(Rect2D rectangle)
    {
        CheckRecording();
        rectangle.Validate();
        if (Math.Abs(_transform.M12) > 0.00001f || Math.Abs(_transform.M21) > 0.00001f)
            throw new NotSupportedException("Please push axis-aligned clips before rotation/skewing.");
        var a = Vector2.Transform(rectangle.Position, _transform);
        var b = Vector2.Transform(new(rectangle.Right, rectangle.Bottom), _transform);
        _clips.Push(_clip);
        _clip = Rect2D.Intersect(_clip, new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y)));
    }

    public void PopClip()
    {
        CheckRecording();
        if (_clips.Count == 0) throw new InvalidOperationException("The clip stack is empty.");
        _clip = _clips.Pop();
    }

    public void DrawImage(Image2D image, Rect2D destination, Vector4? tint = null,
        Sampling2D sampling = Sampling2D.Linear, ImageFlip2D flip = ImageFlip2D.None)
    {
        ArgumentNullException.ThrowIfNull(image);
        RecordImage(image, new(0, 0, image.PixelSize.X, image.PixelSize.Y), destination, tint ?? Vector4.One, sampling, flip);
    }

    public void DrawImage(ImageRegion2D region, Rect2D destination, Vector4? tint = null,
        Sampling2D sampling = Sampling2D.Linear, ImageFlip2D flip = ImageFlip2D.None)
    {
        ArgumentNullException.ThrowIfNull(region);
        RecordImage(region.Image, region.SourcePixels, destination, tint ?? Vector4.One, sampling, flip);
    }

    void RecordImage(Image2D image, Rect2D source, Rect2D destination, Vector4 color, Sampling2D sampling, ImageFlip2D flip)
    {
        CheckRecording();
        destination.Validate();
        ValidateColor(color);
        if (!Enum.IsDefined(sampling) || (flip & ~(ImageFlip2D.Horizontal | ImageFlip2D.Vertical)) != 0)
            throw new ArgumentOutOfRangeException(nameof(sampling));
        ObjectDisposedException.ThrowIf(image.IsDisposed, image);
        if (destination.IsEmpty || _clip.IsEmpty || color.W <= 0) return;
        if (!_images.TryGetValue(image, out var resource))
        {
            resource = image.Acquire();
            _images.Add(image, resource);
        }
        _commands.Add(new(Draw2DKind.Image, destination, source, color, _transform, _clip, resource, Sampling: sampling, Flip: flip));
    }

    public void FillRectangle(Rect2D destination, Vector4 color)
    {
        CheckRecording();
        destination.Validate();
        ValidateColor(color);
        if (!destination.IsEmpty && !_clip.IsEmpty && color.W > 0)
            _commands.Add(new(Draw2DKind.Rectangle, destination, default, color, _transform, _clip));
    }

    /// <summary>
    /// Basic single line drawing, based on baseline positioning, returns the forward width. Do not fold lines, do not shape/kerning, and do not adjust line spacing.
    /// Line breaks, tabulation, and missing glyph explicit errors; The caller should branch or use the located DrawGlyphRun.
    /// </summary>
    public float DrawTexts(FontFace font, int fontSize, ReadOnlySpan<char> text, Vector2 baseline, Vector4 color, float scale = 1)
    {
        CheckRecording();
        ValidateText(font, fontSize, scale, color);
        ValidatePoint(baseline);
        // First validate the entire line to avoid leaving half-line commands with formatting errors.
        float advance = MeasureTexts(font, fontSize, text) * scale;
        float x = baseline.X;
        foreach (var rune in text.EnumerateRunes())
        {
            font.TryGetGlyphLayoutMetrics(fontSize, rune.Value, out var metrics, out _);
            RecordGlyph(font, fontSize, new(rune.Value, new(x, baseline.Y)), color, scale, metrics);
            x += metrics.AdvanceWidth * scale;
        }
        return advance;
    }

    public static float MeasureTexts(FontFace font, int fontSize, ReadOnlySpan<char> text)
    {
        ValidateText(font, fontSize, 1, Vector4.One);
        float width = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsControl(rune) || !font.TryGetGlyphLayoutMetrics(fontSize, rune.Value, out var metrics, out _))
                throw new ArgumentException($"Unsupported single-line glyph U+{rune.Value:X}; please have the caller break lines or choose a different font.", nameof(text));
            width += metrics.AdvanceWidth;
        }
        return width;
    }

    /// <summary>Copy the position of each glyph to the current frame; do not save the caller's array, nor reflow. scale only scales the glyphs, not the baseline position.</summary>
    public void DrawGlyphRun(FontFace font, int fontSize, ReadOnlySpan<Glyph2D> glyphs, Vector4 color, float scale = 1)
    {
        CheckRecording();
        ValidateText(font, fontSize, scale, color);
        foreach (var glyph in glyphs)
        {
            ValidatePoint(glyph.Baseline);
            if (!Rune.IsValid(glyph.CodePoint) || !font.TryGetGlyphLayoutMetrics(fontSize, glyph.CodePoint, out var metrics, out _))
                throw new ArgumentException($"Unsupported glyph U+{glyph.CodePoint:X}; please have the caller choose a different font.", nameof(glyphs));
            RecordGlyph(font, fontSize, glyph, color, scale, metrics);
        }
    }

    void RecordGlyph(FontFace font, int size, Glyph2D glyph, Vector4 color, float scale, GlyphMetrics metrics)
    {
        if (_clip.IsEmpty || color.W <= 0 || scale == 0 || (metrics.Width == 0 && metrics.Height == 0)) return;
        _commands.Add(new(Draw2DKind.Glyph, new(glyph.Baseline.X, glyph.Baseline.Y, 0, 0), default,
            color, _transform, _clip, Font: font, FontSize: size, CodePoint: glyph.CodePoint, GlyphScale: scale));
    }

    static void ValidateText(FontFace font, int size, float scale, Vector4 color)
    {
        ArgumentNullException.ThrowIfNull(font);
        if (size <= 0 || !float.IsFinite(scale) || scale < 0) throw new ArgumentOutOfRangeException(nameof(size));
        ValidateColor(color);
    }

    static void ValidateColor(Vector4 color)
    {
        if (!float.IsFinite(color.X) || !float.IsFinite(color.Y) || !float.IsFinite(color.Z) || !float.IsFinite(color.W))
            throw new ArgumentOutOfRangeException(nameof(color));
    }

    static void ValidatePoint(Vector2 point)
    {
        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y)) throw new ArgumentOutOfRangeException(nameof(point));
    }

    static bool IsFinite(Matrix3x2 m) => float.IsFinite(m.M11) && float.IsFinite(m.M12) && float.IsFinite(m.M21)
        && float.IsFinite(m.M22) && float.IsFinite(m.M31) && float.IsFinite(m.M32);

    void CheckRecording()
    {
        if (!_recording || Environment.CurrentManagedThreadId != _thread)
            throw new InvalidOperationException("Can only draw within the current frame's command recording callback.");
    }
}
