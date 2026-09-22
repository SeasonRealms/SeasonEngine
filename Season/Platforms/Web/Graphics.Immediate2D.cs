// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Microsoft.JSInterop;
using FontFace = global::Season.Fonts.Font;

namespace Season.Platforms.Web;

internal partial class Graphics
{
    ImmediateBackend? _immediate;
    bool _closed;
    public IImmediate2DBackend Immediate2D
    {
        get
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            return _immediate ??= new ImmediateBackend(this);
        }
    }

    void ReleaseTexture(WGPUTexture texture)
    {
        if (texture.HostOwned && !_closed) return;
        if (!DictionaryWGPUTexture.TryGetValue(texture.Name, out var current) || current != texture) return;
        DictionaryWGPUTexture.Remove(texture.Name);
        _jsRuntime.InvokeVoid("seasonWebGPU.retireTexture", texture.Name);
    }

    internal async Task DisposeImmediateAsync()
    {
        _closed = true;
        if (!_initialized) return;
        try
        {
            _immediate?.Dispose();
            // Late async loads must finish before destroying the JS owner; LoadImageAsync rolls them back.
            await Task.WhenAll(_textureLoads.Values.ToArray());
        }
        finally
        {
            try { foreach (var texture in DictionaryWGPUTexture.Values.ToArray()) ReleaseTexture(texture); }
            finally { await _jsRuntime.InvokeVoidAsync("seasonWebGPU.closeImmediate"); }
        }
    }

    sealed class ImmediateBackend(Graphics owner) : IImmediate2DBackend
    {
        // Must match the other immediate backends so one baked raster serves every 2D display size.
        const int RasterSize = 48;
        readonly HashSet<ImageLease> _leases = new();
        readonly Dictionary<string, int> _prewarmCursors = new();
        readonly List<float> _parameters = new();
        readonly List<string> _textures = new();
        Draw2D? _prepared;
        bool _submitted, _disposed, _pipelineReady;
        public Vector2 OutputSize => DeviceServices.BaseApp.DeviceResolution;

        public Image2D LoadImage(string name, Vector2 designSize)
        {
            ValidateLoad(name, designSize);
            if (!OperatingSystem.IsBrowser()) throw new PlatformNotSupportedException("Web loads require the browser runtime.");
            if (!owner.DictionaryWGPUTexture.TryGetValue(name, out var texture))
                throw new InvalidOperationException("Web images must be preloaded with Image2D.LoadAsync before Draw.");
            texture.AddRef();
            var lease = new ImageLease(this, texture);
            try
            {
                var image = new Image2D(name, designSize, lease);
                _leases.Add(lease);
                return image;
            }
            catch { texture.Release(); throw; }
        }

        void ValidateLoad(string name, Vector2 size)
        {
            ObjectDisposedException.ThrowIf(_disposed || owner._closed, this);
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            Rect2D.ValidateSize(size);
            if (_prepared != null) throw new InvalidOperationException("Load images before frame preparation.");
        }

        public async Task<Image2D> LoadImageAsync(string name, Vector2 designSize)
        {
            ValidateLoad(name, designSize);
            if (!await owner.LoadTextureAsync(name, leaseOwned: true)) throw new FileNotFoundException("Cannot load Web image.", name);
            if (_disposed || owner._closed)
            {
                if (owner.DictionaryWGPUTexture.TryGetValue(name, out var late) && late.RefCount == 0)
                    owner.ReleaseTexture(late);
                throw new ObjectDisposedException(nameof(ImmediateBackend));
            }
            return LoadImage(name, designSize);
        }

        public int PrewarmGlyphs(FontFace font, int maxCount)
        {
            ArgumentNullException.ThrowIfNull(font);
            ObjectDisposedException.ThrowIf(_disposed, this);
            return GlyphPrewarm.Run(owner._glyphAtlas, RasterSize, font, maxCount, _prewarmCursors);
        }

        public void Prepare(Draw2D frame)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!frame.IsSealed || _prepared != null) throw new InvalidOperationException("Expected one sealed frame.");
            _prepared = frame;
            if (!_pipelineReady)
            {
                owner._jsRuntime.InvokeVoid("seasonWebGPU.initializeImmediate", ImmediateShader.Source);
                _pipelineReady = true;
            }
            // Preserve the legacy named atlas even when the first text is from Draw2D.
            _ = owner._glyphAtlas.AtlasTexture;
            foreach (var command in frame.Commands)
            {
                var destination = command.Destination;
                var source = command.Source;
                float range = 0;
                WGPUTexture texture;
                if (command.Kind == Draw2DKind.Glyph)
                {
                    if (!owner._glyphAtlas.TryEnsureStableGlyph(command.Font!, RasterSize, command.CodePoint, out var entry, out texture))
                        throw new InvalidOperationException($"Cannot rasterize U+{command.CodePoint:X}.");
                    var m = entry.GlyphMetrics;
                    if (!m.HasPlaneBounds) throw new InvalidOperationException("MSDF plane bounds required.");
                    float scale = command.FontSize / (float)RasterSize * command.GlyphScale;
                    destination = new(destination.X + m.PlaneLeft * scale, destination.Y - m.PlaneTop * scale,
                        (m.PlaneRight - m.PlaneLeft) * scale, (m.PlaneTop - m.PlaneBottom) * scale);
                    source = new(entry.SourceX, entry.SourceY, entry.SourceWidth, entry.SourceHeight);
                    range = entry.PixelRange;
                }
                else if (command.Kind == Draw2DKind.Image)
                {
                    if (command.Image is not ImageLease lease || lease.Owner != this || lease.Released)
                        throw new InvalidOperationException("Image belongs to another or closed Web device.");
                    texture = lease.Texture;
                }
                else
                {
                    texture = WGPUTexture.CreateFromPixels("White", 1, 1);
                    source = new(0, 0, 1, 1);
                }
                if (destination.IsEmpty) continue;
                var origin = Vector2.Transform(destination.Position, command.Transform);
                var x = Vector2.TransformNormal(new(destination.Width, 0), command.Transform);
                var y = Vector2.TransformNormal(new(0, destination.Height), command.Transform);
                var ndc = new Vector2(2 / frame.OutputSize.X, -2 / frame.OutputSize.Y);
                origin = origin * ndc + new Vector2(-1, 1); x *= ndc; y *= ndc;
                float iw = 1f / texture.Width, ih = 1f / texture.Height;
                var uv = new Vector4(source.X * iw, source.Y * ih, source.Width * iw, source.Height * ih);
                if ((command.Flip & ImageFlip2D.Horizontal) != 0) { uv.X += uv.Z; uv.Z = -uv.Z; }
                if ((command.Flip & ImageFlip2D.Vertical) != 0) { uv.Y += uv.W; uv.W = -uv.W; }
                float insetX = Math.Min(0.5f, source.Width * 0.5f), insetY = Math.Min(0.5f, source.Height * 0.5f);
                Add(new(origin, x.X, x.Y)); Add(new(y, 0, 0)); Add(uv); Add(command.Color);
                Add(new(command.Clip.X, command.Clip.Y, command.Clip.Right, command.Clip.Bottom));
                Add(new(command.Sampling == Sampling2D.Point ? 1 : 0, range, iw, ih));
                Add(new((source.X + insetX) * iw, (source.Y + insetY) * ih,
                    (source.Right - insetX) * iw, (source.Bottom - insetY) * ih));
                _textures.Add(texture.Name);
            }
            owner._glyphAtlas.FlushStablePages();
            owner._jsRuntime.InvokeVoid("seasonWebGPU.prepareImmediate", _textures.ToArray(), _parameters.ToArray());
        }

        void Add(Vector4 value)
        {
            _parameters.Add(value.X); _parameters.Add(value.Y); _parameters.Add(value.Z); _parameters.Add(value.W);
        }

        public void Submit(Draw2D frame)
        {
            if (_disposed || _prepared != frame || _submitted) throw new InvalidOperationException("Invalid immediate submit.");
            owner._jsRuntime.InvokeVoid("seasonWebGPU.submitImmediate");
            _submitted = true;
        }

        public void CompleteFrame()
        {
            _parameters.Clear(); _textures.Clear(); _prepared = null; _submitted = false;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            CompleteFrame();
            foreach (var lease in _leases.ToArray()) lease.Dispose();
            owner._glyphAtlas.DisposeStablePages(t => owner._jsRuntime.InvokeVoid("seasonWebGPU.retireTexture", t.Name));
        }

        sealed class ImageLease(ImmediateBackend owner, WGPUTexture texture) : Image2DResource
        {
            internal ImmediateBackend Owner => owner;
            internal WGPUTexture Texture => texture;
            internal bool Released;
            public override Vector2 PixelSize => new(texture.Width, texture.Height);
            public override void Dispose()
            {
                if (Released) return;
                Released = true;
                owner._leases.Remove(this);
                texture.Release();
            }
        }
    }
}
