// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Season.Fonts;
using Season.Platforms.Shared.LinuxAndroid.Vulkan;
using VkTexture = Season.Platforms.Shared.LinuxAndroid.Vulkan.Texture;
// Silk.NET.Vulkan type aliases
// to avoid conflicts with Vulkan.Device / Maui.Device
using SVk = Silk.NET.Vulkan.Vk;
using VkDs = Silk.NET.Vulkan.DescriptorSet;
using VkDescriptorType = Silk.NET.Vulkan.DescriptorType;
using VkDescriptorBufferInfo = Silk.NET.Vulkan.DescriptorBufferInfo;
using VkWriteDescriptorSet = Silk.NET.Vulkan.WriteDescriptorSet;
using VkDescriptorImageInfo = Silk.NET.Vulkan.DescriptorImageInfo;
using VkImageLayout = Silk.NET.Vulkan.ImageLayout;
using VkStructureType = Silk.NET.Vulkan.StructureType;
using VkIndexType = Silk.NET.Vulkan.IndexType;
using VkPipelineBindPoint = Silk.NET.Vulkan.PipelineBindPoint;
using VkBufferUsageFlags = Silk.NET.Vulkan.BufferUsageFlags;
using VkMemoryPropertyFlags = Silk.NET.Vulkan.MemoryPropertyFlags;
using VkResult = Silk.NET.Vulkan.Result;
using VkPipelineStageFlags = Silk.NET.Vulkan.PipelineStageFlags;
using VkAccessFlags = Silk.NET.Vulkan.AccessFlags;
using VkShaderStageFlags = Silk.NET.Vulkan.ShaderStageFlags;
using VkBufferMemoryBarrier = Silk.NET.Vulkan.BufferMemoryBarrier;

namespace Season.Platforms.Shared.LinuxAndroid;

/// <summary>
/// IGraphics implementation for the Linux / Android platform.
/// Its structure mirrors Windows/Graphics.cs one to one,
/// replacing only DX classes with their Vulkan equivalents
/// (DXTexture -> Texture, DXSprite2D/3D -> VKSprite2D/3D, DXModel -> VKModel,
/// DXMesh3D -> VKMesh3D, DirectX.Device.textureUploadBatch.Execute(...) -> VK Device.TextureUploadBatch.Execute()).
///
/// Dictionary and lock usage follow the DX baseline strictly, with identical behavior.
/// </summary>
internal unsafe class Graphics : IGraphics
{
    readonly GlyphAtlasManager<VkTexture> _glyphAtlas = new(
        2048, 2048,
        createAtlasTexture: (w, h) => VkTexture.CreateEmpty((uint)w, (uint)h, "TextAtlas"),
        uploadFullPixels: (tex, pixels) => tex.UploadPixels(pixels),
        uploadSubRects: (tex, pixels, atlasW, atlasH, rects) =>
        {
            var atlasRects = new AtlasUploadRect[rects.Length];
            for (int i = 0; i < rects.Length; i++)
                atlasRects[i] = new AtlasUploadRect(rects[i].X, rects[i].Y, rects[i].Width, rects[i].Height);
            tex.UploadSubRects(pixels, atlasW, atlasH, atlasRects);
        },
        getCurrentFrameIndex: () => Vulkan.Device.FrameIndex);

    Dictionary<string, VkTexture> DictionaryVKTexture = new();

    Dictionary<(string, long), VKSprite2D> DictionarySprite = new();

    // Procedural geometric shapes
    Dictionary<(Season.Controls.ShapeType, int, int, int), VkTexture> DictionaryShapeTexture = new();
    Dictionary<(Season.Controls.ShapeType, long), VKSprite2D> DictionaryShape = new();

    Dictionary<(string, long), VKModel> DictionaryModel = new();
    Dictionary<string, Task<VKModel>> DictionaryModelResource = new();

    Dictionary<(string, long), VKSprite3D> DictionarySprite3D = new();

    Dictionary<(string, long), VKMesh3D> DictionaryMesh3D = new();
    Dictionary<(string, long), VKInstancedMesh3D> DictionaryInstancedMesh3D = new();
    Dictionary<(string, long), VKInstancedModel> DictionaryInstancedModel = new();

    // Phase 4: frame-level state for the Outline2D mask pass
    // collected by RenderOutlineMask and consumed by BlitToBackbuffer
    VKRenderTarget _outlineMaskTarget;
    bool _outline2DFrameActive;
    float _outline2DFrameWidth;

    // ── Text GPU Instancing ──
    /// <summary>Lightweight ITextureHolder with no GPU resources,
    /// used instead of the heavier VKSprite2D.</summary>
    internal sealed class TextGlyphHolder : ITextureHolder
    {
        public Controls.Texture Texture { get; set; } = new Controls.Texture();
    }

    /// <summary>GPU instancing state for a single Texts control,
    /// aligned with DX TextInstanceState.</summary>
    internal unsafe struct VKTextInstanceState
    {
        // Glyph data
        // (StorageBuffer, binding 10, N-buffered per frame:
        // direct CPU writes would race with GPU reads from in-flight frames
        // if one buffer were shared across frames)
        public BufferResource[] GlyphBuffers;
        public byte*[] GlyphMappedPtrs;
        public int GlyphCapacity;
        public int GlyphAtlasVersionBuilt;
        public bool GlyphDirty;
        public bool CanDraw;

        // Instance transforms (VertexBuffer slot 1, N-buffered per frame)
        public BufferResource[] InstanceBuffers;
        public byte*[] InstanceMappedPtrs;
        public uint InstanceFrameMask;
        public int InstanceCount;
        public int InstanceCapacity;    // Allocated capacity (grown geometrically), >= InstanceCount

        // TextDrawParams (UBO binding 11, N-buffered per Texts control and per frame)
        // This must remain per-control:
        // if shared globally, a later Texts recorded in the same frame would overwrite the UBO,
        // and every Texts instance executed by the GPU would read the last written color/alpha
        // and tint the whole screen.
        public BufferResource[] DrawParamsBuffers;
        public byte*[] DrawParamsMappedPtrs;

        // DescriptorSets
        // (binding glyph storage + atlas texture + default UBO, one set per frame)
        public VkDs[] DescriptorSets;
    }

    Dictionary<Texts, VKTextInstanceState> _textInstances = new();

    // Shared resources for text GPU instancing
    BufferResource _textMatrixBuffer;          // identity matrix UBO (b0)
    byte* _mappedTextMatrix;
    BufferResource _textMaterialBuffer;        // renderMode=2, isInstanced=1 (b2)
    byte* _mappedTextMaterial;
    VkTexture _whiteTexture;                   // Placeholder texture (binding 5-8)

    // Dedicated DescriptorPool for text
    // isolated from the Sprite shared pool to avoid cross-binding contamination
    Vulkan.DescriptorAllocator? _textDescriptorAllocator;

    public void Init()
    {
        // Shared UBOs for text GPU instancing
        _textMatrixBuffer = Vulkan.Device.ResourceManager.CreateConstantBuffer(
            (uint)Unsafe.SizeOf<MatrixBuffer>(), out _mappedTextMatrix);
        var identityMatrix = new MatrixBuffer
        {
            World = Matrix4x4.Transpose(Matrix4x4.Identity),
            View = Matrix4x4.Transpose(Matrix4x4.Identity),
            Projection = Matrix4x4.Transpose(Matrix4x4.Identity),
        };
        Unsafe.Write(_mappedTextMatrix, identityMatrix);

        _textMaterialBuffer = Vulkan.Device.ResourceManager.CreateConstantBuffer(
            (uint)Unsafe.SizeOf<MaterialParams>(), out _mappedTextMaterial);
        var textMaterial = new MaterialParams
        {
            BaseColor = new Vector4(1, 1, 1, 1),
            MetallicFactor = 0f,
            RoughnessFactor = 1f,
            UseAlbedoMap = 1,
            RenderMode = 2,     // TextMsdf
            IsInstanced = 1,
        };
        Unsafe.Write(_mappedTextMaterial, textMaterial);

        // Create a dedicated DescriptorPool for text, fully isolated from the Sprite shared pool
        _textDescriptorAllocator = new Vulkan.DescriptorAllocator(
            Vulkan.Device.Vk, Vulkan.Device.LogicalDevice, capacity: 64);

        DeviceServices.BaseApp.AddLog(LogType.Backend, $"{DateTime.UtcNow} [VK Text] Init renderMode={textMaterial.RenderMode} isInstanced={textMaterial.IsInstanced} matBufSize={Unsafe.SizeOf<MaterialParams>()}");

        // Note: TextDrawParams UBOs are per-Texts, per-frame resources
        // (VKTextInstanceState.DrawParamsBuffers),
        // created in LoadTexts.
        // They must not be shared globally, or multiple controls in the same frame
        // would overwrite each other's colors.

        // Get or create the 1x1 white placeholder texture
        // (Vulkan requires a valid ImageView for every binding)
        _whiteTexture = Vulkan.Device.White;
    }

    public async Task<bool> LoadSprite2D(Sprite2D sprite2D)
    {
        VKSprite2D vkSprite2D = null!;

        lock (DictionarySprite)
        {
            if (sprite2D.IsDisposed) return false;

            if (DictionarySprite.TryGetValue((sprite2D.Name, sprite2D.ID), out vkSprite2D!))
            {
                if (vkSprite2D == null || vkSprite2D.VKTexture == null)
                {

                }
                else
                {
                    sprite2D.OriginWidth = (int)vkSprite2D.VKTexture.Width;
                    sprite2D.OriginHeight = (int)vkSprite2D.VKTexture.Height;
                }
            }
            else
            {
                try
                {
                    VkTexture view = null!;

                    lock (DictionaryVKTexture)
                    {
                        if (DictionaryVKTexture.TryGetValue(sprite2D.Name, out view!))
                        {
                            view.AddRef();
                        }
                        else
                        {
                            INativeImageDecoder imageResult = null!;

                            if (ImageUtils.CreateImageExist(sprite2D.Name))
                            {
                                imageResult = ImageUtils.CreateImage(sprite2D.Name);
                            }
                            else
                            {
                                if (StorageService.FileExist(StorageService.DirectoryBase, sprite2D.Name))
                                {

                                }
                                else
                                {
                                    StorageService.CopyToLocal(sprite2D.Name);
                                }

                                StorageService.TryGetStream(StorageService.DirectoryBase, sprite2D.Name, out Stream stream, out string errMsg);

                                using (stream)
                                {
                                    if (stream == null)
                                    {

                                    }
                                    else
                                    {
                                        var imageExt = sprite2D.Ext;
                                        if (imageExt.IsNullOrWhiteSpace())
                                        {
                                            imageExt = System.IO.Path.GetExtension(sprite2D.Name).ToLower();
                                        }

                                        imageResult = ImageUtils.GetImageFromStream(stream, imageExt);
                                    }
                                }
                            }

                            if (imageResult == null)
                            {

                            }
                            else
                            {
                                view = new VkTexture(imageResult);
                                view.Name = sprite2D.Name;

                                ExecuteUpload();
                            }

                            if (view == null)
                            {
                                DeviceServices.BaseApp.AddLog(LogType.Error, $"{DateTime.UtcNow} LoadTextureAsync GetTexture {sprite2D.Name}");
                            }

                            DictionaryVKTexture.Add(sprite2D.Name, view);
                        }
                    }

                    try
                    {
                        // Use Sprite instead of Texture2D
                        vkSprite2D = new VKSprite2D(view);
                    }
                    catch (Exception ex)
                    {
                        DeviceServices.BaseApp.AddLog(LogType.Error, $"{DateTime.UtcNow} LoadTextureAsync new Sprite {ex}");
                    }

                    lock (DictionarySprite)
                    {
                        if (DictionarySprite.ContainsKey((sprite2D.Name, sprite2D.ID)))
                        {
                            //impossible
                        }
                        else
                        {
                            DictionarySprite.Add((sprite2D.Name, sprite2D.ID), vkSprite2D);
                        }
                    }
                }
                catch (Exception ex)
                {
                    DeviceServices.BaseApp.AddLog(LogType.Error, $"{DateTime.UtcNow} LoadTextureAsync new Sprite {ex}");
                }
            }
        }

        return true;
    }

    public void UpdateSprite2D(Sprite2D sprite)
    {
        VKSprite2D vkSprite = null!;

        lock (DictionarySprite)
        {
            if (DictionarySprite.TryGetValue((sprite.Name, sprite.ID), out vkSprite!))
            {
                if (vkSprite == null || vkSprite.VKTexture == null)
                {

                }
                else
                {
                    sprite.Ready = true;

                    // Texture replacement
                    if (sprite.TextureOverride.HasValue)
                    {
                        var source = sprite.TextureOverride;
                        sprite.TextureOverride = default;
                        ReplaceSpriteTexture(vkSprite, source);
                    }

                    if (sprite.Changed)
                    {
                        sprite.Changed = false;

                        vkSprite.SpriteRef = sprite;

                        vkSprite.Update();
                    }
                }
            }
        }
    }

    static INativeImageDecoder? ResolveDecoder(TextureUpdateSource source)
    {
        if (source.Image != null) return source.Image;
        if (source.Path != null) return DecodeImageFromPath(source.Path);
        return null;
    }

    static INativeImageDecoder? DecodeImageFromPath(string path)
    {
        if (ImageUtils.CreateImageExist(path)) return ImageUtils.CreateImage(path);
        if (!StorageService.FileExist(StorageService.DirectoryBase, path))
            StorageService.CopyToLocal(path);
        StorageService.TryGetStream(StorageService.DirectoryBase, path, out Stream stream, out _);
        if (stream == null) return null;
        using (stream) return ImageUtils.GetImageFromStream(stream, null);
    }

    void ReplaceSpriteTexture(VKSpriteQuad vkSprite, TextureUpdateSource source)
    {
        var decoder = ResolveDecoder(source);
        if (decoder == null) return;

        var oldTex = vkSprite.VKTexture;
        if (oldTex == null) { decoder.Dispose(); return; }

        if ((uint)decoder.Width == oldTex.Width
            && (uint)decoder.Height == oldTex.Height
            && oldTex.RefCount == 1)
        {
            oldTex.UploadPixels(decoder.PixelSpan);
        }
        else
        {
            var newTex = VkTexture.CreateFromDecoder(decoder);
            ExecuteUpload();
            vkSprite.VKTexture = newTex;
        }
        decoder.Dispose();
    }

    public void DrawSprite2D(Sprite2D sprite)
    {
        VKSprite2D vkSprite = null!;

        lock (DictionarySprite)
        {
            if (DictionarySprite.TryGetValue((sprite.Name, sprite.ID), out vkSprite!))
            {

            }
            else
            {
                //texture.Changed = true;
            }
        }

        if (vkSprite == null || vkSprite.VKTexture == null)
        {

        }
        else
        {
            vkSprite.Draw();
        }
    }

    // Texts (GPU instancing architecture, aligned with the Windows side)

