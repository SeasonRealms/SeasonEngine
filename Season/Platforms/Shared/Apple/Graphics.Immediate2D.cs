// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Season.Platforms.Shared.Apple.Metal;
using MtlTexture = Season.Platforms.Shared.Apple.Metal.Texture;

namespace Season.Platforms.Shared.Apple;

internal unsafe partial class Graphics
{
    readonly object _immediateSync = new();
    ImmediateBackend? _immediate2D;
    bool _immediateClosed;

    public IImmediate2DBackend Immediate2D
    {
        get
        {
            lock (_immediateSync)
            {
                ObjectDisposedException.ThrowIf(_immediateClosed, this);
                return _immediate2D ??= new ImmediateBackend(this);
            }
        }
    }

    internal void DisposeImmediate2D()
    {
        BaseApp.ResizeSemaphore.Wait();
        try
        {
            lock (_immediateSync)
            {
                _immediateClosed = true;
                _immediate2D?.Dispose();
            }
        }
        finally { BaseApp.ResizeSemaphore.Release(); }
    }

    sealed class ImmediateBackend(Graphics owner) : IImmediate2DBackend
    {
        const int RasterSize = 64;
        readonly object _lifetime = new();
        readonly HashSet<ImageLease> _leases = new();
        readonly List<(MtlTexture Texture, Draw2DConstants Constants)> _quads = new(256);
        Draw2DPipeline? _pipeline;
        Draw2D? _prepared;
        bool _submitted, _disposed;

        public Vector2 OutputSize => new(Metal.Device.Display.Width, Metal.Device.Display.Height);

        public Image2D LoadImage(string name, Vector2 designSize)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            Rect2D.ValidateSize(designSize);
            if (Metal.Device.GraphicsEncoder != null)
                throw new InvalidOperationException("Load images before rendering.");
            BaseApp.ResizeSemaphore.Wait();
            try
            {
                lock (_lifetime)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    MtlTexture texture;
                    lock (owner.DictionaryMtlTexture)
                    {
                        if (owner.DictionaryMtlTexture.TryGetValue(name, out var existing))
                        {
                            texture = existing;
                            texture.AddRef();
                        }
                        else
                        {
                            using var decoder = DecodeImageFromPath(name)
                                ?? throw new FileNotFoundException("Cannot load immediate 2D image.", name);
                            texture = MtlTexture.CreateFromDecoder(decoder);
                            texture.Name = name;
                            try
                            {
                                owner.ExecuteUpload();
                                owner.DictionaryMtlTexture.Add(name, texture);
                            }
                            catch
                            {
                                Metal.Device.TextureUploadBatch.GetTasks().Remove(texture);
                                texture.Dispose();
                                throw;
                            }
                        }
                    }
                    var lease = new ImageLease(this, name, texture);
                    try
                    {
                        var image = new Image2D(name, designSize, lease);
                        _leases.Add(lease);
                        return image;
                    }
                    catch { Release(lease); throw; }
                }
            }
            finally { BaseApp.ResizeSemaphore.Release(); }
        }

        public void Prepare(Draw2D frame)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!frame.IsSealed || _prepared != null || Metal.Device.GraphicsEncoder != null ||
                Metal.Device.GraphicsCommandBuffer == null)
                throw new InvalidOperationException("Prepare requires a sealed frame outside render passes.");
            _prepared = frame;
            _submitted = false;
            BaseApp.ResizeSemaphore.Wait();
            try
            {
                _pipeline ??= new Draw2DPipeline();
                foreach (var command in frame.Commands)
                {
                    var destination = command.Destination;
                    var source = command.Source;
                    MtlTexture texture;
                    float pixelRange = 0;
                    if (command.Kind == Draw2DKind.Glyph)
                    {
                        if (!owner._glyphAtlas.TryEnsureStableGlyph(command.Font!, RasterSize, command.CodePoint, out var entry, out texture))
                            throw new InvalidOperationException($"Cannot rasterize glyph U+{command.CodePoint:X}.");
                        var metrics = entry.GlyphMetrics;
                        if (!metrics.HasPlaneBounds) throw new InvalidOperationException("MSDF plane bounds are required.");
                        float scale = command.FontSize / (float)RasterSize * command.GlyphScale;
                        destination = new(destination.X + metrics.PlaneLeft * scale, destination.Y - metrics.PlaneTop * scale,
                            (metrics.PlaneRight - metrics.PlaneLeft) * scale, (metrics.PlaneTop - metrics.PlaneBottom) * scale);
                        source = new(entry.SourceX, entry.SourceY, entry.SourceWidth, entry.SourceHeight);
                        pixelRange = entry.PixelRange;
                    }
                    else if (command.Kind == Draw2DKind.Image)
                    {
                        if (command.Image is not ImageLease lease || lease.Owner != this || lease.Released)
                            throw new InvalidOperationException("Image belongs to a different or closed graphics device.");
                        texture = lease.Texture;
                    }
                    else
                    {
                        texture = Metal.Device.White;
                        source = new(0, 0, 1, 1);
                    }
                    if (!destination.IsEmpty)
                        _quads.Add((texture, Draw2DConstants.Build(command, destination, source,
                            texture.Width, texture.Height, frame.OutputSize, pixelRange)));
                }
                // Private textures are uploaded on the same queue before this frame is committed.
                // Tracked hazards serialize earlier reads with these writes; no CPU texture mutation.
                owner._glyphAtlas.FlushStablePages();
            }
            finally { BaseApp.ResizeSemaphore.Release(); }
        }

        public void Submit(Draw2D frame)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_prepared != frame || _submitted || !frame.IsSealed || Metal.Device.GraphicsEncoder == null ||
                Metal.Device.ActivePassId != RenderPassId.Overlay)
                throw new InvalidOperationException("Immediate 2D must submit its prepared frame exactly once in Overlay.");
            _submitted = true;
            foreach (var quad in _quads) _pipeline!.Draw(quad.Texture, quad.Constants);
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
                lock (owner.DictionaryMtlTexture)
                {
                    if (lease.Texture.RefCount == 1 &&
                        owner.DictionaryMtlTexture.TryGetValue(lease.Name, out var current) && current == lease.Texture)
                        owner.DictionaryMtlTexture.Remove(lease.Name);
                    // Default Metal command buffers retain encoded native resources until completion.
                    lease.Texture.Release();
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
                owner._glyphAtlas.DisposeStablePages(texture => texture.Dispose());
                _pipeline?.Dispose();
                _pipeline = null;
            }
        }

        sealed class ImageLease(ImmediateBackend owner, string name, MtlTexture texture) : Image2DResource
        {
            internal ImmediateBackend Owner => owner;
            internal string Name => name;
            internal MtlTexture Texture => texture;
            internal bool Released;
            public override Vector2 PixelSize => new(texture.Width, texture.Height);
            public override void Dispose() => owner.Release(this);
        }
    }
}
