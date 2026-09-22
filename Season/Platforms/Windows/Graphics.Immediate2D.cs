// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Season.Platforms.Windows.DirectX;
using FontFace = global::Season.Fonts.Font;

namespace Season.Platforms.Windows;

internal unsafe partial class Graphics
{
    readonly object _immediateSync = new();
    ImmediateBackend? _immediate2D;

    public IImmediate2DBackend Immediate2D
    {
        get { lock (_immediateSync) return _immediate2D ??= new ImmediateBackend(this); }
    }

    internal void DisposeImmediate2D()
    {
        lock (_immediateSync) _immediate2D?.Dispose();
    }

    sealed class ImmediateBackend(Graphics owner) : IImmediate2DBackend
    {
        // The pixel density of the grating is fixed, and the logical font size only affects the target geometry, without creating new cache entries with DPI/scaling.
        // 48 keeps the whole 2D display range (26/28/48/50) on one shared raster while shrinking glyph boxes ~44% versus 64.
        const int RasterSize = 48;
        readonly object _lifetime = new();
        readonly HashSet<ImageLease> _leases = new();
        readonly Dictionary<string, int> _prewarmCursors = new();
        readonly List<(DXTexture Texture, Draw2DConstants Constants)> _quads = new(256);
        Draw2DPipeline? _pipeline;
        Draw2D? _prepared;
        bool _submitted;
        bool _disposed;

        public Vector2 OutputSize
        {
            get
            {
                var size = DirectX.Device.GetBackBufferSize();
                return new(size.Width, size.Height);
            }
        }

