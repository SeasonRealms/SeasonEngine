// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Metal;
using Season.Fonts;
using Season.Platforms.Shared.Apple.Metal;
using System.Runtime.CompilerServices;
using MTLTexture = Season.Platforms.Shared.Apple.Metal.Texture;

namespace Season.Platforms.Shared.Apple;

/// <summary>
/// IGraphics implementation for iOS and MacCatalyst.
/// Its structure mirrors LinuxAndroid/Graphics.cs and Windows/Graphics.cs one to one,
/// replacing DX and VK classes only with their Metal equivalents:
///   DXTexture / VkTexture          -> Metal.Texture, aliased as MTLTexture
///   DXSprite2D / VKSprite2D        → MTLSprite2D
///   DXSprite3D / VKSprite3D        → MTLSprite3D
///   DXModel    / VKModel           → MTLModel
///   DXMesh3D   / VKMesh3D          → MTLMesh3D
///   textureUploadBatch.Execute(...) → Apple.Metal.Device.TextureUploadBatch.Execute()
///
/// Dictionary and lock usage follows the shared blueprint exactly, preserving identical behavior.
/// </summary>
internal unsafe class Graphics : IGraphics
{
    readonly GlyphAtlasManager<MTLTexture> _glyphAtlas = new(
        2048, 2048,
        createAtlasTexture: (w, h) => MTLTexture.CreateEmpty((uint)w, (uint)h, "TextAtlas"),
        uploadFullPixels: (tex, pixels) => tex.UploadPixels(pixels),
        uploadSubRects: (tex, pixels, atlasW, atlasH, rects) =>
        {
            var atlasRects = new AtlasUploadRect[rects.Length];
            for (int i = 0; i < rects.Length; i++)
                atlasRects[i] = new AtlasUploadRect(rects[i].X, rects[i].Y, rects[i].Width, rects[i].Height);
            tex.UploadSubRects(pixels, atlasW, atlasH, atlasRects);
        },
        getCurrentFrameIndex: () => (uint)Metal.Device.FrameIndex);

    Dictionary<string, MTLTexture> DictionaryMtlTexture = new();

    Dictionary<(string, long), MTLSprite2D> DictionarySprite = new();

    // -- Shape, procedural geometry --
    Dictionary<(Season.Controls.ShapeType, int, int, int), MTLTexture> DictionaryShapeTexture = new();
    Dictionary<(Season.Controls.ShapeType, long), MTLSprite2D> DictionaryShape = new();

    Dictionary<(string, long), MTLModel> DictionaryModel = new();
    Dictionary<string, Task<MTLModel>> DictionaryModelResource = new();

    Dictionary<(string, long), MTLSprite3D> DictionarySprite3D = new();

    Dictionary<(string, long), MTLMesh3D> DictionaryMesh3D = new();
    Dictionary<(string, long), MTLInstancedMesh3D> DictionaryInstancedMesh3D = new();
    Dictionary<(string, long), MTLInstancedModel> DictionaryInstancedModel = new();

    // -- Phase 4: Outline2D mask path, mirrored with DX and VK --

    /// <summary>Outline2D mask RT for the current frame, using BackbufferCompatible and BGRA8 with MatchBackbufferSize, lazily created and kept alive.</summary>
    MTLRenderTarget? _outlineMaskTarget;

    /// <summary>Whether any primitive group activated Outline2D in the current frame. Aggregated by RenderOutlineMask and consumed by BlitToBackbuffer.</summary>
    bool _outline2DFrameActive;

    /// <summary>Frame-level outline width for the current frame. Takes the maximum width across all groups so the widest outline stays fully visible.</summary>
    float _outline2DFrameWidth;

    // ── Text GPU Instancing ──
    /// <summary>Lightweight ITextureHolder with no GPU resources, used instead of the heavier MTLSprite2D.</summary>
    internal sealed class TextGlyphHolder : ITextureHolder
    {
        public Controls.Texture Texture { get; set; } = new Controls.Texture();
    }

    /// <summary>GPU-instancing state for one Texts control, aligned with DX and VK TextInstanceState.
    /// Metal simplifies this path by removing DescriptorSets, because each draw binds buffers directly with SetBuffer,
    /// and IMTLBuffer.Contents stays persistently writable, removing the need for a separate mapped-pointer array.</summary>
    internal struct MTLTextInstanceState
    {
        // -- Glyph data, reusing VS buffer(5) morphDeltas and buffered N times per frame:
        //    sharing one buffer across frames would let direct CPU writes race against GPU reads from in-flight frames. --
        public IMTLBuffer[] GlyphBuffers;
        public int GlyphCapacity;
        public int GlyphAtlasVersionBuilt;
        public bool GlyphDirty;
        public bool CanDraw;

        // -- Instance transforms, using VS buffer(2) as the per-instance stream and buffered N times per frame. --
        public IMTLBuffer[] InstanceBuffers;
        public uint InstanceFrameMask;
        public int InstanceCount;
        public int InstanceCapacity;    // Allocated capacity, grown exponentially, always greater than or equal to InstanceCount.

        // -- TextDrawParams, VS buffer(7) and FS buffer(3), one per Texts control and buffered N times per frame. --
        // Each control must own its own copy.
        // If shared globally, later Texts recorded in the same frame would overwrite the UBO,
        // and every Texts draw would read the last written color and alpha, tinting the entire screen.
        public IMTLBuffer[] DrawParamsBuffers;
    }

    Dictionary<Texts, MTLTextInstanceState> _textInstances = new();

    // -- Shared resources for Text GPU instancing --
    IMTLBuffer _textMatrixBuffer = null!;      // Identity-matrix UBO at VS buffer(1), written during Init and shared read-only afterward.
    IMTLBuffer _textMaterialBuffer = null!;    // renderMode=2, isInstanced=1 at VS buffer(4) and FS buffer(2), same lifetime and sharing model.
    IMTLBuffer _textQuadVertexBuffer = null!;  // Unit quad with 6 vertices and no indices, matching the MTLSpriteQuad DrawPrimitives convention.
    IMTLBuffer _textRestoreMaterialBuffer = null!; // renderMode=0, isInstanced=0, restores VS buffer(4) after DrawTexts.
                                                   // The sprite path does not bind this slot, so leftover text material would incorrectly enter the VS text branch and remap UVs.

    public void Init()
    {
        var rm = Metal.Device.ResourceManager;

        // -- Shared UBOs for Text GPU instancing, aligned with VK Graphics.Init. --
        _textMatrixBuffer = rm.CreateConstantBuffer((nuint)Unsafe.SizeOf<MatrixBuffer>());
        var identityMatrix = new MatrixBuffer
        {
            World = Matrix4x4.Transpose(Matrix4x4.Identity),
            View = Matrix4x4.Transpose(Matrix4x4.Identity),
            Projection = Matrix4x4.Transpose(Matrix4x4.Identity),
        };
        *(MatrixBuffer*)_textMatrixBuffer.Contents = identityMatrix;

        _textMaterialBuffer = rm.CreateConstantBuffer((nuint)Unsafe.SizeOf<Metal.MaterialParams>());
        var textMaterial = new Metal.MaterialParams
        {
            BaseColor = new Vector4(1, 1, 1, 1),
            MetallicFactor = 0f,
            RoughnessFactor = 1f,
            UseAlbedoMap = 1,
            RenderMode = 2,     // TextMsdf
            IsInstanced = 1,
        };
        *(Metal.MaterialParams*)_textMaterialBuffer.Contents = textMaterial;

        // -- Unit-quad vertex buffer:
        //    positions in plus or minus 0.5 and UVs in 0 to 1.
        //    Expands the VK UnitQuad index order 0,1,2,1,3,2 into 6 vertices,
        //    matching the non-indexed Sprite DrawPrimitives(Triangle, 0, 6) convention on Metal. --
        var v0 = CreateTextQuadVertex(-0.5f, -0.5f, 0f, 1f);
        var v1 = CreateTextQuadVertex(0.5f, -0.5f, 1f, 1f);
        var v2 = CreateTextQuadVertex(-0.5f, 0.5f, 0f, 0f);
        var v3 = CreateTextQuadVertex(0.5f, 0.5f, 1f, 0f);
        _textQuadVertexBuffer = rm.CreateVertexBuffer(new[] { v0, v1, v2, v1, v3, v2 });

        // -- Neutral material, renderMode=0 and isInstanced=0, restored into VS buffer(4) after text drawing. --
        _textRestoreMaterialBuffer = rm.CreateConstantBuffer((nuint)Unsafe.SizeOf<Metal.MaterialParams>());
        *(Metal.MaterialParams*)_textRestoreMaterialBuffer.Contents = new Metal.MaterialParams
        {
            BaseColor = new Vector4(1, 1, 1, 1),
            MetallicFactor = 0f,
            RoughnessFactor = 1f,
        };

        // Note:
        // TextDrawParams UBO is a per-Texts, per-frame resource stored in MTLTextInstanceState.DrawParamsBuffers.
        // It is created in LoadTexts and must not be shared globally,
        // or multiple controls in the same frame would overwrite each other's colors.
    }

    static Vertex CreateTextQuadVertex(float x, float y, float u, float v)
    {
        return new Vertex
        {
            Position = new Vector3(x, y, 0),
            TexCoord = new Vector2(u, v),
            Normal = Vector3.UnitZ,
            Tangent = new Vector4(1, 0, 0, 1),
            Joints = Vector4.Zero,
            Weights = Vector4.Zero,
        };
    }

