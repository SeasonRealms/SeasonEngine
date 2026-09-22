// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Season.Platforms.Shared.LinuxAndroid.Vulkan;
using VkTexture = Season.Platforms.Shared.LinuxAndroid.Vulkan.Texture;

namespace Season.Platforms.Shared.LinuxAndroid;

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
        // Same lock order as loading: resize/upload gate, then backend lifetime.
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
        readonly List<(VkTexture Texture, Draw2DConstants Constants)> _quads = new(256);
        readonly List<Silk.NET.Vulkan.DescriptorSet> _sets = new(256);
        Draw2DPipeline? _pipeline;
        Draw2D? _prepared;
        bool _submitted, _disposed;

        public Vector2 OutputSize => new(Vulkan.Device.SwapChain.Extent.Width, Vulkan.Device.SwapChain.Extent.Height);

        public Image2D LoadImage(string name, Vector2 designSize)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            Rect2D.ValidateSize(designSize);
            if (Vulkan.Device.InRenderPass) throw new InvalidOperationException("Load images before rendering.");
            BaseApp.ResizeSemaphore.Wait();
            try
            {
                lock (_lifetime)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    VkTexture texture;
                    lock (owner.DictionaryVKTexture)
                    {
                        if (owner.DictionaryVKTexture.TryGetValue(name, out var existing))
                        {
                            texture = existing;
                            texture.AddRef();
                        }
                        else
                        {
                            using var decoder = DecodeImageFromPath(name)
                                ?? throw new FileNotFoundException("Cannot load immediate 2D image.", name);
                            texture = VkTexture.CreateFromDecoder(decoder);
                            texture.Name = name;
                            try { owner.ExecuteUpload(); }
                            catch
                            {
                                // Staging allocation can fail before Execute enters its cleanup block.
                                Vulkan.Device.TextureUploadBatch.GetTasks().Remove(texture);
                                texture.Dispose();
                                throw;
                            }
                            owner.DictionaryVKTexture.Add(name, texture);
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
            if (!frame.IsSealed || _prepared != null || Vulkan.Device.InRenderPass)
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
                    VkTexture texture;
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
                        texture = Vulkan.Device.White;
                        source = new(0, 0, 1, 1);
                    }
                    if (!destination.IsEmpty)
                        _quads.Add((texture, BuildConstants(command, destination, source, texture, frame.OutputSize, pixelRange)));
                }
                owner._glyphAtlas.FlushStablePages();
                foreach (var quad in _quads)
                {
                    // Upload dependencies and layout transitions belong outside Overlay.
                    quad.Texture.EnsureReadyForRendering(Vulkan.Device.GraphicsCommandBuffer);
                    _sets.Add(_pipeline.Prepare(quad.Texture));
                }
            }
            finally { BaseApp.ResizeSemaphore.Release(); }
        }

        static Draw2DConstants BuildConstants(Draw2DCommand command, Rect2D destination, Rect2D source,
            VkTexture texture, Vector2 outputSize, float pixelRange)
        {
            var origin = Vector2.Transform(destination.Position, command.Transform);
            var axisX = Vector2.TransformNormal(new(destination.Width, 0), command.Transform);
            var axisY = Vector2.TransformNormal(new(0, destination.Height), command.Transform);
            // Season Vulkan passes use a negative-height viewport. No WinUI composition compensation.
            var toNdc = new Vector2(2 / outputSize.X, -2 / outputSize.Y);
            origin = origin * toNdc + new Vector2(-1, 1);
            axisX *= toNdc;
            axisY *= toNdc;
            float invW = 1f / texture.Width, invH = 1f / texture.Height;
            var uv = new Vector4(source.X * invW, source.Y * invH, source.Width * invW, source.Height * invH);
            if ((command.Flip & ImageFlip2D.Horizontal) != 0) { uv.X += uv.Z; uv.Z = -uv.Z; }
            if ((command.Flip & ImageFlip2D.Vertical) != 0) { uv.Y += uv.W; uv.W = -uv.W; }
            float insetX = Math.Min(0.5f, source.Width * 0.5f), insetY = Math.Min(0.5f, source.Height * 0.5f);
            return new()
            {
                OriginXAxis = new(origin, axisX.X, axisX.Y), YAxis = new(axisY, 0, 0),
                Uv = uv, Color = command.Color,
                Clip = new(command.Clip.X, command.Clip.Y, command.Clip.Right, command.Clip.Bottom),
                Parameters = new(command.Sampling == Sampling2D.Point ? 1 : 0, pixelRange, invW, invH),
                UvClamp = new((source.X + insetX) * invW, (source.Y + insetY) * invH,
                    (source.Right - insetX) * invW, (source.Bottom - insetY) * invH)
            };
        }

        public void Submit(Draw2D frame)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_prepared != frame || _submitted || !frame.IsSealed || !Vulkan.Device.InRenderPass ||
                Vulkan.Device.ActivePassId != RenderPassId.Overlay || _sets.Count != _quads.Count)
                throw new InvalidOperationException("Immediate 2D must submit its prepared frame exactly once in Overlay.");
            _submitted = true;
            for (int i = 0; i < _quads.Count; i++) _pipeline!.Draw(_sets[i], _quads[i].Constants);
        }

        public void CompleteFrame()
        {
            _quads.Clear();
            _sets.Clear();
            _pipeline?.CompleteFrame();
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
                lock (owner.DictionaryVKTexture)
                {
                    if (lease.Texture.RefCount == 1 &&
                        owner.DictionaryVKTexture.TryGetValue(lease.Name, out var current) && current == lease.Texture)
                        owner.DictionaryVKTexture.Remove(lease.Name);
                    // Texture.Release enqueues native handles; descriptors retire independently after the same timeline.
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
                if (_pipeline is { } pipeline)
                {
                    Vulkan.Device.EnqueueDeferredRelease(pipeline.Dispose);
                    _pipeline = null;
                }
            }
        }

        sealed class ImageLease(ImmediateBackend owner, string name, VkTexture texture) : Image2DResource
        {
            internal ImmediateBackend Owner => owner;
            internal string Name => name;
            internal VkTexture Texture => texture;
            internal bool Released;
            public override Vector2 PixelSize => new(texture.Width, texture.Height);
            public override void Dispose() => owner.Release(this);
        }
    }
}