    public async Task<bool> LoadTexts(Texts texts)
    {
        if (texts?.TexsLoading?.Length == 0)
            return false;

        var texsLoading = texts.TexsLoading;
        int totalCount = texsLoading.Length + (texts.ShowDot ? 1 : 0);

        // Phase 1: count valid glyphs and ensure every glyph is already in the atlas
        var validIndices = new int[totalCount];
        int validCount = 0;

        for (int i = 0; i < texsLoading.Length; i++)
        {
            ref var tex = ref texsLoading[i];
            if (tex.TexType is TexType.NewLine or TexType.Space or TexType.Missing)
                continue;
            if (!TryEnsureGlyphEntry(ref tex, out var entry))
                continue;
            validIndices[validCount++] = i;
        }

        // Handle the dot glyph
        bool hasDot = false;
        if (texts.ShowDot && TryEnsureGlyphEntry(ref texts._dotRef, out var dotEntry))
        {
            validIndices[validCount] = -1;  // -1 means the dot glyph
            hasDot = true;
            validCount++;
        }

        if (validCount == 0)
            return false;

        // Phase 2: create instance buffers + per-text glyph buffer
        var instanceData = new InstanceTransformData[validCount];
        var holders = new ITextureHolder[totalCount];

        int instanceIdx = 0;
        for (int v = 0; v < validCount; v++)
        {
            int srcIdx = validIndices[v];
            bool isDot = srcIdx < 0;
            ref var tex = ref isDot ? ref texts._dotRef : ref texsLoading[srcIdx];

            if (!TryEnsureGlyphEntry(ref tex, out var entry))
                continue;

            tex.AtlasVersion = entry.AtlasVersion;
            tex.GlyphMetrics = entry.GlyphMetrics;
            tex.Factor = entry.PixelRange;

            // slot 1: write a zero matrix initially to hide the instance
            instanceData[instanceIdx] = CreateHiddenInstanceData();

            // Create TextGlyphHolder
            var holder = new TextGlyphHolder();
            holder.Texture.TextureType = TextureType.TextMsdf;
            holder.Texture.SourceX = entry.SourceX;
            holder.Texture.SourceY = entry.SourceY;
            holder.Texture.SourceWidth = entry.SourceWidth;
            holder.Texture.SourceHeight = entry.SourceHeight;
            holder.Texture.OriginWidth = entry.Width;
            holder.Texture.OriginHeight = entry.Height;
            holder.Texture.Factor = entry.PixelRange;
            holder.Texture.Ready = true;

            int storeIdx = isDot ? texsLoading.Length : srcIdx;
            if (isDot)
                texts.dotTextureHolderLoading = holder;
            else
                holders[storeIdx] = holder;

            instanceIdx++;
        }

        // Create GPU instance buffers + glyph storage buffer + descriptor sets
        int frameCount = (int)Vulkan.Device.frameCount;
        var state = new VKTextInstanceState
        {
            InstanceCount = instanceIdx,
            GlyphCapacity = 0,
            GlyphAtlasVersionBuilt = -1,
            GlyphDirty = true,
            CanDraw = false,
            InstanceFrameMask = 0,
            InstanceBuffers = new BufferResource[frameCount],
            InstanceMappedPtrs = new byte*[frameCount],
            InstanceCapacity = instanceData.Length,
            DrawParamsBuffers = new BufferResource[frameCount],
            DrawParamsMappedPtrs = new byte*[frameCount],
            DescriptorSets = new VkDs[frameCount],
        };

        var defaultDrawParams = new VKTextDrawParams
        {
            AtlasSize = Vector2.One,
            PxRange = Season.Fonts.Font.PixelRange,
            GlobalAlpha = 1f,
            TextColor = Vector4.One,
        };
        for (int fi = 0; fi < frameCount; fi++)
        {
            state.InstanceBuffers[fi] = Vulkan.Device.ResourceManager.CreateVertexBuffer(instanceData);
            state.InstanceMappedPtrs[fi] = null;
            state.DrawParamsBuffers[fi] = Vulkan.Device.ResourceManager.CreateConstantBuffer(
                (uint)Unsafe.SizeOf<VKTextDrawParams>(), out state.DrawParamsMappedPtrs[fi]);
            Unsafe.Write(state.DrawParamsMappedPtrs[fi], defaultDrawParams);
        }

        if (!EnsureGlyphBufferCapacity(ref state, Math.Max(instanceIdx, 1)))
        {
            ReleaseTextInstanceResources(ref state);
            return false;
        }

        // Initialize glyph buffers with hidden glyph data on all frames
        var hiddenGlyph = CreateHiddenGlyphData();
        for (int fi = 0; fi < frameCount; fi++)
        {
            var glyphPtr = (VKTextGlyphData*)state.GlyphMappedPtrs[fi];
            for (int i = 0; i < Math.Max(instanceIdx, 1); i++)
                glyphPtr[i] = hiddenGlyph;
        }

        // Allocate one dedicated DescriptorSet per frame
        // because per-frame buffers must be N-buffered
        AllocateTextDescriptorSets(ref state, _glyphAtlas.AtlasTexture, frameCount);

        // Replace the previous state
        if (_textInstances.TryGetValue(texts, out var prevState))
            ReleaseTextInstanceResources(ref prevState);

        _textInstances[texts] = state;
        texts.textureHoldersLoading = holders;

        DeviceServices.BaseApp.AddLog(LogType.Backend, $"{DateTime.UtcNow} [VK Text] LoadTexts instanceCount={instanceIdx} frameCount={frameCount} glyphCap={state.GlyphCapacity} atlasReady={_glyphAtlas.AtlasTexture != null} atlasVer={_glyphAtlas.Version}");
        return true;
    }

    /// <summary>Incremental append
    /// (see the IGraphics.AppendTexts contract).
    /// Only atlas entries and holders for newly added glyphs are created.
    /// Buffers grow geometrically without rebuilding per-text state,
    /// so existing resources do not need to be released or recreated.
    /// GlyphDirty must be set to true because appending shifts the dot instance index
    /// and requires a full recomputation of glyph data.</summary>
    public Task<bool> AppendTexts(Texts texts, Tex[] appendTexs, ITextureHolder[] appendHolders)
    {
        if (texts == null || appendTexs == null || appendHolders == null
            || appendTexs.Length == 0 || appendHolders.Length != appendTexs.Length)
            return Task.FromResult(false);

        if (!_textInstances.TryGetValue(texts, out var state) || state.InstanceBuffers == null || state.InstanceCount <= 0)
            return Task.FromResult(false);

        int added = 0;
        for (int i = 0; i < appendTexs.Length; i++)
        {
            ref var tex = ref appendTexs[i];
            if (tex.TexType is TexType.NewLine or TexType.Space or TexType.Missing)
                continue;
            if (!TryEnsureGlyphEntry(ref tex, out var entry))
                continue;

            tex.AtlasVersion = entry.AtlasVersion;
            tex.GlyphMetrics = entry.GlyphMetrics;
            tex.Factor = entry.PixelRange;

            var holder = new TextGlyphHolder();
            holder.Texture.TextureType = TextureType.TextMsdf;
            holder.Texture.SourceX = entry.SourceX;
            holder.Texture.SourceY = entry.SourceY;
            holder.Texture.SourceWidth = entry.SourceWidth;
            holder.Texture.SourceHeight = entry.SourceHeight;
            holder.Texture.OriginWidth = entry.Width;
            holder.Texture.OriginHeight = entry.Height;
            holder.Texture.Factor = entry.PixelRange;
            holder.Texture.Ready = true;

            appendHolders[i] = holder;
            added++;
        }

        // Pure whitespace append (for example only spaces/newlines):
        // instance count does not change, and only higher-level layout needs to advance.
        if (added == 0)
            return Task.FromResult(true);

        int required = state.InstanceCount + added;

        if (!EnsureInstanceBufferCapacity(ref state, required) || !EnsureGlyphBufferCapacity(ref state, required))
            return Task.FromResult(false);

        state.InstanceCount = required;
        state.GlyphDirty = true;
        state.InstanceFrameMask = 0;
        state.CanDraw = false;

        // Do not write the state back after Dispose,
        // to avoid "reviving" resources that have already been queued for release
        if (texts.IsDisposed || !_textInstances.ContainsKey(texts))
            return Task.FromResult(false);

        _textInstances[texts] = state;
        return Task.FromResult(true);
    }

    public void UpdateTexts(Texts texts)
    {
        if (texts?.Texs?.Length <= 0)
        {
            if (_textInstances.TryGetValue(texts, out var emptyState))
            {
                emptyState.CanDraw = false;
                _textInstances[texts] = emptyState;
            }
            return;
        }

        // GPU instancing path
        if (_textInstances.TryGetValue(texts, out var state))
        {
            var texs = texts.Texs;
            var holders = texts.textureHolders;
            int instanceCount = state.InstanceCount;
            if (instanceCount <= 0 || state.GlyphMappedPtrs == null || !EnsureGlyphBufferCapacity(ref state, instanceCount))
            {
                state.CanDraw = false;
                _textInstances[texts] = state;
                return;
            }

            int frameIndex = (int)Vulkan.Device.FrameIndex;
            // Use frame 0 as the primary glyph-data write buffer,
            // then copy to the remaining frames after upload for full-frame synchronization
            var glyphPtr = (VKTextGlyphData*)state.GlyphMappedPtrs[0];
            uint frameBit = 1u << frameIndex;
            bool uploadGlyphData = state.GlyphDirty || state.GlyphAtlasVersionBuilt != _glyphAtlas.Version;

            // Check whether the layout changed
            bool layoutChanged = uploadGlyphData;
            if (!layoutChanged)
            {
                if (holders != null)
                {
                    for (int i = 0; i < holders.Length; i++)
                    {
                        if (holders[i] is TextGlyphHolder h && h.Texture.Changed)
                        {
                            layoutChanged = true;
                            break;
                        }
                    }
                }
                if (!layoutChanged && texts.dotTextureHolder is TextGlyphHolder dh && dh.Texture.Changed)
                    layoutChanged = true;
            }

            if (layoutChanged)
                state.InstanceFrameMask = 0;

            bool writeInstanceData = layoutChanged;

            if (!uploadGlyphData && !writeInstanceData)
            {
                state.CanDraw = true;
                _textInstances[texts] = state;
                return;
            }

            // Rebuild instance data into a temporary array when layout changes
            var instanceData = writeInstanceData ? new InstanceTransformData[instanceCount] : null;

            float n = 1f / DeviceServices.BaseApp.CompositionScale.X;
            var extendRes = DeviceServices.BaseApp.ExtendResolution;
            var deviceRes = DeviceServices.BaseApp.DeviceResolution;
            var globalScale = DeviceServices.BaseApp.Scale;
            float atlasW = _glyphAtlas.AtlasTexture?.Width ?? 1f;
            float atlasH = _glyphAtlas.AtlasTexture?.Height ?? 1f;

            int instIdx = 0;
            state.CanDraw = false;

            for (int i = 0; i < texs.Length; i++)
            {
                ref var tex = ref texs[i];
                if (tex.TexType is TexType.NewLine or TexType.Space or TexType.Missing)
                    continue;

                if (holders == null || i >= holders.Length || holders[i] is not TextGlyphHolder holder)
                    continue;

                var t = holder.Texture;
                if (t.Changed)
                    t.Changed = false;

                if (uploadGlyphData)
                {
                    if (!TryEnsureGlyphEntry(ref tex, out var entry))
                    {
                        instIdx++;
                        continue;
                    }

                    if (tex.AtlasVersion != entry.AtlasVersion)
                    {
                        tex.AtlasVersion = entry.AtlasVersion;
                        tex.Factor = entry.PixelRange;
                        t.SourceX = entry.SourceX;
                        t.SourceY = entry.SourceY;
                        t.SourceWidth = entry.SourceWidth;
                        t.SourceHeight = entry.SourceHeight;
                        t.OriginWidth = entry.Width;
                        t.OriginHeight = entry.Height;
                        t.Factor = entry.PixelRange;
                    }

                    bool hasColorOverride = tex.Color.HasValue;
                    var glyphColor = hasColorOverride ? tex.Color.Value.AsVector4 : Vector4.One;

                    glyphPtr[instIdx] = new VKTextGlyphData
                    {
                        UvRect = new Vector4(t.SourceX / atlasW, t.SourceY / atlasH, t.SourceWidth / atlasW, t.SourceHeight / atlasH),
                        Color = glyphColor,
                        Metrics = new Vector4((float)t.OriginWidth, (float)t.OriginHeight, (float)t.Factor, hasColorOverride ? 1f : 0f),
                    };
                }

                if (writeInstanceData)
                {
                    float glyphAlpha = Math.Clamp(t.Alpha, 0f, 1f);
                    if (glyphAlpha <= 0f || t.Width <= 0f || t.Height <= 0f)
                    {
                        instanceData[instIdx] = CreateHiddenInstanceData();
                        instIdx++;
                        continue;
                    }

                    float ndcPosX = t.PosX * n;
                    float ndcPosY = t.PosY * n;
                    float ndcWidth = t.Width * n;
                    float ndcHeight = t.Height * n;
                    float ndcX = (ndcPosX - extendRes.X / 2) / (extendRes.X / 2);
                    float ndcY = (extendRes.Y / 2 - ndcPosY) / (extendRes.Y / 2);
                    float ndcScaledWidth = ndcWidth * globalScale / (deviceRes.X / 2);
                    float ndcScaledHeight = ndcHeight * globalScale / (deviceRes.Y / 2);

                    var world = Matrix4x4.CreateScale(ndcScaledWidth, ndcScaledHeight, 1)
                        * Matrix4x4.CreateTranslation(ndcX + ndcScaledWidth / 2, ndcY - ndcScaledHeight / 2, 0);

                    instanceData[instIdx] = new InstanceTransformData
                    {
                        Row0 = new Vector4(world.M11, world.M12, world.M13, world.M14),
                        Row1 = new Vector4(world.M21, world.M22, world.M23, world.M24),
                        Row2 = new Vector4(world.M31, world.M32, world.M33, world.M34),
                        Row3 = new Vector4(world.M41, world.M42, world.M43, world.M44),
                        MorphWeights = Vector4.Zero,
                    };
                }

                instIdx++;
            }

            // Handle the dot glyph - Changed must always be cleared unconditionally
            if (texts.dotTextureHolder is TextGlyphHolder dotHolder)
            {
                var dt = dotHolder.Texture;
                if (dt.Changed)
                    dt.Changed = false;

                if (texts.LastPos != null)
                {
                    if (uploadGlyphData)
                    {
                        if (TryEnsureGlyphEntry(ref texts._dotRef, out var dotEntry))
                        {
                            if (texts._dotRef.AtlasVersion != dotEntry.AtlasVersion)
                            {
                                texts._dotRef.AtlasVersion = dotEntry.AtlasVersion;
                                dt.SourceX = dotEntry.SourceX;
                                dt.SourceY = dotEntry.SourceY;
                                dt.SourceWidth = dotEntry.SourceWidth;
                                dt.SourceHeight = dotEntry.SourceHeight;
                                dt.OriginWidth = dotEntry.Width;
                                dt.OriginHeight = dotEntry.Height;
                                dt.Factor = dotEntry.PixelRange;
                            }

                            bool hasDotColorOverride = texts._dotRef.Color.HasValue;
                            var dotGlyphColor = hasDotColorOverride ? texts._dotRef.Color.Value.AsVector4 : Vector4.One;

                            glyphPtr[instIdx] = new VKTextGlyphData
                            {
                                UvRect = new Vector4(dt.SourceX / atlasW, dt.SourceY / atlasH, dt.SourceWidth / atlasW, dt.SourceHeight / atlasH),
                                Color = dotGlyphColor,
                                Metrics = new Vector4((float)dt.OriginWidth, (float)dt.OriginHeight, (float)dt.Factor, hasDotColorOverride ? 1f : 0f),
                            };
                        }
                    }

                    if (writeInstanceData)
                    {
                        float dotAlpha = Math.Clamp(dt.Alpha, 0f, 1f);
                        if (dotAlpha <= 0f || dt.Width <= 0f || dt.Height <= 0f)
                        {
                            instanceData[instIdx] = CreateHiddenInstanceData();
                            instIdx++;
                            goto AfterDot;
                        }

                        float dpx = dt.PosX * n;
                        float dpy = dt.PosY * n;
                        float dw = dt.Width * n;
                        float dh = dt.Height * n;
                        float dnx = (dpx - extendRes.X / 2) / (extendRes.X / 2);
                        float dny = (extendRes.Y / 2 - dpy) / (extendRes.Y / 2);
                        float dsw = dw * globalScale / (deviceRes.X / 2);
                        float dsh = dh * globalScale / (deviceRes.Y / 2);

                        var dworld = Matrix4x4.CreateScale(dsw, dsh, 1)
                            * Matrix4x4.CreateTranslation(dnx + dsw / 2, dny - dsh / 2, 0);

                        instanceData[instIdx] = new InstanceTransformData
                        {
                            Row0 = new Vector4(dworld.M11, dworld.M12, dworld.M13, dworld.M14),
                            Row1 = new Vector4(dworld.M21, dworld.M22, dworld.M23, dworld.M24),
                            Row2 = new Vector4(dworld.M31, dworld.M32, dworld.M33, dworld.M34),
                            Row3 = new Vector4(dworld.M41, dworld.M42, dworld.M43, dworld.M44),
                            MorphWeights = Vector4.Zero,
                        };
                    }
                    instIdx++;
                }
            }

        AfterDot:
            // Fill the remaining slots
            for (; instIdx < instanceCount; instIdx++)
            {
                if (uploadGlyphData)
                    glyphPtr[instIdx] = CreateHiddenGlyphData();
                if (writeInstanceData)
                    instanceData[instIdx] = CreateHiddenInstanceData();
            }

            // Multi-frame synchronization: write to all frame buffers
            if (writeInstanceData)
            {
                for (int fi = 0; fi < state.InstanceBuffers.Length; fi++)
                {
                    if (state.InstanceBuffers[fi].Buffer.Handle == 0)
                        continue;

                    void* p;
                    var ib = state.InstanceBuffers[fi];
                    if (Vulkan.Device.Vk.MapMemory(Vulkan.Device.LogicalDevice, ib.Memory, 0, (ulong)(instanceCount * sizeof(InstanceTransformData)), 0, &p) == VkResult.Success)
                    {
                        for (int j = 0; j < instanceCount; j++)
                            Unsafe.Write((byte*)p + j * sizeof(InstanceTransformData), instanceData[j]);
                        Vulkan.Device.Vk.UnmapMemory(Vulkan.Device.LogicalDevice, ib.Memory);
                        state.InstanceFrameMask |= (1u << fi);
                    }
                }
            }

            if (uploadGlyphData)
            {
                // Multi-frame synchronization: copy glyph data to the remaining frame buffers
                uint glyphBytes = (uint)(instanceCount * Unsafe.SizeOf<VKTextGlyphData>());
                for (int gfi = 1; gfi < state.GlyphMappedPtrs.Length; gfi++)
                    Unsafe.CopyBlock(state.GlyphMappedPtrs[gfi], state.GlyphMappedPtrs[0], glyphBytes);

                state.GlyphAtlasVersionBuilt = _glyphAtlas.Version;
                state.GlyphDirty = false;
            }
            state.CanDraw = true;
            _textInstances[texts] = state;
            DeviceServices.BaseApp.AddLog(LogType.Backend, $"{DateTime.UtcNow} [VK Text] UpdateTexts CanDraw=true instCount={instanceCount} glyphDirty={uploadGlyphData} layoutChanged={writeInstanceData} frameMask={state.InstanceFrameMask:X}");
        }
    }

