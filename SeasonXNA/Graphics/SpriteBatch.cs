// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using SeasonXNA.Hosting;
using SeasonXNA.Internal;
using Affine = System.Numerics.Matrix3x2;
using Tint = System.Numerics.Vector4;
using Region = global::Season.Rendering.ImageRegion2D;
using NativeRect = global::Season.Rendering.Rect2D;
using Sampling = global::Season.Rendering.Sampling2D;
using Flip = global::Season.Rendering.ImageFlip2D;
using Canvas2D = global::Season.Rendering.Draw2D;
using System.Text;

namespace Microsoft.Xna.Framework.Graphics;

/// <summary>Deferred images and laid-out text on an explicitly bound Season frame.</summary>
public sealed class SpriteBatch : IDisposable
{
    private readonly DrawContext _context;
    private readonly List<Sprite> _sprites = new(256);
    private bool _begun;
    private bool _disposed;
    private Affine _transform;
    private Sampling _sampling;
    private readonly Stack<Rectangle?> _clips = new();
    private Rectangle? _clip;
    private readonly record struct Sprite(Texture2D? Texture, Region? Region, Affine Transform, Tint Color, Flip Flip,
        SpriteFont? Font = null, TextLayout? Text = null, Rectangle? Clip = null);

    public SpriteBatch(DrawContext context) => _context = context ?? throw new ArgumentNullException(nameof(context));
    public bool IsDisposed => _disposed;