        public Image2D LoadImage(string name, Vector2 designSize)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            Rect2D.ValidateSize(designSize);
            BaseApp.ResizeSemaphore.Wait();
            try
            {
                lock (_lifetime)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    DXTexture texture;
                    lock (owner.DictionaryDXTexture)
                    {
                        if (owner.DictionaryDXTexture.TryGetValue(name, out var existing) && existing != null)
                        {
                            texture = existing;
                            texture.AddRef();
                        }
                        else
                        {
                            var decoder = DecodeImageFromPath(name)
                                ?? throw new FileNotFoundException("Unable to load real-time 2D images.", name);
                            texture = DXTexture.CreateFromDecoder(decoder);
                            texture.Name = name;
                            try { owner.ExecuteUpload(); }
                            catch { texture.Dispose(); throw; }
                            owner.DictionaryDXTexture[name] = texture;
                        }
                    }
                    var lease = new ImageLease(this, name, texture);
                    _leases.Add(lease);
                    return new Image2D(name, designSize, lease);
                }
            }
            finally { BaseApp.ResizeSemaphore.Release(); }
        }

        public int PrewarmGlyphs(FontFace font, int maxCount)
        {
            ArgumentNullException.ThrowIfNull(font);
            ObjectDisposedException.ThrowIf(_disposed, this);
            BaseApp.ResizeSemaphore.Wait();
            try { return GlyphPrewarm.Run(owner._glyphAtlas, RasterSize, font, maxCount, _prewarmCursors); }
            finally { BaseApp.ResizeSemaphore.Release(); }
        }

        public void Prepare(Draw2D frame)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!frame.IsSealed || _prepared != null)
                throw new InvalidOperationException("Only one sealed, unsubmitted frame can be prepared at a time.");
            _prepared = frame;
            _submitted = false;
            _pipeline ??= new Draw2DPipeline();
            //Serial with backend load/texture updates, glyph generation and upload are both outside of pass.
            BaseApp.ResizeSemaphore.Wait();
            try
            {
                foreach (var command in frame.Commands)
                {
                    var destination = command.Destination;
                    var source = command.Source;
                    DXTexture texture;
                    float pixelRange = 0;
                    if (command.Kind == Draw2DKind.Glyph)
                    {
                        if (!owner._glyphAtlas.TryEnsureStableGlyph(command.Font!, RasterSize, command.CodePoint, out var entry, out texture))
                            throw new InvalidOperationException($"Glyph U+{command.CodePoint:X} rasterization failed.");
                        var metrics = entry.GlyphMetrics;
                        if (!metrics.HasPlaneBounds)
                            throw new InvalidOperationException("Real-time glyphs require explicit MSDF plane bounds.");
                        float scale = command.FontSize / (float)RasterSize * command.GlyphScale;
                        destination = new(destination.X + metrics.PlaneLeft * scale, destination.Y - metrics.PlaneTop * scale,
                            (metrics.PlaneRight - metrics.PlaneLeft) * scale, (metrics.PlaneTop - metrics.PlaneBottom) * scale);
                        source = new(entry.SourceX, entry.SourceY, entry.SourceWidth, entry.SourceHeight);
                        pixelRange = entry.PixelRange;
                    }
                    else if (command.Kind == Draw2DKind.Image)
                    {
                        if (command.Image is not ImageLease lease || lease.Owner != this || lease.Released)
                            throw new InvalidOperationException("Image resources do not belong to the current graphics device or have been released.");
                        texture = lease.Texture;
                    }
                    else
                    {
                        texture = DirectX.Device.White;
                        source = new(0, 0, 1, 1);
                    }
                    if (destination.IsEmpty) continue;
                    var constants = BuildConstants(command, destination, source, texture, frame.OutputSize, pixelRange);
                    _quads.Add((texture, constants));
                }
                owner._glyphAtlas.FlushStablePages();
            }
            finally { BaseApp.ResizeSemaphore.Release(); }
        }

        static Draw2DConstants BuildConstants(Draw2DCommand command, Rect2D destination, Rect2D source,
            DXTexture texture, Vector2 outputSize, float pixelRange)
        {
            var origin = Vector2.Transform(destination.Position, command.Transform);
            var axisX = Vector2.TransformNormal(new(destination.Width, 0), command.Transform);
            var axisY = Vector2.TransformNormal(new(0, destination.Height), command.Transform);
            // The WinUI exchange chain follows the existing synthesis method of the engine: first shrink the drawing into the upper left corner, and then enlarge the synthesizer by DPI.
            // The public canvas and input still use physical pixels; Only here will geometry and cropping be converted to the actual rendering area.
            var composition = DeviceServices.BaseApp?.CompositionScale ?? Vector2.One;
            var toTarget = new Vector2(
                float.IsFinite(composition.X) && composition.X > 1e-4f ? 1 / composition.X : 1,
                float.IsFinite(composition.Y) && composition.Y > 1e-4f ? 1 / composition.Y : 1);
            origin *= toTarget;
            axisX *= toTarget;
            axisY *= toTarget;
            var toNdc = new Vector2(2 / outputSize.X, -2 / outputSize.Y);
            origin = origin * toNdc + new Vector2(-1, 1);
            axisX *= toNdc;
            axisY *= toNdc;
            float invW = 1f / texture.Width, invH = 1f / texture.Height;
            var uv = new Vector4(source.X * invW, source.Y * invH, source.Width * invW, source.Height * invH);
            if ((command.Flip & ImageFlip2D.Horizontal) != 0) { uv.X += uv.Z; uv.Z = -uv.Z; }
            if ((command.Flip & ImageFlip2D.Vertical) != 0) { uv.Y += uv.W; uv.W = -uv.W; }
            float insetX = Math.Min(0.5f, source.Width * 0.5f), insetY = Math.Min(0.5f, source.Height * 0.5f);
            return new Draw2DConstants
            {
                OriginXAxis = new(origin, axisX.X, axisX.Y),
                YAxis = new(axisY, 0, 0),
                Uv = uv,
                Color = command.Color,
                Clip = new(command.Clip.X * toTarget.X, command.Clip.Y * toTarget.Y,
                    command.Clip.Right * toTarget.X, command.Clip.Bottom * toTarget.Y),
                Parameters = new(command.Sampling == Sampling2D.Point ? 1 : 0, pixelRange, invW, invH),
                // Limit the filtering to the source area of the current frame to prevent high-definition atlas frame串色.
                UvClamp = new((source.X + insetX) * invW, (source.Y + insetY) * invH,
                    (source.Right - insetX) * invW, (source.Bottom - insetY) * invH)
            };
        }

        public void Submit(Draw2D frame)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_prepared != frame || _submitted || !frame.IsSealed || DirectX.Device.ActivePassId != RenderPassId.Overlay)
                throw new InvalidOperationException("Real-time 2D frames can only be submitted once in the Overlay.");
            _submitted = true;
            foreach (var quad in _quads)
                _pipeline!.Draw(quad.Texture, quad.Constants);
        }

        public void CompleteFrame()
        {
            _quads.Clear();
            _prepared = null;
            _submitted = false;
        }

        void Release(ImageLease lease)
        {
            lock (_lifetime)
            {
                if (lease.Released) return;
                lease.Released = true;
                _leases.Remove(lease);
                var texture = lease.Texture;
                lock (owner.DictionaryDXTexture)
                {
                    if (texture.RefCount <= 1)
                    {
                        if (owner.DictionaryDXTexture.TryGetValue(lease.Name, out var current) && current == texture)
                            owner.DictionaryDXTexture.Remove(lease.Name);
                        // The descriptor cannot be returned immediately; Even if the submitted Image2D is disposed of, the GPU may still read it.
                        DirectX.Device.EnqueueDeferredRelease(DirectX.Device.GetCurrentRetireFenceValue(), texture.Release);
                    }
                    else texture.Release();
                }
            }
        }

        public void Dispose()
        {
            lock (_lifetime)
            {
                if (_disposed) return;
                _disposed = true;
                CompleteFrame();
                foreach (var lease in _leases.ToArray()) Release(lease);
                ulong fence = DirectX.Device.GetCurrentRetireFenceValue();
                owner._glyphAtlas.DisposeStablePages(texture => DirectX.Device.EnqueueDeferredRelease(fence, texture.Dispose));
                if (_pipeline != null)
                {
                    var pipeline = _pipeline;
                    DirectX.Device.EnqueueDeferredRelease(fence, pipeline.Dispose);
                    _pipeline = null;
                }
            }
        }

        sealed class ImageLease(ImmediateBackend owner, string name, DXTexture texture) : Image2DResource
        {
            internal ImmediateBackend Owner => owner;
            internal string Name => name;
            internal DXTexture Texture => texture;
            internal bool Released;
            public override Vector2 PixelSize => new(texture.Width, texture.Height);
            public override void Dispose() => owner.Release(this);
        }
    }
}