    public void DrawTexts(Texts texts)
    {
        if (texts?.Texs?.Length == 0)
        {
            if (_textInstances.TryGetValue(texts, out var emptyState))
            {
                emptyState.CanDraw = false;
                _textInstances[texts] = emptyState;
            }
            return;
        }

        // GPU instancing path: a single vkCmdDrawIndexed call
        if (_textInstances.TryGetValue(texts, out var state) && state.InstanceCount > 0)
        {
            var cmd = Vulkan.Device.GraphicsCommandBuffer;
            int fi = (int)Vulkan.Device.FrameIndex;
            if (!state.CanDraw || state.GlyphBuffers == null || fi >= state.GlyphBuffers.Length
                || state.GlyphBuffers[fi].Buffer.Handle == 0
                || state.InstanceBuffers == null || fi >= state.InstanceBuffers.Length)
            {
                DeviceServices.BaseApp.AddLog(LogType.Backend, $"{DateTime.UtcNow} [VK Text] DrawTexts SKIP CanDraw={state.CanDraw} glyphBufsNull={state.GlyphBuffers == null} instBufNull={state.InstanceBuffers == null} fi={fi} instBufLen={state.InstanceBuffers?.Length ?? -1}");
                return;
            }

            // Ensure the atlas texture is ready
            _glyphAtlas.AtlasTexture?.EnsureReadyForRendering(cmd);

            // Set the pipeline (Transparent + DoubleSided)
            Pipeline.SetPipeline(cmd, PipelineMode.Transparent, doubleSided: true);

            // Write TextDrawParams
            // using a per-Texts, per-frame dedicated UBO to eliminate same-frame cross-control overwrites
            var texSize = new Vector2(
                _glyphAtlas.AtlasTexture?.Width ?? 1f,
                _glyphAtlas.AtlasTexture?.Height ?? 1f);
            var textColor = texts.Color.AsVector4;
            var drawParams = new VKTextDrawParams
            {
                AtlasSize = texSize,
                PxRange = Season.Fonts.Font.PixelRange,
                GlobalAlpha = Math.Clamp(texts.Alpha, 0f, 1f),
                TextColor = textColor,
            };
            Unsafe.Write(state.DrawParamsMappedPtrs[fi], drawParams);

            // binding 11 is already bound to this control's dedicated DrawParamsBuffers[fi]
            // in AllocateTextDescriptorSets.
            // Never call vkUpdateDescriptorSets dynamically here:
            // updating descriptor sets that may still be referenced by in-flight command buffers
            // is undefined behavior and can cause random binding contamination
            // such as text recoloring or masking artifacts.
            var set = state.DescriptorSets[fi];

            // Bind VB slot 0: unit quad; slot 1: current-frame instance buffer
            var vb0 = Pipeline.UnitQuadVertexBuffer.Buffer;
            var vb1 = state.InstanceBuffers[fi].Buffer;
            ulong vbOffset0 = 0, vbOffset1 = 0;
            var vbs = stackalloc Silk.NET.Vulkan.Buffer[2] { vb0, vb1 };
            var vbOffsets = stackalloc ulong[2] { vbOffset0, vbOffset1 };
            Vulkan.Device.Vk.CmdBindVertexBuffers(cmd, 0, 2, vbs, vbOffsets);

            // Bind IB: unit quad
            // CreateIndexBuffer automatically stores indices <= 65535 as Uint16.
            // Unit quad indices are 0..3, so the actual storage is 16-bit
            // and must be bound as Uint16.
            Vulkan.Device.Vk.CmdBindIndexBuffer(cmd, Pipeline.UnitQuadIndexBuffer.Buffer, 0, VkIndexType.Uint16);

            // Bind the descriptor set
            // allocated from the dedicated text pool, with all 12 bindings already written at creation time
            var pipelineLayout = Pipeline.PipelineLayout;

            Vulkan.Device.Vk.CmdBindDescriptorSets(cmd, VkPipelineBindPoint.Graphics, pipelineLayout, 0, 1, &set, 0, null);

            // Single instanced draw
            Vulkan.Device.Vk.CmdDrawIndexed(cmd, 6, (uint)state.InstanceCount, 0, 0, 0);
        }
    }

    public void DisposeTexts(Texts texts)
    {
        // Release GPU instancing resources
        if (_textInstances.TryGetValue(texts, out var state))
        {
            ReleaseTextInstanceResources(ref state);
            _textInstances.Remove(texts);
        }

        // Release holder references
        // TextGlyphHolder has no GPU resources, so this only clears references
        if (texts.textureHoldersLoading != null)
        {
            foreach (var holder in texts.textureHoldersLoading)
            {
                if (holder is IDisposable d)
                    d.Dispose();
            }
        }
        if (texts.textureHolders != null)
        {
            foreach (var holder in texts.textureHolders)
            {
                if (holder is IDisposable d)
                    d.Dispose();
            }
        }

        if (texts.dotTextureHolderLoading is IDisposable ddl)
            ddl.Dispose();

        if (texts.dotTextureHolder is IDisposable dd)
            dd.Dispose();

        texts.textureHoldersLoading = null;
        texts.textureHolders = null;
        texts.dotTextureHolderLoading = null;
        texts.dotTextureHolder = null;
    }

    public void FlushTextAtlas()
    {
        _glyphAtlas.FlushPendingUploadsOnRenderThread();
    }

    public void DisposeTextureHolders(ITextureHolder[] holders)
    {
        if (holders == null || holders.Length == 0)
            return;

        foreach (var holder in holders)
        {
            if (holder is IDisposable d)
                d.Dispose();
        }
    }

    // ============================================================
    // Helpers for text GPU instancing
    // ============================================================

    static VKTextGlyphData CreateHiddenGlyphData()
    {
        return new VKTextGlyphData
        {
            UvRect = Vector4.Zero,
            Color = Vector4.One,
            Metrics = Vector4.Zero,
        };
    }

    static InstanceTransformData CreateHiddenInstanceData()
    {
        return new InstanceTransformData
        {
            Row0 = Vector4.Zero,
            Row1 = Vector4.Zero,
            Row2 = Vector4.Zero,
            Row3 = Vector4.Zero,
            MorphWeights = Vector4.Zero,
        };
    }

    /// <summary>Ensure per-frame instance-buffer capacity using geometric growth,
    /// so buffer creation during incremental appends is amortized to O(1).
    /// New buffers do not copy old contents:
    /// the caller must clear InstanceFrameMask and set GlyphDirty at the same time,
    /// so the next UpdateTexts rebuilds all instances through a "full-frame synchronized full rewrite"
    /// path, preserving the anti-flicker invariant.
    /// Instance buffers are vertex buffers rather than descriptor-set resources,
    /// so growing them does not require descriptor reallocation.</summary>
    bool EnsureInstanceBufferCapacity(ref VKTextInstanceState state, int requiredCount)
    {
        requiredCount = Math.Max(requiredCount, 1);
        int frameCount = (int)Vulkan.Device.frameCount;
        if (state.InstanceBuffers != null
            && state.InstanceBuffers.Length == frameCount
            && state.InstanceCapacity >= requiredCount)
            return true;

        if (frameCount <= 0)
            return false;

        int capacity = Math.Max(requiredCount, Math.Max(state.InstanceCapacity * 2, 64));

        var seed = new InstanceTransformData[capacity];
        var hidden = CreateHiddenInstanceData();
        for (int i = 0; i < capacity; i++)
            seed[i] = hidden;

        var buffers = new BufferResource[frameCount];
        for (int fi = 0; fi < frameCount; fi++)
            buffers[fi] = Vulkan.Device.ResourceManager.CreateVertexBuffer(seed);

        var previousBuffers = state.InstanceBuffers;
        state.InstanceBuffers = buffers;
        state.InstanceMappedPtrs = new byte*[frameCount];
        state.InstanceCapacity = capacity;
        state.InstanceFrameMask = 0;

        // Old buffers may still be referenced by in-flight frames and must be released deferred
        ReleaseInstanceBuffersDeferred(previousBuffers);
        return true;
    }

    /// <summary>Queue an instance-buffer group for deferred release
    /// (they are not persistently mapped, so direct destruction is sufficient).</summary>
    void ReleaseInstanceBuffersDeferred(BufferResource[] buffers)
    {
        if (buffers == null)
            return;

        Vulkan.Device.EnqueueDeferredRelease(() =>
        {
            var rm = Vulkan.Device.ResourceManager;
            foreach (var ib in buffers)
            {
                if (ib.Buffer.Handle != 0)
                    rm.DestroyBuffer(ib);
            }
        });
    }

    bool EnsureGlyphBufferCapacity(ref VKTextInstanceState state, int requiredCount)
    {
        requiredCount = Math.Max(requiredCount, 1);
        int frameCount = (int)Vulkan.Device.frameCount;
        if (state.GlyphBuffers != null && state.GlyphMappedPtrs != null
            && state.GlyphBuffers.Length == frameCount
            && state.GlyphBuffers[0].Buffer.Handle != 0
            && state.GlyphCapacity >= requiredCount)
            return true;

        // Old buffers may still be referenced by in-flight frames.
        // They must never be destroyed immediately and must go through deferred release.
        ReleaseGlyphBuffersDeferred(ref state);

        ulong size = (ulong)(requiredCount * Unsafe.SizeOf<VKTextGlyphData>());
        var buffers = new BufferResource[frameCount];
        var ptrs = new byte*[frameCount];
        for (int fi = 0; fi < frameCount; fi++)
        {
            buffers[fi] = Vulkan.Device.ResourceManager.CreateBuffer(
                size,
                VkBufferUsageFlags.StorageBufferBit | VkBufferUsageFlags.TransferDstBit,
                VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit);

            void* p;
            if (Vulkan.Device.Vk.MapMemory(Vulkan.Device.LogicalDevice, buffers[fi].Memory, 0, size, 0, &p) != VkResult.Success)
            {
                // Roll back the newly created buffers from this attempt
                // (they were never submitted to the GPU and can be destroyed immediately)
                for (int j = 0; j <= fi; j++)
                {
                    if (buffers[j].Buffer.Handle == 0)
                        continue;
                    if (j < fi)
                        Vulkan.Device.Vk.UnmapMemory(Vulkan.Device.LogicalDevice, buffers[j].Memory);
                    Vulkan.Device.ResourceManager.DestroyBuffer(buffers[j]);
                }
                return false;
            }
            ptrs[fi] = (byte*)p;
        }

        state.GlyphBuffers = buffers;
        state.GlyphMappedPtrs = ptrs;
        state.GlyphCapacity = requiredCount;

        // If descriptor sets already exist
        // (for example when UpdateTexts grows buffers mid-frame),
        // they still point to obsolete buffers.
        // They must not be updated in place through vkUpdateDescriptorSets
        // because that is UB under in-flight references.
        // Allocate new sets and defer releasing the old ones.
        if (state.DescriptorSets != null)
        {
            var oldSets = state.DescriptorSets;
            var alloc = _textDescriptorAllocator;
            Vulkan.Device.EnqueueDeferredRelease(() =>
            {
                foreach (var ds in oldSets)
                {
                    if (ds.Handle != 0)
                        alloc!.FreeSet(ds);
                }
            });
            state.DescriptorSets = new VkDs[frameCount];
            AllocateTextDescriptorSets(ref state, _glyphAtlas.AtlasTexture, frameCount);
        }
        return true;
    }

    /// <summary>Queue a glyph-buffer group for deferred release and clear the state references.
    /// These buffers are persistently mapped once created,
    /// so they must be unmapped before release.</summary>
    void ReleaseGlyphBuffersDeferred(ref VKTextInstanceState state)
    {
        var oldBuffers = state.GlyphBuffers;
        state.GlyphBuffers = null!;
        state.GlyphMappedPtrs = null!;
        state.GlyphCapacity = 0;

        if (oldBuffers == null)
            return;

        Vulkan.Device.EnqueueDeferredRelease(() =>
        {
            var vk = Vulkan.Device.Vk;
            var d = Vulkan.Device.LogicalDevice;
            var rm = Vulkan.Device.ResourceManager;
            foreach (var buf in oldBuffers)
            {
                if (buf.Buffer.Handle == 0)
                    continue;
                vk.UnmapMemory(d, buf.Memory);
                rm.DestroyBuffer(buf);
            }
        });
    }

    void ReleaseTextInstanceResources(ref VKTextInstanceState state)
    {
        // Every resource here may still be referenced by in-flight command buffers
        // (for example while the GPU is consuming old frames during LoadTexts rebuild / DisposeTexts).
        // Queue all of them for deferred release and destroy them only after the timeline fence advances.
        var instanceBuffers = state.InstanceBuffers;
        var drawParamsBuffers = state.DrawParamsBuffers;
        var descriptorSets = state.DescriptorSets;
        var alloc = _textDescriptorAllocator;

        state.InstanceBuffers = null!;
        state.InstanceMappedPtrs = null!;
        state.InstanceCapacity = 0;
        state.DrawParamsBuffers = null!;
        state.DrawParamsMappedPtrs = null!;
        state.DescriptorSets = null!;

        ReleaseInstanceBuffersDeferred(instanceBuffers);

        if (drawParamsBuffers != null || descriptorSets != null)
        {
            Vulkan.Device.EnqueueDeferredRelease(() =>
            {
                var rm = Vulkan.Device.ResourceManager;

                if (drawParamsBuffers != null)
                {
                    // CreateConstantBuffer uses persistent mapping,
                    // and DestroyBuffer will implicitly unmap through FreeMemory
                    foreach (var dpb in drawParamsBuffers)
                    {
                        if (dpb.Buffer.Handle != 0)
                            rm.DestroyBuffer(dpb);
                    }
                }

                if (descriptorSets != null && alloc != null)
                {
                    foreach (var ds in descriptorSets)
                    {
                        if (ds.Handle != 0)
                            alloc.FreeSet(ds);
                    }
                }
            });
        }

        ReleaseGlyphBuffersDeferred(ref state);
    }