    // Deliberately no Effect, DepthStencilState or RasterizerState placeholder parameters.
    public void Begin(SpriteSortMode sortMode = SpriteSortMode.Deferred, BlendState? blendState = null,
        SamplerState? samplerState = null, Matrix? transformMatrix = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_begun) throw new InvalidOperationException("End must be called before Begin.");
        DrawContracts.ValidateSortMode(sortMode);
        var transform = DrawContracts.ToAffine(transformMatrix ?? Matrix.Identity);
        _context.Register(this);
        _transform = transform;
        _sampling = (samplerState ?? SamplerState.LinearClamp).IsPoint ? Sampling.Point : Sampling.Linear;
        _begun = true;
    }

    /// <summary>
    /// Clips subsequent draws to <paramref name="rectangle"/>, intersected with the enclosing clip.
    /// The rectangle shares the coordinate space of draw positions; it is captured per sprite at queue time
    /// and becomes a per-command clip rect in the canvas (Draw2D), honored by every platform backend.
    /// Pair with <see cref="PopClip"/>; a clip that intersects to nothing hides its sprites entirely.
    /// </summary>
    public void PushClip(Rectangle rectangle)
    {
        CheckBegun();
        _clips.Push(_clip);
        _clip = _clip is { } current ? Rectangle.Intersect(current, rectangle) : rectangle;
    }

    /// <summary>Restores the enclosing clip pushed by <see cref="PushClip"/>.</summary>
    public void PopClip()
    {
        CheckBegun();
        if (_clips.Count == 0) throw new InvalidOperationException("The clip stack is empty.");
        _clip = _clips.Pop();
    }

    public void Draw(Texture2D texture, Vector2 position, Color color) =>
        Draw(texture, position, null, color, 0, Vector2.Zero, Vector2.One, SpriteEffects.None, 0);

    public void Draw(Texture2D texture, Vector2 position, Rectangle? sourceRectangle, Color color) =>
        Draw(texture, position, sourceRectangle, color, 0, Vector2.Zero, Vector2.One, SpriteEffects.None, 0);

    public void Draw(Texture2D texture, Rectangle destinationRectangle, Color color) =>
        Draw(texture, destinationRectangle, null, color, 0, Vector2.Zero, SpriteEffects.None, 0);

    public void Draw(Texture2D texture, Rectangle destinationRectangle, Rectangle? sourceRectangle, Color color) =>
        Draw(texture, destinationRectangle, sourceRectangle, color, 0, Vector2.Zero, SpriteEffects.None, 0);

    public void Draw(Texture2D texture, Vector2 position, Rectangle? sourceRectangle, Color color,
        float rotation, Vector2 origin, float scale, SpriteEffects effects, float layerDepth) =>
        Draw(texture, position, sourceRectangle, color, rotation, origin, new Vector2(scale), effects, layerDepth);

    public void Draw(Texture2D texture, Vector2 position, Rectangle? sourceRectangle, Color color,
        float rotation, Vector2 origin, Vector2 scale, SpriteEffects effects, float layerDepth)
    {
        var source = Validate(texture, sourceRectangle);
        Queue(texture, source, position, color, rotation, origin, scale, effects, layerDepth);
    }

    public void Draw(Texture2D texture, Rectangle destinationRectangle, Rectangle? sourceRectangle, Color color,
        float rotation, Vector2 origin, SpriteEffects effects, float layerDepth)
    {
        var source = Validate(texture, sourceRectangle);
        if (destinationRectangle.Width < 0 || destinationRectangle.Height < 0)
            throw new ArgumentOutOfRangeException(nameof(destinationRectangle), "Use scale/effects for mirroring, not negative rectangle sizes.");
        Queue(texture, source, new(destinationRectangle.X, destinationRectangle.Y), color, rotation, origin,
            new(destinationRectangle.Width / (float)source.Width, destinationRectangle.Height / (float)source.Height),
            effects, layerDepth);
    }

    private Rectangle Validate(Texture2D texture, Rectangle? sourceRectangle)
    {
        CheckBegun();
        _ = _context.Canvas;
        ArgumentNullException.ThrowIfNull(texture);
        _ = texture.Image;
        var source = sourceRectangle ?? texture.Bounds;
        DrawContracts.ValidateSource(source, texture.Width, texture.Height);
        return source;
    }

    private void Queue(Texture2D texture, Rectangle source, Vector2 position, Color color, float rotation,
        Vector2 origin, Vector2 scale, SpriteEffects effects, float depth)
    {
        DrawContracts.ValidateEffects(effects);
        var tint = DrawContracts.ToStraightTint(color);
        var transform = BuildTransform(position, rotation, origin, scale, depth);
        ValidateExtent(transform, source.Width, source.Height);
        // Validate before eliding invisible sprites, so invalid inputs never disappear silently.
        if (scale.X == 0 || scale.Y == 0 || color.A == 0) return;
        _sprites.Add(new(texture, texture.GetRegion(source), transform, tint, (Flip)effects, Clip: _clip));
    }

    public void DrawString(SpriteFont spriteFont, string text, Vector2 position, Color color) =>
        DrawString(spriteFont, text, position, color, 0, Vector2.Zero, Vector2.One, SpriteEffects.None, 0);

    public void DrawString(SpriteFont spriteFont, string text, Vector2 position, Color color,
        float rotation, Vector2 origin, float scale, SpriteEffects effects, float layerDepth) =>
        DrawString(spriteFont, text, position, color, rotation, origin, new Vector2(scale), effects, layerDepth);

    public void DrawString(SpriteFont spriteFont, string text, Vector2 position, Color color,
        float rotation, Vector2 origin, Vector2 scale, SpriteEffects effects, float layerDepth)
    {
        CheckBegun();
        var canvasToOutput = _context.Canvas.CanvasToOutput;
        if (_sampling != Sampling.Linear)
            throw new NotSupportedException("MSDF text requires LinearClamp. End the image batch and begin a linear text batch.");
        ArgumentNullException.ThrowIfNull(spriteFont);
        DrawContracts.ValidateEffects(effects);
        var tint = DrawContracts.ToStraightTint(color);
        var transform = BuildTransform(position, rotation, origin, scale, layerDepth);
        var layout = spriteFont.Layout(text);
        // Mirror the entire logical layout box, including glyph shapes, rather than only reversing character order.
        var mirror = new Affine(
            (effects & SpriteEffects.FlipHorizontally) != 0 ? -1 : 1, 0,
            0, (effects & SpriteEffects.FlipVertically) != 0 ? -1 : 1,
            (effects & SpriteEffects.FlipHorizontally) != 0 ? layout.Size.X : 0,
            (effects & SpriteEffects.FlipVertically) != 0 ? layout.Size.Y : 0);
        transform = mirror * transform;
        ValidateExtent(transform, layout.Size.X, layout.Size.Y, canvasToOutput);
        // Ink can overhang the advance box. Validate every baseline as well.
        foreach (var glyph in layout.Glyphs)
            ValidateExtent(Affine.CreateTranslation(glyph.Baseline) * transform, 0, 0, canvasToOutput);
        if (scale.X == 0 || scale.Y == 0 || color.A == 0 || layout.Glyphs.Length == 0) return;
        _sprites.Add(new(null, null, transform, tint, Flip.None, spriteFont, layout, _clip));
    }

    public void DrawString(SpriteFont spriteFont, StringBuilder text, Vector2 position, Color color) =>
        DrawString(spriteFont, Snapshot(text), position, color);

    public void DrawString(SpriteFont spriteFont, StringBuilder text, Vector2 position, Color color,
        float rotation, Vector2 origin, float scale, SpriteEffects effects, float layerDepth) =>
        DrawString(spriteFont, Snapshot(text), position, color, rotation, origin, scale, effects, layerDepth);

    public void DrawString(SpriteFont spriteFont, StringBuilder text, Vector2 position, Color color,
        float rotation, Vector2 origin, Vector2 scale, SpriteEffects effects, float layerDepth) =>
        DrawString(spriteFont, Snapshot(text), position, color, rotation, origin, scale, effects, layerDepth);

    private static string Snapshot(StringBuilder text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.ToString();
    }

    private Affine BuildTransform(Vector2 position, float rotation, Vector2 origin, Vector2 scale, float depth)
    {
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) ||
            !float.IsFinite(origin.X) || !float.IsFinite(origin.Y) ||
            !float.IsFinite(scale.X) || !float.IsFinite(scale.Y) || !float.IsFinite(rotation))
            throw new ArgumentOutOfRangeException(nameof(position), "Sprite geometry must be finite.");
        if (!float.IsFinite(depth) || depth < 0 || depth > 1)
            throw new ArgumentOutOfRangeException(nameof(depth), "Depth must be in [0,1]; Deferred does not sort by it.");
        float cos = (float)Math.Cos(rotation), sin = (float)Math.Sin(rotation);
        return Affine.CreateTranslation(-origin.X, -origin.Y) * Affine.CreateScale(scale.X, scale.Y)
            * new Affine(cos, sin, -sin, cos, 0, 0) * Affine.CreateTranslation(position.X, position.Y) * _transform;
    }

    private void ValidateExtent(Affine transform, float width, float height)
        => ValidateExtent(transform, width, height, _context.Canvas.CanvasToOutput);

    private static void ValidateExtent(Affine transform, float width, float height, Affine canvasToOutput)
    {
        var outputTransform = transform * canvasToOutput;
        if (!Finite(transform) || !Finite(outputTransform) ||
            !FiniteCorner(width, height, outputTransform))
            throw new ArgumentOutOfRangeException(nameof(transform), "Draw transform overflow.");
    }

    public void End()
    {
        CheckBegun();
        var canvas = _context.Canvas; // A wrong-thread call must not cancel the owner's batch.
        try
        {
            // All resource handles must remain alive through End.
            foreach (var sprite in _sprites)
            {
                if (sprite.Texture is { } texture) _ = texture.Image;
                else _ = sprite.Font!.Font;
            }
            foreach (var sprite in _sprites)
            {
                if (sprite.Clip is { } clip)
                {
                    // The clip was captured in draw-position space; map it to canvas space and let Draw2D
                    // intersect it with the parent clip. It is pushed before the sprite transform because
                    // Draw2D.PushClip requires an axis-aligned current transform.
                    if (clip.Width <= 0 || clip.Height <= 0) continue; // Empty clip: the sprite is fully hidden.
                    canvas.PushClip(ToCanvasClip(clip));
                    try { FlushSprite(canvas, sprite); }
                    finally { canvas.PopClip(); }
                }
                else
                {
                    FlushSprite(canvas, sprite);
                }
            }
        }
        finally
        {
            _context.Unregister(this);
            CancelFromContext();
        }
    }

    private void FlushSprite(Canvas2D canvas, in Sprite sprite)
    {
        canvas.PushTransform(sprite.Transform);
        try
        {
            if (sprite.Region is { } region)
                canvas.DrawImage(region, new NativeRect(0, 0, region.SourcePixels.Width,
                    region.SourcePixels.Height), sprite.Color, _sampling, sprite.Flip);
            else
                canvas.DrawGlyphRun(sprite.Font!.Font, sprite.Font.Size, sprite.Text!.Glyphs, sprite.Color);
        }
        finally { canvas.PopTransform(); }
    }

    /// <summary>
    /// Draw-position space to canvas space: the batch transform (Begin's transformMatrix) applies to draw
    /// positions, while Draw2D.PushClip only re-applies the canvas transform (CanvasToOutput). With a
    /// rotated/skewed batch transform the four mapped corners collapse to their axis-aligned bounding box.
    /// </summary>
    private NativeRect ToCanvasClip(Rectangle clip)
    {
        var a = System.Numerics.Vector2.Transform(new System.Numerics.Vector2(clip.Left, clip.Top), _transform);
        var b = System.Numerics.Vector2.Transform(new System.Numerics.Vector2(clip.Right, clip.Top), _transform);
        var c = System.Numerics.Vector2.Transform(new System.Numerics.Vector2(clip.Left, clip.Bottom), _transform);
        var d = System.Numerics.Vector2.Transform(new System.Numerics.Vector2(clip.Right, clip.Bottom), _transform);
        float x = Math.Min(Math.Min(a.X, b.X), Math.Min(c.X, d.X));
        float y = Math.Min(Math.Min(a.Y, b.Y), Math.Min(c.Y, d.Y));
        float right = Math.Max(Math.Max(a.X, b.X), Math.Max(c.X, d.X));
        float bottom = Math.Max(Math.Max(a.Y, b.Y), Math.Max(c.Y, d.Y));
        return new NativeRect(x, y, right - x, bottom - y);
    }

    internal void CancelFromContext()
    {
        _sprites.Clear();
        _clips.Clear();
        _clip = null;
        _begun = false;
    }

    private void CheckBegun()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_begun) throw new InvalidOperationException("Begin must be called before Draw or End.");
    }

    private static bool Finite(Affine value) =>
        float.IsFinite(value.M11) && float.IsFinite(value.M12) && float.IsFinite(value.M21) &&
        float.IsFinite(value.M22) && float.IsFinite(value.M31) && float.IsFinite(value.M32);

    private static bool FiniteCorner(float width, float height, Affine transform)
    {
        var a = System.Numerics.Vector2.Transform(new(width, 0), transform);
        var b = System.Numerics.Vector2.Transform(new(0, height), transform);
        var c = System.Numerics.Vector2.Transform(new(width, height), transform);
        return float.IsFinite(a.X) && float.IsFinite(a.Y) && float.IsFinite(b.X) &&
            float.IsFinite(b.Y) && float.IsFinite(c.X) && float.IsFinite(c.Y);
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (_begun) _context.Unregister(this);
        CancelFromContext();
        _sprites.TrimExcess();
        _disposed = true;
    }
}