    public async Task<bool> LoadSprite2D(Sprite2D sprite2D)
    {
        MTLSprite2D mtlSprite2D = null!;

        lock (DictionarySprite)
        {
            if (sprite2D.IsDisposed) return false;

            if (DictionarySprite.TryGetValue((sprite2D.Name, sprite2D.ID), out mtlSprite2D!))
            {
                if (mtlSprite2D == null || mtlSprite2D.AlbedoTexture == null)
                {

                }
                else
                {
                    sprite2D.OriginWidth = (int)mtlSprite2D.AlbedoTexture.Width;
                    sprite2D.OriginHeight = (int)mtlSprite2D.AlbedoTexture.Height;
                }
            }
            else
            {
                try
                {
                    MTLTexture view = null!;

                    lock (DictionaryMtlTexture)
                    {
                        if (DictionaryMtlTexture.TryGetValue(sprite2D.Name, out view!))
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
                                view = new MTLTexture(imageResult);
                                view.Name = sprite2D.Name;

                                ExecuteUpload();
                            }

                            if (view == null)
                            {
                                DeviceServices.BaseApp.AddLog(LogType.Backend, $"{DateTime.UtcNow} LoadTextureAsync GetTexture {sprite2D.Name}");
                            }

                            DictionaryMtlTexture.Add(sprite2D.Name, view);
                        }
                    }

                    try
                    {
                        // Use Sprite as the replacement for Texture2D.
                        mtlSprite2D = new MTLSprite2D(view);

                        sprite2D.OriginWidth = (int)mtlSprite2D.AlbedoTexture.Width;
                        sprite2D.OriginHeight = (int)mtlSprite2D.AlbedoTexture.Height;
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
                            DictionarySprite.Add((sprite2D.Name, sprite2D.ID), mtlSprite2D);
                        }
                    }
                }
                catch (Exception ex)
                {
                    DeviceServices.BaseApp.AddLog(LogType.Error, $"{DateTime.UtcNow} LoadTextureAsync {ex}");
                }
            }
        }

        return true;
    }

    public void UpdateSprite2D(Sprite2D sprite)
    {
        MTLSprite2D mtlSprite = null!;

        lock (DictionarySprite)
        {
            if (DictionarySprite.TryGetValue((sprite.Name, sprite.ID), out mtlSprite!))
            {
                if (mtlSprite == null || mtlSprite.AlbedoTexture == null)
                {

                }
                else
                {
                    sprite.Ready = true;

                    // -- Texture replacement, newly added. --
                    if (sprite.TextureOverride.HasValue)
                    {
                        var source = sprite.TextureOverride;
                        sprite.TextureOverride = default;
                        ReplaceSpriteTexture(mtlSprite, source);
                    }

                    if (sprite.Changed)
                    {
                        sprite.Changed = false;

                        mtlSprite.SpriteRef = sprite;

                        mtlSprite.Update();
                    }
                }
            }
        }
    }

    /// <summary>Resolve TextureUpdateSource into an INativeImageDecoder. Image takes precedence over Path.</summary>
    static INativeImageDecoder? ResolveDecoder(TextureUpdateSource source)
    {
        if (source.Image != null) return source.Image;
        if (source.Path != null) return DecodeImageFromPath(source.Path);
        return null;
    }

    static INativeImageDecoder? DecodeImageFromPath(string path)
    {
        if (ImageUtils.CreateImageExist(path))
            return ImageUtils.CreateImage(path);

        if (!StorageService.FileExist(StorageService.DirectoryBase, path))
            StorageService.CopyToLocal(path);

        StorageService.TryGetStream(StorageService.DirectoryBase, path, out Stream stream, out _);
        if (stream == null) return null;

        using (stream)
            return ImageUtils.GetImageFromStream(stream, null);
    }

    /// <summary>Replace the single texture used by a Sprite.</summary>
    void ReplaceSpriteTexture(MTLSpriteQuad mtlSprite, TextureUpdateSource source)
    {
        var decoder = ResolveDecoder(source);
        if (decoder == null) return;

        var oldTex = mtlSprite.AlbedoTexture;
        if (oldTex == null) { decoder.Dispose(); return; }

        if ((uint)decoder.Width == oldTex.Width
            && (uint)decoder.Height == oldTex.Height
            && oldTex.RefCount == 1)
        {
            oldTex.UploadPixels(decoder.PixelSpan);
        }
        else
        {
            var newTex = MTLTexture.CreateFromDecoder(decoder);
            ExecuteUpload();
            mtlSprite.AlbedoTexture = newTex;
        }

        decoder.Dispose();
    }

    public void DrawSprite2D(Sprite2D sprite)
    {
        MTLSprite2D mtlSprite = null!;

        lock (DictionarySprite)
        {
            if (DictionarySprite.TryGetValue((sprite.Name, sprite.ID), out mtlSprite!))
            {

            }
            else
            {
                //sprite.Changed = true;
            }
        }

        if (mtlSprite == null || mtlSprite.AlbedoTexture == null)
        {

        }
        else
        {
            mtlSprite.Draw();
        }
    }

    // -- Texts, using the GPU-instancing architecture aligned with DX and VK --

    public async Task<bool> LoadTexts(Texts texts)
    {
        if (texts?.TexsLoading?.Length == 0)
            return false;

        var texsLoading = texts.TexsLoading;
        int totalCount = texsLoading.Length + (texts.ShowDot ? 1 : 0);

        // Phase one: count valid glyphs and ensure every glyph already exists in the atlas.
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

        // Handle the dot glyph.
        if (texts.ShowDot && TryEnsureGlyphEntry(ref texts._dotRef, out var dotEntry))
        {
            validIndices[validCount] = -1;  // -1 stands for the dot glyph.
            validCount++;
        }

        if (validCount == 0)
            return false;

        // Phase two: create instance buffers plus the per-text glyph buffer.
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

            // Slot 1: write a zero matrix initially to hide the instance.
            instanceData[instanceIdx] = CreateHiddenInstanceData();

            // Create the TextGlyphHolder.
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

        // Create the GPU instance buffers plus the glyph buffer.
        // Metal has no descriptor set here, so drawing binds them directly with SetBuffer.
        int frameCount = Metal.Device.frameCount;
        var rm = Metal.Device.ResourceManager;
        var state = new MTLTextInstanceState
        {
            InstanceCount = instanceIdx,
            GlyphCapacity = 0,
            GlyphAtlasVersionBuilt = -1,
            GlyphDirty = true,
            CanDraw = false,
            InstanceFrameMask = 0,
            InstanceBuffers = new IMTLBuffer[frameCount],
            InstanceCapacity = instanceData.Length,
            DrawParamsBuffers = new IMTLBuffer[frameCount],
        };

        var defaultDrawParams = new MTLTextDrawParams
        {
            AtlasSize = Vector2.One,
            PxRange = Season.Fonts.Font.PixelRange,
            GlobalAlpha = 1f,
            TextColor = Vector4.One,
        };
        for (int fi = 0; fi < frameCount; fi++)
        {
            state.InstanceBuffers[fi] = rm.CreateVertexBuffer(instanceData);
            state.DrawParamsBuffers[fi] = rm.CreateConstantBuffer((nuint)Unsafe.SizeOf<MTLTextDrawParams>());
            *(MTLTextDrawParams*)state.DrawParamsBuffers[fi].Contents = defaultDrawParams;
        }

        EnsureGlyphBufferCapacity(ref state, Math.Max(instanceIdx, 1));

        // Initialize the glyph buffer with hidden glyphs across the whole frame set.
        var hiddenGlyph = CreateHiddenGlyphData();
        for (int fi = 0; fi < frameCount; fi++)
        {
            var glyphPtr = (MTLTextGlyphData*)state.GlyphBuffers[fi].Contents;
            for (int i = 0; i < Math.Max(instanceIdx, 1); i++)
                glyphPtr[i] = hiddenGlyph;
        }

        // Replace the previous state.
        if (_textInstances.TryGetValue(texts, out var prevState))
            ReleaseTextInstanceResources(ref prevState);

        _textInstances[texts] = state;
        texts.textureHoldersLoading = holders;

        return true;
    }

    /// <summary>Incremental append path, see the contract on IGraphics.AppendTexts.
    /// Only creates atlas entries and holders for newly appended glyphs.
    /// Buffers grow exponentially without rebuilding the per-text state, so existing resources do not need release and rebuild.
    /// GlyphDirty must be set to true because appending shifts the dot instance index, so glyph data must be rebuilt as a whole.</summary>
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

        // Pure whitespace append, such as spaces or newlines only:
        // the instance count stays unchanged and only layout needs to advance at the upper layer.
        if (added == 0)
            return Task.FromResult(true);

        int required = state.InstanceCount + added;

        if (!EnsureInstanceBufferCapacity(ref state, required) || !EnsureGlyphBufferCapacity(ref state, required))
            return Task.FromResult(false);

        state.InstanceCount = required;
        state.GlyphDirty = true;
        state.InstanceFrameMask = 0;
        state.CanDraw = false;

        // Do not write back after Dispose, or released resources would be resurrected into the dictionary.
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

        // GPU-instancing path.
        if (_textInstances.TryGetValue(texts, out var state))
        {
            var texs = texts.Texs;
            var holders = texts.textureHolders;
            int instanceCount = state.InstanceCount;
            if (instanceCount <= 0 || state.GlyphBuffers == null || !EnsureGlyphBufferCapacity(ref state, instanceCount))
            {
                state.CanDraw = false;
                _textInstances[texts] = state;
                return;
            }

            // Use frame 0 as the primary write buffer for glyph data,
            // then copy it to the remaining frame buffers for full-frame synchronization.
            var glyphPtr = (MTLTextGlyphData*)state.GlyphBuffers[0].Contents;
            bool uploadGlyphData = state.GlyphDirty || state.GlyphAtlasVersionBuilt != _glyphAtlas.Version;

            // Check whether layout changed.
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

            // When layout changes, compute instance data into a temporary array.
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

                    glyphPtr[instIdx] = new MTLTextGlyphData
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

            // Handle the dot glyph. Changed must always be cleared unconditionally.
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

                            glyphPtr[instIdx] = new MTLTextGlyphData
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
            // Fill the remaining slots.
            for (; instIdx < instanceCount; instIdx++)
            {
                if (uploadGlyphData)
                    glyphPtr[instIdx] = CreateHiddenGlyphData();
                if (writeInstanceData)
                    instanceData[instIdx] = CreateHiddenInstanceData();
            }

            // Multi-frame synchronization:
            // write all frame buffers directly because IMTLBuffer.Contents stays persistently writable with no Map or Unmap needed.
            if (writeInstanceData)
            {
                for (int fi = 0; fi < state.InstanceBuffers.Length; fi++)
                {
                    var dst = (InstanceTransformData*)state.InstanceBuffers[fi].Contents;
                    for (int j = 0; j < instanceCount; j++)
                        dst[j] = instanceData[j];
                    state.InstanceFrameMask |= (1u << fi);
                }
            }

            if (uploadGlyphData)
            {
                // Multi-frame synchronization:
                // copy glyph data into the remaining frame buffers.
                uint glyphBytes = (uint)(instanceCount * Unsafe.SizeOf<MTLTextGlyphData>());
                for (int gfi = 1; gfi < state.GlyphBuffers.Length; gfi++)
                    Unsafe.CopyBlock((void*)state.GlyphBuffers[gfi].Contents, (void*)state.GlyphBuffers[0].Contents, glyphBytes);

                state.GlyphAtlasVersionBuilt = _glyphAtlas.Version;
                state.GlyphDirty = false;
            }
            state.CanDraw = true;
            _textInstances[texts] = state;
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

        // GPU-instancing path: one instanced DrawPrimitives call.
        if (_textInstances.TryGetValue(texts, out var state) && state.InstanceCount > 0)
        {
            var enc = Metal.Device.GraphicsEncoder;
            int fi = Metal.Device.FrameIndex;
            if (enc == null || !state.CanDraw
                || state.GlyphBuffers == null || fi >= state.GlyphBuffers.Length
                || state.InstanceBuffers == null || fi >= state.InstanceBuffers.Length
                || _glyphAtlas.AtlasTexture == null)
            {
                return;
            }

            // Set the pipeline, Transparent plus DoubleSided, including static sampler(0).
            Pipeline.SetPipeline(enc, PipelineMode.Transparent, doubleSided: true);

            // Write TextDrawParams, using a per-Texts, per-frame dedicated UBO to prevent same-frame cross-control overwrites.
            var drawParams = new MTLTextDrawParams
            {
                AtlasSize = new Vector2(_glyphAtlas.AtlasTexture.Width, _glyphAtlas.AtlasTexture.Height),
                PxRange = Season.Fonts.Font.PixelRange,
                GlobalAlpha = Math.Clamp(texts.Alpha, 0f, 1f),
                TextColor = texts.Color.AsVector4,
            };
            *(MTLTextDrawParams*)state.DrawParamsBuffers[fi].Contents = drawParams;

            // VS slots:
            // 0=unit quad, 1=identity Matrices, 2=instance stream, 3=IdentityBone,
            // 4=text material, 5=glyph data reusing morphDeltas, 6=InstanceBone placeholder, 7=TextDrawParams.
            enc.SetVertexBuffer(_textQuadVertexBuffer, 0, 0);
            enc.SetVertexBuffer(_textMatrixBuffer, 0, 1);
            enc.SetVertexBuffer(state.InstanceBuffers[fi], 0, 2);
            enc.SetVertexBuffer(MTLPrimitiveGroup.IdentityBoneBuffers[fi], 0, 3);
            enc.SetVertexBuffer(_textMaterialBuffer, 0, 4);
            enc.SetVertexBuffer(state.GlyphBuffers[fi], 0, 5);
            enc.SetVertexBuffer(MTLPrimitiveGroup.IdentityInstanceBoneBuffers[fi], 0, 6);
            enc.SetVertexBuffer(state.DrawParamsBuffers[fi], 0, 7);

            // FS slots:
            // 1=SceneLights, 2=text material, 3=TextDrawParams, texture(0)=atlas.
            enc.SetFragmentBuffer(MTLPrimitiveGroup.LightConstantBuffers[fi], 0, 1);
            enc.SetFragmentBuffer(_textMaterialBuffer, 0, 2);
            enc.SetFragmentBuffer(state.DrawParamsBuffers[fi], 0, 3);

            var fallback = Metal.Device.White;
            enc.SetFragmentTexture(_glyphAtlas.AtlasTexture.Image, 0);
            enc.SetFragmentTexture(fallback.Image, 1);
            enc.SetFragmentTexture(fallback.Image, 2);
            enc.SetFragmentTexture(fallback.Image, 3);
            enc.SetFragmentTexture(fallback.Image, 4);

            // Single instanced draw, using the unit quad with 6 non-indexed vertices.
            enc.DrawPrimitives(MTLPrimitiveType.Triangle, 0, 6, (nuint)state.InstanceCount);

            // Restore VS buffer(4).
            // Encoder state persists inside the pass, and the sprite path does not bind this slot.
            // If renderMode=2 and isInstanced=1 text material were left behind,
            // later sprites would incorrectly enter the VS text branch and have UVs remapped by glyph data.
            enc.SetVertexBuffer(_textRestoreMaterialBuffer, 0, 4);
        }
    }

    public void DisposeTexts(Texts texts)
    {
        // Release GPU-instancing resources.
        if (_textInstances.TryGetValue(texts, out var state))
        {
            ReleaseTextInstanceResources(ref state);
            _textInstances.Remove(texts);
        }

        // Release holder references.
        // TextGlyphHolder owns no GPU resources and only needs its references cleared.
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

    // -- Pass orchestration, steps 1 through 3 of 1-1:
    //    thin forwarding into Metal.Device, with the pass state machine, RPD and encoder switching, living on the Device side.
    //    On Metal, a pass is an encoder, and once BeginPass opens a new encoder, all draw points are routed automatically through Device.GraphicsEncoder.
    //    For the full catalog of Metal platform-specific rules, see the Metal/Device.cs class header.

    public void BeginPass(in Season.Rendering.PassDesc desc)
    {
        Metal.Device.BeginPass(desc);
    }

    public void EndPass()
    {
        Metal.Device.EndPass();
    }

    public Season.Rendering.RenderTarget CreateRenderTarget(in Season.Rendering.RenderTargetDesc desc)
    {
        // Four step-3 shapes, matching DX, VK, and WebGPU:
        // BackbufferCompatible plus None for step-2 SceneColor,
        // Rgba16Float plus None for 1-4 HDR,
        // Rg16Float plus None for 2-3 SceneVelocity under contract clause 2, with no dedicated depth because velocity relies on the explicit depth target of Scene pass,
        // and None plus D32Float for the 1-5 depth-only shadow map.
        // Metal has no offscreen MSAA path here.
        bool colorForm = (desc.ColorFormat == Season.Rendering.RtFormat.BackbufferCompatible ||
                          desc.ColorFormat == Season.Rendering.RtFormat.Rgba16Float ||
                          desc.ColorFormat == Season.Rendering.RtFormat.Rg16Float) &&
                         desc.DepthFormat == Season.Rendering.RtFormat.None;
        bool depthOnlyForm = desc.ColorFormat == Season.Rendering.RtFormat.None &&
                             desc.DepthFormat == Season.Rendering.RtFormat.D32Float;
        if (!colorForm && !depthOnlyForm)
            throw new NotSupportedException($"[CreateRenderTarget] Unsupported format combination for now: color={desc.ColorFormat}, depth={desc.DepthFormat}.");
        if (desc.SampleCount > 1)
            throw new NotSupportedException("[CreateRenderTarget] Offscreen MSAA is not supported yet.");

        int w = desc.MatchBackbufferSize ? Metal.Device.Display.Width : (int)desc.Width;
        int h = desc.MatchBackbufferSize ? Metal.Device.Display.Height : (int)desc.Height;
        if (w <= 0 || h <= 0)
            throw new ArgumentException($"[CreateRenderTarget] Invalid size {w}x{h}.");

        return new MTLRenderTarget(desc, w, h);
    }

    /// <summary>Phase 4 lazy creation for the Outline2D mask RT, using BackbufferCompatible and BGRA8 with MatchBackbufferSize.
    /// It follows the colorForm whitelist in CreateRenderTarget and always uses SampleCount=1, mirrored with DX and VK EnsureOutlineMaskTarget.</summary>
    Season.Rendering.RenderTarget EnsureOutlineMaskTarget()
    {
        if (_outlineMaskTarget != null)
            return _outlineMaskTarget;

        _outlineMaskTarget = (MTLRenderTarget)CreateRenderTarget(new Season.Rendering.RenderTargetDesc
        {
            ColorFormat = Season.Rendering.RtFormat.BackbufferCompatible,
            MatchBackbufferSize = true,
            SampleCount = 1,
        });
        return _outlineMaskTarget;
    }

    bool TryAccumulateOutline2D(MTLPrimitiveGroup group)
    {
        if (group == null || !group.Outline2DActive)
            return false;

        // Color is carried per pixel inside the mask by each group, allowing multiple colors in one frame.
        // See OUTLINE_MASK FS and blit_fs_outline_composite.
        // The frame level only aggregates width, taking the maximum so the widest outline stays fully visible.
        _outline2DFrameActive = true;
        _outline2DFrameWidth = MathF.Max(_outline2DFrameWidth, group.Outline2DMaskWidth);
        return true;
    }

    public void RenderOutlineMask()
    {
        _outline2DFrameActive = false;
        _outline2DFrameWidth = 0f;

        var drawGroups = new List<MTLPrimitiveGroup>();

        lock (DictionaryModel)
        {
            foreach (var pair in DictionaryModel)
                if (TryAccumulateOutline2D(pair.Value))
                    drawGroups.Add(pair.Value);
        }

        lock (DictionaryMesh3D)
        {
            foreach (var pair in DictionaryMesh3D)
                if (TryAccumulateOutline2D(pair.Value))
                    drawGroups.Add(pair.Value);
        }

        // Instanced controls, InstancedMesh3D and InstancedModel, also support per-instance Outline2D masks.
        // Activation state is aggregated during the platform Update phase from each instance or host Highlight.Outline2D through MTLInstancedPrimitiveGroup.
        lock (DictionaryInstancedMesh3D)
        {
            foreach (var pair in DictionaryInstancedMesh3D)
                if (TryAccumulateOutline2D(pair.Value))
                    drawGroups.Add(pair.Value);
        }

        lock (DictionaryInstancedModel)
        {
            foreach (var pair in DictionaryInstancedModel)
                if (TryAccumulateOutline2D(pair.Value))
                    drawGroups.Add(pair.Value);
        }

        if (!_outline2DFrameActive || drawGroups.Count == 0)
            return;

        BeginPass(new Season.Rendering.PassDesc
        {
            Id = Season.Rendering.RenderPassId.OutlineMask,
            ColorTarget = EnsureOutlineMaskTarget(),
            DepthTarget = Season.Rendering.FrameSchedule.SceneDepth,
            ClearColor = Vector4.Zero,
            ClearColorEnable = true,
            ClearDepthEnable = false,
            StoreDepth = false,
        });

        for (int i = 0; i < drawGroups.Count; i++)
            drawGroups[i].DrawOutlineMask();

        EndPass();
    }

    /// <summary>Step D of 2-3, contract clause 12:
    /// this entry point is the last HDR-to-LDR composition point before presentation, and the scene source may be overridden by SceneColorOverride, the TAA resolve output.
    /// Under the FXAA tier this entry has already degenerated into FXAA resolve because composition moved into Post,
    /// and the override becomes effective in RenderPostPass instead.
    /// Taa and Fxaa are mutually exclusive, so only one path can be active.
    /// In phase 4, Outline2D composition runs immediately after scene blit or FXAA in both branches, mirrored with DX and VK.</summary>
    public void BlitToBackbuffer(Season.Rendering.RenderTarget src)
    {
        if (src is not MTLRenderTarget rt) return;
        var enc = Metal.Device.GraphicsEncoder;
        if (enc == null) return;
        // Step D of 2-1:
        // when the source is PostColor, where the uber pass already closed tonemap plus bloom and luma is stored in alpha,
        // run the FXAA resolve path.
        // Otherwise keep the direct tonemap plus optional bloom presentation path, mirrored with Windows/Graphics.cs.
        if (ReferenceEquals(src, Season.Rendering.FrameSchedule.PostColor))
        {
            BlitPipeline.DrawFxaa(enc, rt);
            if (_outline2DFrameActive && _outlineMaskTarget != null)
                BlitPipeline.DrawOutlineComposite(enc, _outlineMaskTarget, _outline2DFrameWidth);
            return;
        }
        BlitPipeline.Draw(enc, rt, ResolveBloomTexture(), ResolveAoTexture(), ResolveSceneOverrideTexture());
        if (_outline2DFrameActive && _outlineMaskTarget != null)
            BlitPipeline.DrawOutlineComposite(enc, _outlineMaskTarget, _outline2DFrameWidth);
    }

    /// <summary>Step D of 2-1:
    /// Post-pass content invoked by FrameSchedule.RenderPost, with FXAA and PostColor registered as a pair.
    /// The uber pass composes tonemap plus optional bloom into LDR PostColor and bakes luma into alpha.
    /// After composition moves downward, FinalBlit degenerates into FXAA resolve.
    /// See the contract-1 revision in RenderQuality 1-4, mirrored with Windows/Graphics.cs.
    /// Step C of 2-2 forwards AO through the same point, and clause 12 of 2-3 forwards the scene override there as well.</summary>
    internal void RenderPostPass(Season.Basic.IGraphics g, Season.Rendering.RenderTarget sceneColor)
    {
        if (sceneColor is not MTLRenderTarget rt) return;
        var enc = Metal.Device.GraphicsEncoder;
        if (enc == null) return;
        BlitPipeline.DrawUber(enc, rt, ResolveBloomTexture(), ResolveAoTexture(), ResolveSceneOverrideTexture());
    }

    /// <summary>Resolve bloom-chain output from the instance dictionary through FrameSchedule.BloomTexture. Null means no bloom.</summary>
    MTLTexture? ResolveBloomTexture()
    {
        var bloomName = Season.Rendering.FrameSchedule.BloomTexture;
        if (bloomName == null)
            return null;
        lock (DictionaryMtlTexture)
        {
            DictionaryMtlTexture.TryGetValue(bloomName, out var bloom);
            return bloom;
        }
    }

    /// <summary>Step C of 2-2: resolve GTAO output from the instance dictionary through FrameSchedule.AoTexture. Null means no AO.</summary>
    MTLTexture? ResolveAoTexture()
    {
        var aoName = Season.Rendering.FrameSchedule.AoTexture;
        if (aoName == null)
            return null;
        lock (DictionaryMtlTexture)
        {
            DictionaryMtlTexture.TryGetValue(aoName, out var ao);
            return ao;
        }
    }

    /// <summary>Contract clause 12 of 2-3: resolve TAA output from the instance dictionary through FrameSchedule.SceneColorOverride.
    /// Null means no override, and BlitPipeline falls back to the SceneColor RT with zero residual state.</summary>
    MTLTexture? ResolveSceneOverrideTexture()
    {
        var sceneName = Season.Rendering.FrameSchedule.SceneColorOverride;
        if (sceneName == null)
            return null;
        lock (DictionaryMtlTexture)
        {
            DictionaryMtlTexture.TryGetValue(sceneName, out var scene);
            return scene;
        }
    }

    /// <summary>Clause 10 of 2-4: resolve the DDGI irradiance atlas, a compute 2D texture, from the singleton instance dictionary by full name.
    /// This is a static entry point called once per frame by MTLPrimitiveGroup.SetLighting, mirroring the resolve semantics of bloom and AO.
    /// It always returns null when the resource is not ready or the name is missing, and the consumer side falls back to Device.White in Device.BeginPass.</summary>
    internal static MTLTexture? FindDdgiAtlas(string name)
    {
        // The contract says a missing name must always return null.
        // Dictionary<string,...> throws ArgumentNullException on a null key,
        // so callers that may pass a null FrameSchedule name must be guarded here rather than relying on outer gating.
        if (name == null)
            return null;
        if (Season.Basic.Graphics.Instance is Graphics g)
        {
            lock (g.DictionaryMtlTexture)
            {
                g.DictionaryMtlTexture.TryGetValue(name, out var atlas);
                return atlas;
            }
        }
        return null;
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
    // Helpers for Text GPU instancing
    // ============================================================

    static MTLTextGlyphData CreateHiddenGlyphData()
    {
        return new MTLTextGlyphData
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

    /// <summary>Ensure per-frame instance-buffer capacity using exponential growth so incremental appends amortize buffer creation to O(1).
    /// New buffers do not copy old contents.
    /// The caller must clear InstanceFrameMask and set GlyphDirty at the same time,
    /// so the next UpdateTexts rebuilds all instances by full-frame synchronized full writes, preserving the anti-flicker invariant.</summary>
    bool EnsureInstanceBufferCapacity(ref MTLTextInstanceState state, int requiredCount)
    {
        requiredCount = Math.Max(requiredCount, 1);
        int frameCount = Metal.Device.frameCount;
        if (state.InstanceBuffers != null
            && state.InstanceBuffers.Length == frameCount
            && state.InstanceCapacity >= requiredCount)
            return true;

        if (frameCount <= 0)
            return false;

        // Metal command buffers retain references to encoded buffers until execution completes.
        // Releasing the C# side reference is enough, which is equivalent to the delayed-release queue effect on VK.
        if (state.InstanceBuffers != null)
        {
            foreach (var buf in state.InstanceBuffers)
                buf?.Dispose();
        }

        int capacity = Math.Max(requiredCount, Math.Max(state.InstanceCapacity * 2, 64));

        var seed = new InstanceTransformData[capacity];
        var hidden = CreateHiddenInstanceData();
        for (int i = 0; i < capacity; i++)
            seed[i] = hidden;

        var buffers = new IMTLBuffer[frameCount];
        for (int fi = 0; fi < frameCount; fi++)
            buffers[fi] = Metal.Device.ResourceManager.CreateVertexBuffer(seed);

        state.InstanceBuffers = buffers;
        state.InstanceCapacity = capacity;
        state.InstanceFrameMask = 0;
        return true;
    }

    bool EnsureGlyphBufferCapacity(ref MTLTextInstanceState state, int requiredCount)
    {
        requiredCount = Math.Max(requiredCount, 1);
        int frameCount = Metal.Device.frameCount;
        if (state.GlyphBuffers != null
            && state.GlyphBuffers.Length == frameCount
            && state.GlyphCapacity >= requiredCount)
            return true;

        // Metal command buffers retain references to encoded buffers until execution completes.
        // Releasing the C# side reference is enough, which is equivalent to the delayed-release queue effect on VK.
        if (state.GlyphBuffers != null)
        {
            foreach (var buf in state.GlyphBuffers)
                buf?.Dispose();
        }

        nuint size = (nuint)(requiredCount * Unsafe.SizeOf<MTLTextGlyphData>());
        var buffers = new IMTLBuffer[frameCount];
        for (int fi = 0; fi < frameCount; fi++)
            buffers[fi] = Metal.Device.ResourceManager.CreateBuffer(size);

        state.GlyphBuffers = buffers;
        state.GlyphCapacity = requiredCount;
        return true;
    }

    void ReleaseTextInstanceResources(ref MTLTextInstanceState state)
    {
        // Metal command buffers retain references to encoded resources until execution completes, so direct Dispose is safe.
        if (state.InstanceBuffers != null)
        {
            foreach (var buf in state.InstanceBuffers)
                buf?.Dispose();
        }
        if (state.DrawParamsBuffers != null)
        {
            foreach (var buf in state.DrawParamsBuffers)
                buf?.Dispose();
        }
        if (state.GlyphBuffers != null)
        {
            foreach (var buf in state.GlyphBuffers)
                buf?.Dispose();
        }

        state.InstanceBuffers = null!;
        state.DrawParamsBuffers = null!;
        state.GlyphBuffers = null!;
        state.GlyphCapacity = 0;
        state.InstanceCapacity = 0;
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
            DeviceServices.BaseApp.AddLog(LogType.Error, $"{DateTime.UtcNow} LoadTextureAsync {tex} TexType.Missing {ex}");
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
            MTLModel mtlModel;
            try
            {
                var template = task.GetAwaiter().GetResult();
                mtlModel = template.CreateInstance(model, MTLPrimitiveGroup.Camera);
            }
            catch
            {
                mtlModel = new MTLModel(model.Name);
                mtlModel.Load(model, MTLPrimitiveGroup.Camera);
                ExecuteUpload();
            }

            lock (DictionaryModel)
            {
                if (!DictionaryModel.ContainsKey((model.Name, model.ID)))
                    DictionaryModel.Add((model.Name, model.ID), mtlModel);
                else
                    mtlModel.Dispose();
            }
        });

        return true;
    }

    Task<MTLModel> GetOrCreateSharedModelAsync(string modelName)
    {
        Task<MTLModel> sharedTask;
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

    Task<MTLModel> CreateSharedModelAsync(string modelName)
    {
        var templateModel = new Model
        {
            Name = modelName,
            Alpha = 1f
        };

        var template = new MTLModel(modelName);
        template.Load(templateModel, MTLPrimitiveGroup.Camera);
        ExecuteUpload();
        return Task.FromResult(template);
    }

    public void UpdateModel(Model model, float time)
    {
        MTLModel mtlModel = null!;

        lock (DictionaryModel)
        {
            if (DictionaryModel.TryGetValue((model.Name, model.ID), out mtlModel!))
            {
                // -- Material overrides, newly added. --
                ProcessModelOverrides(model, mtlModel);

                mtlModel.Update(model, time);
            }
        }
    }

    /// <summary>Consume all material-override properties on Model and reset them to null or default after processing.</summary>
    void ProcessModelOverrides(Model model, MTLPrimitiveGroup mtlGroup)
    {
        TryReplaceModelTexture(model, mtlGroup, model.BaseColorOverride, TextureSlot.BaseColor, () => model.BaseColorOverride = default);
        TryReplaceModelTexture(model, mtlGroup, model.NormalOverride, TextureSlot.Normal, () => model.NormalOverride = default);
        TryReplaceModelTexture(model, mtlGroup, model.MetallicRoughnessOverride, TextureSlot.MetallicRoughness, () => model.MetallicRoughnessOverride = default);
        TryReplaceModelTexture(model, mtlGroup, model.OcclusionOverride, TextureSlot.Occlusion, () => model.OcclusionOverride = default);
        TryReplaceModelTexture(model, mtlGroup, model.EmissiveTextureOverride, TextureSlot.Emissive, () => model.EmissiveTextureOverride = default);

        bool hasParamOverride = model.MetallicOverride.HasValue
                             || model.RoughnessOverride.HasValue
                             || model.EmissiveFactorOverride.HasValue;
        if (hasParamOverride)
        {
            mtlGroup.SyncMaterialParams(model.MetallicOverride, model.RoughnessOverride, model.EmissiveFactorOverride);
            model.MetallicOverride = null;
            model.RoughnessOverride = null;
            model.EmissiveFactorOverride = null;
        }
    }

    void TryReplaceModelTexture(Model model, MTLPrimitiveGroup mtlGroup,
        TextureUpdateSource source, TextureSlot slot, Action clearSource)
    {
        if (!source.HasValue) return;
        clearSource();
        var decoder = ResolveDecoder(source);
        if (decoder == null) return;
        mtlGroup.ReplaceTextureBySlot(slot, decoder);
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
            MTLModel mtlModel3D = null!;

            lock (DictionaryModel)
            {
                if (DictionaryModel.TryGetValue((model.Name, model.ID), out mtlModel3D!))
                {

                }
                else
                {
                    //texture.Changed = true;
                }
            }

            if (mtlModel3D == null)
            {

            }
            else
            {
                mtlModel3D.Draw();
            }
        }
    }

    // ============================================================
    // 1-5 Shadow pass: per-control projection dispatch and pass-orchestration entry point
    // ============================================================

    public void DrawModelShadow(Model model)
    {
        MTLModel mtlModel = null!;
        lock (DictionaryModel)
        {
            DictionaryModel.TryGetValue((model.Name, model.ID), out mtlModel!);
        }
        mtlModel?.DrawShadow();
    }

    public void DrawMesh3DShadow(Season.Controls.Mesh3D mesh)
    {
        MTLMesh3D mtlMesh = null!;
        lock (DictionaryMesh3D)
        {
            DictionaryMesh3D.TryGetValue((mesh.Name, mesh.ID), out mtlMesh!);
        }
        mtlMesh?.DrawShadow();
    }

    public void DrawInstancedModelShadow(InstancedModel model)
    {
        MTLInstancedModel mtlModel = null!;
        lock (DictionaryInstancedModel)
        {
            DictionaryInstancedModel.TryGetValue((model.ModelName, model.ID), out mtlModel!);
        }
        mtlModel?.DrawShadow();
    }

    public void DrawInstancedMesh3DShadow(InstancedMesh3D mesh)
    {
        MTLInstancedMesh3D mtlMesh = null!;
        lock (DictionaryInstancedMesh3D)
        {
            DictionaryInstancedMesh3D.TryGetValue((mesh.Name, mesh.ID), out mtlMesh!);
        }
        mtlMesh?.DrawShadow();
    }

    /// <summary>
    /// Contents of the 1-5 Shadow pass, invoked by FrameSchedule.RenderShadow.
    /// After switching to the shadow PSO, it sets a controlled viewport and light-space matrix for each atlas quadrant through SetVertexBytes buffer(8),
    /// then replays the shared-layer DrawShadow traversal once per cascade or spotlight.
    /// The atlas has already been fully cleared by BeginPass.
    /// When no light is active, return immediately because shader-side shadowParams stays all-zero and no sampling occurs.
    /// This mirrors Windows/Graphics.cs RenderShadowPass one to one, with the explicit encoder parameter being the Metal-specific difference.
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

        var enc = Metal.Device.GraphicsEncoder;
        Pipeline.SetShadowPipeline(enc);

        if (CascadedShadow.SunActive)
        {
            for (int slot = 0; slot < CascadedShadow.ActiveCascadeCount; slot++)
            {
                CascadedShadow.GetAtlasViewport(slot, out int x, out int y, out int size);
                Metal.Device.SetShadowViewport(x, y, size);
                // Clause 7:
                // BeginSlot publishes both the matrix and the culling frustum together because they come from the same source and must not diverge.
                Pipeline.SetShadowViewProj(enc, CascadedShadow.BeginSlot(slot));
                app.DrawShadow();
            }
        }

        if (CascadedShadow.SpotActive)
        {
            CascadedShadow.GetAtlasViewport(CascadedShadow.SpotSlot, out int sx, out int sy, out int ssize);
            Metal.Device.SetShadowViewport(sx, sy, ssize);
            Pipeline.SetShadowViewProj(enc, CascadedShadow.BeginSlot(CascadedShadow.SpotSlot));
            app.DrawShadow();
        }

        CascadedShadow.EndPass();
    }

    public async Task<bool> LoadSprite3D(Sprite3D sprite)
    {
        lock (DictionarySprite3D)
        {
            if (DictionarySprite3D.ContainsKey((sprite.Name, sprite.ID)))
                return true;
        }

        MTLTexture view = null!;
        lock (DictionaryMtlTexture)
        {
            if (!DictionaryMtlTexture.TryGetValue(sprite.Name, out view!))
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
                    view = new MTLTexture(imageResult);
                    view.Name = sprite.Name;
                    ExecuteUpload();
                }
                if (view != null)
                    DictionaryMtlTexture.Add(sprite.Name, view);
            }
        }

        var mtlSprite3D = new MTLSprite3D(view);

        lock (DictionarySprite3D)
        {
            if (!DictionarySprite3D.ContainsKey((sprite.Name, sprite.ID)))
                DictionarySprite3D.Add((sprite.Name, sprite.ID), mtlSprite3D);
        }

        return true;
    }

    public void UpdateSprite3D(Sprite3D sprite, float time)
    {
        MTLSprite3D mtlSprite3D = null!;
        lock (DictionarySprite3D)
        {
            if (DictionarySprite3D.TryGetValue((sprite.Name, sprite.ID), out mtlSprite3D!))
            {
                // -- Texture replacement, newly added. --
                if (sprite.TextureOverride.HasValue)
                {
                    var source = sprite.TextureOverride;
                    sprite.TextureOverride = default;
                    ReplaceSpriteTexture(mtlSprite3D, source);
                }

                mtlSprite3D.Update(
                    new Vector3(sprite.PosX, sprite.PosY, sprite.PosZ),
                    new Vector2(sprite.Width ?? 1f, sprite.Height ?? 1f),
                    sprite.Rotation,
                    MTLPrimitiveGroup.Camera.View,
                    MTLPrimitiveGroup.Camera.Projection,
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

        MTLSprite3D mtlSprite3D = null!;
        lock (DictionarySprite3D)
        {
            DictionarySprite3D.TryGetValue((sprite.Name, sprite.ID), out mtlSprite3D!);
        }
        mtlSprite3D?.Draw();
    }

    public void DisposeSprite3D(Sprite3D sprite)
    {
        MTLSprite3D mtlSprite3D = null!;
        lock (DictionarySprite3D)
        {
            var key = (sprite.Name, sprite.ID);
            if (DictionarySprite3D.TryGetValue(key, out mtlSprite3D!))
                DictionarySprite3D.Remove(key);
        }
        mtlSprite3D?.Dispose();

        lock (DictionaryMtlTexture)
        {
            if (DictionaryMtlTexture.TryGetValue(sprite.Name, out var mtlTex) && mtlTex != null)
            {
                mtlTex.Release();
                if (mtlTex.RefCount == 0)
                    DictionaryMtlTexture.Remove(sprite.Name);
            }
        }
        sprite.Ready = false;
    }

    /// <summary>
    /// Load one texture into DictionaryMtlTexture on demand and return the MTLTexture.
    /// Reuses the LoadSprite3D loading chain:
    /// StorageService -> ImageResult -> new MTLTexture(imageResult) + ExecuteUpload.
    /// </summary>
    MTLTexture EnsureMtlTexture(string name)
    {
        if (name.IsNullOrWhiteSpace())
            return null!;

        MTLTexture view = null!;
        lock (DictionaryMtlTexture)
        {
            if (DictionaryMtlTexture.TryGetValue(name, out view!))
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
                view = new MTLTexture(imageResult);
                view.Name = name;
                ExecuteUpload();
            }

            if (view != null)
                DictionaryMtlTexture.Add(name, view);

            return view!;
        }
    }

    // -- Mesh3D surface texture resolution, using direct GPU upload for pixel sources and path-source reuse, mirrored with Windows/Graphics.cs --

    static string ProcTextureName(string meshName, long meshId, int surfaceIndex, SurfaceTextureSlot slot)
        => $"proc:{meshName}:{meshId}:{surfaceIndex}:{slot}";

    /// <summary>
    /// Resolve one texture source from a Surface slot into an MTLTexture registered in DictionaryMtlTexture:
    /// - Image branch, procedural pixels:
    ///   CreateFromDecoder uploads directly to the GPU with no file I/O,
    ///   registers under a synthesized name, and executes ExecuteUpload immediately.
    ///   The MTL constructor does not dispose the decoder, so this method disposes it centrally.
    /// - Path branch:
    ///   reuse the existing EnsureMtlTexture loading chain.
    /// Note:
    /// overrides are not cleared here because ProcessMaterial still needs to query GetTextureSource and HasTexture.
    /// The caller clears TextureOverride only once after Load completes, following the one-shot consumption contract.
    /// </summary>
    MTLTexture EnsureSurfaceTexture(string meshName, long meshId, int surfaceIndex, Season.Controls.Surface surface, SurfaceTextureSlot slot)
    {
        var source = surface.GetTextureSource(slot);
        if (!source.HasValue)
            return null!;

        if (source.Image != null)
        {
            var name = ProcTextureName(meshName, meshId, surfaceIndex, slot);
            lock (DictionaryMtlTexture)
            {
                if (DictionaryMtlTexture.TryGetValue(name, out var cached))
                {
                    source.Image.Dispose();   // Already registered, so do not upload again. Only dispose the decoder to avoid leaks.
                    return cached;
                }
            }

            var tex = MTLTexture.CreateFromDecoder(source.Image);
            source.Image.Dispose();
            tex.Name = name;
            ExecuteUpload();

            lock (DictionaryMtlTexture)
                DictionaryMtlTexture[name] = tex;

            return tex;
        }

        return EnsureMtlTexture(source.Path);
    }

    /// <summary>Pre-resolve all five texture slots of one Surface, automatically skipping empty sources.</summary>
    void EnsureSurfaceTextures(string meshName, long meshId, int surfaceIndex, Season.Controls.Surface surface)
    {
        EnsureSurfaceTexture(meshName, meshId, surfaceIndex, surface, SurfaceTextureSlot.BaseColor);
        EnsureSurfaceTexture(meshName, meshId, surfaceIndex, surface, SurfaceTextureSlot.Normal);
        EnsureSurfaceTexture(meshName, meshId, surfaceIndex, surface, SurfaceTextureSlot.MetallicRoughness);
        EnsureSurfaceTexture(meshName, meshId, surfaceIndex, surface, SurfaceTextureSlot.Occlusion);
        EnsureSurfaceTexture(meshName, meshId, surfaceIndex, surface, SurfaceTextureSlot.Emissive);
    }

    /// <summary>Clear TextureOverride from all Surface slots after Load completes, following the one-shot consumption contract.</summary>
    static void ClearSurfaceOverrides(Season.Controls.Surface surface)
    {
        surface.ClearTextureOverride(SurfaceTextureSlot.BaseColor);
        surface.ClearTextureOverride(SurfaceTextureSlot.Normal);
        surface.ClearTextureOverride(SurfaceTextureSlot.MetallicRoughness);
        surface.ClearTextureOverride(SurfaceTextureSlot.Occlusion);
        surface.ClearTextureOverride(SurfaceTextureSlot.Emissive);
    }

    /// <summary>
    /// Build the slot-based resolver used by *Mesh3D.ProcessMaterial.
    /// Pixel sources look up their synthesized names and path sources look up their path names.
    /// Both resolve to MTLTexture instances already registered in DictionaryMtlTexture before Load.
    /// Missing entries return null and fall back to White.
    /// </summary>
    Func<Season.Controls.Surface, TextureSlot, MTLTexture> BuildSurfaceTextureResolver(string meshName, long meshId, IList<Season.Controls.Surface> surfaces)
    {
        return (surface, slot) =>
        {
            var source = surface.GetTextureSource((SurfaceTextureSlot)slot);
            if (!source.HasValue)
                return null!;

            var name = source.Image != null
                ? ProcTextureName(meshName, meshId, surfaces.IndexOf(surface), (SurfaceTextureSlot)slot)
                : source.Path;

            lock (DictionaryMtlTexture)
            {
                DictionaryMtlTexture.TryGetValue(name, out var tex);
                return tex!;
            }
        };
    }

    /// <summary>Release procedural textures registered under synthesized names for all five slots of one Surface. The caller must already hold the DictionaryMtlTexture lock.</summary>
    void ReleaseProcSurfaceTextures(string meshName, long meshId, int surfaceIndex)
    {
        ReleaseProcTexture(ProcTextureName(meshName, meshId, surfaceIndex, SurfaceTextureSlot.BaseColor));
        ReleaseProcTexture(ProcTextureName(meshName, meshId, surfaceIndex, SurfaceTextureSlot.Normal));
        ReleaseProcTexture(ProcTextureName(meshName, meshId, surfaceIndex, SurfaceTextureSlot.MetallicRoughness));
        ReleaseProcTexture(ProcTextureName(meshName, meshId, surfaceIndex, SurfaceTextureSlot.Occlusion));
        ReleaseProcTexture(ProcTextureName(meshName, meshId, surfaceIndex, SurfaceTextureSlot.Emissive));

        void ReleaseProcTexture(string name)
        {
            if (DictionaryMtlTexture.TryGetValue(name, out var tex) && tex != null)
            {
                tex.Release();
                if (tex.RefCount == 0)
                    DictionaryMtlTexture.Remove(name);
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

        // 1. Pre-resolve every texture source referenced by all Surfaces.
        //    Pixel sources upload directly to the GPU through CreateFromDecoder with no temp files,
        //    while path sources reuse EnsureMtlTexture and empty sources are skipped automatically.
        for (int i = 0; i < mesh.Surfaces.Count; i++)
            EnsureSurfaceTextures(mesh.Name, mesh.ID, i, mesh.Surfaces[i]);

        // 2. Construct MTLMesh3D by resolving cached MTLTexture objects per slot, with pure-color fallback when missing.
        var mtlMesh = new MTLMesh3D(mesh.Name);
        mtlMesh.Load(mesh, MTLPrimitiveGroup.Camera, BuildSurfaceTextureResolver(mesh.Name, mesh.ID, mesh.Surfaces));

        // 3. Clear TextureOverride after Load completes, following the one-shot consumption contract.
        foreach (var surface in mesh.Surfaces)
            ClearSurfaceOverrides(surface);

        lock (DictionaryMesh3D)
        {
            if (!DictionaryMesh3D.ContainsKey((mesh.Name, mesh.ID)))
                DictionaryMesh3D.Add((mesh.Name, mesh.ID), mtlMesh);
        }

        return true;
    }

    public void UpdateMesh3D(Season.Controls.Mesh3D mesh, float time)
    {
        MTLMesh3D mtlMesh = null!;
        lock (DictionaryMesh3D)
        {
            DictionaryMesh3D.TryGetValue((mesh.Name, mesh.ID), out mtlMesh!);
        }
        mtlMesh?.Update(mesh, time);
    }

    public void DrawMesh3D(Season.Controls.Mesh3D mesh)
    {
        if (mesh.Alpha == 0f)
            return;

        MTLMesh3D mtlMesh = null!;
        lock (DictionaryMesh3D)
        {
            DictionaryMesh3D.TryGetValue((mesh.Name, mesh.ID), out mtlMesh!);
        }
        mtlMesh?.Draw();
    }

    public void DisposeMesh3D(Season.Controls.Mesh3D mesh)
    {
        MTLMesh3D mtlMesh = null!;
        lock (DictionaryMesh3D)
        {
            var key = (mesh.Name, mesh.ID);
            if (DictionaryMesh3D.TryGetValue(key, out mtlMesh!))
                DictionaryMesh3D.Remove(key);
        }

        // Metal command buffers retain references to in-flight resources, see Device rule 5.
        // Releasing the C# wrapper immediately is safe,
        // and no delayed-release queue is needed, unlike the fence-gated paths on DX and VK.
        mtlMesh?.Dispose();

        // Release textures referenced by the Surface objects using MTLTexture reference counts.
        lock (DictionaryMtlTexture)
        {
            // Release procedural pixel-source textures slot by slot.
            // They are registered under synthesized names and owned privately by the mesh.
            for (int i = 0; i < mesh.Surfaces.Count; i++)
                ReleaseProcSurfaceTextures(mesh.Name, mesh.ID, i);

            foreach (var surface in mesh.Surfaces)
            {
                var path = surface.BaseColorTexturePath;
                if (string.IsNullOrEmpty(path)) continue;
                if (DictionaryMtlTexture.TryGetValue(path, out var mtlTex) && mtlTex != null)
                {
                    mtlTex.Release();
                    if (mtlTex.RefCount == 0)
                        DictionaryMtlTexture.Remove(path);
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

        // 1. Pre-resolve every texture source referenced by all Surfaces.
        //    Pixel sources upload directly to the GPU with no temp files, while path sources reuse EnsureMtlTexture.
        for (int i = 0; i < mesh.Surfaces.Count; i++)
            EnsureSurfaceTextures(mesh.Name, mesh.ID, i, mesh.Surfaces[i]);

        // 2. Construct MTLInstancedMesh3D by resolving cached MTLTexture objects per slot, with pure-color fallback when missing.
        var mtlMesh = new MTLInstancedMesh3D(mesh.Name);
        mtlMesh.Load(mesh, MTLPrimitiveGroup.Camera, BuildSurfaceTextureResolver(mesh.Name, mesh.ID, mesh.Surfaces));

        // 3. Clear TextureOverride after Load completes, following the one-shot consumption contract.
        foreach (var surface in mesh.Surfaces)
            ClearSurfaceOverrides(surface);

        lock (DictionaryInstancedMesh3D)
        {
            if (!DictionaryInstancedMesh3D.ContainsKey((mesh.Name, mesh.ID)))
                DictionaryInstancedMesh3D.Add((mesh.Name, mesh.ID), mtlMesh);
            else
                mtlMesh.Dispose();
        }

        return true;
    }

    public void UpdateInstancedMesh3D(InstancedMesh3D mesh, float time)
    {
        MTLInstancedMesh3D mtlMesh = null!;
        lock (DictionaryInstancedMesh3D)
        {
            DictionaryInstancedMesh3D.TryGetValue((mesh.Name, mesh.ID), out mtlMesh!);
        }
        mtlMesh?.Update(mesh, time);
    }

    public void DrawInstancedMesh3D(InstancedMesh3D mesh)
    {
        if (mesh.Alpha == 0f)
            return;

        MTLInstancedMesh3D mtlMesh = null!;
        lock (DictionaryInstancedMesh3D)
        {
            DictionaryInstancedMesh3D.TryGetValue((mesh.Name, mesh.ID), out mtlMesh!);
        }
        mtlMesh?.Draw();
    }

    public void DisposeInstancedMesh3D(InstancedMesh3D mesh)
    {
        MTLInstancedMesh3D mtlMesh = null!;
        lock (DictionaryInstancedMesh3D)
        {
            var key = (mesh.Name, mesh.ID);
            if (DictionaryInstancedMesh3D.TryGetValue(key, out mtlMesh!))
                DictionaryInstancedMesh3D.Remove(key);
        }
        mtlMesh?.Dispose();

        lock (DictionaryMtlTexture)
        {
            // Release procedural pixel-source textures slot by slot.
            // They are registered under synthesized names and owned privately by the mesh.
            for (int i = 0; i < mesh.Surfaces.Count; i++)
                ReleaseProcSurfaceTextures(mesh.Name, mesh.ID, i);

            foreach (var surface in mesh.Surfaces)
            {
                var path = surface.BaseColorTexturePath;
                if (string.IsNullOrEmpty(path))
                    continue;

                if (DictionaryMtlTexture.TryGetValue(path, out var mtlTex) && mtlTex != null)
                {
                    mtlTex.Release();
                    if (mtlTex.RefCount == 0)
                        DictionaryMtlTexture.Remove(path);
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

            var mtlInstancedModel = new MTLInstancedModel(model.ModelName);
            mtlInstancedModel.Load(template, wrapperModel, MTLPrimitiveGroup.Camera);

            // v2 picking:
            // inject the instanced GltfAsset so the node tree, animation, and bone palette stay aligned with instanced rendering.
            model.Asset = mtlInstancedModel.Asset;

            // 1-3:
            // write shared-template local bounds back into the control once during loading,
            // providing the instance-level sphere quick-cull data source.
            model.TemplateLocalBounds = template.Asset.Model.LocalBounds;
            // Follow the same unified positioning convention for the raw bounds as well,
            // providing the data source for instance anchors and per-axis scaling before animation enlargement.
            model.TemplateLocalBoundsRaw = template.Asset.Model.LocalBoundsRaw;

            var animNames = mtlInstancedModel.GetAnimationNames();
            model.AnimationClipCount = animNames.Count;
            model.AnimationNames = animNames;

            lock (DictionaryInstancedModel)
            {
                if (!DictionaryInstancedModel.ContainsKey((model.ModelName, model.ID)))
                    DictionaryInstancedModel.Add((model.ModelName, model.ID), mtlInstancedModel);
                else
                    mtlInstancedModel.Dispose();
            }
        });

        return true;
    }

    public void UpdateInstancedModel(InstancedModel model, float time)
    {
        MTLInstancedModel mtlModel = null!;
        lock (DictionaryInstancedModel)
        {
            DictionaryInstancedModel.TryGetValue((model.ModelName, model.ID), out mtlModel!);
        }
        mtlModel?.Update(model, time);
    }

    public void DrawInstancedModel(InstancedModel model)
    {
        if (model.Alpha == 0f)
            return;

        MTLInstancedModel mtlModel = null!;
        lock (DictionaryInstancedModel)
        {
            DictionaryInstancedModel.TryGetValue((model.ModelName, model.ID), out mtlModel!);
        }
        mtlModel?.Draw();
    }

    public void DisposeInstancedModel(InstancedModel model)
    {
        MTLInstancedModel mtlModel = null!;
        lock (DictionaryInstancedModel)
        {
            var key = (model.ModelName, model.ID);
            if (DictionaryInstancedModel.TryGetValue(key, out mtlModel!))
                DictionaryInstancedModel.Remove(key);
        }
        mtlModel?.Dispose();
    }

    public void DisposeModel(Model model)
    {
        MTLModel mtlModel = null!;
        lock (DictionaryModel)
        {
            var key = (model.Name, model.ID);
            if (DictionaryModel.TryGetValue(key, out mtlModel!))
                DictionaryModel.Remove(key);
        }
        mtlModel?.Dispose();

        // The shared template cache, DictionaryModelResource, is reused by all Model controls with the same name.
        // It does not participate in per-control release, matching the DisposeInstancedModel contract.
        model.Ready = false;
    }

    public void DisposeSprite2D(Sprite2D sprite)
    {
        MTLSprite2D mtlSprite2D = null!;

        lock (DictionarySprite)
        {
            var key = (sprite.Name, sprite.ID);
            if (DictionarySprite.TryGetValue(key, out mtlSprite2D!))
            {
                DictionarySprite.Remove(key);
            }
        }

        if (mtlSprite2D != null)
        {
            mtlSprite2D.SpriteRef = null; // Clear the reference.
            mtlSprite2D.Dispose();

            // Release the shared texture only when the Sprite was actually loaded and therefore holds a texture reference.
            lock (DictionaryMtlTexture)
            {
                if (DictionaryMtlTexture.TryGetValue(sprite.Name, out var mtlTex) && mtlTex != null)
                {
                    mtlTex.Release();
                    if (mtlTex.RefCount == 0)
                    {
                        DictionaryMtlTexture.Remove(sprite.Name);
                    }
                }
            }
        }

        sprite.Ready = false;
    }

    /// <summary>
    /// Trigger one batch texture upload on the transfer path.
    /// Equivalent to textureUploadBatch.Execute(commandList, queue) on DX and TextureUploadBatch.Execute() on VK.
    /// The Metal implementation wraps BlitCommandEncoder, Commit, and WaitUntilCompleted internally.
    /// </summary>
    public void ExecuteUpload()
    {
        Metal.Device.TextureUploadBatch?.Execute();
    }

    // -- Shape, procedural geometry --

    public async Task<bool> LoadShape(Season.Controls.Shape shape)
    {
        // Width and Height may be null during AddControl.
        // Casting null float? to int would throw and make Load fail.
        int shapeW = Math.Max(1, (int)(shape.Width ?? 1f));
        int shapeH = Math.Max(1, (int)(shape.Height ?? 1f));

        // RectFrame textures are determined by the tuple Type, W, H, and Border.
        // All other types keep Border at 0.
        // Use the same clamp range as CreateImageRectFrame, 1 to min(W,H)/2,
        // to avoid duplicate images under multiple equivalent keys.
        int shapeBorder = shape.Type == Season.Controls.ShapeType.RectFrame
            ? Math.Clamp((int)shape.Border, 1, Math.Min(shapeW, shapeH) / 2)
            : 0;

        var textureKey = shape.Type == Season.Controls.ShapeType.Dot
            ? (shape.Type, 1, 1, 0)
            : (shape.Type, shapeW, shapeH, shapeBorder);
        var instanceKey = (shape.Type, shape.ID);

        MTLSprite2D mtlSprite2D = null;

        lock (DictionaryShape)
        {
            if (shape.IsDisposed) return false;

            // A past failure may have cached a null entry.
            // Treat it as non-existent, remove it, and rebuild.
            if (DictionaryShape.TryGetValue(instanceKey, out mtlSprite2D)
                && (mtlSprite2D == null || mtlSprite2D.AlbedoTexture == null))
            {
                DictionaryShape.Remove(instanceKey);
                mtlSprite2D = null;
            }

            if (mtlSprite2D != null)
            {
                shape.OriginWidth = (int)mtlSprite2D.AlbedoTexture.Width;
                shape.OriginHeight = (int)mtlSprite2D.AlbedoTexture.Height;
            }
            else
            {
                // Fetch or create the shared shape texture, cached by Type, Width, and Height.
                MTLTexture mtlTexture = null;

                lock (DictionaryShapeTexture)
                {
                    if (DictionaryShapeTexture.TryGetValue(textureKey, out mtlTexture!))
                    {

                    }
                    else
                    {
                        var imageDecoder = Season.Models.ImageUtils.CreateShapeImage(shape.Type, shapeW, shapeH, shapeBorder);

                        if (imageDecoder != null)
                        {
                            mtlTexture = new MTLTexture(imageDecoder);
                            ExecuteUpload();
                        }

                        if (mtlTexture == null)
                        {
                            DeviceServices.BaseApp.AddLog(LogType.Backend, $"{DateTime.UtcNow} LoadShape MTLTexture=null {shape.Type}");
                        }
                        else
                        {
                            // Cache only on success so later requests with the same key are not polluted by null.
                            DictionaryShapeTexture[textureKey] = mtlTexture;
                        }
                    }
                }

                if (mtlTexture == null)
                {
                    // Shared-texture creation failed.
                    // Do not register an empty entry, and let Load return false so upper-layer logging can identify the failure.
                    return false;
                }

                try
                {
                    mtlSprite2D = new MTLSprite2D(mtlTexture);

                    shape.OriginWidth = (int)mtlSprite2D.AlbedoTexture.Width;
                    shape.OriginHeight = (int)mtlSprite2D.AlbedoTexture.Height;
                }
                catch (Exception ex)
                {
                    DeviceServices.BaseApp.AddLog(LogType.Error, $"{DateTime.UtcNow} LoadShape new MTLSprite2D {shape.Type} {ex}");

                    return false;
                }

                lock (DictionaryShape)
                {
                    if (!DictionaryShape.ContainsKey(instanceKey))
                    {
                        DictionaryShape.Add(instanceKey, mtlSprite2D);
                    }
                }
            }
        }

        return true;
    }

    public void UpdateShape(Season.Controls.Shape shape)
    {
        MTLSprite2D? mtlSprite = null;

        lock (DictionaryShape)
        {
            DictionaryShape.TryGetValue((shape.Type, shape.ID), out mtlSprite);
        }

        if (mtlSprite == null || mtlSprite.AlbedoTexture == null)
            return;

        shape.Ready = true;

        // Texture replacement.
        if (shape.TextureOverride.HasValue)
        {
            var source = shape.TextureOverride;
            shape.TextureOverride = default;
            ReplaceSpriteTexture(mtlSprite, source);
        }

        if (shape.Changed)
        {
            shape.Changed = false;
            mtlSprite.SpriteRef = shape;
            mtlSprite.Update();
        }
    }

    public void DrawShape(Season.Controls.Shape shape)
    {
        MTLSprite2D? mtlSprite = null;

        lock (DictionaryShape)
        {
            DictionaryShape.TryGetValue((shape.Type, shape.ID), out mtlSprite);
        }

        if (mtlSprite == null || mtlSprite.AlbedoTexture == null)
            return;

        mtlSprite.Draw();
    }

    public void DisposeShape(Season.Controls.Shape shape)
    {
        MTLSprite2D? mtlSprite = null;

        lock (DictionaryShape)
        {
            var key = (shape.Type, shape.ID);
            if (DictionaryShape.TryGetValue(key, out mtlSprite))
                DictionaryShape.Remove(key);
        }

        mtlSprite?.Dispose();

        shape.Ready = false;
    }

    // -- 1-6 Compute foundation, using kernel registration. See the contract in IGraphics and Compute.cs. --
    // Dispatch is recorded into the single IMTLCommandBuffer for the current frame, allocated by BeforeRender.
    // The FrameStart phase happens inside FrameSchedule.Execute before the first render pass,
    // so the render encoder is not open yet and opening and closing a compute encoder is valid because a pass is an encoder on Metal, rule 1.
    // There are zero explicit barriers:
    // the Metal driver tracks hazards automatically under rule 2,
    // and dispatch-to-draw write-to-read dependencies are guaranteed by queue order plus driver synchronization,
    // with no DX or VK style transition or barrier closing section.

    public bool ComputeSupported => Metal.Device.MtlDevice != null;

    /// <summary>Parameter-level validation is centralized here under the same rule set on all backends.
    /// Missing MSL source returns null for graceful fallback.
    /// Binding-declaration violations throw exceptions because they are programming errors.
    /// MSL compile or PSO creation failures are logged and return null for graceful registration-time fallback with zero platform residue.</summary>
    public Season.Rendering.ComputeKernel CreateComputeKernel(Season.Rendering.ComputeKernelDesc desc)
    {
        if (!ComputeSupported || string.IsNullOrEmpty(desc.Source.Msl))
            return null!;

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
                throw new ArgumentException($"[CreateComputeKernel] '{desc.Name}': Params must be 16-byte aligned and less than or equal to 128 bytes, got {size}.");
        }

        try
        {
            return new MTLComputeKernel(desc);
        }
        catch (Exception ex)
        {
            DeviceServices.BaseApp.AddLog(LogType.Error, $"{DateTime.UtcNow} [CreateComputeKernel] '{desc.Name}' compile or creation failed: {ex.Message}");
            return null!;
        }
    }

    /// <summary>Register storage textures into DictionaryMtlTexture by name.
    /// LoadSprite2D can then hit them, AddRef them, and skip file loading.
    /// Sprite2D consumes them by name with zero code changes, equivalent to the two-ended registration used by DictionaryWGPUTexture on the Web backend.
    /// CreateEmpty already uses ShaderRead, ShaderWrite, and Private,
    /// which satisfies both compute writes and fragment sampling.
    /// There is no upload chain because there are no initial pixels, so creation means ready.
    /// In 2-1, rgba16float is used for the HDR intermediate bloom chain.
    /// In 1-8, format mapping goes through MTLTexture.MapComputeFormat as the single source of truth, shared with the 3D path.</summary>
    public void CreateComputeTexture(string name, uint width, uint height,
        Season.Rendering.ComputeStorageFormat format = Season.Rendering.ComputeStorageFormat.Rgba8Unorm)
    {
        var mtlFormat = MTLTexture.MapComputeFormat(format);
        lock (DictionaryMtlTexture)
        {
            if (DictionaryMtlTexture.TryGetValue(name, out var existing))
            {
                // Recreate in place when the size no longer matches.
                // The C# object identity stays stable so Sprite2D AddRef references remain valid.
                if (existing.Width != width || existing.Height != height)
                    existing.Recreate(width, height);
                return;
            }
            var tex = MTLTexture.CreateEmpty(width, height, name, mtlFormat);
            tex.Ready = true;
            DictionaryMtlTexture.Add(name, tex);
        }
    }

    /// <summary>In 1-8, 3D storage textures live in the dedicated static dictionary of <see cref="MTLTexture3D"/> and are not written into DictionaryMtlTexture.
    /// DictionaryMtlTexture is consumed by Sprite2D, LoadSprite2D, and materials by name,
    /// and writing 3D textures there would let those 2D paths fetch k3D textures by mistake.
    /// Cubemap in 1-7 already follows the same isolation rule.
    /// Visualization of 3D volumes must go through an effect-side 3D-to-2D slice kernel.
    /// This backend supports all five format intents natively, with no fallback chain,
    /// so no format-capability validation is needed.
    /// Creation failure is only logged.
    /// If the texture is not created, DispatchCompute simply cannot find it by name and skips the frame,
    /// reusing the existing resource-not-ready semantics.</summary>
    public void CreateComputeTexture3D(string name, uint width, uint height, uint depth,
        Season.Rendering.ComputeStorageFormat format = Season.Rendering.ComputeStorageFormat.Rgba8Unorm)
    {
        if (!ComputeSupported) return;
        try
        {
            MTLTexture3D.CreateOrUpdate(name, width, height, depth, MTLTexture3D.MapComputeFormat(format));
        }
        catch (Exception ex)
        {
            DeviceServices.BaseApp.AddLog(LogType.Error, $"{DateTime.UtcNow} [CreateComputeTexture3D] '{name}' "
                + $"{width}x{height}x{depth} creation failed: {ex.Message}");
        }
    }

    public Season.Rendering.StorageBuffer CreateStorageBuffer(uint sizeInBytes)
        => new MTLStorageBuffer(sizeInBytes);

    /// <summary>In 1-8, constant-block upload uses StorageModeShared staging plus a blit encoder because MTLStorageBuffer is StorageModePrivate and has no CPU mapping.
    /// It must be recorded into the current-frame GraphicsCommandBuffer and must stay outside any pass.
    /// Under the Metal rule that a pass is an encoder, rule 1, a blit encoder cannot be opened while a render encoder is active,
    /// and the contract also restricts this method to the frame-loop thread outside passes.
    /// Ordering against the following dispatch is guaranteed by encoder order inside the same command buffer
    /// plus automatic driver hazard tracking, with no explicit barrier under rule 2.
    /// In step 0 of 2-4, staging changed into an N-buffered ring owned by the buffer itself,
    /// partitioned by FrameIndex and kept alive after creation, so per-frame calls allocate nothing and avoid in-flight frame races.
    /// See MTLStorageBuffer.TryGetStagingForCurrentFrame.</summary>
    public void UpdateStorageBuffer(Season.Rendering.StorageBuffer buffer, ReadOnlySpan<byte> data)
    {
        if (!ComputeSupported || data.Length == 0) return;
        var cmd = Metal.Device.GraphicsCommandBuffer;
        if (cmd == null) return;

        var dst = (MTLStorageBuffer)buffer;
        int size = (int)Math.Min((uint)data.Length, dst.SizeInBytes);
        if (size <= 0) return;

        var staging = dst.TryGetStagingForCurrentFrame();
        if (staging == null) return;

        fixed (byte* pSrc = data)
        {
            Unsafe.CopyBlock((void*)staging.Contents, pSrc, (uint)size);
        }

        var blit = cmd.CreateBlitCommandEncoder(new MTLBlitPassDescriptor());
        if (blit == null)
        {
            DeviceServices.BaseApp.AddLog(LogType.Backend, $"{DateTime.UtcNow} [UpdateStorageBuffer] CreateBlitCommandEncoder failed, render encoder may still be open, skipping this upload.");
            return;
        }
        blit.CopyFromBuffer(staging, 0, dst.Buffer, 0, (nuint)size);
        blit.EndEncoding();
    }

    // -- 1-7 Cubemap, see the contract summary in the IGraphics and RenderQuality class headers --
    // Cubes are registered by name into the dedicated static dictionary of MTLTextureCube.
    // They are not merged into DictionaryMtlTexture because that dictionary carries Texture2D semantics
    // and is consumed by Sprite2D and materials by name.
    // Mixing cube textures into it would let those paths fetch a dimension they cannot sample.

    public bool TextureCubeSupported => Metal.Device.MtlDevice != null;

    /// <summary>Face order is +X, -X, +Y, -Y, +Z, -Z, which maps to cube slices 0 through 5.
    /// The shared layer has already validated that all six faces are square and share the same size.
    /// Resource creation and upload happen synchronously, so the returned object is ready to use.
    /// Creation failure is logged and returns null, gracefully falling back to the 1-2 constant ambient-light path,
    /// matching the same rule on D3D12 and Vulkan.</summary>
    public Season.Rendering.TextureCube CreateTextureCube(string name, int size,
        Season.Rendering.TextureCubeFormat format, INativeImageDecoder[] faces)
    {
        if (!TextureCubeSupported) return null;
        try
        {
            var cube = MTLTextureCube.CreateFromDecoders(name, size, format, faces);
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
        var cmd = Metal.Device.GraphicsCommandBuffer;
        if (cmd == null)
            return;

        var kernel = (MTLComputeKernel)args.Kernel;
        var bindings = kernel.Desc.Bindings;

        var enc = cmd.ComputeCommandEncoder;
        if (enc == null)
            return;
        enc.Label = kernel.Label;

        enc.SetComputePipelineState(kernel.PipelineState);

        // Params go through SetBytes at buffer(0), using inline small constants with no buffer object required.
        if (kernel.ParamsSize > 0)
        {
            fixed (byte* pParams = args.Params)
            {
                enc.SetBytes((IntPtr)pParams, kernel.ParamsSize, 0);
            }
        }

        // Mechanical per-binding mapping:
        // textures go to texture in declaration order,
        // buffers go to buffer in declaration order plus 1,
        // and SampledTexture uses sampler(0), the static linear-clamp sampler, which only needs one bind.
        // If a resource is not ready, either name not registered or upload not finished,
        // end encoding and skip this frame's dispatch. An empty encoder has no side effects.
        int r = 0;
        nuint textureIdx = 0, bufferIdx = 0;
        bool samplerBound = false;
        for (int i = 0; i < bindings.Length; i++)
        {
            if (bindings[i].Type == Season.Rendering.ComputeBindingType.Params)
                continue;

            ref readonly var res = ref args.Resources[r++];

            if (res.Buffer is MTLStorageBuffer buffer)
            {
                enc.SetBuffer(buffer.Buffer, 0, bufferIdx + 1);
                bufferIdx++;
                continue;
            }

            // Step D of 2-1:
            // sample RT color directly, for example bloom reading SceneColor.
            // There is zero explicit barrier because the driver tracks hazards automatically under rule 2,
            // and ColorTexture already carries ShaderRead usage.
            // If the target is not ready, such as during lazy recreation, skip this frame's dispatch.
            // Step C of 2-2:
            // DepthTexture resolves to the DepthTexture of a depth-only RT, namely SceneDepth under contract clause 3,
            // where the depth-only shape already carries ShaderRead usage and MSL access::read needs no sampler.
            if (res.Target is MTLRenderTarget targetRT)
            {
                bool wantDepth = bindings[i].Type == Season.Rendering.ComputeBindingType.DepthTexture;
                var rtTex = wantDepth ? targetRT.DepthTexture : targetRT.ColorTexture;
                if (rtTex == null)
                {
                    enc.EndEncoding();
                    return;
                }
                enc.SetTexture(rtTex, textureIdx);
                textureIdx++;
                if (bindings[i].Type == Season.Rendering.ComputeBindingType.SampledTexture && !samplerBound)
                {
                    enc.SetSamplerState(Metal.Pipeline.StaticSampler, 0);
                    samplerBound = true;
                }
                continue;
            }

            // In 1-8, 3D bindings look up the dedicated MTLTexture3D dictionary.
            // DictionaryMtlTexture carries Texture2D semantics, and mixing them would silently bind the wrong dimension.
            // 3D and 2D textures share the same textureIdx numbering domain because MSL [[texture(n)]]
            // uses one flat index space regardless of texture dimension, matching the shared register-counting convention of HLSL t and u registers.
            if (bindings[i].Type == Season.Rendering.ComputeBindingType.SampledTexture3D
                || bindings[i].Type == Season.Rendering.ComputeBindingType.StorageTexture3DWrite)
            {
                var tex3d = MTLTexture3D.Find(res.TextureName);
                if (tex3d == null || !System.Threading.Volatile.Read(ref tex3d.Ready))
                {
                    enc.EndEncoding();
                    return;
                }
                enc.SetTexture(tex3d.Image, textureIdx);
                textureIdx++;
                if (bindings[i].Type == Season.Rendering.ComputeBindingType.SampledTexture3D && !samplerBound)
                {
                    // StaticSampler uses ClampToEdge and Linear on S, T, and R,
                    // so MSL sample naturally gives trilinear filtering plus edge clamping.
                    // 1-8 needs no new sampler here, see the MTLTexture3D class header.
                    enc.SetSamplerState(Metal.Pipeline.StaticSampler, 0);
                    samplerBound = true;
                }
                continue;
            }

            MTLTexture? tex = null;
            if (res.TextureName != null)
            {
                lock (DictionaryMtlTexture)
                {
                    DictionaryMtlTexture.TryGetValue(res.TextureName, out tex);
                }
            }
            if (tex == null || !System.Threading.Volatile.Read(ref tex.Ready))
            {
                enc.EndEncoding();
                return;
            }

            enc.SetTexture(tex.Image, textureIdx);
            textureIdx++;

            if (bindings[i].Type == Season.Rendering.ComputeBindingType.SampledTexture)
            {
                // The Metal upload chain is synchronized on the CPU through cmd.WaitUntilCompleted.
                // Once Ready is true, pixels are resident, so only the self-check value needs clearing.
                tex.UploadFenceValue = 0;
                if (!samplerBound)
                {
                    enc.SetSamplerState(Metal.Pipeline.StaticSampler, 0);
                    samplerBound = true;
                }
            }
        }

        // In 1-8, threadgroup size comes from kernel.ThreadsPerGroup.
        // MSL has no compile-time declaration for it, so the second dispatch argument is the only source of truth.
        // The value comes from ComputeKernelDesc.WorkgroupX, Y, and Z, defaulting to 8,8,1.
        // The existing seven effects keep the default unchanged,
        // so behavior stays literally equivalent to the previous line with zero regression.
        // This backend is the only one among the four that consumes this field at runtime.
        enc.DispatchThreadgroups(
            new MTLSize((nint)args.GroupsX, (nint)args.GroupsY, (nint)args.GroupsZ),
            kernel.ThreadsPerGroup);
        enc.EndEncoding();

        // No post synchronization is needed under rule 2.
        // Resource lifetime is guaranteed by command-buffer retained references under rule 5.
    }
}