    /// <summary>
    /// Allocate and write DescriptorSets for a Texts control on every frame (N-buffered).
    /// Bound resources:
    ///   binding 0: text matrix UBO (identity, shared across frames)
    ///   binding 1: global light UBO[fi]
    ///   binding 2: text material UBO (renderMode=2, isInstanced=1, shared across frames)
    ///   binding 3: global identity bone UBO[fi]
    ///   binding 4: atlas texture
    ///   binding 5-8: white placeholder textures
    ///   binding 9: default instance bone storage buffer[fi]
    ///   binding 10: per-Texts glyph storage buffer[fi] (N-buffered per frame)
    ///   binding 11: per-Texts text draw params UBO[fi]
    /// </summary>
    void AllocateTextDescriptorSets(ref VKTextInstanceState state, VkTexture atlasTexture, int frameCount)
    {
        var whiteView = _whiteTexture.View;

        DeviceServices.BaseApp.AddLog(LogType.Backend, $"{DateTime.UtcNow} [VK Text] AllocateTextDS frameCount={frameCount} atlasNotNull={atlasTexture != null} whiteViewHandle={whiteView.Handle}");

        for (int fi = 0; fi < frameCount; fi++)
        {
            // Allocate from the dedicated text DescriptorPool,
            // fully isolated from the Sprite shared pool
            var set = _textDescriptorAllocator!.AllocateSet(Pipeline.SetLayout);
            state.DescriptorSets[fi] = set;

            // UBO infos (shared across frames)
            var matrixInfo = new VkDescriptorBufferInfo
            { Buffer = _textMatrixBuffer.Buffer, Offset = 0, Range = SVk.WholeSize };
            var materialInfo = new VkDescriptorBufferInfo
            { Buffer = _textMaterialBuffer.Buffer, Offset = 0, Range = SVk.WholeSize };
            var glyphInfo = new VkDescriptorBufferInfo
            { Buffer = state.GlyphBuffers[fi].Buffer, Offset = 0, Range = SVk.WholeSize };

            // UBO infos (per frame)
            var lightInfo = new VkDescriptorBufferInfo
            { Buffer = VKPrimitiveGroup.LightConstantBuffers[fi].Buffer, Offset = 0, Range = SVk.WholeSize };
            var boneInfo = new VkDescriptorBufferInfo
            { Buffer = VKPrimitiveGroup.IdentityBoneBuffers[fi].Buffer, Offset = 0, Range = SVk.WholeSize };
            var instanceBoneInfo = new VkDescriptorBufferInfo
            { Buffer = Pipeline.IdentityInstanceBoneBuffers[fi].Buffer, Offset = 0, Range = SVk.WholeSize };
            var drawParamsInfo = new VkDescriptorBufferInfo
            { Buffer = state.DrawParamsBuffers[fi].Buffer, Offset = 0, Range = SVk.WholeSize };

            // Texture image infos
            var imgInfos = stackalloc VkDescriptorImageInfo[5];
            imgInfos[0] = new VkDescriptorImageInfo
            { ImageView = atlasTexture.View, ImageLayout = VkImageLayout.ShaderReadOnlyOptimal };
            for (int i = 1; i < 5; i++)
                imgInfos[i] = new VkDescriptorImageInfo
                { ImageView = whiteView, ImageLayout = VkImageLayout.ShaderReadOnlyOptimal };

            // Write 16 bindings
            // (2-3 Step C adds default zero placeholders for previous-frame SSBO data at bindings 13/14/15)
            var writes = stackalloc VkWriteDescriptorSet[16];
            writes[0] = MakeTextWrite(set, 0, VkDescriptorType.UniformBuffer, &matrixInfo);
            writes[1] = MakeTextWrite(set, 1, VkDescriptorType.UniformBuffer, &lightInfo);
            writes[2] = MakeTextWrite(set, 2, VkDescriptorType.UniformBuffer, &materialInfo);
            writes[3] = MakeTextWrite(set, 3, VkDescriptorType.UniformBuffer, &boneInfo);
            for (int i = 0; i < 5; i++)
                writes[4 + i] = MakeTextImageWrite(set, (uint)(4 + i), imgInfos + i);
            writes[9] = MakeTextWrite(set, 9, VkDescriptorType.StorageBuffer, &instanceBoneInfo);
            writes[10] = MakeTextWrite(set, 10, VkDescriptorType.StorageBuffer, &glyphInfo);
            writes[11] = MakeTextWrite(set, 11, VkDescriptorType.UniformBuffer, &drawParamsInfo);

            // 1-5: binding 12 shadow atlas.
            // The text path does not sample shadows, but SetLayout declares this binding,
            // so a valid placeholder must still be provided.
            // stackalloc memory is not zero-initialized, and an unwritten writes[12]
            // would contain stack garbage that makes the WSL Vulkan driver abort on an invalid descriptor.
            var shadowInfo = default(VkDescriptorImageInfo);
            if (Season.Rendering.FrameSchedule.ShadowMap is VKRenderTarget shadowRt && shadowRt.DepthView.Handle != 0)
            {
                shadowInfo = new VkDescriptorImageInfo
                { ImageView = shadowRt.DepthView, ImageLayout = VkImageLayout.ShaderReadOnlyOptimal };
            }
            else
            {
                // Shadows disabled: fill binding 12 with the White placeholder texture
                // to avoid UB from stack garbage
                shadowInfo = new VkDescriptorImageInfo
                { ImageView = whiteView, ImageLayout = VkImageLayout.ShaderReadOnlyOptimal };
            }
            writes[12] = MakeTextImageWrite(set, 12, &shadowInfo);

            // 2-3 Step C: previous-frame SSBO data (binding 13/14/15),
            // using default zero-value placeholders
            var prevBoneInfo = new VkDescriptorBufferInfo
            { Buffer = Pipeline.DefaultPrevBoneBuffers[fi].Buffer, Offset = 0, Range = SVk.WholeSize };
            var prevInstanceWorldInfo = new VkDescriptorBufferInfo
            { Buffer = Pipeline.DefaultPrevInstanceWorldBuffers[fi].Buffer, Offset = 0, Range = SVk.WholeSize };
            var prevMorphWeightsInfo = new VkDescriptorBufferInfo
            { Buffer = Pipeline.DefaultPrevMorphWeightsBuffers[fi].Buffer, Offset = 0, Range = SVk.WholeSize };
            writes[13] = MakeTextWrite(set, 13, VkDescriptorType.StorageBuffer, &prevBoneInfo);
            writes[14] = MakeTextWrite(set, 14, VkDescriptorType.StorageBuffer, &prevInstanceWorldInfo);
            writes[15] = MakeTextWrite(set, 15, VkDescriptorType.StorageBuffer, &prevMorphWeightsInfo);

            Vulkan.Device.Vk.UpdateDescriptorSets(Vulkan.Device.LogicalDevice, 16, writes, 0, null);
        }
    }

    static VkWriteDescriptorSet MakeTextWrite(VkDs set, uint binding, VkDescriptorType descriptorType, VkDescriptorBufferInfo* info)
    {
        return new VkWriteDescriptorSet
        {
            SType = VkStructureType.WriteDescriptorSet,
            DstSet = set,
            DstBinding = binding,
            DstArrayElement = 0,
            DescriptorType = descriptorType,
            DescriptorCount = 1,
            PBufferInfo = info
        };
    }

    static VkWriteDescriptorSet MakeTextImageWrite(VkDs set, uint binding, VkDescriptorImageInfo* info)
    {
        return new VkWriteDescriptorSet
        {
            SType = VkStructureType.WriteDescriptorSet,
            DstSet = set,
            DstBinding = binding,
            DstArrayElement = 0,
            DescriptorType = VkDescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            PImageInfo = info
        };
    }

    bool TryEnsureGlyphEntry(ref Tex tex, out GlyphAtlasEntry entry)
    {
        entry = default;

        if (tex.TexType is TexType.NewLine or TexType.Space or TexType.Missing)
        {
            return false;
        }

        int size = (int)DeviceServices.BaseApp.FontSize;

        try
        {
            if (!_glyphAtlas.TryEnsureGlyph(size, tex.Value, out entry))
            {
                tex.TexType = TexType.Missing;
                return false;
            }
        }
        catch (Exception ex)
        {
            tex.TexType = TexType.Missing;
            DeviceServices.BaseApp.AddLog(LogType.Error, $"{DateTime.UtcNow} LoadTexTexture EnsureGlyph {ex}");
            return false;
        }

        tex.GlyphMetrics = entry.GlyphMetrics;
        tex.Factor = entry.PixelRange;
        return true;
    }

    // ── Model ──

    public async Task<bool> LoadModel(Season.Controls.Model model)
    {
        lock (DictionaryModel)
        {
            if (DictionaryModel.ContainsKey((model.Name, model.ID)))
            {
                return true;
            }
        }

        GetOrCreateSharedModelAsync(model.Name).ContinueWith(task =>
        {
            VKModel vkModel;
            try
            {
                var template = task.GetAwaiter().GetResult();
                vkModel = template.CreateInstance(model, VKPrimitiveGroup.Camera);
            }
            catch
            {
                vkModel = new VKModel(model.Name);
                vkModel.Load(model, VKPrimitiveGroup.Camera);
                ExecuteUpload();
            }

            lock (DictionaryModel)
            {
                if (!DictionaryModel.ContainsKey((model.Name, model.ID)))
                    DictionaryModel.Add((model.Name, model.ID), vkModel);
                else
                    vkModel.Dispose();
            }
        });

        return true;
    }

    Task<VKModel> GetOrCreateSharedModelAsync(string modelName)
    {
        Task<VKModel> sharedTask;
        lock (DictionaryModelResource)
        {
            if (!DictionaryModelResource.TryGetValue(modelName, out sharedTask))
            {
                sharedTask = CreateSharedModelAsync(modelName);
                DictionaryModelResource[modelName] = sharedTask;
            }
        }

        return sharedTask.ContinueWith(task =>
        {
            if (task.IsFaulted || task.IsCanceled)
            {
                lock (DictionaryModelResource)
                {
                    if (DictionaryModelResource.TryGetValue(modelName, out var cachedTask) && cachedTask == sharedTask)
                        DictionaryModelResource.Remove(modelName);
                }
            }

            return task.GetAwaiter().GetResult();
        });
    }

    Task<VKModel> CreateSharedModelAsync(string modelName)
    {
        var templateModel = new Model
        {
            Name = modelName,
            Alpha = 1f
        };

        var template = new VKModel(modelName);
        template.Load(templateModel, VKPrimitiveGroup.Camera);
        ExecuteUpload();
        return Task.FromResult(template);
    }

    public void UpdateModel(Model model, float time)
    {
        VKModel vkModel = null!;

        lock (DictionaryModel)
        {
            if (DictionaryModel.TryGetValue((model.Name, model.ID), out vkModel!))
            {
                // Material overrides
                ProcessModelOverrides(model, vkModel);

                vkModel.Update(model, time);
            }
        }
    }

    void ProcessModelOverrides(Model model, VKPrimitiveGroup vkGroup)
    {
        TryReplaceModelTexture(model, vkGroup, model.BaseColorOverride, TextureSlot.BaseColor, () => model.BaseColorOverride = default);
        TryReplaceModelTexture(model, vkGroup, model.NormalOverride, TextureSlot.Normal, () => model.NormalOverride = default);
        TryReplaceModelTexture(model, vkGroup, model.MetallicRoughnessOverride, TextureSlot.MetallicRoughness, () => model.MetallicRoughnessOverride = default);
        TryReplaceModelTexture(model, vkGroup, model.OcclusionOverride, TextureSlot.Occlusion, () => model.OcclusionOverride = default);
        TryReplaceModelTexture(model, vkGroup, model.EmissiveTextureOverride, TextureSlot.Emissive, () => model.EmissiveTextureOverride = default);

        bool hasParam = model.MetallicOverride.HasValue
                     || model.RoughnessOverride.HasValue
                     || model.EmissiveFactorOverride.HasValue;
        if (hasParam)
        {
            vkGroup.SyncMaterialParams(model.MetallicOverride, model.RoughnessOverride, model.EmissiveFactorOverride);
            model.MetallicOverride = null;
            model.RoughnessOverride = null;
            model.EmissiveFactorOverride = null;
        }
    }

    void TryReplaceModelTexture(Model model, VKPrimitiveGroup vkGroup,
        TextureUpdateSource source, TextureSlot slot, Action clearSource)
    {
        if (!source.HasValue) return;
        clearSource();
        var decoder = ResolveDecoder(source);
        if (decoder == null) return;
        vkGroup.ReplaceTextureBySlot(slot, decoder);
        ExecuteUpload();
        decoder.Dispose();
    }

    public void DrawModel(Model model)
    {
        if (model.Name.IsNullOrWhiteSpace() || model.Alpha == 0)
        {

        }
        else
        {
            VKModel vkModel3D = null!;

            lock (DictionaryModel)
            {
                if (DictionaryModel.TryGetValue((model.Name, model.ID), out vkModel3D!))
                {

                }
                else
                {
                    //texture.Changed = true;
                }
            }

            if (vkModel3D == null)
            {

            }
            else
            {
                vkModel3D.Draw();
            }
        }
    }

    // ============================================================
    // 1-5 Shadow pass: per-control projection dispatch + pass orchestration entry
    // mirrors DX Graphics 1:1
    // ============================================================

    public void DrawModelShadow(Model model)
    {
        VKModel vkModel = null!;
        lock (DictionaryModel)
        {
            DictionaryModel.TryGetValue((model.Name, model.ID), out vkModel!);
        }
        vkModel?.DrawShadow();
    }

    public void DrawMesh3DShadow(Season.Controls.Mesh3D mesh)
    {
        VKMesh3D vkMesh = null!;
        lock (DictionaryMesh3D)
        {
            DictionaryMesh3D.TryGetValue((mesh.Name, mesh.ID), out vkMesh!);
        }
        vkMesh?.DrawShadow();
    }

    public void DrawInstancedModelShadow(InstancedModel model)
    {
        VKInstancedModel vkModel = null!;
        lock (DictionaryInstancedModel)
        {
            DictionaryInstancedModel.TryGetValue((model.ModelName, model.ID), out vkModel!);
        }
        vkModel?.DrawShadow();
    }

    public void DrawInstancedMesh3DShadow(InstancedMesh3D mesh)
    {
        VKInstancedMesh3D vkMesh = null!;
        lock (DictionaryInstancedMesh3D)
        {
            DictionaryInstancedMesh3D.TryGetValue((mesh.Name, mesh.ID), out vkMesh!);
        }
        vkMesh?.DrawShadow();
    }

    /// <summary>
    /// 1-5 Shadow pass body (FrameSchedule.RenderShadow callback):
    /// after switching to the shadow PSO, set the controlled viewport + light-space matrix
    /// (VS push constant) for each atlas quadrant,
    /// then replay the shared-layer DrawShadow traversal
    /// (once per cascade / spotlight).
    /// The atlas is fully cleared by BeginPass;
    /// when no light is active, return directly
    /// because shader-side shadowParams stay all zero and nothing is sampled.
    /// </summary>
    internal void RenderShadowPass(Season.Basic.IGraphics g)
    {
        if (!RenderQuality.Current.ShadowsEnabled)
            return;
        if (!CascadedShadow.SunActive && !CascadedShadow.SpotActive)
            return;

        var app = DeviceServices.BaseApp;
        if (app == null)
            return;

        var cmd = Vulkan.Device.GraphicsCommandBuffer;
        Vulkan.Pipeline.SetShadowPipeline(cmd);

        if (CascadedShadow.SunActive)
        {
            for (int slot = 0; slot < CascadedShadow.ActiveCascadeCount; slot++)
            {
                CascadedShadow.GetAtlasViewport(slot, out int x, out int y, out int size);
                Vulkan.Device.SetShadowViewport(x, y, size);
                // Clause 7: BeginSlot publishes both the matrix and the culling frustum
                // from the same source and must not be bypassed
                Vulkan.Pipeline.SetShadowViewProj(cmd, CascadedShadow.BeginSlot(slot));
                app.DrawShadow();
            }
        }

        if (CascadedShadow.SpotActive)
        {
            CascadedShadow.GetAtlasViewport(CascadedShadow.SpotSlot, out int sx, out int sy, out int ssize);
            Vulkan.Device.SetShadowViewport(sx, sy, ssize);
            Vulkan.Pipeline.SetShadowViewProj(cmd, CascadedShadow.BeginSlot(CascadedShadow.SpotSlot));
            app.DrawShadow();
        }

        CascadedShadow.EndPass();
    }

