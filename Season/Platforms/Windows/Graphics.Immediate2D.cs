// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Season.Platforms.Windows.DirectX;

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
        // 光栅像素密度固定，逻辑字号只影响目标几何，不随 DPI/缩放制造新的缓存项。
        const int RasterSize = 64;
        readonly object _lifetime = new();
        readonly HashSet<ImageLease> _leases = new();
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
                                ?? throw new FileNotFoundException("无法加载即时 2D 图片。", name);
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

        public void Prepare(Draw2D frame)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!frame.IsSealed || _prepared != null)
                throw new InvalidOperationException("只能准备一个已经封口、尚未提交的帧。");
            _prepared = frame;
            _submitted = false;
            _pipeline ??= new Draw2DPipeline();
            // 与后台 Load/纹理更新串行，字形生成、上传均处于 pass 外。
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
                            throw new InvalidOperationException($"字形 U+{command.CodePoint:X} 光栅化失败。");
                        var metrics = entry.GlyphMetrics;
                        if (!metrics.HasPlaneBounds)
                            throw new InvalidOperationException("即时字形需要明确的 MSDF plane bounds。");
                        float scale = command.FontSize / (float)RasterSize * command.GlyphScale;
                        destination = new(destination.X + metrics.PlaneLeft * scale, destination.Y - metrics.PlaneTop * scale,
                            (metrics.PlaneRight - metrics.PlaneLeft) * scale, (metrics.PlaneTop - metrics.PlaneBottom) * scale);
                        source = new(entry.SourceX, entry.SourceY, entry.SourceWidth, entry.SourceHeight);
                        pixelRange = entry.PixelRange;
                    }
                    else if (command.Kind == Draw2DKind.Image)
                    {
                        if (command.Image is not ImageLease lease || lease.Owner != this || lease.Released)
                            throw new InvalidOperationException("图片资源不属于当前图形设备或已经关闭。");
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
            // WinUI 交换链沿用引擎现有的合成方式：绘制先缩入左上角，合成器再按 DPI 放大。
            // 公共画布和输入仍使用物理像素；仅此处将几何与裁剪转换到实际渲染区域。
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
                // 将过滤限制在当前帧的源区域内，防止高清图集邻帧串色。
                UvClamp = new((source.X + insetX) * invW, (source.Y + insetY) * invH,
                    (source.Right - insetX) * invW, (source.Bottom - insetY) * invH)
            };
        }

        public void Submit(Draw2D frame)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_prepared != frame || _submitted || !frame.IsSealed || DirectX.Device.ActivePassId != RenderPassId.Overlay)
                throw new InvalidOperationException("即时 2D 帧只能在 Overlay 中提交一次。");
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
                        // 描述符不能立即归还；即便已提交的 Image2D 被 Dispose，GPU 仍可能读取它。
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
