// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using FontFace = Season.Fonts.Font;

namespace Season.Rendering;

public enum Sampling2D { Linear, Point }
[Flags]
public enum ImageFlip2D { None = 0, Horizontal = 1, Vertical = 2 }

/// <summary>已定位的 Unicode 码点及其基线原点。引擎不再对这些位置做排版。</summary>
public readonly record struct Glyph2D(int CodePoint, Vector2 Baseline);

/// <summary>
/// 即时 2D 平台扩展点。Prepare 在所有 pass 之前准备资源；Submit 仅在 Overlay 内提交。
/// CompleteFrame 必须在成功或异常后调用，不负责 Present。未实现的平台返回 null。
/// 除 LoadImage 外，所有方法均由帧线程调用；Dispose 在停止帧提交后执行。
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
/// 可复用的帧命令记录器；只在帧线程使用。图片、矩形、字形严格保留提交顺序。
/// 宿主在 BaseApp.Draw2D 中提供此对象，不创建 Control，也不直接操作平台绘制 API。
/// 坐标以 DesignSize 为逻辑画布，居中等比适配物理输出；不再叠乘 DPI 或 BaseApp.Scale。
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

    /// <summary>输入必须是物理像素；Windows TouchService 坐标需先乘 BaseApp.Scale 还原，不再乘 DPI。</summary>
    public Vector2 ToCanvas(Vector2 outputPixels) => Vector2.Transform(outputPixels, OutputToCanvas);
    public Vector2 ToOutput(Vector2 canvasPoint) => Vector2.Transform(canvasPoint, CanvasToOutput);

    public void BeginFrame(Vector2 designSize, Vector2 outputSize)
    {
        if (_recording || IsSealed) throw new InvalidOperationException("上一帧尚未完成。");
        Rect2D.ValidateSize(designSize);
        Rect2D.ValidateSize(outputSize);
        float scale = Math.Min(outputSize.X / designSize.X, outputSize.Y / designSize.Y);
        if (!float.IsFinite(scale) || scale <= 0)
            throw new ArgumentOutOfRangeException(nameof(outputSize), "画布与输出的尺寸比超出有效范围。");
        var offset = (outputSize - designSize * scale) * 0.5f;
        var transform = Matrix3x2.CreateScale(scale) * Matrix3x2.CreateTranslation(offset);
        if (!Matrix3x2.Invert(transform, out var inverse) || !IsFinite(inverse) || inverse.M11 <= 0 || inverse.M22 <= 0)
            throw new ArgumentOutOfRangeException(nameof(outputSize), "画布变换无法求逆。");
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
            throw new InvalidOperationException("PushTransform/PushClip 必须与 Pop 配对。");
        _recording = false;
        IsSealed = true;
    }

    /// <summary>提交后或异常时清空命令并归还资源引用，保留容器容量。</summary>
    public void Clear()
    {
        if ((_recording || IsSealed) && _thread != Environment.CurrentManagedThreadId)
            throw new InvalidOperationException("帧命令只能由所属帧线程清理。");
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
        if (_transforms.Count == 0) throw new InvalidOperationException("变换栈为空。");
        _transform = _transforms.Pop();
    }

    /// <summary>
    /// 裁剪在推入时转换为输出像素并与父裁剪求交，不受后续变换影响。
    /// 首版仅接受轴对齐局部坐标系；旋转/错切后可继续使用已经推入的外层裁剪。
    /// </summary>
    public void PushClip(Rect2D rectangle)
    {
        CheckRecording();
        rectangle.Validate();
        if (Math.Abs(_transform.M12) > 0.00001f || Math.Abs(_transform.M21) > 0.00001f)
            throw new NotSupportedException("请在旋转/错切之前推入轴对齐裁剪。");
        var a = Vector2.Transform(rectangle.Position, _transform);
        var b = Vector2.Transform(new(rectangle.Right, rectangle.Bottom), _transform);
        _clips.Push(_clip);
        _clip = Rect2D.Intersect(_clip, new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y)));
    }

    public void PopClip()
    {
        CheckRecording();
        if (_clips.Count == 0) throw new InvalidOperationException("裁剪栈为空。");
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
    /// 基础单行绘制，以基线定位，返回前进宽度。不折行、不做 shaping/kerning，不调整行距。
    /// 换行、制表和缺失字形显式报错；调用者应分行或使用已定位的 DrawGlyphRun。
    /// </summary>
    public float DrawTexts(FontFace font, int fontSize, ReadOnlySpan<char> text, Vector2 baseline, Vector4 color, float scale = 1)
    {
        CheckRecording();
        ValidateText(font, fontSize, scale, color);
        ValidatePoint(baseline);
        // 先验证整行，避免格式错误留下半行命令。
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
                throw new ArgumentException($"不支持的单行字形 U+{rune.Value:X}；请由调用者分行或选择字体。", nameof(text));
            width += metrics.AdvanceWidth;
        }
        return width;
    }

    /// <summary>复制每个字形的位置到本帧；不保存调用者数组，也不重新排版。scale 只缩放字形，不缩放基线位置。</summary>
    public void DrawGlyphRun(FontFace font, int fontSize, ReadOnlySpan<Glyph2D> glyphs, Vector4 color, float scale = 1)
    {
        CheckRecording();
        ValidateText(font, fontSize, scale, color);
        foreach (var glyph in glyphs)
        {
            ValidatePoint(glyph.Baseline);
            if (!Rune.IsValid(glyph.CodePoint) || !font.TryGetGlyphLayoutMetrics(fontSize, glyph.CodePoint, out var metrics, out _))
                throw new ArgumentException($"字体缺少字形 U+{glyph.CodePoint:X}。", nameof(glyphs));
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
            throw new InvalidOperationException("只能在当前帧的命令记录回调中绘制。");
    }
}