    public async Task<bool> LoadSprite3D(Sprite3D sprite)
    {
        try
        {
            lock (DictionarySprite3D)
            {
                if (DictionarySprite3D.ContainsKey((sprite.Name, sprite.ID)))
                    return true;
            }

            VkTexture view = null!;
            lock (DictionaryVKTexture)
            {
                if (!DictionaryVKTexture.TryGetValue(sprite.Name, out view!))
                {
                    INativeImageDecoder imageResult = null!;
                    if (ImageUtils.CreateImageExist(sprite.Name))
                    {
                        imageResult = ImageUtils.CreateImage(sprite.Name);
                    }
                    else
                    {
                        if (!StorageService.FileExist(StorageService.DirectoryBase, sprite.Name))
                            StorageService.CopyToLocal(sprite.Name);
                        StorageService.TryGetStream(StorageService.DirectoryBase, sprite.Name, out Stream stream, out string errMsg);
                        using (stream)
                        {
                            if (stream != null)
                            {
                                var imageExt = sprite.Ext;
                                if (imageExt.IsNullOrWhiteSpace())
                                    imageExt = System.IO.Path.GetExtension(sprite.Name).ToLower();

                                imageResult = ImageUtils.GetImageFromStream(stream, imageExt);
                            }
                        }
                    }
                    if (imageResult != null)
                    {
                        view = new VkTexture(imageResult);
                        view.Name = sprite.Name;
                        ExecuteUpload();
                    }
                    if (view != null)
                        DictionaryVKTexture.Add(sprite.Name, view);
                }
            }

            var vkSprite3D = new VKSprite3D(view);

            lock (DictionarySprite3D)
            {
                if (!DictionarySprite3D.ContainsKey((sprite.Name, sprite.ID)))
                    DictionarySprite3D.Add((sprite.Name, sprite.ID), vkSprite3D);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }

        return true;
    }

    public void UpdateSprite3D(Sprite3D sprite, float time)
    {
        VKSprite3D vkSprite3D = null!;
        lock (DictionarySprite3D)
        {
            if (DictionarySprite3D.TryGetValue((sprite.Name, sprite.ID), out vkSprite3D!))
            {
                    // Texture replacement
                if (sprite.TextureOverride.HasValue)
                {
                    var source = sprite.TextureOverride;
                    sprite.TextureOverride = default;
                    ReplaceSpriteTexture(vkSprite3D, source);
                }

                vkSprite3D.Update(
                    new Vector3(sprite.PosX, sprite.PosY, sprite.PosZ),
                    new Vector2(sprite.Width ?? 1f, sprite.Height ?? 1f),
                    sprite.Rotation,
                    VKPrimitiveGroup.Camera.View,
                    VKPrimitiveGroup.Camera.Projection,
                    sprite.Mode,
                    sprite.Color,
                    sprite.Alpha);
            }
        }
    }

    public void DrawSprite3D(Sprite3D sprite)
    {
        if (sprite.Name.IsNullOrWhiteSpace() || sprite.Alpha == 0)
            return;

        VKSprite3D vkSprite3D = null!;
        lock (DictionarySprite3D)
        {
            DictionarySprite3D.TryGetValue((sprite.Name, sprite.ID), out vkSprite3D!);
        }
        vkSprite3D?.Draw();
    }

    public void DisposeSprite3D(Sprite3D sprite)
    {
        VKSprite3D vkSprite3D = null!;
        lock (DictionarySprite3D)
        {
            var key = (sprite.Name, sprite.ID);
            if (DictionarySprite3D.TryGetValue(key, out vkSprite3D!))
                DictionarySprite3D.Remove(key);
        }
        vkSprite3D?.Dispose();

        lock (DictionaryVKTexture)
        {
            if (DictionaryVKTexture.TryGetValue(sprite.Name, out var vkTex) && vkTex != null)
            {
                vkTex.Release();
                if (vkTex.RefCount == 0)
                    DictionaryVKTexture.Remove(sprite.Name);
            }
        }
        sprite.Ready = false;
    }

    /// <summary>
    /// Load a single texture into DictionaryVKTexture on demand and return the VkTexture.
    /// Reuses the LoadSprite3D loading chain:
    /// StorageService -> ImageResult -> new VkTexture(imageResult) + ExecuteUpload.
    /// </summary>
    VkTexture EnsureVKTexture(string name)
    {
        if (name.IsNullOrWhiteSpace())
            return null!;

        VkTexture view = null!;
        lock (DictionaryVKTexture)
        {
            if (DictionaryVKTexture.TryGetValue(name, out view!))
                return view;

            INativeImageDecoder imageResult = null!;
            if (ImageUtils.CreateImageExist(name))
            {
                imageResult = ImageUtils.CreateImage(name);
            }
            else
            {
                if (!StorageService.FileExist(StorageService.DirectoryBase, name))
                    StorageService.CopyToLocal(name);
                StorageService.TryGetStream(StorageService.DirectoryBase, name, out Stream stream, out string errMsg);
                using (stream)
                {
                    if (stream != null)
                    {
                        imageResult = ImageUtils.GetImageFromStream(stream, null);
                    }
                }
            }

            if (imageResult != null)
            {
                view = new VkTexture(imageResult);
                view.Name = name;
                ExecuteUpload();
            }

            if (view != null)
                DictionaryVKTexture.Add(name, view);

            return view!;
        }
    }

    // Mesh3D surface-texture resolution
    // procedural pixel sources avoid disk writes and path sources are reused,
    // mirroring Windows/Graphics.cs

    static string ProcTextureName(string meshName, long meshId, int surfaceIndex, SurfaceTextureSlot slot)
        => $"proc:{meshName}:{meshId}:{surfaceIndex}:{slot}";

    /// <summary>
    /// Resolve the texture source of one Surface slot into a VkTexture registered in DictionaryVKTexture:
    /// - Image branch (procedural pixels): CreateFromDecoder uploads directly to the GPU
    ///   with no file I/O at all,
    ///   registers under a composed name and executes ExecuteUpload immediately
    ///   (the VK constructor does not dispose the decoder; this method does it uniformly);
    /// - Path branch: reuses the existing EnsureVKTexture loading chain.
    /// Note: this path does not clear Override,
    /// because ProcessMaterial still queries through GetTextureSource/HasTexture.
    /// The caller clears TextureOverride uniformly after Load completes
    /// under the one-shot consumption contract.
    /// </summary>
    VkTexture EnsureSurfaceTexture(string meshName, long meshId, int surfaceIndex, Season.Controls.Surface surface, SurfaceTextureSlot slot)
    {
        var source = surface.GetTextureSource(slot);
        if (!source.HasValue)
            return null!;

        if (source.Image != null)
        {
            var name = ProcTextureName(meshName, meshId, surfaceIndex, slot);
            lock (DictionaryVKTexture)
            {
                if (DictionaryVKTexture.TryGetValue(name, out var cached))
                {
                    source.Image.Dispose();   // Already registered; do not upload again, only dispose the decoder to avoid leaks
                    return cached;
                }
            }

            var tex = VkTexture.CreateFromDecoder(source.Image);
            source.Image.Dispose();
            tex.Name = name;
            ExecuteUpload();

            lock (DictionaryVKTexture)
                DictionaryVKTexture[name] = tex;

            return tex;
        }

        return EnsureVKTexture(source.Path);
    }

    /// <summary>Pre-resolve all five texture slots of a single Surface
    /// (empty sources are skipped automatically).</summary>
    void EnsureSurfaceTextures(string meshName, long meshId, int surfaceIndex, Season.Controls.Surface surface)
    {
        EnsureSurfaceTexture(meshName, meshId, surfaceIndex, surface, SurfaceTextureSlot.BaseColor);
        EnsureSurfaceTexture(meshName, meshId, surfaceIndex, surface, SurfaceTextureSlot.Normal);
        EnsureSurfaceTexture(meshName, meshId, surfaceIndex, surface, SurfaceTextureSlot.MetallicRoughness);
        EnsureSurfaceTexture(meshName, meshId, surfaceIndex, surface, SurfaceTextureSlot.Occlusion);
        EnsureSurfaceTexture(meshName, meshId, surfaceIndex, surface, SurfaceTextureSlot.Emissive);
    }

    /// <summary>Clear TextureOverride on all Surface slots after Load completes
    /// (one-shot consumption contract).</summary>
    static void ClearSurfaceOverrides(Season.Controls.Surface surface)
    {
        surface.ClearTextureOverride(SurfaceTextureSlot.BaseColor);
        surface.ClearTextureOverride(SurfaceTextureSlot.Normal);
        surface.ClearTextureOverride(SurfaceTextureSlot.MetallicRoughness);
        surface.ClearTextureOverride(SurfaceTextureSlot.Occlusion);
        surface.ClearTextureOverride(SurfaceTextureSlot.Emissive);
    }

    /// <summary>
    /// Build a slot-based resolver for *Mesh3D.ProcessMaterial:
    /// procedural pixel sources resolve by composed name, path sources by path name,
    /// both against VkTexture entries registered in DictionaryVKTexture before Load.
    /// Missing entries return null and fall back to White.
    /// </summary>
    Func<Season.Controls.Surface, TextureSlot, VkTexture> BuildSurfaceTextureResolver(string meshName, long meshId, IList<Season.Controls.Surface> surfaces)
    {
        return (surface, slot) =>
        {
            var source = surface.GetTextureSource((SurfaceTextureSlot)slot);
            if (!source.HasValue)
                return null!;

            var name = source.Image != null
                ? ProcTextureName(meshName, meshId, surfaces.IndexOf(surface), (SurfaceTextureSlot)slot)
                : source.Path;

            lock (DictionaryVKTexture)
            {
                DictionaryVKTexture.TryGetValue(name, out var tex);
                return tex!;
            }
        };
    }

    /// <summary>Release procedural textures registered under the five composed slot names of one Surface
    /// (the caller must hold the DictionaryVKTexture lock).</summary>
    void ReleaseProcSurfaceTextures(string meshName, long meshId, int surfaceIndex)
    {
        ReleaseProcTexture(ProcTextureName(meshName, meshId, surfaceIndex, SurfaceTextureSlot.BaseColor));
        ReleaseProcTexture(ProcTextureName(meshName, meshId, surfaceIndex, SurfaceTextureSlot.Normal));
        ReleaseProcTexture(ProcTextureName(meshName, meshId, surfaceIndex, SurfaceTextureSlot.MetallicRoughness));
        ReleaseProcTexture(ProcTextureName(meshName, meshId, surfaceIndex, SurfaceTextureSlot.Occlusion));
        ReleaseProcTexture(ProcTextureName(meshName, meshId, surfaceIndex, SurfaceTextureSlot.Emissive));

        void ReleaseProcTexture(string name)
        {
            if (DictionaryVKTexture.TryGetValue(name, out var tex) && tex != null)
            {
                tex.Release();
                if (tex.RefCount == 0)
                    DictionaryVKTexture.Remove(name);
            }
        }
    }

    public async Task<bool> LoadMesh3D(Season.Controls.Mesh3D mesh)
    {
        lock (DictionaryMesh3D)
        {
            if (DictionaryMesh3D.ContainsKey((mesh.Name, mesh.ID)))
                return true;
        }

        // 1. Pre-resolve every texture source referenced by all Surfaces:
        //    procedural pixel sources are uploaded directly to the GPU through CreateFromDecoder
        //    with no disk writes,
        //    while path sources reuse EnsureVKTexture
        //    (empty sources are skipped automatically)
        for (int i = 0; i < mesh.Surfaces.Count; i++)
            EnsureSurfaceTextures(mesh.Name, mesh.ID, i, mesh.Surfaces[i]);

        // 2. Build VKMesh3D: resolve cached VkTextures by slot
        //    and use solid-color fallback when missing
        var vkMesh = new VKMesh3D(mesh.Name);
        vkMesh.Load(mesh, VKPrimitiveGroup.Camera, BuildSurfaceTextureResolver(mesh.Name, mesh.ID, mesh.Surfaces));

        // 3. Clear TextureOverride after Load completes
        //    under the one-shot consumption contract
        foreach (var surface in mesh.Surfaces)
            ClearSurfaceOverrides(surface);

        lock (DictionaryMesh3D)
        {
            if (!DictionaryMesh3D.ContainsKey((mesh.Name, mesh.ID)))
                DictionaryMesh3D.Add((mesh.Name, mesh.ID), vkMesh);
        }

        return true;
    }

    public void UpdateMesh3D(Season.Controls.Mesh3D mesh, float time)
    {
        VKMesh3D vkMesh = null!;
        lock (DictionaryMesh3D)
        {
            DictionaryMesh3D.TryGetValue((mesh.Name, mesh.ID), out vkMesh!);
        }
        vkMesh?.Update(mesh, time);
    }

    public void DrawMesh3D(Season.Controls.Mesh3D mesh)
    {
        if (mesh.Alpha == 0f)
            return;

        VKMesh3D vkMesh = null!;
        lock (DictionaryMesh3D)
        {
            DictionaryMesh3D.TryGetValue((mesh.Name, mesh.ID), out vkMesh!);
        }
        vkMesh?.Draw();
    }

    public void DisposeMesh3D(Season.Controls.Mesh3D mesh)
    {
        VKMesh3D vkMesh = null!;
        lock (DictionaryMesh3D)
        {
            var key = (mesh.Name, mesh.ID);
            if (DictionaryMesh3D.TryGetValue(key, out vkMesh!))
                DictionaryMesh3D.Remove(key);
        }

        // In-flight command buffers may still reference VB/IB/CB,
        // so release must go through timeline-gated deferred destruction
        // (Android tilers must not destroy in-flight resources immediately;
        // same contract as DX DisposeMesh3D)
        if (vkMesh != null)
            Vulkan.Device.EnqueueDeferredRelease(vkMesh.Dispose);

        // Release textures referenced by the Surface list
        // based on VkTexture reference counting
        lock (DictionaryVKTexture)
        {
            // Release procedural pixel-source textures slot by slot
            // (registered under composed names and owned by the mesh)
            for (int i = 0; i < mesh.Surfaces.Count; i++)
                ReleaseProcSurfaceTextures(mesh.Name, mesh.ID, i);

            foreach (var surface in mesh.Surfaces)
            {
                var path = surface.BaseColorTexturePath;
                if (string.IsNullOrEmpty(path)) continue;
                if (DictionaryVKTexture.TryGetValue(path, out var vkTex) && vkTex != null)
                {
                    vkTex.Release();
                    if (vkTex.RefCount == 0)
                        DictionaryVKTexture.Remove(path);
                }
            }
        }

        mesh.Ready = false;
    }

    public async Task<bool> LoadInstancedMesh3D(InstancedMesh3D mesh)
    {
        lock (DictionaryInstancedMesh3D)
        {
            if (DictionaryInstancedMesh3D.ContainsKey((mesh.Name, mesh.ID)))
                return true;
        }

        // 1. Pre-resolve all texture sources referenced by Surfaces
        //    (procedural pixel sources upload directly to the GPU with no disk writes;
        //    path sources reuse EnsureVKTexture)
        for (int i = 0; i < mesh.Surfaces.Count; i++)
            EnsureSurfaceTextures(mesh.Name, mesh.ID, i, mesh.Surfaces[i]);

        // 2. Build VKInstancedMesh3D: resolve cached VkTextures by slot
        //    and use solid-color fallback when missing
        var vkMesh = new VKInstancedMesh3D(mesh.Name);
        vkMesh.Load(mesh, VKPrimitiveGroup.Camera, BuildSurfaceTextureResolver(mesh.Name, mesh.ID, mesh.Surfaces));

        // 3. Clear TextureOverride after Load completes
        //    under the one-shot consumption contract
        foreach (var surface in mesh.Surfaces)
            ClearSurfaceOverrides(surface);

        lock (DictionaryInstancedMesh3D)
        {
            if (!DictionaryInstancedMesh3D.ContainsKey((mesh.Name, mesh.ID)))
                DictionaryInstancedMesh3D.Add((mesh.Name, mesh.ID), vkMesh);
            else
                vkMesh.Dispose();
        }

        return true;
    }

    public void UpdateInstancedMesh3D(InstancedMesh3D mesh, float time)
    {
        VKInstancedMesh3D vkMesh = null!;
        lock (DictionaryInstancedMesh3D)
        {
            DictionaryInstancedMesh3D.TryGetValue((mesh.Name, mesh.ID), out vkMesh!);
        }
        vkMesh?.Update(mesh, time);
    }

    public void DrawInstancedMesh3D(InstancedMesh3D mesh)
    {
        if (mesh.Alpha == 0f)
            return;

        VKInstancedMesh3D vkMesh = null!;
        lock (DictionaryInstancedMesh3D)
        {
            DictionaryInstancedMesh3D.TryGetValue((mesh.Name, mesh.ID), out vkMesh!);
        }
        vkMesh?.Draw();
    }

    public void DisposeInstancedMesh3D(InstancedMesh3D mesh)
    {
        VKInstancedMesh3D vkMesh = null!;
        lock (DictionaryInstancedMesh3D)
        {
            var key = (mesh.Name, mesh.ID);
            if (DictionaryInstancedMesh3D.TryGetValue(key, out vkMesh!))
                DictionaryInstancedMesh3D.Remove(key);
        }

        // Same contract as DisposeMesh3D:
        // instance buffers may still be referenced by in-flight command buffers,
        // so release must go through timeline-gated deferred destruction
        if (vkMesh != null)
            Vulkan.Device.EnqueueDeferredRelease(vkMesh.Dispose);

        lock (DictionaryVKTexture)
        {
            // Release procedural pixel-source textures slot by slot
            // (registered under composed names and owned by the mesh)
            for (int i = 0; i < mesh.Surfaces.Count; i++)
                ReleaseProcSurfaceTextures(mesh.Name, mesh.ID, i);

            foreach (var surface in mesh.Surfaces)
            {
                var path = surface.BaseColorTexturePath;
                if (string.IsNullOrEmpty(path))
                    continue;

                if (DictionaryVKTexture.TryGetValue(path, out var vkTex) && vkTex != null)
                {
                    vkTex.Release();
                    if (vkTex.RefCount == 0)
                        DictionaryVKTexture.Remove(path);
                }
            }
        }

        mesh.Ready = false;
    }

    // ── InstancedModel（GLB GPU Instancing）──

    public async Task<bool> LoadInstancedModel(InstancedModel model)
    {
        lock (DictionaryInstancedModel)
        {
            if (DictionaryInstancedModel.ContainsKey((model.ModelName, model.ID)))
            {
                return true;
            }
        }

        GetOrCreateSharedModelAsync(model.ModelName).ContinueWith(task =>
        {
            var template = task.GetAwaiter().GetResult();

            var wrapperModel = new Season.Controls.Model
            {
                Name = model.ModelName,
                Alpha = model.Alpha,
                MaterialColor = null,
                Unlit = false
            };

            var vkInstancedModel = new VKInstancedModel(model.ModelName);
            vkInstancedModel.Load(template, wrapperModel, VKPrimitiveGroup.Camera);

            // v2 picking: inject the instanced GltfAsset
            // so the node tree / animation / bone palette share the same source as instanced rendering
            model.Asset = vkInstancedModel.Asset;

            // 1-3: copy the shared-template local bounds back to the control
            // as the data source for instance-level sphere quick culling, once during load
            model.TemplateLocalBounds = template.Asset.Model.LocalBounds;
            // Unified transform convention: likewise copy back the raw bounds
            // as the data source for instance anchors / per-axis scaling, before animation expansion
            model.TemplateLocalBoundsRaw = template.Asset.Model.LocalBoundsRaw;

            var animNames = vkInstancedModel.GetAnimationNames();
            model.AnimationClipCount = animNames.Count;
            model.AnimationNames = animNames;

            lock (DictionaryInstancedModel)
            {
                if (!DictionaryInstancedModel.ContainsKey((model.ModelName, model.ID)))
                    DictionaryInstancedModel.Add((model.ModelName, model.ID), vkInstancedModel);
                else
                    // Same contract as DisposeInstancedModel:
                    // newly created GPU resources may still be in flight, so release them deferred
                    Vulkan.Device.EnqueueDeferredRelease(vkInstancedModel.Dispose);
            }
        });

        return true;
    }

    public void UpdateInstancedModel(InstancedModel model, float time)
    {
        VKInstancedModel vkModel = null!;
        lock (DictionaryInstancedModel)
        {
            DictionaryInstancedModel.TryGetValue((model.ModelName, model.ID), out vkModel!);
        }
        vkModel?.Update(model, time);
    }

    public void DrawInstancedModel(InstancedModel model)
    {
        if (model.Alpha == 0f)
            return;

        VKInstancedModel vkModel = null!;
        lock (DictionaryInstancedModel)
        {
            DictionaryInstancedModel.TryGetValue((model.ModelName, model.ID), out vkModel!);
        }
        vkModel?.Draw();
    }

    public void DisposeInstancedModel(InstancedModel model)
    {
        VKInstancedModel vkModel = null!;
        lock (DictionaryInstancedModel)
        {
            var key = (model.ModelName, model.ID);
            if (DictionaryInstancedModel.TryGetValue(key, out vkModel!))
                DictionaryInstancedModel.Remove(key);
        }

        // Same contract as DisposeMesh3D:
        // resources may still be referenced by in-flight command buffers,
        // so release must go through timeline-gated deferred destruction
        if (vkModel != null)
            Vulkan.Device.EnqueueDeferredRelease(vkModel.Dispose);
    }

    public void DisposeModel(Model model)
    {
        VKModel vkModel = null!;
        lock (DictionaryModel)
        {
            var key = (model.Name, model.ID);
            if (DictionaryModel.TryGetValue(key, out vkModel!))
                DictionaryModel.Remove(key);
        }

        // Same contract as DisposeMesh3D:
        // resources may still be referenced by in-flight command buffers,
        // so release must go through timeline-gated deferred destruction
        if (vkModel != null)
            Vulkan.Device.EnqueueDeferredRelease(vkModel.Dispose);

        // The shared-template cache (DictionaryModelResource) is shared by Model controls
        // with the same name and does not participate in per-control release
        // (same contract shape as DisposeInstancedModel).
        model.Ready = false;
    }

    public void DisposeSprite2D(Sprite2D sprite)
    {
        VKSprite2D vkSprite2D = null!;

        lock (DictionarySprite)
        {
            var key = (sprite.Name, sprite.ID);
            if (DictionarySprite.TryGetValue(key, out vkSprite2D!))
            {
                DictionarySprite.Remove(key);
            }
        }

        if (vkSprite2D != null)
        {
            vkSprite2D.SpriteRef = null; // Clear the reference
            vkSprite2D.Dispose();

            // Release the shared texture only if the Sprite was actually loaded
            // and holds a texture reference
            lock (DictionaryVKTexture)
            {
                if (DictionaryVKTexture.TryGetValue(sprite.Name, out var vkTex) && vkTex != null)
                {
                    vkTex.Release();
                    if (vkTex.RefCount == 0)
                    {
                        DictionaryVKTexture.Remove(sprite.Name);
                    }
                }
            }
        }

        sprite.Ready = false;
    }

    /// <summary>
    /// Trigger one batched texture upload on the transfer queue,
    /// equivalent to the DX-side textureUploadBatch.Execute(commandList, queue).
    /// On Vulkan, TextureUploadBatch.Execute() already encapsulates
    /// transfer command buffer recording + Submit + Signal internally.
    /// </summary>
    public void ExecuteUpload()
    {
        Vulkan.Device.TextureUploadBatch?.Execute();
    }

    // Shape (procedural geometry)

    public async Task<bool> LoadShape(Season.Controls.Shape shape)
    {
        // Width/Height may still be null when AddControl runs:
        // casting (int)(float?)null would throw and make Load fail.
        int shapeW = Math.Max(1, (int)(shape.Width ?? 1f));
        int shapeH = Math.Max(1, (int)(shape.Height ?? 1f));

        // RectFrame textures are determined by the tuple (Type, W, H, Border),
        // while Border is always 0 for the other types.
        // Clamp Border to [1, min(W, H)/2], matching CreateImageRectFrame,
        // to avoid duplicate keys producing multiple copies of the same texture.
        int shapeBorder = shape.Type == Season.Controls.ShapeType.RectFrame
            ? Math.Clamp((int)shape.Border, 1, Math.Min(shapeW, shapeH) / 2)
            : 0;

        var textureKey = shape.Type == Season.Controls.ShapeType.Dot
            ? (shape.Type, 1, 1, 0)
            : (shape.Type, shapeW, shapeH, shapeBorder);
        var instanceKey = (shape.Type, shape.ID);

        VKSprite2D vkSprite2D = null;

        lock (DictionaryShape)
        {
            if (shape.IsDisposed) return false;

            // Historical failures may have cached a null entry:
            // treat it as missing, remove it, and rebuild
            if (DictionaryShape.TryGetValue(instanceKey, out vkSprite2D)
                && (vkSprite2D == null || vkSprite2D.VKTexture == null))
            {
                DictionaryShape.Remove(instanceKey);
                vkSprite2D = null;
            }

            if (vkSprite2D != null)
            {
                shape.OriginWidth = (int)vkSprite2D.VKTexture.Width;
                shape.OriginHeight = (int)vkSprite2D.VKTexture.Height;
            }
            else
            {
                // Get or create the shared shape texture
                // cached by Type + Width + Height
                VkTexture vkTexture = null;

                lock (DictionaryShapeTexture)
                {
                    if (DictionaryShapeTexture.TryGetValue(textureKey, out vkTexture!))
                    {

                    }
                    else
                    {
                        var imageDecoder = Season.Models.ImageUtils.CreateShapeImage(shape.Type, shapeW, shapeH, shapeBorder);

                        if (imageDecoder != null)
                        {
                            vkTexture = new VkTexture(imageDecoder);
                            ExecuteUpload();
                        }

                        if (vkTexture == null)
                        {
                            DeviceServices.BaseApp.AddLog(LogType.Backend, $"{DateTime.UtcNow} LoadShape VkTexture=null {shape.Type}");
                        }
                        else
                        {
                            // Cache only on success to avoid polluting future requests for the same key with null
                            DictionaryShapeTexture[textureKey] = vkTexture;
                        }
                    }
                }

                if (vkTexture == null)
                {
                    // Shared-texture creation failed:
                    // do not register an empty entry, and let Load return false
                    // so the caller can locate the issue from logs
                    return false;
                }

                try
                {
                    vkSprite2D = new VKSprite2D(vkTexture);

                    shape.OriginWidth = (int)vkSprite2D.VKTexture.Width;
                    shape.OriginHeight = (int)vkSprite2D.VKTexture.Height;
                }
                catch (Exception ex)
                {
                    DeviceServices.BaseApp.AddLog(LogType.Error, $"{DateTime.UtcNow} LoadShape new VKSprite2D {shape.Type} {ex}");

                    return false;
                }

                lock (DictionaryShape)
                {
                    if (!DictionaryShape.ContainsKey(instanceKey))
                    {
                        DictionaryShape.Add(instanceKey, vkSprite2D);
                    }
                }
            }
        }

        return true;
    }

    public void UpdateShape(Season.Controls.Shape shape)
    {
        VKSprite2D? vkSprite = null;

        lock (DictionaryShape)
        {
            DictionaryShape.TryGetValue((shape.Type, shape.ID), out vkSprite);
        }

        if (vkSprite == null || vkSprite.VKTexture == null)
            return;

        shape.Ready = true;

        // Texture replacement
        if (shape.TextureOverride.HasValue)
        {
            var source = shape.TextureOverride;
            shape.TextureOverride = default;
            ReplaceSpriteTexture(vkSprite, source);
        }

        if (shape.Changed)
        {
            shape.Changed = false;
            vkSprite.SpriteRef = shape;
            vkSprite.Update();
        }
    }

    public void DrawShape(Season.Controls.Shape shape)
    {
        VKSprite2D? vkSprite = null;

        lock (DictionaryShape)
        {
            DictionaryShape.TryGetValue((shape.Type, shape.ID), out vkSprite);
        }

        if (vkSprite == null || vkSprite.VKTexture == null)
            return;

        vkSprite.Draw();
    }

    public void DisposeShape(Season.Controls.Shape shape)
    {
        VKSprite2D? vkSprite = null;

        lock (DictionaryShape)
        {
            var key = (shape.Type, shape.ID);
            if (DictionaryShape.TryGetValue(key, out vkSprite))
                DictionaryShape.Remove(key);
        }

        vkSprite?.Dispose();

        shape.Ready = false;
    }

    // Pass orchestration (Step 1) / offscreen rendering (Step 2):
    // delegated to Vulkan.Device, mirroring Windows/Graphics.cs
    public Season.Rendering.RenderTarget CreateRenderTarget(in Season.Rendering.RenderTargetDesc desc) => Vulkan.Device.CreateRenderTarget(desc);

    public void BeginPass(in Season.Rendering.PassDesc desc) => Vulkan.Device.BeginPass(desc);

    public void EndPass() => Vulkan.Device.EndPass();

    VKRenderTarget EnsureOutlineMaskTarget()
    {
        if (_outlineMaskTarget != null)
            return _outlineMaskTarget;

        _outlineMaskTarget = (VKRenderTarget)CreateRenderTarget(new Season.Rendering.RenderTargetDesc
        {
            ColorFormat = Season.Rendering.RtFormat.BackbufferCompatible,
            MatchBackbufferSize = true,
            SampleCount = 1,
        });
        return _outlineMaskTarget;
    }

    bool TryAccumulateOutline2D(VKPrimitiveGroup group)
    {
        if (group == null || !group.Outline2DActive)
            return false;

        // Color is carried per pixel inside the mask by each group
        // (for multi-color frames, see VKPrimitiveGroup.DrawOutlineMask /
        // BlitPipeline.RecordOutlineComposite).
        // At the frame level, only width is aggregated
        // by taking the maximum, so the widest outline remains fully visible.
        _outline2DFrameActive = true;
        _outline2DFrameWidth = MathF.Max(_outline2DFrameWidth, group.Outline2DMaskWidth);

        return true;
    }

    public void RenderOutlineMask()
    {
        _outline2DFrameActive = false;
        _outline2DFrameWidth = 0f;

        var drawGroups = new List<VKPrimitiveGroup>();

        lock (DictionaryModel)
        {
            foreach (var pair in DictionaryModel)
            {
                if (TryAccumulateOutline2D(pair.Value))
                    drawGroups.Add(pair.Value);
            }
        }

        lock (DictionaryMesh3D)
        {
            foreach (var pair in DictionaryMesh3D)
            {
                if (TryAccumulateOutline2D(pair.Value))
                    drawGroups.Add(pair.Value);
            }
        }

        // Instanced controls (InstancedMesh3D / InstancedModel):
        // Outline2D also supports per-instance masks.
        // Active state is aggregated during the platform Update phase
        // from each instance / host Highlight.Outline2D by VKInstancedPrimitiveGroup.
        lock (DictionaryInstancedMesh3D)
        {
            foreach (var pair in DictionaryInstancedMesh3D)
            {
                if (TryAccumulateOutline2D(pair.Value))
                    drawGroups.Add(pair.Value);
            }
        }

        lock (DictionaryInstancedModel)
        {
            foreach (var pair in DictionaryInstancedModel)
            {
                if (TryAccumulateOutline2D(pair.Value))
                    drawGroups.Add(pair.Value);
            }
        }

        if (!_outline2DFrameActive || drawGroups.Count == 0)
            return;

        var maskRT = EnsureOutlineMaskTarget();

        BeginPass(new Season.Rendering.PassDesc
        {
            Id = Season.Rendering.RenderPassId.OutlineMask,
            ColorTarget = maskRT,
            DepthTarget = Season.Rendering.FrameSchedule.SceneDepth,
            ClearColor = Vector4.Zero,
            ClearColorEnable = true,
            ClearDepthEnable = false,
            StoreDepth = false,
        });

        for (int i = 0; i < drawGroups.Count; i++)
        {
            drawGroups[i].DrawOutlineMask();
        }

        EndPass();
    }

    /// <summary>2-3 contract clause 12:
    /// the scene source is resolved and forwarded through FrameSchedule.SceneColorOverride
    /// (the resolve output under the TAA tier).
    /// Under the FXAA tier this entry point has already degenerated into an FXAA resolve
    /// because composition is finished inside Post,
    /// so scene override only takes effect in RenderPostPass.
    /// Phase 4: when Outline2D is active, also forward the mask RT
    /// and the frame-level maximum width for on-screen composition inside FinalBlit.</summary>
    public void BlitToBackbuffer(Season.Rendering.RenderTarget src)
    {
        // 2-1 Step D: when the source is the LDR PostColor produced by the post uber pass
        // (luma stored in alpha), present through the FXAA variant.
        // This is mutually exclusive with tonemap/bloom because composition already finished in Post,
        // mirroring Windows/Graphics.cs.
        if (ReferenceEquals(src, Season.Rendering.FrameSchedule.PostColor))
        {
            Vulkan.Device.BlitToBackbuffer(src, null, fxaa: true,
                outlineMask: _outline2DFrameActive ? _outlineMaskTarget : null,
                outlineWidth: _outline2DFrameWidth);
            return;
        }

        Vulkan.Device.BlitToBackbuffer(src, ResolveBloomTexture(), aoTex: ResolveAoTexture(),
            sceneTex: ResolveSceneOverrideTexture(),
            outlineMask: _outline2DFrameActive ? _outlineMaskTarget : null,
            outlineWidth: _outline2DFrameWidth);
    }

    /// <summary>2-1 Step D: Post pass body
    /// (FrameSchedule.RenderPost callback, with FXAA tier and PostColor registered as a pair):
    /// the uber pass composes tonemap(+bloom) -> LDR PostColor and bakes luma into alpha.
    /// After composition moved downstream, FinalBlit degenerates into an FXAA resolve;
    /// see the RenderQuality 1-4 contract 1 revision, mirroring Windows/Graphics.cs.
    /// 2-2 Step C: AO is forwarded at the same point.
    /// 2-3 clause 12: scene override is also forwarded at the same point.</summary>
    internal void RenderPostPass(Season.Basic.IGraphics g, Season.Rendering.RenderTarget sceneColor)
        => Vulkan.Device.RenderPostUber(sceneColor, ResolveBloomTexture(), ResolveAoTexture(),
            ResolveSceneOverrideTexture());

    /// <summary>Resolve the bloom-chain output from the instance dictionary through
    /// FrameSchedule.BloomTexture (null means no bloom).</summary>
    VkTexture? ResolveBloomTexture()
    {
        var bloomName = Season.Rendering.FrameSchedule.BloomTexture;
        if (bloomName == null)
            return null;
        lock (DictionaryVKTexture)
        {
            DictionaryVKTexture.TryGetValue(bloomName, out var bloom);
            return bloom;
        }
    }

    /// <summary>2-2 Step C: resolve the GTAO output from the instance dictionary through
    /// FrameSchedule.AoTexture (null means no AO).</summary>
    VkTexture? ResolveAoTexture()
    {
        var aoName = Season.Rendering.FrameSchedule.AoTexture;
        if (aoName == null)
            return null;
        lock (DictionaryVKTexture)
        {
            DictionaryVKTexture.TryGetValue(aoName, out var ao);
            return ao;
        }
    }

    /// <summary>2-3 contract clause 12: resolve the TAA resolve output from the instance dictionary
    /// through FrameSchedule.SceneColorOverride
    /// (null means no override, and Device falls back to the SceneColor RT with zero residue).</summary>
    VkTexture? ResolveSceneOverrideTexture()
    {
        var sceneName = Season.Rendering.FrameSchedule.SceneColorOverride;
        if (sceneName == null)
            return null;
        lock (DictionaryVKTexture)
        {
            DictionaryVKTexture.TryGetValue(sceneName, out var scene);
            return scene;
        }
    }

    /// <summary>2-4 clause 10: resolve the DDGI irradiance atlas
    /// (compute 2D texture) from the singleton instance dictionary by full name.
    /// Static entry point: called once per frame by VKPrimitiveGroup.SetLighting,
    /// mirroring bloom/AO resolve semantics.
    /// Returns null when not ready or when the name is missing,
    /// and consumers then fall back to Device.White on binding 17.</summary>
    internal static VkTexture? FindDdgiAtlas(string name)
    {
        // The contract says "missing name must always return null":
        // Dictionary<string, ...> throws ArgumentNullException on a null key,
        // so calls that may pass a null FrameSchedule name must be guarded here
        // instead of relying on every caller to do it.
        if (name == null)
            return null;
        if (Season.Basic.Graphics.Instance is Graphics g)
        {
            lock (g.DictionaryVKTexture)
            {
                g.DictionaryVKTexture.TryGetValue(name, out var atlas);
                return atlas;
            }
        }
        return null;
    }

    // 1-6 Compute infrastructure
    // kernel registration model, see the contract in IGraphics/Compute.cs
    // Dispatches are recorded into the current frame's GraphicsCommandBuffer
    // (already begun in BeforeRender).
    // The FrameStart phase occurs inside FrameSchedule.Execute and before the first BeginPass
    // (InRenderPass=false).
    // All layout barriers are centralized inside DispatchCompute and recorded outside render passes,
    // satisfying Android tiler constraints
    // (see the comment on Texture.EnsureReadyForRendering).

    public bool ComputeSupported => Vulkan.Device.LogicalDevice.Handle != 0;

    /// <summary>Centralized parameter-level validation
    /// using the same rules across all four backends:
    /// missing GLSL source degrades by returning null;
    /// binding declaration violations throw exceptions as programming errors;
    /// glslang compilation / pipeline creation failures are logged and return null
    /// for graceful registration-time degradation with no platform residue.</summary>
    public Season.Rendering.ComputeKernel? CreateComputeKernel(Season.Rendering.ComputeKernelDesc desc)
    {
        if (!ComputeSupported || string.IsNullOrEmpty(desc.Source.Glsl))
            return null;

        var bindings = desc.Bindings;
        desc.ValidateWorkgroupSize();
        for (int i = 0; i < bindings.Length; i++)
        {
            if (bindings[i].Type != Season.Rendering.ComputeBindingType.Params)
                continue;
            if (i != 0)
                throw new ArgumentException($"[CreateComputeKernel] '{desc.Name}': Params must be placed at Bindings[0].");
            var size = bindings[i].SizeInBytes;
            if (size == 0 || size % 16 != 0 || size > 128)
                throw new ArgumentException($"[CreateComputeKernel] '{desc.Name}': Params must be 16-byte aligned and <= 128B (got {size}).");
        }

        // 1-8: the concrete format of storage-write slots must actually be supported by this device
        // as STORAGE_IMAGE.
        // Vulkan's mandatory support table does not include
        // R16_SFLOAT / R8_UNORM / R16G16_SFLOAT,
        // so runtime vkGetPhysicalDeviceFormatProperties checks are required.
        // When unsupported, return null for graceful registration-time degradation
        // instead of silently falling back to a wider format:
        // in GLSL/SPIR-V, the format qualifier in
        // `layout(r16f) writeonly image3D`
        // must exactly match the image-view format under Vulkan rules.
        // Silent format substitution would immediately become a layout/shader mismatch
        // (validation error + undefined results).
        // Enabling a real downgrade chain would require paired edits to the effect GLSL format qualifiers,
        // the same constraint faced by WebGPU's _mapStorageFormat,
        // so both backends take the same conclusion.
        // Note: SampledTexture3D slots do not carry StorageFormat
        // because the concrete format is known only when the texture is created.
        // Their SampledImageFilterLinearBit validation happens in CreateComputeTexture3D.
        for (int i = 0; i < bindings.Length; i++)
        {
            if (bindings[i].Type != Season.Rendering.ComputeBindingType.StorageTextureWrite
                && bindings[i].Type != Season.Rendering.ComputeBindingType.StorageTexture3DWrite)
                continue;
            var fmt = VkTexture.MapComputeFormat(bindings[i].StorageFormat);
            if (!CheckComputeFormatSupport(fmt, needLinearFilter: false))
            {
                DeviceServices.BaseApp.AddLog(LogType.Backend, $"{DateTime.UtcNow} [CreateComputeKernel] '{desc.Name}': "
                    + $"binding {i} ({bindings[i].Type}) format {bindings[i].StorageFormat} -> {fmt} "
                    + "is not supported as STORAGE_IMAGE on this device; degrading during registration (effect not registered).");
                return null;
            }
        }

        try
        {
            return new VKComputeKernel(desc);
        }
        catch (Exception ex)
        {
            DeviceServices.BaseApp.AddLog(LogType.Error, $"{DateTime.UtcNow} [CreateComputeKernel] '{desc.Name}' compile/create failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 1-8: query whether this device supports a concrete format for compute usage
    /// through vkGetPhysicalDeviceFormatProperties on the OptimalTiling plane.
    /// Vulkan's mandatory support table does not include
    /// R16_SFLOAT / R8_UNORM / R16G16_SFLOAT as STORAGE_IMAGE,
    /// so these three new formats must be checked at runtime.
    ///
    /// <paramref name="needLinearFilter"/>:
    /// textures used by 3D sampled slots also require SampledImageFilterLinearBit.
    /// The immutable StaticSampler declared in the set layout is always Linear,
    /// so sampling a format without linear-filter support is a validation error,
    /// not a silent downgrade to nearest.
    ///
    /// Unsupported formats always degrade during registration
    /// (log + do not register), with no silent format substitution:
    /// in GLSL/SPIR-V, the format qualifier in
    /// `layout(r16f) writeonly image3D`
    /// must exactly match the image-view format under Vulkan rules.
    /// Changing formats means a layout/shader mismatch.
    /// A real downgrade chain would require paired edits to the effect GLSL format qualifiers,
    /// the same constraint faced by WebGPU's _mapStorageFormat,
    /// so both backends take the same conclusion.
    /// </summary>
    static bool CheckComputeFormatSupport(Silk.NET.Vulkan.Format format, bool needLinearFilter)
    {
        Vulkan.Device.Vk.GetPhysicalDeviceFormatProperties(Vulkan.Device.PhysicalDevice, format, out var props);
        var f = props.OptimalTilingFeatures;
        if ((f & Silk.NET.Vulkan.FormatFeatureFlags.StorageImageBit) == 0)
            return false;
        if (needLinearFilter
            && (f & Silk.NET.Vulkan.FormatFeatureFlags.SampledImageFilterLinearBit) == 0)
            return false;
        return true;
    }

    /// <summary>Register storage textures into DictionaryVKTexture by name
    /// (LoadSprite2D will hit them and AddRef without going through file loading),
    /// so Sprite2D can consume them by name with no code changes,
    /// matching the semantics of DictionaryDXTexture registration on the DX side.
    /// 2-1 Step D: adds rgba16float support for the bloom chain, aligned with D3D12.
    /// 1-8: format mapping now goes through VkTexture.MapComputeFormat
    /// as the single source of truth shared with the 3D path.</summary>
    public void CreateComputeTexture(string name, uint width, uint height,
        Season.Rendering.ComputeStorageFormat format = Season.Rendering.ComputeStorageFormat.Rgba8Unorm)
    {
        var vkFormat = VkTexture.MapComputeFormat(format);
        lock (DictionaryVKTexture)
        {
            if (DictionaryVKTexture.TryGetValue(name, out var existing))
            {
                // Recreate in place on size mismatch
                // so the C# object identity stays stable and Sprite2D AddRef references remain valid
                if (existing.Width != width || existing.Height != height)
                    existing.RecreateComputeStorage(width, height);
                return;
            }
            DictionaryVKTexture.Add(name, VkTexture.CreateComputeStorage(name, width, height, vkFormat));
        }
    }

    /// <summary>1-8: 3D storage textures go into the dedicated
    /// <see cref="Vulkan.VKTexture3D"/> registry and are deliberately not written into DictionaryVKTexture.
    /// Entries in DictionaryVKTexture are consumed by Sprite2D/LoadSprite2D and material paths by name,
    /// so writing 3D textures there would hand those 2D paths a Type3D view
    /// (the 1-7 cubemap path already established the same isolation rule).
    /// Visualization of 3D volumes must go through an effect-side 3D -> 2D slicing kernel.
    /// Both format capability and size capability validation are centralized here.
    /// On failure, only log and do not create the texture
    /// (DispatchCompute then misses it by name and skips this frame,
    /// matching existing "resource not ready" semantics),
    /// preserving graceful registration-time degradation.</summary>
    public void CreateComputeTexture3D(string name, uint width, uint height, uint depth,
        Season.Rendering.ComputeStorageFormat format = Season.Rendering.ComputeStorageFormat.Rgba8Unorm)
    {
        if (!ComputeSupported) return;

        var vkFormat = Vulkan.VKTexture3D.MapComputeFormat(format);
        // Sampled slots always go through the immutable Linear sampler,
        // so both capability bits are required
        if (!CheckComputeFormatSupport(vkFormat, needLinearFilter: true))
        {
            DeviceServices.BaseApp.AddLog(LogType.Backend, $"{DateTime.UtcNow} [CreateComputeTexture3D] '{name}': "
                + $"format {format} -> {vkFormat} lacks STORAGE_IMAGE or linear-filter support; not creating texture.");
            return;
        }

        // maxImageDimension3D only guarantees a minimum of 256 by contract,
        // so reject oversized textures explicitly instead of letting the driver fail
        Vulkan.Device.Vk.GetPhysicalDeviceProperties(Vulkan.Device.PhysicalDevice, out var devProps);
        var limit = devProps.Limits.MaxImageDimension3D;
        if (width > limit || height > limit || depth > limit)
        {
            DeviceServices.BaseApp.AddLog(LogType.Backend, $"{DateTime.UtcNow} [CreateComputeTexture3D] '{name}': "
                + $"{width}x{height}x{depth} exceeds this device's maxImageDimension3D={limit}; not creating texture.");
            return;
        }

        try
        {
            Vulkan.VKTexture3D.CreateOrUpdate(name, width, height, depth, vkFormat);
        }
        catch (Exception ex)
        {
            DeviceServices.BaseApp.AddLog(LogType.Error, $"{DateTime.UtcNow} [CreateComputeTexture3D] '{name}' "
                + $"{width}x{height}x{depth} creation failed: {ex.Message}");
        }
    }

    public Season.Rendering.StorageBuffer CreateStorageBuffer(uint sizeInBytes)
        => new VKStorageBuffer(sizeInBytes);

    /// <summary>1-8: upload constant blocks.
    /// VKStorageBuffer is DEVICE_LOCAL and unmapped,
    /// so uploads go through staging + vkCmdCopyBuffer.
    /// They are recorded into the current GraphicsCommandBuffer
    /// and must happen outside render passes
    /// (Android tiler restriction, same as DispatchCompute barrier centralization).
    /// The contract also requires this method to be called only from the frame-loop thread and outside passes.
    /// 2-4 Step 0: staging is implemented as an N-buffered ring owned by the buffer itself
    /// (one slot per FrameIndex, built once and kept resident),
    /// so per-frame calls allocate nothing and avoid in-flight frame races;
    /// see VKStorageBuffer.GetStagingForCurrentFrame.</summary>
    public void UpdateStorageBuffer(Season.Rendering.StorageBuffer buffer, ReadOnlySpan<byte> data)
    {
        if (!ComputeSupported || data.Length == 0) return;
        var cmd = Vulkan.Device.GraphicsCommandBuffer;
        if (cmd.Handle == 0) return;

        var vkBuffer = (VKStorageBuffer)buffer;
        var dst = vkBuffer.Buffer;
        ulong size = Math.Min((ulong)data.Length, dst.Size);
        if (size == 0) return;

        var staging = vkBuffer.TryGetStagingForCurrentFrame();
        if (staging.Buffer.Handle == 0)
            return;

        var vk = Vulkan.Device.Vk;
        void* p;
        if (vk.MapMemory(Vulkan.Device.LogicalDevice, staging.Memory, 0, size, 0, &p) != VkResult.Success)
        {
            DeviceServices.BaseApp.AddLog(LogType.Error, $"{DateTime.UtcNow} [UpdateStorageBuffer] vkMapMemory failed; skipping this upload.");
            return;
        }
        fixed (byte* pSrc = data)
        {
            Unsafe.CopyBlock(p, pSrc, (uint)size);
        }
        vk.UnmapMemory(Vulkan.Device.LogicalDevice, staging.Memory);

        var region = new Silk.NET.Vulkan.BufferCopy { SrcOffset = 0, DstOffset = 0, Size = size };
        vk.CmdCopyBuffer(cmd, staging.Buffer, dst.Buffer, 1, &region);

        // transfer write -> later compute/vertex reads
        // same-frame dispatch may consume it immediately, so visibility must be synchronized explicitly
        var barrier = new VkBufferMemoryBarrier
        {
            SType = VkStructureType.BufferMemoryBarrier,
            SrcAccessMask = VkAccessFlags.TransferWriteBit,
            DstAccessMask = VkAccessFlags.ShaderReadBit,
            SrcQueueFamilyIndex = SVk.QueueFamilyIgnored,
            DstQueueFamilyIndex = SVk.QueueFamilyIgnored,
            Buffer = dst.Buffer,
            Offset = 0,
            Size = SVk.WholeSize,
        };
        vk.CmdPipelineBarrier(cmd,
            VkPipelineStageFlags.TransferBit,
            VkPipelineStageFlags.ComputeShaderBit | VkPipelineStageFlags.VertexShaderBit,
            0, 0, null, 1, &barrier, 0, null);
    }

    // 1-7 Cubemap
    // see the contract in IGraphics / Season.Rendering.TextureCube

    public bool TextureCubeSupported => Vulkan.Device.LogicalDevice.Handle != 0;

    /// <summary>Face order is +X, -X, +Y, -Y, +Z, -Z
    /// (array layers 0..5 respectively).
    /// The shared layer already validates that all six faces are same-size squares.
    /// On creation failure, log and return null
    /// for graceful degradation to the 1-2 constant ambient light path,
    /// matching D3D12-side behavior.</summary>
    public Season.Rendering.TextureCube CreateTextureCube(string name, int size,
        Season.Rendering.TextureCubeFormat format, INativeImageDecoder[] faces)
    {
        if (!TextureCubeSupported) return null;
        try
        {
            var cube = Vulkan.VKTextureCube.CreateFromDecoders(name, size, format, faces);
            if (cube == null) return null;
            return new Season.Rendering.TextureCube
            {
                Name = name,
                Size = size,
                Format = format,
                Ready = true,
            };
        }
        catch (Exception ex)
        {
            DeviceServices.BaseApp.AddLog(LogType.Error, $"{DateTime.UtcNow} [CreateTextureCube] '{name}' creation failed: {ex.Message}");
            return null;
        }
    }

    public void DispatchCompute(in Season.Rendering.ComputeDispatchArgs args)
    {
        var cmd = Vulkan.Device.GraphicsCommandBuffer;
        if (cmd.Handle == 0)
            return;

        var vk = Vulkan.Device.Vk;
        var kernel = (VKComputeKernel)args.Kernel;
        var bindings = kernel.Desc.Bindings;

        Vulkan.Device.PushDebugGroup(kernel.LabelZ);

        // Dedicated descriptor set for this dispatch:
        // multiple dispatches within the same frame acquire sets one by one.
        // vkUpdateDescriptorSets takes effect immediately,
        // so reusing a single set would make earlier recorded dispatches read the final overwrite.
        // The ring fence at the end of AfterRender guarantees the full ring of this frame slot has retired,
        // so overwriting within the current frame is safe.
        var set = kernel.AcquireSet();
        int n = bindings.Length;
        var writes = stackalloc VkWriteDescriptorSet[n == 0 ? 1 : n];
        var imageInfos = stackalloc VkDescriptorImageInfo[n == 0 ? 1 : n];
        var bufferInfos = stackalloc VkDescriptorBufferInfo[n == 0 ? 1 : n];
        uint writeCount = 0;

        // Per binding: resolve resources -> record pre-layout barriers -> assemble descriptor writes.
        // Resolved results are stored in kernel scratch slots for post-dispatch cleanup with zero allocation.
        // If a resource is not ready
        // (name not registered / upload not completed),
        // skip this frame's dispatch.
        // Any already recorded barriers are harmless.
        int r = 0;
        for (int i = 0; i < n; i++)
        {
            kernel.ResolvedScratch[i] = null;
            if (bindings[i].Type == Season.Rendering.ComputeBindingType.Params)
                continue;

            ref readonly var res = ref args.Resources[r++];

            if (res.Buffer is VKStorageBuffer buffer)
            {
                bufferInfos[i] = new VkDescriptorBufferInfo
                {
                    Buffer = buffer.Buffer.Buffer,
                    Offset = 0,
                    Range = buffer.Buffer.Size,
                };
                writes[writeCount++] = new VkWriteDescriptorSet
                {
                    SType = VkStructureType.WriteDescriptorSet,
                    DstSet = set,
                    DstBinding = (uint)i,
                    DescriptorCount = 1,
                    DescriptorType = VkDescriptorType.StorageBuffer,
                    PBufferInfo = &bufferInfos[i],
                };
                kernel.ResolvedScratch[i] = buffer;
                continue;
            }

            // 2-1 Step D: offscreen RT used as sampled input
            // (for example bloom prefilter reading SceneColor).
            // At the AfterScene phase, Scene RP has already transitioned finalLayout
            // to ShaderReadOnlyOptimal and visibility is closed by subpass dependencies,
            // so ColorView can be bound directly with zero extra barriers.
            // No post-dispatch cleanup is needed, so ResolvedScratch is not populated.
            // 2-2 Step C: DepthTexture resolves the DepthView of a depth-only RT
            // (SceneDepth, contract clause 3).
            // The dual-target Scene RP also ends in ShaderReadOnlyOptimal with zero extra barriers.
            if (res.Target is VKRenderTarget targetRT)
            {
                bool wantDepth = bindings[i].Type == Season.Rendering.ComputeBindingType.DepthTexture;
                if (wantDepth ? targetRT.HasColor : !targetRT.HasColor)
                {
                    // Shape does not match the binding declaration
                    // DepthTexture requires a depth-only RT, while SampledTexture requires a color plane
                    Vulkan.Device.PopDebugGroup();
                    return;
                }
                imageInfos[i] = new VkDescriptorImageInfo
                {
                    ImageView = wantDepth ? targetRT.DepthView : targetRT.ColorView,
                    ImageLayout = VkImageLayout.ShaderReadOnlyOptimal,
                    // Leave Sampler empty:
                    // the set layout declares immutable StaticSampler / StaticPointSampler
                };
                writes[writeCount++] = new VkWriteDescriptorSet
                {
                    SType = VkStructureType.WriteDescriptorSet,
                    DstSet = set,
                    DstBinding = (uint)i,
                    DescriptorCount = 1,
                    DescriptorType = VkDescriptorType.CombinedImageSampler,
                    PImageInfo = &imageInfos[i],
                };
                continue;
            }

            // 1-8: 3D bindings resolve through the dedicated VKTexture3D registry.
            // DictionaryVKTexture is 2D-only by semantics,
            // and mixing the two would silently bind the wrong dimension.
            // Layout rules are isomorphic to the 2D case:
            // write slots -> General, sampled slots -> ShaderReadOnlyOptimal.
            if (bindings[i].Type == Season.Rendering.ComputeBindingType.SampledTexture3D
                || bindings[i].Type == Season.Rendering.ComputeBindingType.StorageTexture3DWrite)
            {
                var tex3d = res.TextureName != null ? Vulkan.VKTexture3D.Find(res.TextureName) : null;
                if (tex3d == null || !System.Threading.Volatile.Read(ref tex3d.Ready))
                {
                    Vulkan.Device.PopDebugGroup();
                    return;
                }

                bool write3d = bindings[i].Type == Season.Rendering.ComputeBindingType.StorageTexture3DWrite;
                var layout3d = write3d ? VkImageLayout.General : VkImageLayout.ShaderReadOnlyOptimal;
                if (write3d)
                    // Undefined (first frame) / ShaderReadOnly (later frames) -> General:
                    // writing must wait until compute sampling from the previous frame is finished
                    tex3d.TransitionTo(cmd, layout3d,
                        VkPipelineStageFlags.ComputeShaderBit, VkPipelineStageFlags.ComputeShaderBit,
                        VkAccessFlags.ShaderReadBit, VkAccessFlags.ShaderWriteBit);
                else
                    // Content is written by an earlier kernel in the same frame
                    // (there is no upload chain),
                    // so srcAccess uses ShaderWrite to close the write -> read dependency
                    tex3d.TransitionTo(cmd, layout3d,
                        VkPipelineStageFlags.ComputeShaderBit, VkPipelineStageFlags.ComputeShaderBit,
                        VkAccessFlags.ShaderWriteBit, VkAccessFlags.ShaderReadBit);

                imageInfos[i] = new VkDescriptorImageInfo
                {
                    ImageView = tex3d.View,
                    ImageLayout = layout3d,
                    // Leave Sampler empty:
                    // sampled-slot set layout declares immutable StaticSampler
                    // (ClampToEdge + Linear on all three axes)
                };
                writes[writeCount++] = new VkWriteDescriptorSet
                {
                    SType = VkStructureType.WriteDescriptorSet,
                    DstSet = set,
                    DstBinding = (uint)i,
                    DescriptorCount = 1,
                    DescriptorType = write3d ? VkDescriptorType.StorageImage : VkDescriptorType.CombinedImageSampler,
                    PImageInfo = &imageInfos[i],
                };
                kernel.ResolvedScratch[i] = tex3d;
                continue;
            }

            VkTexture? tex = null;
            if (res.TextureName != null)
            {
                lock (DictionaryVKTexture)
                {
                    DictionaryVKTexture.TryGetValue(res.TextureName, out tex);
                }
            }
            if (tex == null || !System.Threading.Volatile.Read(ref tex.Ready))
            {
                Vulkan.Device.PopDebugGroup();
                return;
            }

            if (bindings[i].Type == Season.Rendering.ComputeBindingType.StorageTextureWrite)
            {
                // Undefined (first frame) / ShaderReadOnly (later frames) -> General:
                // writing must wait until previous-frame draw/compute sampling is finished
                tex.TransitionTo(cmd, VkImageLayout.General,
                    VkPipelineStageFlags.FragmentShaderBit | VkPipelineStageFlags.ComputeShaderBit,
                    VkPipelineStageFlags.ComputeShaderBit,
                    VkAccessFlags.ShaderReadBit, VkAccessFlags.ShaderWriteBit);
                imageInfos[i] = new VkDescriptorImageInfo
                {
                    ImageView = tex.View,
                    ImageLayout = VkImageLayout.General,
                };
                writes[writeCount++] = new VkWriteDescriptorSet
                {
                    SType = VkStructureType.WriteDescriptorSet,
                    DstSet = set,
                    DstBinding = (uint)i,
                    DescriptorCount = 1,
                    DescriptorType = VkDescriptorType.StorageImage,
                    PImageInfo = &imageInfos[i],
                };
            }
            else // SampledTexture
            {
                // Upload-chain texture:
                // cross-queue visibility is guaranteed by the upload path's CPU wait chain
                // (same semantics as EnsureReadyForRendering),
                // so only the layout transition needs to be closed here
                tex.TransitionTo(cmd, VkImageLayout.ShaderReadOnlyOptimal,
                    VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.ComputeShaderBit,
                    VkAccessFlags.TransferWriteBit, VkAccessFlags.ShaderReadBit);
                tex.UploadFenceValue = 0;
                imageInfos[i] = new VkDescriptorImageInfo
                {
                    ImageView = tex.View,
                    ImageLayout = VkImageLayout.ShaderReadOnlyOptimal,
                    // Leave Sampler empty:
                    // the set layout declares immutable StaticSampler
                };
                writes[writeCount++] = new VkWriteDescriptorSet
                {
                    SType = VkStructureType.WriteDescriptorSet,
                    DstSet = set,
                    DstBinding = (uint)i,
                    DescriptorCount = 1,
                    DescriptorType = VkDescriptorType.CombinedImageSampler,
                    PImageInfo = &imageInfos[i],
                };
            }
            kernel.ResolvedScratch[i] = tex;
        }

        if (writeCount > 0)
            vk.UpdateDescriptorSets(Vulkan.Device.LogicalDevice, writeCount, writes, 0, null);

        vk.CmdBindPipeline(cmd, VkPipelineBindPoint.Compute, kernel.PipelineState);

        if (kernel.ParamsSize > 0)
        {
            fixed (byte* pParams = args.Params)
            {
                vk.CmdPushConstants(cmd, kernel.PipelineLayout, VkShaderStageFlags.ComputeBit, 0, kernel.ParamsSize, pParams);
            }
        }

        vk.CmdBindDescriptorSets(cmd, VkPipelineBindPoint.Compute, kernel.PipelineLayout, 0, 1, &set, 0, null);
        vk.CmdDispatch(cmd, args.GroupsX, args.GroupsY, args.GroupsZ);

        // Post-dispatch cleanup:
        // storage textures transition to ShaderReadOnlyOptimal
        // so draw-side sampling can consume them directly.
        // The barrier itself provides write -> read synchronization;
        // sampled textures already in that layout early-out with zero work.
        // RW buffers receive an extra buffer memory barrier for same-frame kernel-chain dependencies.
        for (int i = 0; i < n; i++)
        {
            switch (kernel.ResolvedScratch[i])
            {
                case VkTexture tex:
                    tex.TransitionTo(cmd, VkImageLayout.ShaderReadOnlyOptimal,
                        VkPipelineStageFlags.ComputeShaderBit,
                        VkPipelineStageFlags.FragmentShaderBit | VkPipelineStageFlags.ComputeShaderBit,
                        VkAccessFlags.ShaderWriteBit, VkAccessFlags.ShaderReadBit);
                    break;
                // 1-8: transition written 3D volumes to ShaderReadOnlyOptimal
                // for later same-frame kernel sampling.
                // The barrier itself provides write -> read synchronization.
                // Sampled slots are already in that layout and TransitionTo early-outs with zero work.
                // dstStage only needs compute,
                // because 3D textures never enter the 2D dictionary and therefore cannot be consumed by draw paths.
                case Vulkan.VKTexture3D tex3d:
                    tex3d.TransitionTo(cmd, VkImageLayout.ShaderReadOnlyOptimal,
                        VkPipelineStageFlags.ComputeShaderBit, VkPipelineStageFlags.ComputeShaderBit,
                        VkAccessFlags.ShaderWriteBit, VkAccessFlags.ShaderReadBit);
                    break;
                case VKStorageBuffer buffer when bindings[i].Type == Season.Rendering.ComputeBindingType.StorageBufferReadWrite:
                    var bufBarrier = new VkBufferMemoryBarrier
                    {
                        SType = VkStructureType.BufferMemoryBarrier,
                        SrcAccessMask = VkAccessFlags.ShaderWriteBit,
                        DstAccessMask = VkAccessFlags.ShaderReadBit,
                        SrcQueueFamilyIndex = SVk.QueueFamilyIgnored,
                        DstQueueFamilyIndex = SVk.QueueFamilyIgnored,
                        Buffer = buffer.Buffer.Buffer,
                        Offset = 0,
                        Size = SVk.WholeSize,
                    };
                    vk.CmdPipelineBarrier(cmd,
                        VkPipelineStageFlags.ComputeShaderBit,
                        VkPipelineStageFlags.ComputeShaderBit | VkPipelineStageFlags.VertexShaderBit,
                        0, 0, null, 1, &bufBarrier, 0, null);
                    break;
            }
            kernel.ResolvedScratch[i] = null;
        }

        Vulkan.Device.PopDebugGroup();
    }

    }
