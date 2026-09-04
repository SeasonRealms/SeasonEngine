// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Microsoft.JSInterop;
using Season.Fonts;
// [SkyDebug] Diagnostic use of Unsafe.SizeOf<SceneLightParams> (for invisible-starfield investigation; remove after the issue is resolved)
using System.Runtime.CompilerServices;

namespace Season.Platforms.Web;

internal sealed class WebInstancedDiagState
{
    public int drawCalls { get; set; }
    public string? lastCacheKey { get; set; }
    public int lastInstanceCount { get; set; }
    public int lastInstanceBytes { get; set; }
    public string? lastModeKey { get; set; }
    public bool deviceLost { get; set; }
    public string? deviceLostReason { get; set; }
    public string? uncapturedError { get; set; }
    public string? lastError { get; set; }
}

/// <summary>
/// WebGPU-side implementation of <see cref="IGraphics"/>. All real GPU objects live in JS in
/// <c>seasonWebGPU.js</c>, and this class forwards calls through <c>[JSImport]</c>.
///
/// ── Index of WebGPU platform-specific rules (stabilized in 1-1 Step 0~3; see the shared-layer
/// summaries of PassDesc/FrameSchedule/IGraphics for cross-platform contracts):
/// ① The pass state machine lives on the JS side: <c>_passEncoder</c> is a JS global, and every draw
///    function references it implicitly. Switching it in <c>beginPass</c> automatically routes all
///    existing draws to the current pass. The C# side only flattens <see cref="PassDesc"/> into scalar
///    arguments for forwarding (struct JSInterop serialization has known issues) and does not own any
///    pass state.
/// ② This is a zero-barrier platform: the browser tracks resource states automatically and performs
///    attachment↔sampling transitions implicitly, so the shared-layer contract of "no barriers inside a
///    pass" is naturally satisfied (contrast: VK relies on render-pass baking + deferred queues, DX on
///    state tracking).
/// ③ Pipeline↔pass attachment-set compatibility (validation is strictest on this platform, and errors are
///    reported asynchronously to the console rather than thrown):
///    the main pipeline is baked for the Scene target format (LDR = preferred format, HDR tier =
///    rgba16float, 1-4 Step A) plus a depth24plus depth-stencil. Under HDR, the Scene pass always renders
///    offscreen (<c>HdrSceneColor</c> already includes the <c>UseOffscreenSceneColor</c> condition), so the
///    main pipeline never renders the backbuffer. The blit pipeline is always baked for the preferred
///    format (FinalBlit always renders the backbuffer). A depth-only pass (depth32float shadow map, since
///    depth24plus cannot be sampled) requires the dedicated 1-5 pipeline. The Overlay pass renders the
///    backbuffer directly and therefore needs the overlay family (baked with preferred format +
///    depth-always/no-write; after the first pass uses <c>storeOp=discard</c>, backbuffer depth contents are
///    undefined on load. Routing is done via JS <c>_passOverlay</c>, mirroring the VK overlay PSO family).
///    Always check the browser console when doing regression validation.
/// ④ Offscreen render targets use name-as-handle: the C# <c>WGPURenderTarget</c> only stores the string
///    <c>"rt_{id}"</c>, while the real resource lives in JS <c>_renderTargets[name]</c>. Match-backbuffer
///    targets are lazily rebuilt when <c>beginPass</c> resolves them (the name stays stable). The four blit
///    variants auto-select point/linear based on source size (point = identity mapping via
///    <c>textureLoad(fragCoord)</c>, linear = UV sampling with NDC→UV Y flip), and tonemap is selected
///    automatically from the source format (rgba16float, 1-4 Step A).
/// ⑤ Submission is merged: one encoder, one submit, with multiple passes represented as multiple segments
///    inside that encoder. Pending batches (Mesh3D/Skinned) must be flushed inside <c>EndPass</c> so they
///    land on the current pass encoder before the pass closes. The single source of truth for WGSL lives in
///    <c>WebGPUPipeline.cs</c> (<c>Mesh3DShader</c>/<c>BlitShader</c> are sent once during initialization;
///    JS contains no shader source). Any change to <c>seasonWebGPU.js</c> must be synced to all three copies
///    (<c>src/Platforms/Web/js</c> as the source, then the <c>wwwroot/js</c> copies in CreatorWeb and
///    SampleWeb) and verified by building both TFMs.
/// ⑥ 1-5 shadows (completed across all four backends; see the shared summaries of
///    CascadedShadow/LightParams/RenderQuality for the contract):
///    the JS-side <c>_passDepthOnly</c> performs implicit routing. Once <c>beginPass(depthOnly)</c> sets the
///    flag, <c>drawMesh3DBatch</c>/<c>drawInstancedMesh3D</c> automatically switch to the shadow pipeline
///    (vertex-only, cullMode none, bias baked in) and skip transparent objects. C# fully reuses the batch
///    dispatch path and adds no new draw-specific JSImport. The light matrix reuses the Projection slot while
///    View is set to Identity (the existing pre-multiply chain stays correct, so no shader variant split is
///    needed). A dedicated shadow bind-group layout (only 0/7/8/9) avoids validation errors when the atlas
///    is bound as an attachment. The atlas also uses name-as-handle (<c>setShadowAtlas</c> stores the name
///    and bind-group creation resolves it, with a 1×1 dummy depth view as fallback). In <c>RenderShadowPass</c>,
///    each quadrant must flush the two batches before switching the viewport because batch submission is
///    deferred while <c>setViewport</c> takes effect immediately.
/// ⑦ 1-7 cubemap + environment IBL (see the summary at the top of RenderQuality): cube resources also use
///    name-as-handle (<c>createTextureCube</c> creates a six-layer rgba8unorm texture with
///    <c>viewDimension:'cube'</c> and stores it in JS <c>_textureCubes</c>; <c>setEnvCube</c> stores the name
///    every frame and bind-group creation resolves it at binding 15, with a 1×1 all-black fallback cube when
///    unregistered). The sampler reuses binding 1, and the vertex-only shadow layout does not need to grow
///    because the cube is only referenced statically from <c>fs_main</c>. Environment parameters
///    (<c>EnvParams</c>/<c>IrradianceSH9</c>) are appended to the lighting UBO and uploaded together with the
///    full 1136-byte block, so no extra UBO is introduced. WGSL always samples through
///    <c>textureSampleLevel(..., 0.0)</c> to avoid uniformity / implicit-derivative restrictions.
/// </summary>
internal class Graphics : IGraphics
{
    // ── HDR SceneColor (1-4 Step A, mirrored from DX/VK Device; WebGPU has no separate Device class,
    // and this class hosts the behavior) ──
    // WebApp finalizes this from the RenderQuality tier before constructing Graphics / calling
    // InitializeAsync (main pipeline bake + WGSL upload). It must not change afterward.
    // false = Step 2 baseline (BackbufferCompatible, one-click fallback).
    internal static bool HdrSceneColor;

    /// <summary>
    /// Linearize the HDR-chain clear color: the display-space background color is approximated to linear
    /// space with <c>pow(2.2)</c>, which is the inverse of the <c>pow(1/2.2)</c> encoding in the FinalBlit
    /// tonemap variant. This keeps the background visually aligned with the LDR baseline. Alpha is unchanged.
    /// </summary>
    internal static System.Numerics.Vector4 LinearizeClearColor(in System.Numerics.Vector4 c) => new(
        MathF.Pow(c.X, 2.2f), MathF.Pow(c.Y, 2.2f), MathF.Pow(c.Z, 2.2f), c.W);

    internal readonly IJSInProcessRuntime _jsRuntime;

    internal static readonly float[] _scratchMatrix48 = new float[48];
    internal static readonly float[] _scratchUniform = new float[WebGPUUniformLayout.TotalFloats];

    internal static byte[] ToByteArray(float[] source)
    {
        if (source == null || source.Length == 0)
            return Array.Empty<byte>();

        var bytes = new byte[source.Length * sizeof(float)];
        Buffer.BlockCopy(source, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    internal static byte[] ToByteArray(ushort[] source)
    {
        if (source == null || source.Length == 0)
            return Array.Empty<byte>();

        var bytes = new byte[source.Length * sizeof(ushort)];
        Buffer.BlockCopy(source, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    internal static byte[] ToByteArray(uint[] source)
    {
        if (source == null || source.Length == 0)
            return Array.Empty<byte>();

        var bytes = new byte[source.Length * sizeof(uint)];
        Buffer.BlockCopy(source, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    internal static byte[] ToByteArray(Matrix4x4[] source)
    {
        if (source == null || source.Length == 0)
            return Array.Empty<byte>();

        var floats = new float[source.Length * 16];
        for (int i = 0; i < source.Length; i++)
        {
            int offset = i * 16;
            var m = source[i];
            floats[offset + 0] = m.M11; floats[offset + 1] = m.M12; floats[offset + 2] = m.M13; floats[offset + 3] = m.M14;
            floats[offset + 4] = m.M21; floats[offset + 5] = m.M22; floats[offset + 6] = m.M23; floats[offset + 7] = m.M24;
            floats[offset + 8] = m.M31; floats[offset + 9] = m.M32; floats[offset + 10] = m.M33; floats[offset + 11] = m.M34;
            floats[offset + 12] = m.M41; floats[offset + 13] = m.M42; floats[offset + 14] = m.M43; floats[offset + 15] = m.M44;
        }

        return ToByteArray(floats);
    }

    /// <summary>Aligned with <see cref="WebGPUPipeline.UniformBytes"/> (432 bytes) and defined uniformly by the shared layer.</summary>
    const int BYTES_PER_UNIFORM = WebGPUPipeline.UniformBytes;
    // The Web backend previously uploaded only one primitive per frame, so complex skinned glTF assets
    // could remain in a "partially uploaded" state for a long time, which looked inconsistent with
    // native animation/skinning. Raising the budget lets a full model finish uploading within a few frames.
    const int MAX_STATIC_MESH_UPLOADS_PER_FRAME = 4;
    readonly List<string> _batchCacheKeys = new(128);
    byte[] _batchUniformBytes = new byte[128 * BYTES_PER_UNIFORM];
    int _batchCount = 0;
    readonly List<string> _skinnedBatchCacheKeys = new(128);
    byte[] _skinnedBatchUniformBytes = new byte[128 * BYTES_PER_UNIFORM];
    int _skinnedBatchCount = 0;
    string? _currentSkinnedBatchSkinKey;
    readonly Queue<PendingStaticMeshUpload> _pendingStaticMeshUploads = new();
    readonly Dictionary<string, PendingStaticMeshUpload> _pendingStaticMeshUploadLookup = new();
    readonly HashSet<string> _uploadedStaticMeshKeys = new();

    // 1-5: Current quadrant light-space VP (written per quadrant by RenderShadowPass and placed into the
    // Projection slot by the DrawShadow path; the View slot is Identity, so the WGSL chain
    // u.projection*u.view*worldPos = LVP^T * v is equivalent to CPU-side pos * LVP).
    internal Matrix4x4 _shadowViewProj = Matrix4x4.Identity;

    public void Init()
    {

    }

    internal void EnqueueDrawMesh3D(string cacheKey, float[] uniform100)
    {
        int needed = (_batchCount + 1) * BYTES_PER_UNIFORM;
        if (needed > _batchUniformBytes.Length)
        {
            int newCap = Math.Max(needed, _batchUniformBytes.Length + _batchUniformBytes.Length / 2);
            newCap = ((newCap + BYTES_PER_UNIFORM - 1) / BYTES_PER_UNIFORM) * BYTES_PER_UNIFORM;
            Array.Resize(ref _batchUniformBytes, newCap);
        }

        Buffer.BlockCopy(uniform100, 0, _batchUniformBytes, _batchCount * BYTES_PER_UNIFORM, BYTES_PER_UNIFORM);

        if (_batchCacheKeys.Count == _batchCount)
            _batchCacheKeys.Add(cacheKey);
        else
            _batchCacheKeys[_batchCount] = cacheKey;

        _batchCount++;
    }

    internal void BeginSkinnedModelDraw(string skinKey, byte[] boneMatricesBytes)
    {
        FlushDrawMesh3DBatch();

        if (_skinnedBatchCount > 0 && !string.Equals(_currentSkinnedBatchSkinKey, skinKey, StringComparison.Ordinal))
            FlushDrawSkinnedMeshBatch();

        _currentSkinnedBatchSkinKey = skinKey;

        if (boneMatricesBytes != null && boneMatricesBytes.Length > 0)
            WebGPUInterop.UploadSkinnedBones(skinKey, boneMatricesBytes);
    }

    internal void EnqueueDrawSkinnedMesh(string cacheKey, float[] uniform100)
    {
        if (string.IsNullOrEmpty(_currentSkinnedBatchSkinKey))
            throw new InvalidOperationException("Skinned batch requires BeginSkinnedModelDraw before enqueue.");

        int needed = (_skinnedBatchCount + 1) * BYTES_PER_UNIFORM;
        if (needed > _skinnedBatchUniformBytes.Length)
        {
            int newCap = Math.Max(needed, _skinnedBatchUniformBytes.Length + _skinnedBatchUniformBytes.Length / 2);
            newCap = ((newCap + BYTES_PER_UNIFORM - 1) / BYTES_PER_UNIFORM) * BYTES_PER_UNIFORM;
            Array.Resize(ref _skinnedBatchUniformBytes, newCap);
        }

        Buffer.BlockCopy(uniform100, 0, _skinnedBatchUniformBytes, _skinnedBatchCount * BYTES_PER_UNIFORM, BYTES_PER_UNIFORM);

        if (_skinnedBatchCacheKeys.Count == _skinnedBatchCount)
            _skinnedBatchCacheKeys.Add(cacheKey);
        else
            _skinnedBatchCacheKeys[_skinnedBatchCount] = cacheKey;

        _skinnedBatchCount++;
    }

    internal void FlushDrawMesh3DBatch()
    {
        if (_batchCount == 0) return;

        // [JSImport] Pass the scratch slice directly as MemoryView, avoiding a fresh byte[] copy on each
        // flush. cacheKeys are copied into an exact string[] instead of allocating a List via GetRange,
        // because JSImport does not support List<string>.
        var keys = new string[_batchCount];
        _batchCacheKeys.CopyTo(0, keys, 0, _batchCount);

        WebGPUInterop.DrawMesh3DBatch(
            keys,
            _batchUniformBytes.AsSpan(0, _batchCount * BYTES_PER_UNIFORM),
            _batchCount,
            null);

        _batchCount = 0;
    }

    internal void FlushDrawSkinnedMeshBatch()
    {
        if (_skinnedBatchCount == 0 || string.IsNullOrEmpty(_currentSkinnedBatchSkinKey))
            return;

        var keys = new string[_skinnedBatchCount];
        _skinnedBatchCacheKeys.CopyTo(0, keys, 0, _skinnedBatchCount);

        WebGPUInterop.DrawMesh3DBatch(
            keys,
            _skinnedBatchUniformBytes.AsSpan(0, _skinnedBatchCount * BYTES_PER_UNIFORM),
            _skinnedBatchCount,
            _currentSkinnedBatchSkinKey);

        _skinnedBatchCount = 0;
    }

    internal void EndSkinnedModelDraw()
    {
        FlushDrawSkinnedMeshBatch();
        _currentSkinnedBatchSkinKey = null;
    }

    internal void EnqueueStaticMeshUpload(
        string ownerName,
        WGPUPrimitiveData prim,
        string textureName,
        string normalName,
        string mrName,
        string aoName,
        string emissiveName)
    {
        if (string.IsNullOrEmpty(prim.CacheKey))
            prim.CacheKey = $"MDL:{ownerName}:{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(prim):X}";

        if (_uploadedStaticMeshKeys.Contains(prim.CacheKey))
        {
            prim.Uploaded = true;
            prim.UploadQueued = false;
            prim.LastTextureName = textureName;
            prim.LastNormalTextureName = normalName;
            prim.LastMRTextureName = mrName;
            prim.LastAOTextureName = aoName;
            prim.LastEmissiveTextureName = emissiveName;
            return;
        }

        if (_pendingStaticMeshUploadLookup.TryGetValue(prim.CacheKey, out var pending))
        {
            pending.TextureName = textureName;
            pending.NormalTextureName = normalName;
            pending.MRTextureName = mrName;
            pending.AOTextureName = aoName;
            pending.EmissiveTextureName = emissiveName;
            pending.Primitive = prim;
            return;
        }

        var upload = new PendingStaticMeshUpload
        {
            OwnerName = ownerName,
            Primitive = prim,
            TextureName = textureName,
            NormalTextureName = normalName,
            MRTextureName = mrName,
            AOTextureName = aoName,
            EmissiveTextureName = emissiveName,
        };

        prim.UploadQueued = true;
        _pendingStaticMeshUploads.Enqueue(upload);
        _pendingStaticMeshUploadLookup[prim.CacheKey] = upload;

        DeviceServices.BaseApp.AddLog(LogType.Backend, $"{DateTime.UtcNow} [ModelGPU] queued upload cacheKey={prim.CacheKey} owner={ownerName}");
    }

    internal void UpdateStaticMeshVertices(WGPUPrimitiveData prim)
    {
        if (prim == null || !prim.Uploaded || prim.VertexBytes == null || prim.VertexBytes.Length == 0)
            return;

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        WebGPUInterop.UpdateStaticMeshVertices(prim.CacheKey, prim.VertexBytes);
    }

    void ProcessPendingStaticMeshUploads()
    {
        int processed = 0;
        while (processed < MAX_STATIC_MESH_UPLOADS_PER_FRAME && _pendingStaticMeshUploads.Count > 0)
        {
            var pending = _pendingStaticMeshUploads.Dequeue();
            _pendingStaticMeshUploadLookup.Remove(pending.Primitive.CacheKey);

            var prim = pending.Primitive;
            if (prim == null || prim.Uploaded || prim.VertexBytes == null || prim.IndexBytes == null)
                continue;

            var uploadStaticMeshStopwatch = System.Diagnostics.Stopwatch.StartNew();
            // [JSImport] Stream MB-scale vertex/index/morph bytes directly via MemoryView, avoiding interop
            // packaging and extra byte[] attachment bookkeeping.
            WebGPUInterop.UploadStaticMesh(
                prim.CacheKey,
                prim.VertexBytes,
                prim.IndexBytes,
                pending.TextureName,
                pending.NormalTextureName,
                pending.MRTextureName,
                pending.AOTextureName,
                pending.EmissiveTextureName,
                prim.VertexStrideFloats,
                prim.Use32BitIndices ? "uint32" : "uint16",
                prim.DoubleSided,
                prim.HasSkinning,
                prim.MorphDeltasBytes,
                (int)prim.MorphTargetCount,
                (int)prim.MorphVertexCount);

            prim.Uploaded = true;
            prim.UploadQueued = false;
            prim.LastTextureName = pending.TextureName;
            prim.LastNormalTextureName = pending.NormalTextureName;
            prim.LastMRTextureName = pending.MRTextureName;
            prim.LastAOTextureName = pending.AOTextureName;
            prim.LastEmissiveTextureName = pending.EmissiveTextureName;
            _uploadedStaticMeshKeys.Add(prim.CacheKey);
            processed++;
        }

        if (processed > 0)
            DeviceServices.BaseApp.AddLog(LogType.Backend, $"{DateTime.UtcNow} [ModelGPU] processed uploads={processed}, remaining={_pendingStaticMeshUploads.Count}");
    }
    readonly string _canvasId;
    readonly string _assetBasePath;
    bool _initialized;

    internal static Season.Basic.Camera Camera3D;

    // 1-2 Contract 8: mirrored scene lighting (SceneLightParams, 976B after the 2-3 expansion). After
    // UpdateCamera3D injects exposure and jitter each frame, the whole block is uploaded into the shared
    // UBO at JS binding(10). It is no longer inlined into each 108-float draw uniform
    // (those slots are reused for prev-data starting at 2-3; see WebGPUUniformLayout.PrevWorld).
    internal static SceneLightParams Light3D;

    Dictionary<string, WGPUTexture> DictionaryWGPUTexture = new();
    Dictionary<(string, long), WGPUSprite2D> DictionarySprite = new();

    // ── Unified GlyphAtlasManager atlas (aligned with Windows/VK/Metal) ──
    GlyphAtlasManager<WGPUTexture> _glyphAtlas;
    uint _frameIndex;

    // ── Shape (procedural geometry) ──
    Dictionary<(Season.Controls.ShapeType, int, int, int), WGPUTexture> DictionaryShapeTexture = new();
    Dictionary<(Season.Controls.ShapeType, long), WGPUSprite2D> DictionaryShape = new();
    Dictionary<(string, long), WGPUModel> DictionaryModel = new();
    Dictionary<string, Task<WGPUModel>> DictionaryModelResource = new();
    Dictionary<(string, long), WGPUSprite3D> DictionarySprite3D = new();
    Dictionary<(string, long), WGPUMesh3D> DictionaryMesh3D = new();
    Dictionary<(string, long), WGPUInstancedMesh3D> DictionaryInstancedMesh3D = new();
    Dictionary<(string, long), WGPUInstancedModel> DictionaryInstancedModel = new();
    int _instancedDiagPollCounter = 0;
    string? _lastInstancedDiagSignature;

    // Phase 4 (Outline pass): lazy-created mask RT + frame-level aggregation state
    // (mirrors VK/Metal; reset at the start of each RenderOutlineMask call).
    WGPURenderTarget? _outlineMaskTarget;
    bool _outline2DFrameActive;
    float _outline2DFrameWidth;

    public Graphics(IJSInProcessRuntime jsRuntime, string canvasId, HttpClient httpClient, string assetBasePath = "")
    {
        _jsRuntime = jsRuntime;
        _canvasId = canvasId;
        _httpClient = httpClient;
        _assetBasePath = assetBasePath?.Trim().Trim('/') ?? string.Empty;

        _glyphAtlas = new GlyphAtlasManager<WGPUTexture>(
            2048, 2048,
            createAtlasTexture: (w, h) =>
            {
                _jsRuntime.InvokeVoid("seasonWebGPU.createAtlasTexture", "TextAtlas", w, h);
                return WGPUTexture.CreateFromPixels("TextAtlas", (uint)w, (uint)h);
            },
            uploadFullPixels: (tex, pixels) =>
            {
                // Full-atlas upload: the atlas itself is already tightly packed row data for a single rect
                // (bytesPerRow = w * 4).
                WebGPUInterop.UploadGlyphAtlasPackedRects("TextAtlas", pixels,
                    new int[] { 0, 0, 2048, 2048 });
            },
            uploadSubRects: (tex, pixels, atlasW, atlasH, rects) =>
            {
                // Compact dirty-rect upload: extract and concatenate row data for each rect from the full
                // atlas (KB scale), replacing the old uploadGlyphAtlasSubRects path that resent the entire
                // 16 MB atlas every time.
                int totalBytes = 0;
                for (int i = 0; i < rects.Length; i++)
                    totalBytes += rects[i].Width * rects[i].Height * 4;

                var packed = new byte[totalBytes];
                var flatRects = new int[rects.Length * 4];
                int offset = 0;
                for (int i = 0; i < rects.Length; i++)
                {
                    var r = rects[i];
                    flatRects[i * 4] = r.X;
                    flatRects[i * 4 + 1] = r.Y;
                    flatRects[i * 4 + 2] = r.Width;
                    flatRects[i * 4 + 3] = r.Height;
                    int rowBytes = r.Width * 4;
                    for (int row = 0; row < r.Height; row++)
                    {
                        Buffer.BlockCopy(pixels, ((r.Y + row) * atlasW + r.X) * 4, packed, offset, rowBytes);
                        offset += rowBytes;
                    }
                }
                WebGPUInterop.UploadGlyphAtlasPackedRects("TextAtlas", packed, flatRects);
            },
            getCurrentFrameIndex: () => _frameIndex);
    }

    public async Task InitializeAsync()
    {
        // HDR-chain switch (1-4 Step A, mirroring HDR_CHAIN injection on DX/VK): WGSL has no preprocessor,
        // so replace the foldable const with true before upload (compile-time constant folding, zero runtime
        // branch). Changing the tier requires a fresh initialization run.
        // 2-3 Contract 3: inject VELOCITY_OUTPUT with the same pattern (MotionVectors is fixed at
        // initialization time, so only one form is baked per process).
        // Argument 4: the Scene target format baked into the JS-side main pipeline (true = rgba16float).
        // Arguments 5/6 (1-5 Contract 4): shadow bias baked into the shadow pipeline at initialization.
        // Argument 7 (2-3): when true, bake an additional MRT (fs_main_mrt) pipeline variant for Scene-pass routing.
        // Argument 8: the overlay-family module (HDR_CHAIN=false version; pass null on the LDR tier so JS reuses the main module).
        var meshShader = HdrSceneColor
            ? WebGPUPipeline.Mesh3DShader.Replace(
                "const HDR_CHAIN : bool = false;", "const HDR_CHAIN : bool = true;")
            : WebGPUPipeline.Mesh3DShader;
        bool velocityOutput = RenderQuality.Current.MotionVectors;
        if (velocityOutput)
            meshShader = meshShader.Replace(
                "const VELOCITY_OUTPUT : bool = false;", "const VELOCITY_OUTPUT : bool = true;");

        // 2-4 Clause 10: DDGI consumer-side variant switch (mirrors DDGI_ENABLED on DX/VK/Metal and is
        // fixed at initialization time).
        // Step 6: use Settings.RenderQuality as the priority source for the tier (persistent; null falls
        // back to the static default source), matching DdgiEffect.Initialize so the consumer variant and
        // atlas resources are created in sync.
        if ((Season.Basic.DeviceServices.BaseApp?.Settings?.RenderQuality?.GlobalIllumination ?? RenderQuality.DefaultGlobalIllumination) == Season.Rendering.GiMode.Ddgi)
            meshShader = meshShader.Replace(
                "const DDGI_ENABLED : bool = false;", "const DDGI_ENABLED : bool = true;");

        // Overlay-family module (aligned with the Metal overlay library at HDR_CHAIN=0): textually identical
        // to the main module except HDR_CHAIN is forced to false. Overlay renders directly to the backbuffer
        // without FinalBlit, so semantics such as text inverse-ACES compensation / Sprite2D linear direct
        // output under HDR_CHAIN=1 are not reusable here; output must stay in display space
        // (pixel-equivalent to the LDR baseline). Other replacements (VELOCITY_OUTPUT/DDGI_ENABLED) remain
        // identical to the main module so the binding layouts and entry-point set stay in sync.
        string? overlayMeshShader = HdrSceneColor
            ? meshShader.Replace("const HDR_CHAIN : bool = true;", "const HDR_CHAIN : bool = false;")
            : null;
        await _jsRuntime.InvokeVoidAsync("seasonWebGPU.initialize", _canvasId, meshShader, WebGPUPipeline.BlitShader, HdrSceneColor,
            RenderQuality.Current.ShadowDepthBias, RenderQuality.Current.ShadowSlopeScaledDepthBias,
            velocityOutput, overlayMeshShader);
        _initialized = true;
    }

    string ResolveAssetPath(string assetName)
    {
        if (string.IsNullOrWhiteSpace(assetName))
            return assetName;

        if (Uri.TryCreate(assetName, UriKind.Absolute, out _))
            return assetName;

        var relativePath = assetName.TrimStart('/');
        if (string.IsNullOrEmpty(_assetBasePath))
            return relativePath;

        return $"{_assetBasePath}/{relativePath}";
    }

    public void BeginFrame(float r = 1f, float g = 1f, float b = 1f, float a = 1f)
    {
        if (!_initialized) return;
        _frameIndex++;
        WebGPUInterop.BeginFrame(r, g, b, a);
    }

    // ── Pass orchestration (1-1 Step 0+1/2/3): the pass state machine lives in JS
    // (_passEncoder switching, with all draw functions routed implicitly). This side only forwards a
    // flattened PassDesc. Offscreen targets are passed down as name handles (Step 2), and target
    // resolution follows ColorTarget ?? DepthTarget (aligned with DX/VK: color RTs carry their own depth,
    // while depth-only targets are shadow maps).

    public void BeginPass(in Season.Rendering.PassDesc desc)
    {
        if (!_initialized) return;
        // 2-2 dual-target Scene pass: when color + explicit depth coexist, rebind the depth plane to
        // SceneDepth (resolved through JS depthTargetName). All other shapes keep the existing
        // ColorTarget ?? DepthTarget behavior (color RTs carry their paired depth, depth-only targets are shadow maps).
        var target = (desc.ColorTarget ?? desc.DepthTarget) as WGPURenderTarget;
        var depthTarget = (desc.ColorTarget != null ? desc.DepthTarget : null) as WGPURenderTarget;
        // Linearize the clear color for HDR targets (Rgba16Float): this is the inverse of the
        // FinalBlit tonemap variant's pow(1/2.2), keeping the perceived background color aligned with the
        // LDR baseline (the LDR path passes through unchanged).
        var cc = (target != null && target.Desc.ColorFormat == Season.Rendering.RtFormat.Rgba16Float)
            ? LinearizeClearColor(desc.ClearColor)
            : desc.ClearColor;
        WebGPUInterop.BeginPass(
            (int)desc.Id,
            target?.Name,
            desc.ClearColorEnable,
            cc.X, cc.Y, cc.Z, cc.W,
            desc.ClearDepthEnable,
            desc.StoreDepth,
            depthTarget?.Name,
            (desc.VelocityTarget as WGPURenderTarget)?.Name);
    }

    public void EndPass()
    {
        if (!_initialized) return;
        // Pending batches must land on the current passEncoder before the pass closes
        // (empty batches early-return, so flushing across multiple passes is harmless).
        FlushDrawMesh3DBatch();
        FlushDrawSkinnedMeshBatch();
        WebGPUInterop.EndPass();
    }

    // ── Offscreen RT / FinalBlit (1-1 Step 2/3): real resources live on the JS side and are referenced
    // via name-as-handle (see WGPURenderTarget).

    public Season.Rendering.RenderTarget CreateRenderTarget(in Season.Rendering.RenderTargetDesc desc)
    {
        if (!_initialized)
            throw new InvalidOperationException("[CreateRenderTarget] 需在图形初始化完成后调用。");
        // Step 3 has two shapes (aligned with DX/VK): color-only
        // (BackbufferCompatible/Rgba16Float, with its paired depth) and depth-only
        // (D32Float shadow map, used both as attachment and sampled resource). formatKind is encoded for JS (0/1/2).
        // 2-2: depth-only + MatchBackbufferSize = SceneDepth → formatKind 3 (depth24plus, matching the depth
        // format already baked into the pipeline, which is required for dual-target Scene-pass rebinding).
        // Fixed-size depth targets (shadow maps) still use 2.
        // 2-3: Rg16Float color-only = SceneVelocity → formatKind 4
        // (JS creates no paired depth and no blit bind group; it is only used as MRT slot 1 and as a compute
        // sampling source, never as a standalone pass target or present source).
        int formatKind;
        if (desc.ColorFormat == Season.Rendering.RtFormat.BackbufferCompatible && desc.DepthFormat == Season.Rendering.RtFormat.None)
            formatKind = 0;
        else if (desc.ColorFormat == Season.Rendering.RtFormat.Rgba16Float && desc.DepthFormat == Season.Rendering.RtFormat.None)
            formatKind = 1;
        else if (desc.ColorFormat == Season.Rendering.RtFormat.None && desc.DepthFormat == Season.Rendering.RtFormat.D32Float)
            formatKind = desc.MatchBackbufferSize ? 3 : 2;
        else if (desc.ColorFormat == Season.Rendering.RtFormat.Rg16Float && desc.DepthFormat == Season.Rendering.RtFormat.None)
            formatKind = 4;
        else
            throw new NotSupportedException("[CreateRenderTarget] 仅支持 color-only（BackbufferCompatible/Rgba16Float/Rg16Float）或 depth-only（D32Float）形态。");
        if (desc.SampleCount > 1)
            throw new NotSupportedException("[CreateRenderTarget] 离屏 MSAA 暂无消费者。");
        if (!desc.MatchBackbufferSize && (desc.Width == 0 || desc.Height == 0))
            throw new ArgumentException("[CreateRenderTarget] 固定尺寸 RT 需指定非零 Width/Height。");

        var rt = new WGPURenderTarget(desc);
        WebGPUInterop.CreateRenderTarget(rt.Name, (int)desc.Width, (int)desc.Height, desc.MatchBackbufferSize, formatKind);
        return rt;
    }

    /// <summary>2-3 Contract Clause 12: resolve the TAA output from FrameSchedule.SceneColorOverride into
    /// a JS-side texture name and forward it (this backend uses name-as-handle, with the real view stored in
    /// JS <c>_textureViews[name]</c>). This mirrors Windows/Graphics.cs ResolveSceneOverrideTexture, but
    /// there is no C# texture object to hand over here, so this side only validates registration and then
    /// forwards the name. null / unregistered means no override, and JS falls back to the RT's own colorView
    /// with no residue.</summary>
    string? ResolveSceneOverrideName()
    {
        var sceneName = Season.Rendering.FrameSchedule.SceneColorOverride;
        if (sceneName == null) return null;
        lock (DictionaryWGPUTexture)
            return DictionaryWGPUTexture.ContainsKey(sceneName) ? sceneName : null;
    }

    public void BlitToBackbuffer(Season.Rendering.RenderTarget src)
    {
        if (!_initialized) return;
        // 2-1 Step D: when the source is the LDR PostColor produced by the post uber pass
        // (luma stored in alpha), switch to the FXAA variant for presentation. This is mutually exclusive
        // with tonemap/bloom because composition has already completed in Post, matching Windows/Graphics.cs.
        // 2-3: along this path, the HDR→LDR composition point already happened inside the post uber pass
        // (where the override was consumed), so no override is applied here.
        // Phase 4: when Outline2D is active, also pass through the mask RT and the frame-level max width so
        // the JS blit path can do the final on-screen composition.
        if (ReferenceEquals(src, Season.Rendering.FrameSchedule.PostColor))
        {
            WebGPUInterop.BlitToBackbuffer(((WGPURenderTarget)src).Name, 0f, null, 0f, fxaa: true, null, 0f, null,
                _outline2DFrameActive ? _outlineMaskTarget?.Name : null, _outline2DFrameWidth);
            return;
        }
        // 1-4 Step B: exposure is passed with the blit every frame (Contract 5).
        // 2-1: the bloom-chain output is passed by name (null = no bloom; JS falls back to the original
        // tonemap variant automatically if it does not resolve it). These parameters are harmless when the
        // source is LDR and the tonemap variant does not apply.
        // 2-2 Step C: the AO chain output is also passed by name (JS switches to the AO variant only when
        // the source is HDR and the resource resolves; otherwise it falls back automatically).
        // 2-3 Clause 12: without the FXAA tier, this is the last HDR→LDR composition point before present,
        // so the scene source switches to the override here.
        WebGPUInterop.BlitToBackbuffer(
            ((WGPURenderTarget)src).Name,
            RenderQuality.Current.HdrExposure,
            Season.Rendering.FrameSchedule.BloomTexture,
            RenderQuality.Current.BloomIntensity,
            fxaa: false,
            Season.Rendering.FrameSchedule.AoTexture,
            RenderQuality.Current.AoIntensity,
            ResolveSceneOverrideName(),
            _outline2DFrameActive ? _outlineMaskTarget?.Name : null,
            _outline2DFrameWidth);
    }

    /// <summary>2-1 Step D: contents of the Post pass (FrameSchedule.RenderPost callback; the FXAA tier and
    /// PostColor are registered together): the uber pass composites tonemap(+bloom) into LDR PostColor and
    /// packs luma into alpha. After composition moved here, FinalBlit degraded into FXAA resolve; see the
    /// RenderQuality 1-4 Contract 1 revision (mirrors Windows/Graphics.cs).
    /// 2-3 Clause 12: under the FXAA tier, this becomes the last HDR→LDR composition point before present,
    /// so the scene source also resolves through the override here.</summary>
    internal void RenderPostPass(Season.Basic.IGraphics g, Season.Rendering.RenderTarget sceneColor)
    {
        if (!_initialized) return;
        WebGPUInterop.RenderPost(
            ((WGPURenderTarget)sceneColor).Name,
            RenderQuality.Current.HdrExposure,
            Season.Rendering.FrameSchedule.BloomTexture,
            RenderQuality.Current.BloomIntensity,
            Season.Rendering.FrameSchedule.AoTexture,
            RenderQuality.Current.AoIntensity,
            ResolveSceneOverrideName());
    }

    public void EndFrame()
    {
        if (!_initialized) return;
        WebGPUInterop.EndFrame();
        ProcessPendingStaticMeshUploads();
        ProcessPendingStaticMeshUploads();
    }

    // ── 1-6 Compute foundation (WebGPU is the primary validation backend; see shared-layer Compute.cs and
    // default IGraphics member comments for the contract) ──
    // Real pipelines/textures/buffers all live on the JS side via name-as-handle. This is a zero-barrier
    // platform, so synchronization is naturally guaranteed by encode order inside a single encoder
    // (see rule ⑤ in the class header for merged submission semantics). Compilation errors are reported
    // asynchronously to the console (rule ③); synchronous return values only cover parameter-level failure.

    public bool ComputeSupported => _initialized;

    /// <summary>Parameter-level validation converges here (same rules across all four backends): missing WGSL
    /// source returns null as a graceful fallback; invalid binding declarations throw exceptions as programming errors.</summary>
    public Season.Rendering.ComputeKernel? CreateComputeKernel(Season.Rendering.ComputeKernelDesc desc)
    {
        if (!_initialized || string.IsNullOrEmpty(desc.Source.Wgsl))
            return null;

        var bindings = desc.Bindings;
        desc.ValidateWorkgroupSize();
        var sb = new StringBuilder(bindings.Length * 24 + 2);
        sb.Append('[');
        for (int i = 0; i < bindings.Length; i++)
        {
            if (bindings[i].Type == Season.Rendering.ComputeBindingType.Params)
            {
                if (i != 0)
                    throw new ArgumentException($"[CreateComputeKernel] '{desc.Name}': Params 必须位于 Bindings[0]。");
                var size = bindings[i].SizeInBytes;
                if (size == 0 || size % 16 != 0 || size > 128)
                    throw new ArgumentException($"[CreateComputeKernel] '{desc.Name}': Params 需 16B 对齐且 ≤128B（得到 {size}）。");
            }
            if (i > 0) sb.Append(',');
            // 2-1: pass StorageFormat per slot (JS only consumes it for storage-write bindings of type 3/7;
            // it is harmless for the other binding kinds).
            // 1-8: type 6/7 takes the 3D branch (JS layoutEntries sets viewDimension:'3d'), so the protocol
            // does not need extra fields.
            sb.Append("{\"type\":").Append((int)bindings[i].Type)
              .Append(",\"size\":").Append(bindings[i].SizeInBytes)
              .Append(",\"format\":").Append((int)bindings[i].StorageFormat).Append('}');
        }
        sb.Append(']');

        var kernel = new WGPUComputeKernel(desc);
        if (!WebGPUInterop.CreateComputeKernel(kernel.JsName, desc.Source.Wgsl, desc.Source.EntryPoint, sb.ToString()))
            return null;
        return kernel;
    }

    /// <summary>Register storage textures on both sides in sync: JS <c>_textures[name]</c> for draw paths,
    /// plus <c>DictionaryWGPUTexture</c> so LoadSprite2D can early-return on hit without doing an HTTP fetch.
    /// Sprite2D then consumes it by name unchanged.
    /// 2-1: pass the formatKind encoding to JS (0=rgba8unorm, 1=rgba16float), aligned with ComputeStorageFormat.</summary>
    public void CreateComputeTexture(string name, uint width, uint height,
        Season.Rendering.ComputeStorageFormat format = Season.Rendering.ComputeStorageFormat.Rgba8Unorm)
    {
        WebGPUInterop.CreateComputeTexture(name, (int)width, (int)height, (int)format);
        lock (DictionaryWGPUTexture)
        {
            if (DictionaryWGPUTexture.TryGetValue(name, out var existing))
            {
                // JS has already rebuilt the GPUTexture in place while keeping the same name; sync the C# metadata.
                existing.Width = width;
                existing.Height = height;
            }
            else
                DictionaryWGPUTexture[name] = new WGPUTexture { Name = name, Width = width, Height = height };
        }
    }

    public Season.Rendering.StorageBuffer CreateStorageBuffer(uint sizeInBytes)
    {
        var buffer = new WGPUStorageBuffer(sizeInBytes);
        WebGPUInterop.CreateStorageBuffer(buffer.Id, (int)sizeInBytes);
        return buffer;
    }

    /// <summary>1-8: 3D storage textures are registered only into the dedicated JS-side <c>_textures3d</c>
    /// dictionary and **not** into <c>DictionaryWGPUTexture</c>. The latter is the name-based lookup source
    /// for Sprite2D/LoadSprite2D and materials, so writing 3D textures there would make those 2D paths hit a
    /// 3D view instead (the 1-7 cubemap path already follows the same isolation rule). Visualization of 3D
    /// volumes must go through an effect-local 3D→2D slicing kernel.
    /// Creation failure only logs an error (JS already emits console.error), preserving the same graceful
    /// registration-time fallback semantics as the other three backends.</summary>
    public void CreateComputeTexture3D(string name, uint width, uint height, uint depth,
        Season.Rendering.ComputeStorageFormat format = Season.Rendering.ComputeStorageFormat.Rgba8Unorm)
    {
        if (!_initialized) return;
        if (!WebGPUInterop.CreateComputeTexture3D(name, (int)width, (int)height, (int)depth, (int)format))
            DeviceServices.BaseApp.AddLog(LogType.Backend,
                $"{DateTime.UtcNow} [CreateComputeTexture3D] '{name}' {width}×{height}×{depth} 创建失败");
    }

    /// <summary>1-8: constant-block upload (restricted to the frame-loop thread and outside render passes;
    /// JS uses <c>queue.writeBuffer</c>, and ordering relative to later dispatches is naturally guaranteed by
    /// queue submission order, so no barrier is needed).
    /// 2-4 Step 0: this backend can call it every frame without slot partitioning. The spec guarantees that
    /// queue.writeBuffer copies the data at call time, so there is no race with in-flight frames sharing the
    /// same staging buffer. The scratch buffer only grows when capacity is insufficient, so allocations are
    /// amortized to zero.</summary>
    public void UpdateStorageBuffer(Season.Rendering.StorageBuffer buffer, ReadOnlySpan<byte> data)
    {
        if (!_initialized || data.Length == 0) return;
        // JSImport MemoryView does not accept ReadOnlySpan, so route through a growable scratch buffer
        // (same pattern as DispatchCompute params, except constant blocks can exceed 128B and therefore grow
        // on demand). JS copies the data during the synchronous call and does not retain it afterward.
        if (_storageUploadScratch.Length < data.Length)
            _storageUploadScratch = new byte[Math.Max(data.Length, _storageUploadScratch.Length * 2)];
        data.CopyTo(_storageUploadScratch);
        WebGPUInterop.UpdateStorageBuffer(
            ((WGPUStorageBuffer)buffer).Id, _storageUploadScratch.AsSpan(0, data.Length));
    }

    static byte[] _storageUploadScratch = new byte[256];

    // ReadOnlySpan → MemoryView requires Span because JSImport does not support ReadOnlySpan. Route through
    // a static 128B scratch buffer. During the synchronous call, JS copies it via slice() (_interopToU8),
    // so nothing is retained across calls. The frame loop is single-threaded, so there is no contention.
    static readonly byte[] _computeParamsScratch = new byte[128];

    public void DispatchCompute(in Season.Rendering.ComputeDispatchArgs args)
    {
        if (!_initialized) return;
        var kernel = (WGPUComputeKernel)args.Kernel;
        args.Params.CopyTo(_computeParamsScratch);
        WebGPUInterop.DispatchCompute(
            kernel.JsName,
            _computeParamsScratch.AsSpan(0, args.Params.Length),
            kernel.ResolveResourcesJson(args.Resources),
            (int)args.GroupsX, (int)args.GroupsY, (int)args.GroupsZ);
    }

    // ── 1-7 Cubemap (see the summaries at the top of IGraphics / RenderQuality for the contract) ──
    // Real resources live in JS-side _textureCubes through name-as-handle, matching the 1-6 storage-texture
    // convention. The C# side keeps no handle object. Binding 15 in the main-pass bind group is resolved
    // from the SetEnvCube name pushed each frame by UpdateCamera3D.

    public bool TextureCubeSupported => _initialized;

    /// <summary>Face order is +X,-X,+Y,-Y,+Z,-Z (cube layers 0..5). Equal square face dimensions are already
    /// validated by the shared layer. This method packs the six decoded RGBA8 faces into one tightly packed
    /// contiguous buffer and crosses the interop boundary only once. JSImport MemoryView copies on every
    /// call, so sending six times would only add five extra boundary crossings. On creation failure, log and
    /// return null, gracefully degrading to the 1-2 constant ambient-light path, consistent with the other three backends.</summary>
    public Season.Rendering.TextureCube? CreateTextureCube(string name, int size,
        Season.Rendering.TextureCubeFormat format, INativeImageDecoder[] faces)
    {
        if (!TextureCubeSupported) return null;
        try
        {
            if (format != Season.Rendering.TextureCubeFormat.Rgba8Unorm)
                throw new NotSupportedException($"'{name}': 1-7 当前仅实装 Rgba8Unorm（得到 {format}）。");
            if (faces == null || faces.Length != 6)
                throw new ArgumentException($"'{name}': 需恰好 6 张面贴图。", nameof(faces));

            // The decoder contract is tightly packed RGBA8, but row-end padding is allowed
            // (Stride > size * 4), so repack row by row into a tight buffer.
            // A stride smaller than one RGBA8 row means the decoder violated the contract
            // (for example, unexpanded RGB output), so report it explicitly instead of triggering an out-of-range failure.
            int dstStride = size * 4;
            int faceLength = dstStride * size;
            var packed = new byte[faceLength * 6];
            for (int f = 0; f < 6; f++)
            {
                var decoder = faces[f];
                if (decoder == null || decoder.Width != size || decoder.Height != size)
                    throw new ArgumentException(
                        $"'{name}': 面 {(Season.Rendering.CubeFace)f} 尺寸不符（期望 {size}×{size}）。");
                if (decoder.Stride < dstStride)
                    throw new ArgumentException(
                        $"'{name}': 面 {(Season.Rendering.CubeFace)f} 的解码器违反 INativeImageDecoder 的 " +
                        $"RGBA8 契约（Stride={decoder.Stride} < {dstStride}，疑为未展开的 RGB 三通道数据）。");

                var src = decoder.PixelSpan;
                int srcStride = decoder.Stride;
                int dstBase = f * faceLength;
                for (int y = 0; y < size; y++)
                    src.Slice(y * srcStride, dstStride).CopyTo(new Span<byte>(packed, dstBase + y * dstStride, dstStride));
            }

            if (!WebGPUInterop.CreateTextureCube(name, size, packed.AsSpan()))
                return null;

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
            DeviceServices.BaseApp.AddLog(LogType.Error, $"{DateTime.UtcNow} [CreateTextureCube] '{name}' 创建失败: {ex.Message}");
            return null;
        }
    }


    async Task<bool> LoadTextureAsync(string name, bool deferDecodeToNextFrame = false)
    {
        if (DictionaryWGPUTexture.ContainsKey(name))
        {
            return true;
        }

        var url = ResolveAssetPath(name);
        var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = await _jsRuntime.InvokeAsync<WebTextureUploadResult>("seasonWebGPU.loadTexture", name, url, deferDecodeToNextFrame);
        var success = result?.success == true;
        if (success)
        {
            DictionaryWGPUTexture[name] = new WGPUTexture
            {
                Name = name,
                Width = (uint)(result?.width ?? 0),
                Height = (uint)(result?.height ?? 0),
            };
            return true;
        }

        DeviceServices.BaseApp.AddLog(LogType.Backend, $"{DateTime.UtcNow} [LoadTextureAsync] FAILED name={name}");
        return false;
    }


    public async Task<bool> LoadSprite2D(Sprite2D sprite2D)
    {
        WGPUSprite2D wgpuSprite = null;

        lock (DictionarySprite)
        {
            if (sprite2D.IsDisposed) return false;

            if (DictionarySprite.TryGetValue((sprite2D.Name, sprite2D.ID), out wgpuSprite))
            {
                if (wgpuSprite != null && wgpuSprite.WGPUTexture != null)
                {
                    sprite2D.OriginWidth = (int)wgpuSprite.WGPUTexture.Width;
                    sprite2D.OriginHeight = (int)wgpuSprite.WGPUTexture.Height;
                }
                return true;
            }
        }

        await LoadTextureAsync(sprite2D.Name);

        WGPUTexture wgpuTex = null;
        lock (DictionaryWGPUTexture)
        {
            if (DictionaryWGPUTexture.TryGetValue(sprite2D.Name, out wgpuTex))
            {
                wgpuTex.AddRef();
            }
        }

        wgpuSprite = new WGPUSprite2D(wgpuTex ?? new WGPUTexture { Name = sprite2D.Name });

        if (wgpuTex != null)
        {
            sprite2D.OriginWidth = (int)wgpuTex.Width;
            sprite2D.OriginHeight = (int)wgpuTex.Height;
        }

        lock (DictionarySprite)
        {
            if (!DictionarySprite.ContainsKey((sprite2D.Name, sprite2D.ID)))
                DictionarySprite.Add((sprite2D.Name, sprite2D.ID), wgpuSprite);
        }

        return true;
    }

    public void UpdateSprite2D(Sprite2D sprite)
    {
        if (DictionarySprite.TryGetValue((sprite.Name, sprite.ID), out var wgpuSprite))
        {
            sprite.Ready = true;

            // ── Texture replacement ──
            if (sprite.TextureOverride.HasValue)
            {
                var source = sprite.TextureOverride;
                sprite.TextureOverride = default;
                ReplaceSpriteTexture(wgpuSprite, source);
            }

            // ── Changed gate: precompute NDC on layout changes to avoid repeating the same JS interop work every frame ──
            if (sprite.Changed)
            {
                sprite.Changed = false;

                var app = DeviceServices.BaseApp;
                float scaleX = app.Scale, scaleY = app.Scale;
                float screenW = app.DeviceResolution.X, screenH = app.DeviceResolution.Y;

                float x = sprite.PosX * scaleX;
                float y = sprite.PosY * scaleY;
                float w = ((float)sprite.Width > 0 ? (float)sprite.Width : sprite.OriginWidth) * scaleX;
                float h = ((float)sprite.Height > 0 ? (float)sprite.Height : sprite.OriginHeight) * scaleY;

                wgpuSprite.CachedNdcX = (x / screenW) * 2f - 1f;
                wgpuSprite.CachedNdcY = 1f - (y / screenH) * 2f;
                wgpuSprite.CachedNdcW = (w / screenW) * 2f;
                wgpuSprite.CachedNdcH = -(h / screenH) * 2f;
                wgpuSprite.CachedAlpha = sprite.Alpha;
                wgpuSprite.CachedColor = sprite.Color;
                wgpuSprite.CachedFlipX = sprite.FlipX;
                wgpuSprite.CachedFlipY = sprite.FlipY;
                wgpuSprite.CachedClock = sprite.Clock;
                wgpuSprite.CachedSourceX = sprite.SourceX;
                wgpuSprite.CachedSourceY = sprite.SourceY;
                wgpuSprite.CachedSourceWidth = sprite.SourceWidth;
                wgpuSprite.CachedSourceHeight = sprite.SourceHeight;
                wgpuSprite.TransformCached = true;
            }
        }
    }

    /// <summary>Resolve <see cref="TextureUpdateSource"/> into an <see cref="INativeImageDecoder"/>. Image takes priority over Path.</summary>
    INativeImageDecoder? ResolveDecoder(TextureUpdateSource source)
    {
        if (source.Image != null) return source.Image;
        if (source.Path == null) return null;
        // Web-side path decode
        return ImageUtils.CreateImage(source.Path);
    }

    /// <summary>Replace the Sprite's single texture. The Web backend always creates an independent texture to avoid incorrect sharing heuristics.</summary>
    void ReplaceSpriteTexture(WGPUSprite2D wgpuSprite, TextureUpdateSource source)
    {
        var decoder = ResolveDecoder(source);
        if (decoder == null) return;

        var rgba = decoder.PixelSpan.ToArray();  // Managed array crossing the JS interop boundary
        int w = decoder.Width, h = decoder.Height;
        decoder.Dispose();

        string newName = $"sprTex_{Guid.NewGuid():N}";
        _jsRuntime.InvokeVoid("seasonWebGPU.createTextureFromPixels",
            newName, rgba, w, h, /*forceNew=*/true);

        wgpuSprite.WGPUTexture = WGPUTexture.CreateFromPixels(newName, (uint)w, (uint)h);
    }

    public void DrawSprite2D(Sprite2D sprite)
    {
        if (!_initialized || !DictionarySprite.TryGetValue((sprite.Name, sprite.ID), out var wgpuSprite) || !wgpuSprite.TransformCached)
            return;

        float ndcX = wgpuSprite.CachedNdcX;
        float ndcY = wgpuSprite.CachedNdcY;
        float ndcW = wgpuSprite.CachedNdcW;
        float ndcH = wgpuSprite.CachedNdcH;
        float alpha = wgpuSprite.CachedAlpha;
        Vector4 color = wgpuSprite.CachedColor;
        bool flipX = wgpuSprite.CachedFlipX;
        bool flipY = wgpuSprite.CachedFlipY;

        FlushDrawMesh3DBatch();
        FlushDrawSkinnedMeshBatch();

        WebGPUInterop.DrawSprite2D(
            wgpuSprite.WGPUTexture.Name,
            ndcX, ndcY, ndcW, ndcH,
            alpha,
            color.X, color.Y, color.Z, color.W,
            flipX, flipY,
            0f, 0f,
            wgpuSprite.CachedClock,
            wgpuSprite.CachedSourceX, wgpuSprite.CachedSourceY,
            wgpuSprite.CachedSourceWidth, wgpuSprite.CachedSourceHeight);
    }

    // ── Texts (GPU instancing architecture based on GlyphAtlasManager, aligned with DX/VK) ──

    internal sealed class TextGlyphHolder : ITextureHolder
    {
        public Texture Texture { get; set; } = new Texture();
    }

    /// <summary>
    /// GPU-instancing state for a single Texts control.
    /// Web backend: JS keeps persistent instance/glyph buffers indexed by Key
    /// (queue.writeBuffer has no in-flight-frame race here, so a single copy is enough).
    /// </summary>
    sealed class WGPUTextInstanceState
    {
        public string Key;
        public int InstanceCount;
        /// <summary>12 floats per instance: uvRect(4) + color(4) + metrics(4), matching the DX TextGlyphData layout.</summary>
        public float[] GlyphFloats;
        /// <summary>20 floats per instance: world(16) + morphWeights(4), matching the WGPUInstancedMesh3D instance stream.</summary>
        public float[] InstanceFloats;
        public int GlyphAtlasVersionBuilt = -1;
        public bool GlyphDirty = true;
        public bool CanDraw;
    }

    const int GlyphFloatsPerInstance = 12;
    const int InstanceFloatsPerInstance = 20;

    readonly Dictionary<Texts, WGPUTextInstanceState> _textInstances = new();
    readonly object _textInstancesLock = new();
    static int _textInstanceSeed;

    bool TryGetTextInstanceState(Texts texts, out WGPUTextInstanceState state)
    {
        state = null;
        if (texts == null)
            return false;
        lock (_textInstancesLock)
            return _textInstances.TryGetValue(texts, out state);
    }

    public async Task<bool> LoadTexts(Texts texts)
    {
        if (texts?.TexsLoading?.Length == 0)
            return false;

        var texsLoading = texts.TexsLoading;
        int totalCount = texsLoading.Length + (texts.ShowDot ? 1 : 0);

        // Phase 1: count valid glyphs and ensure every glyph is already in the atlas.
        var validIndices = new int[totalCount];
        int validCount = 0;

        for (int i = 0; i < texsLoading.Length; i++)
        {
            ref var tex = ref texsLoading[i];
            if (tex.TexType is TexType.NewLine or TexType.Space or TexType.Missing)
                continue;
            if (!TryEnsureGlyphEntry(ref tex, out _))
                continue;
            validIndices[validCount++] = i;
        }

        if (texts.ShowDot && TryEnsureGlyphEntry(ref texts._dotRef, out _))
            validIndices[validCount++] = -1;  // -1 represents the dot glyph

        if (validCount == 0)
            return false;

        // Phase 2: create holders and per-text state.
        // Keep the initial data hidden (zero matrices). The real layout is computed and uploaded in
        // UpdateTexts, avoiding a giant glyph flash from identity world before the first Position call.
        var holders = new ITextureHolder[texsLoading.Length];

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

            if (isDot)
            {
                if (texts.dotTextureHolderLoading is IDisposable d)
                    d.Dispose();
                texts.dotTextureHolderLoading = holder;
            }
            else
            {
                holders[srcIdx] = holder;
            }

            instanceIdx++;
        }

        var state = new WGPUTextInstanceState
        {
            InstanceCount = instanceIdx,
            GlyphFloats = new float[Math.Max(instanceIdx, 1) * GlyphFloatsPerInstance],
            InstanceFloats = new float[Math.Max(instanceIdx, 1) * InstanceFloatsPerInstance],
        };

        lock (_textInstancesLock)
        {
            // Texts was disposed during LoadTexts: do not write back into the dictionary, or JS-side resources would leak.
            if (texts.IsDisposed)
                return false;

            // Reuse the stable Key: when reloading, let the persistent JS buffers grow on demand instead of being rebuilt.
            if (_textInstances.TryGetValue(texts, out var previousState))
                state.Key = previousState.Key;
            else
                state.Key = $"texts_{System.Threading.Interlocked.Increment(ref _textInstanceSeed)}";

            _textInstances[texts] = state;
        }

        texts.textureHoldersLoading = holders;

        return true;
    }

    /// <summary>Incremental append (see <see cref="IGraphics.AppendTexts"/> for the contract). Only build atlas
    /// entries and holders for newly added glyphs. No GPU resource is rebuilt on the Web side: JS buffers stay
    /// persistent by Key and grow on demand in UpdateTextInstance. This method only needs to enlarge the CPU
    /// staging arrays in sync, sized exactly to the new instance count and uploaded as whole-array spans.
    /// GlyphDirty must be set to true because the dot glyph's instance index can shift after append, so glyph
    /// data must be recomputed as a whole.</summary>
    public Task<bool> AppendTexts(Texts texts, Tex[] appendTexs, ITextureHolder[] appendHolders)
    {
        if (texts == null || appendTexs == null || appendHolders == null
            || appendTexs.Length == 0 || appendHolders.Length != appendTexs.Length)
            return Task.FromResult(false);

        if (!TryGetTextInstanceState(texts, out var state) || state.InstanceCount <= 0)
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

        // Pure-whitespace append (for example, only spaces/newlines): the instance count stays unchanged and
        // the caller only needs to advance layout.
        if (added == 0)
            return Task.FromResult(true);

        int required = state.InstanceCount + added;

        lock (_textInstancesLock)
        {
            // A full rebuild or Dispose happened in the meantime: abandon this incremental update and let the caller fall back.
            if (texts.IsDisposed || !_textInstances.TryGetValue(texts, out var current) || !ReferenceEquals(current, state))
                return Task.FromResult(false);

            var glyphFloats = state.GlyphFloats;
            var instanceFloats = state.InstanceFloats;
            Array.Resize(ref glyphFloats, required * GlyphFloatsPerInstance);
            Array.Resize(ref instanceFloats, required * InstanceFloatsPerInstance);

            state.GlyphFloats = glyphFloats;
            state.InstanceFloats = instanceFloats;
            state.InstanceCount = required;
            state.GlyphDirty = true;
            state.CanDraw = false;
        }

        return Task.FromResult(true);
    }

    public void UpdateTexts(Texts texts)
    {
        if (texts?.Texs?.Length <= 0)
        {
            if (TryGetTextInstanceState(texts, out var emptyState))
                emptyState.CanDraw = false;
            return;
        }

        if (!TryGetTextInstanceState(texts, out var state))
            return;

        var texs = texts.Texs;
        var holders = texts.textureHolders;
        int instanceCount = state.InstanceCount;
        if (instanceCount <= 0)
        {
            state.CanDraw = false;
            return;
        }

        bool uploadGlyphData = state.GlyphDirty || state.GlyphAtlasVersionBuilt != _glyphAtlas.Version;

        // Check whether layout changed: holder.Texture.Changed is set through Position() → ApplyLayoutToHolder.
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

        if (!uploadGlyphData && !layoutChanged)
        {
            state.CanDraw = true;
            return;
        }

        bool writeInstanceData = layoutChanged;
        var glyphFloats = state.GlyphFloats;
        var instanceFloats = state.InstanceFloats;

        var app = DeviceServices.BaseApp;
        float scale = app.Scale;
        float screenW = app.DeviceResolution.X, screenH = app.DeviceResolution.Y;
        float atlasW = _glyphAtlas.AtlasTexture != null ? _glyphAtlas.AtlasTexture.Width : 2048f;
        float atlasH = _glyphAtlas.AtlasTexture != null ? _glyphAtlas.AtlasTexture.Height : 2048f;

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

            // Guard: after LoadTexts writes the new state but before the shared layer swaps holders,
            // old holders may temporarily outnumber the new InstanceCount.
            if (instIdx >= instanceCount)
                break;

            if (uploadGlyphData)
                WriteGlyphData(glyphFloats, instIdx, ref tex, t, atlasW, atlasH);

            if (writeInstanceData)
                WriteGlyphInstance(instanceFloats, instIdx, t, scale, screenW, screenH);

            instIdx++;
        }

        // Handle the dot glyph: Changed must be cleared unconditionally, or when LastPos becomes null the
        // stale dirty flag would force a full rewrite every frame.
        if (texts.dotTextureHolder is TextGlyphHolder dotHolder)
        {
            var dt = dotHolder.Texture;
            if (dt.Changed)
                dt.Changed = false;

            if (texts.LastPos != null && instIdx < instanceCount)
            {
                if (uploadGlyphData)
                    WriteGlyphData(glyphFloats, instIdx, ref texts._dotRef, dt, atlasW, atlasH);

                if (writeInstanceData)
                    WriteGlyphInstance(instanceFloats, instIdx, dt, scale, screenW, screenH);

                instIdx++;
            }
        }

        // Fill unused trailing slots with hidden data (when the dot is hidden or a glyph is missing).
        for (; instIdx < instanceCount; instIdx++)
        {
            if (uploadGlyphData)
                Array.Clear(glyphFloats, instIdx * GlyphFloatsPerInstance, GlyphFloatsPerInstance);
            if (writeInstanceData)
                Array.Clear(instanceFloats, instIdx * InstanceFloatsPerInstance, InstanceFloatsPerInstance);
        }

        // Upload dirty data in one interop call (MemoryView reinterprets float[] as bytes without copying,
        // avoiding ToByteArray allocations).
        WebGPUInterop.UpdateTextInstance(
            state.Key,
            writeInstanceData ? MemoryMarshal.AsBytes(instanceFloats.AsSpan()) : Span<byte>.Empty,
            uploadGlyphData ? MemoryMarshal.AsBytes(glyphFloats.AsSpan()) : Span<byte>.Empty,
            instanceCount);

        if (uploadGlyphData)
        {
            state.GlyphAtlasVersionBuilt = _glyphAtlas.Version;
            state.GlyphDirty = false;
        }
        state.CanDraw = true;
    }

    /// <summary>Write glyph data for a single glyph (12 floats), including local-cache refresh when the atlas version changes (aligned with DX).</summary>
    void WriteGlyphData(float[] dst, int instIdx, ref Tex tex, Texture t, float atlasW, float atlasH)
    {
        bool hasValidEntry = TryEnsureGlyphEntry(ref tex, out var entry);
        if (hasValidEntry && tex.AtlasVersion != entry.AtlasVersion)
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

        float sx = hasValidEntry ? entry.SourceX : t.SourceX;
        float sy = hasValidEntry ? entry.SourceY : t.SourceY;
        float sw = hasValidEntry ? entry.SourceWidth : t.SourceWidth;
        float sh = hasValidEntry ? entry.SourceHeight : t.SourceHeight;
        float gw = hasValidEntry ? entry.Width : t.OriginWidth;
        float gh = hasValidEntry ? entry.Height : t.OriginHeight;
        float pr = hasValidEntry ? entry.PixelRange : t.Factor;
        bool hasColorOverride = tex.Color.HasValue;
        var glyphColor = hasColorOverride ? tex.Color.Value.AsVector4 : Vector4.One;

        int o = instIdx * GlyphFloatsPerInstance;
        dst[o + 0] = sx / atlasW;
        dst[o + 1] = sy / atlasH;
        dst[o + 2] = sw / atlasW;
        dst[o + 3] = sh / atlasH;
        dst[o + 4] = glyphColor.X;
        dst[o + 5] = glyphColor.Y;
        dst[o + 6] = glyphColor.Z;
        dst[o + 7] = glyphColor.W;
        dst[o + 8] = gw;
        dst[o + 9] = gh;
        dst[o + 10] = pr;
        dst[o + 11] = hasColorOverride ? 1f : 0f;
    }

    /// <summary>Write the per-glyph instance world matrix (20 floats). Write a zero matrix to hide it when alpha or size is invalid.</summary>
    static void WriteGlyphInstance(float[] dst, int instIdx, Texture t, float scale, float screenW, float screenH)
    {
        int o = instIdx * InstanceFloatsPerInstance;

        float glyphAlpha = Math.Clamp(t.Alpha, 0f, 1f);
        float w = (t.Width > 0 ? t.Width : t.OriginWidth) * scale;
        float h = (t.Height > 0 ? t.Height : t.OriginHeight) * scale;

        if (glyphAlpha <= 0f || w <= 0f || h <= 0f)
        {
            Array.Clear(dst, o, InstanceFloatsPerInstance);
            return;
        }

        // Web-side NDC convention (matching the old DrawTextGlyph path): top-left anchor → center translation.
        float x = t.PosX * scale;
        float y = t.PosY * scale;
        float ndcX = (x / screenW) * 2f - 1f;
        float ndcY = 1f - (y / screenH) * 2f;
        float ndcW = (w / screenW) * 2f;
        float ndcH = (h / screenH) * 2f;

        var world = Matrix4x4.CreateScale(ndcW, ndcH, 1f)
                  * Matrix4x4.CreateTranslation(ndcX + ndcW / 2f, ndcY - ndcH / 2f, 0f);

        dst[o + 0] = world.M11; dst[o + 1] = world.M12; dst[o + 2] = world.M13; dst[o + 3] = world.M14;
        dst[o + 4] = world.M21; dst[o + 5] = world.M22; dst[o + 6] = world.M23; dst[o + 7] = world.M24;
        dst[o + 8] = world.M31; dst[o + 9] = world.M32; dst[o + 10] = world.M33; dst[o + 11] = world.M34;
        dst[o + 12] = world.M41; dst[o + 13] = world.M42; dst[o + 14] = world.M43; dst[o + 15] = world.M44;
        dst[o + 16] = 0f; dst[o + 17] = 0f; dst[o + 18] = 0f; dst[o + 19] = 0f;  // Zero morphWeights
    }

    public void DrawTexts(Texts texts)
    {
        if (texts?.Texs?.Length == 0 || !_initialized)
            return;

        if (!TryGetTextInstanceState(texts, out var state) || state.InstanceCount <= 0 || !state.CanDraw)
            return;

        FlushDrawMesh3DBatch();
        FlushDrawSkinnedMeshBatch();

        // Single instanced draw: glyph data is indexed directly on the JS side through binding 9 by instanceIndex.
        // Exposure is no longer passed as a draw argument (1-2 Contract 8: text inverse-ACES compensation now
        // reads uLights.params0.y from binding(10), so the old per-draw exposure bypass has been removed to
        // match the other three backends).
        var textColor = texts.Color.AsVector4;
        WebGPUInterop.DrawTextInstanced(
            state.Key,
            Math.Clamp(texts.Alpha, 0f, 1f),
            textColor.X, textColor.Y, textColor.Z, textColor.W,
            Season.Fonts.Font.PixelRange);
    }

    public void DisposeTexts(Texts texts)
    {
        lock (_textInstancesLock)
        {
            if (_textInstances.TryGetValue(texts, out var state))
            {
                _jsRuntime.InvokeVoid("seasonWebGPU.disposeTextInstance", state.Key);
                _textInstances.Remove(texts);
            }
        }

        // TextGlyphHolder has no GPU resources; only clear the references.
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

    public void FlushTextAtlas()
    {
        _glyphAtlas.FlushPendingUploadsOnRenderThread();
    }

    // ── Atlas helper methods ──

    bool TryEnsureGlyphEntry(ref Tex tex, out GlyphAtlasEntry entry)
    {
        entry = default;
        if (tex.TexType is TexType.NewLine or TexType.Space or TexType.Missing)
            return false;
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
            DeviceServices.BaseApp.AddLog(LogType.Error, $"{DateTime.UtcNow} LoadTexTexture EnsureGlyph TexType.Missing {tex} {ex}");
            return false;
        }
        tex.GlyphMetrics = entry.GlyphMetrics;
        tex.Factor = entry.PixelRange;
        return true;
    }

    public async Task<bool> LoadModel(Model model)
    {
        lock (DictionaryModel)
        {
            if (DictionaryModel.ContainsKey((model.Name, model.ID)))
            {
                return true;
            }
        }

        WGPUModel wgpuModel;
        try
        {
            var resourceTemplate = await GetOrCreateSharedModelAsync(model.Name);
            wgpuModel = resourceTemplate.CreateInstance(model);
        }
        catch (Exception ex)
        {
            DeviceServices.BaseApp.AddLog(LogType.Error, $"{DateTime.UtcNow} [WebGPUGraphics] Failed to prepare model resource {model.Name}: {ex}");
            wgpuModel = new WGPUModel(model.Name);
        }

        lock (DictionaryModel)
        {
            if (!DictionaryModel.ContainsKey((model.Name, model.ID)))
                DictionaryModel.Add((model.Name, model.ID), wgpuModel);
        }

        return true;
    }

    async Task<WGPUModel> GetOrCreateSharedModelAsync(string modelName)
    {
        Task<WGPUModel> sharedTask;
        lock (DictionaryModelResource)
        {
            if (!DictionaryModelResource.TryGetValue(modelName, out sharedTask))
            {
                sharedTask = CreateSharedModelAsync(modelName);
                DictionaryModelResource[modelName] = sharedTask;
            }
        }

        try
        {
            return await sharedTask;
        }
        catch
        {
            lock (DictionaryModelResource)
            {
                if (DictionaryModelResource.TryGetValue(modelName, out var cachedTask) && cachedTask == sharedTask)
                    DictionaryModelResource.Remove(modelName);
            }

            throw;
        }
    }

    async Task<WGPUModel> CreateSharedModelAsync(string modelName)
    {
        var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();

        byte[] glbBytes = null;
        var fetchStopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var response = await _httpClient.GetAsync(ResolveAssetPath(modelName));
            if (response.IsSuccessStatusCode)
                glbBytes = await response.Content.ReadAsByteArrayAsync();
            else
                DeviceServices.BaseApp.AddLog(LogType.Backend, $"{DateTime.UtcNow} [WebGPUGraphics] HTTP {(int)response.StatusCode} for {modelName}");
        }
        catch (Exception ex)
        {
            DeviceServices.BaseApp.AddLog(LogType.Error, $"{DateTime.UtcNow} [WebGPUGraphics] HTTP fetch {modelName} error: {ex.Message}");
        }

        if (glbBytes == null)
            throw new InvalidOperationException($"Failed to fetch .glb file: {modelName}");

        DeviceServices.BaseApp.AddLog(LogType.Backend, $"{DateTime.UtcNow} [ModelLoad] {modelName} fetch={fetchStopwatch.ElapsedMilliseconds}ms bytes={glbBytes.Length}");

        var templateModel = new Model { Name = modelName, Alpha = 1f };
        var template = new WGPUModel(modelName);
        template.SetGlbBytes(glbBytes);
        var parseStopwatch = System.Diagnostics.Stopwatch.StartNew();
        template.Load(templateModel, Camera3D);

        DeviceServices.BaseApp.AddLog(LogType.Backend, $"{DateTime.UtcNow} [ModelLoad] {modelName} parse={parseStopwatch.ElapsedMilliseconds}ms total={totalStopwatch.ElapsedMilliseconds}ms");

        _ = UploadModelTexturesInBackground(template);

        return template;
    }

    async Task UploadModelTexturesInBackground(WGPUModel wgpuModel)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await UploadModelTextures(wgpuModel, deferDecodeToNextFrame: true);
        }
        catch (Exception ex)
        {
            DeviceServices.BaseApp.AddLog(LogType.Error, $"{DateTime.UtcNow} [ModelLoad] {wgpuModel.Name} background texture upload failed: {ex}");
        }
    }

    async Task UploadModelTextures(WGPUModel wgpuModel, bool deferDecodeToNextFrame = false)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        int textureRefs = 0;
        foreach (var prim in wgpuModel.GetAllPrimitives())
        {
            if (prim.BaseColorTexture != null)
            {
                textureRefs++;
                await UploadGltfImageTexture(wgpuModel.Name, "baseColor", prim.BaseColorTexture, deferDecodeToNextFrame);
            }
            if (prim.NormalTexture != null)
            {
                textureRefs++;
                await UploadGltfImageTexture(wgpuModel.Name, "normal", prim.NormalTexture, deferDecodeToNextFrame);
            }
            if (prim.MetallicRoughnessTexture != null)
            {
                textureRefs++;
                await UploadGltfImageTexture(wgpuModel.Name, "metallicRoughness", prim.MetallicRoughnessTexture, deferDecodeToNextFrame);
            }
            if (prim.OcclusionTexture != null)
            {
                textureRefs++;
                await UploadGltfImageTexture(wgpuModel.Name, "occlusion", prim.OcclusionTexture, deferDecodeToNextFrame);
            }
            if (prim.EmissiveTexture != null)
            {
                textureRefs++;
                await UploadGltfImageTexture(wgpuModel.Name, "emissive", prim.EmissiveTexture, deferDecodeToNextFrame);
            }
        }
        DeviceServices.BaseApp.AddLog(LogType.Backend, $"{DateTime.UtcNow} [ModelLoad] {wgpuModel.Name} textures={stopwatch.ElapsedMilliseconds}ms refs={textureRefs}");
    }

    async Task UploadGltfImageTexture(string modelName, string channel, SharpGLTF.Schema2.Image? image, bool deferDecodeToNextFrame = false)
    {
        var texName = $"{modelName}-{channel}-{image.LogicalIndex}";

        if (DictionaryWGPUTexture.ContainsKey(texName))
        {
            return;
        }

        var encodedReadStopwatch = System.Diagnostics.Stopwatch.StartNew();
        using var contentStream = image.Content.Open();
        using var encodedMemory = new MemoryStream();
        await contentStream.CopyToAsync(encodedMemory);
        var encodedBytes = encodedMemory.ToArray();

        var uploadStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = await _jsRuntime.InvokeAsync<WebTextureUploadResult>("seasonWebGPU.uploadEncodedTexture",
            texName,
            encodedBytes,
            null,
            deferDecodeToNextFrame);
        var success = result?.success == true;

        if (success)
        {
            DictionaryWGPUTexture[texName] = new WGPUTexture
            {
                Name = texName,
                Width = (uint)(result?.width ?? 0),
                Height = (uint)(result?.height ?? 0)
            };
        }
    }

    public void UpdateModel(Model model, float time)
    {
        WGPUModel wgpuModel = null;
        lock (DictionaryModel)
        {
            DictionaryModel.TryGetValue((model.Name, model.ID), out wgpuModel);
        }

        if (wgpuModel == null) return;

    // ── Material overrides ──
        ProcessModelOverridesWebGPU(model);

        wgpuModel.Update(model, time, Camera3D);
    }

    /// <summary>Consume material overrides from the Model.</summary>
    void ProcessModelOverridesWebGPU(Model model)
    {
        TryReplaceModelTextureWGPU(model, model.BaseColorOverride, 0, () => model.BaseColorOverride = default);
        TryReplaceModelTextureWGPU(model, model.NormalOverride, 1, () => model.NormalOverride = default);
        TryReplaceModelTextureWGPU(model, model.MetallicRoughnessOverride, 2, () => model.MetallicRoughnessOverride = default);
        TryReplaceModelTextureWGPU(model, model.OcclusionOverride, 3, () => model.OcclusionOverride = default);
        TryReplaceModelTextureWGPU(model, model.EmissiveTextureOverride, 4, () => model.EmissiveTextureOverride = default);

        if (model.MetallicOverride.HasValue || model.RoughnessOverride.HasValue || model.EmissiveFactorOverride.HasValue)
        {
            var em = model.EmissiveFactorOverride ?? Vector4.Zero;
            var allPrims = GetModelPrimitives(model);
            foreach (var prim in allPrims)
            {
                WebGPUInterop.UpdateMeshMaterialParams(
                    prim.CacheKey,
                    model.MetallicOverride ?? prim.MetallicFactor,
                    model.RoughnessOverride ?? prim.RoughnessFactor,
                    em.X, em.Y, em.Z);
            }
            model.MetallicOverride = null;
            model.RoughnessOverride = null;
            model.EmissiveFactorOverride = null;
        }
    }

    List<WGPUPrimitiveData> GetModelPrimitives(Model model)
    {
        WGPUModel? wgpuModel = null;
        lock (DictionaryModel)
            DictionaryModel.TryGetValue((model.Name, model.ID), out wgpuModel);
        return wgpuModel?.GetAllPrimitives() ?? new List<WGPUPrimitiveData>();
    }

    void TryReplaceModelTextureWGPU(Model model, TextureUpdateSource source, int slot, Action clearSource)
    {
        if (!source.HasValue) return;
        clearSource();

        var decoder = ResolveDecoder(source);
        if (decoder == null) return;

        var rgba = decoder.PixelSpan.ToArray();
        int w = decoder.Width, h = decoder.Height;
        decoder.Dispose();

        var allPrims = GetModelPrimitives(model);
        if (allPrims.Count == 0) return;

        string newName = $"mdlTex_{slot}_{Guid.NewGuid():N}";
        _jsRuntime.InvokeVoid("seasonWebGPU.createTextureFromPixels", newName, rgba, w, h, true);
        foreach (var prim in allPrims)
        {
            SetTextureNameBySlot(prim, slot, newName);
            _jsRuntime.InvokeVoid("seasonWebGPU.updateMeshTexture", prim.CacheKey, slot, newName);
        }
    }

    static void SetTextureNameBySlot(WGPUPrimitiveData p, int slot, string name)
    {
        switch (slot)
        {
            case 0: p.BaseColorTextureName = name; break;
            case 1: p.NormalTextureName = name; break;
            case 2: p.MetallicRoughnessTextureName = name; break;
            case 3: p.OcclusionTextureName = name; break;
            case 4: p.EmissiveTextureName = name; break;
        }
    }

    /// <summary>Replace the Sprite3D single texture. The Web backend always creates an independent texture to avoid incorrect sharing heuristics.</summary>
    void ReplaceSprite3DTexture(WGPUSprite3D wgpuSprite, TextureUpdateSource source)
    {
        var decoder = ResolveDecoder(source);
        if (decoder == null) return;

        var rgba = decoder.PixelSpan.ToArray();
        int w = decoder.Width, h = decoder.Height;
        decoder.Dispose();

        string newName = $"spr3DTex_{Guid.NewGuid():N}";
        _jsRuntime.InvokeVoid("seasonWebGPU.createTextureFromPixels", newName, rgba, w, h, true);
        wgpuSprite.TextureName = newName;
    }

    static int _drawModelLogCount = 0;

    // [SkyDebug] Temporary diagnostic counters for invisible-starfield investigation
    // (LogType.Backend; remove this block after the issue is resolved)
    static int _skyDebugUploadFrame;
    static bool _skyDebugBoxLogged;
    public void DrawModel(Model model)
    {
        if (model.Name.IsNullOrWhiteSpace() || model.Alpha == 0 || !_initialized)
        {
            if (_drawModelLogCount < 3)
            {
                DeviceServices.BaseApp.AddLog(LogType.Backend, $"{DateTime.UtcNow} [DrawModel] SKIP early Name='{model.Name}' Alpha={model.Alpha} initialized={_initialized}");
                _drawModelLogCount++;
            }
            return;
        }

        WGPUModel wgpuModel = null;
        lock (DictionaryModel)
        {
            DictionaryModel.TryGetValue((model.Name, model.ID), out wgpuModel);
        }

        if (wgpuModel == null || !wgpuModel.TransformInitialized)
        {
            if (_drawModelLogCount < 3)
            {
                DeviceServices.BaseApp.AddLog(LogType.Backend, $"{DateTime.UtcNow} [DrawModel] SKIP dict Name={model.Name} ID={model.ID} wgpuModel={(wgpuModel == null ? "null" : "found")} TransformInit={wgpuModel?.TransformInitialized}");
                _drawModelLogCount++;
            }
            return;
        }

        if (_drawModelLogCount < 3)
        {
            DeviceServices.BaseApp.AddLog(LogType.Backend, $"{DateTime.UtcNow} [DrawModel] DRAW Name={model.Name} Alpha={model.Alpha}");
            _drawModelLogCount++;
        }

        wgpuModel.Draw(this);
    }

    readonly HttpClient _httpClient;

    public async Task<bool> LoadSprite3D(Sprite3D sprite)
    {
        lock (DictionarySprite3D)
        {
            if (DictionarySprite3D.ContainsKey((sprite.Name, sprite.ID)))
                return true;
        }

        await LoadTextureAsync(sprite.Name);

        var wgpuSprite3D = new WGPUSprite3D { TextureName = sprite.Name };

        lock (DictionarySprite3D)
        {
            if (!DictionarySprite3D.ContainsKey((sprite.Name, sprite.ID)))
                DictionarySprite3D.Add((sprite.Name, sprite.ID), wgpuSprite3D);
        }

        return true;
    }

    public void UpdateSprite3D(Sprite3D sprite, float time)
    {
        WGPUSprite3D wgpuSprite3D = null;
        lock (DictionarySprite3D)
        {
            DictionarySprite3D.TryGetValue((sprite.Name, sprite.ID), out wgpuSprite3D);
        }

        if (wgpuSprite3D == null) return;

        // ── Texture replacement ──
        if (sprite.TextureOverride.HasValue)
        {
            var source = sprite.TextureOverride;
            sprite.TextureOverride = default;
            ReplaceSprite3DTexture(wgpuSprite3D, source);
        }

        var cameraView = Camera3D.View;
        var cameraProjection = Camera3D.Projection;

        Matrix4x4.Invert(cameraView, out var viewInv);
        Vector3 cameraPosition = viewInv.Translation;
        Vector3 cameraForward = -new Vector3(viewInv.M31, viewInv.M32, viewInv.M33);

        var position = new Vector3(sprite.PosX, sprite.PosY, sprite.PosZ);
        var size = new Vector2(sprite.Width ?? 1f, sprite.Height ?? 1f);

        Matrix4x4 billboardRot = sprite.Mode switch
        {
            Season.Controls.BillboardMode.Spherical => Matrix4x4.CreateBillboard(
                position, cameraPosition, Vector3.UnitY, cameraForward),
            Season.Controls.BillboardMode.Cylindrical => Matrix4x4.CreateConstrainedBillboard(
                position, cameraPosition, Vector3.UnitY, cameraForward, Vector3.UnitZ),
            _ => Matrix4x4.CreateFromQuaternion(sprite.Rotation) * Matrix4x4.CreateTranslation(position)
        };

        var worldMatrix = Matrix4x4.Identity * billboardRot;

        if (size != Vector2.One)
        {
            worldMatrix = Matrix4x4.CreateScale(size.X, size.Y, 1f) * worldMatrix;
        }

        // 2-3 Contract Clause 6: roll the shadow copy first, then overwrite the current-frame world
        // (same ordering as DXSprite3D.Update).
        // On the first frame, TransformInitialized is still false, so PrevWorldMatrix stays at the all-zero
        // sentinel instead of misinterpreting the default Identity as valid history
        // (the Web-side World starts as Identity rather than zero).
        if (wgpuSprite3D.TransformInitialized)
            wgpuSprite3D.PrevWorldMatrix = wgpuSprite3D.World;

        wgpuSprite3D.World = worldMatrix;
        wgpuSprite3D.View = cameraView;
        wgpuSprite3D.Projection = cameraProjection;
        wgpuSprite3D.TransformInitialized = true;
    }

    public void DrawSprite3D(Sprite3D sprite)
    {
        if (sprite.Name.IsNullOrWhiteSpace() || sprite.Alpha == 0 || !_initialized)
        {
            return;
        }

        WGPUSprite3D wgpuSprite3D = null;
        lock (DictionarySprite3D)
        {
            DictionarySprite3D.TryGetValue((sprite.Name, sprite.ID), out wgpuSprite3D);
        }

        if (wgpuSprite3D == null || !wgpuSprite3D.TransformInitialized)
        {
            return;
        }

        var matrixData = _scratchMatrix48;
        CopyMatrixTransposed(wgpuSprite3D.World, matrixData, 0);
        CopyMatrixTransposed(wgpuSprite3D.View, matrixData, 16);
        CopyMatrixTransposed(wgpuSprite3D.Projection, matrixData, 32);

        Vector4 color = sprite.Color;
        float finalAlpha = color.W * sprite.Alpha;

        var uniformData = _scratchUniform;
        Array.Clear(uniformData, 48, WebGPUUniformLayout.TotalFloats - 48);
        Array.Copy(matrixData, 0, uniformData, 0, 48);
        // 2-3 Contract Clause 6: the prev slot must be written after Clear.
        WritePrevMatrices(uniformData, wgpuSprite3D.PrevWorldMatrix);
        var w = new WebGPUUniformWriter(uniformData);
        w.SetBaseColor(new Vector4(color.X, color.Y, color.Z, 1f));
        WriteLightUniform(uniformData, renderMode: 0, metallic: 0f, roughness: 0.5f, alpha: finalAlpha, emissive: Vector3.Zero, alphaMode: 2u, alphaCutoff: 0.5f);

        FlushDrawMesh3DBatch();
        FlushDrawSkinnedMeshBatch();

        _jsRuntime.InvokeVoid("seasonWebGPU.drawSprite3D",
            sprite.Name,
            uniformData,
            sprite.Mode == Season.Controls.BillboardMode.None ? 0 : 1);
    }

    public void DisposeSprite3D(Sprite3D sprite)
    {
        var key = (sprite.Name, sprite.ID);
        lock (DictionarySprite3D)
        {
            if (DictionarySprite3D.TryGetValue(key, out var wgpuSprite3D))
                DictionarySprite3D.Remove(key);
        }
        sprite.Ready = false;
    }

    // ── Mesh3D surface texture resolution (pixel sources avoid disk, path sources are reused; mirrors the native backends) ──

    static string ProcTextureName(string meshName, long meshId, int surfaceIndex, SurfaceTextureSlot slot)
        => $"proc:{meshName}:{meshId}:{surfaceIndex}:{slot}";

    /// <summary>Clear TextureOverride for all Surface slots after Load completes (single-consumption contract).</summary>
    static void ClearSurfaceOverrides(Surface surface)
    {
        surface.ClearTextureOverride(SurfaceTextureSlot.BaseColor);
        surface.ClearTextureOverride(SurfaceTextureSlot.Normal);
        surface.ClearTextureOverride(SurfaceTextureSlot.MetallicRoughness);
        surface.ClearTextureOverride(SurfaceTextureSlot.Occlusion);
        surface.ClearTextureOverride(SurfaceTextureSlot.Emissive);
    }

    /// <summary>
    /// Resolve a single Surface texture slot into a JS-side texture name:
    /// - Image branch (procedural pixels): upload directly through createTextureFromPixels with no temp file,
    ///   and register WGPUTexture metadata under the synthesized name;
    /// - Path branch: reuse LoadTextureAsync (HTTP fetch), using the path itself as the name;
    /// - Empty source: fall back to "White".
    /// </summary>
    async Task<string> ResolveSurfaceSlotTexture(string meshName, long meshId, int surfaceIndex, Surface surface, SurfaceTextureSlot slot)
    {
        var source = surface.GetTextureSource(slot);
        if (!source.HasValue)
            return "White";

        if (source.Image != null)
        {
            var name = ProcTextureName(meshName, meshId, surfaceIndex, slot);
            lock (DictionaryWGPUTexture)
            {
                if (DictionaryWGPUTexture.ContainsKey(name))
                {
                    source.Image.Dispose();   // Already registered; do not upload again, only dispose the decoder to avoid leaking it
                    return name;
                }
            }

            var rgba = source.Image.PixelSpan.ToArray();  // Managed array crossing the JS interop boundary
            int w = source.Image.Width, h = source.Image.Height;
            source.Image.Dispose();

            _jsRuntime.InvokeVoid("seasonWebGPU.createTextureFromPixels", name, rgba, w, h, /*forceNew=*/true);

            lock (DictionaryWGPUTexture)
                DictionaryWGPUTexture[name] = WGPUTexture.CreateFromPixels(name, (uint)w, (uint)h);

            return name;
        }

        await LoadTextureAsync(source.Path);
        return source.Path;
    }

    /// <summary>
    /// Pre-resolve all five Surface texture slots and store a resolution snapshot. The Web Draw path rebuilds
    /// uniforms every frame, so it needs to retain the names resolved during Load. TextureFlags must be
    /// computed before clearing overrides, following the HasTexture "declared means enabled" semantics.
    /// </summary>
    async Task ResolveMeshSurfaceTextures(string meshName, long meshId, IList<Surface> surfaces, Dictionary<Surface, WGPUMesh3D.ResolvedTextureSet> resolvedMap)
    {
        for (int i = 0; i < surfaces.Count; i++)
        {
            var surface = surfaces[i];
            var set = new WGPUMesh3D.ResolvedTextureSet
            {
                BaseColor = await ResolveSurfaceSlotTexture(meshName, meshId, i, surface, SurfaceTextureSlot.BaseColor),
                Normal = await ResolveSurfaceSlotTexture(meshName, meshId, i, surface, SurfaceTextureSlot.Normal),
                MetallicRoughness = await ResolveSurfaceSlotTexture(meshName, meshId, i, surface, SurfaceTextureSlot.MetallicRoughness),
                Occlusion = await ResolveSurfaceSlotTexture(meshName, meshId, i, surface, SurfaceTextureSlot.Occlusion),
                Emissive = await ResolveSurfaceSlotTexture(meshName, meshId, i, surface, SurfaceTextureSlot.Emissive),
            };

            if (surface.HasTexture(SurfaceTextureSlot.MetallicRoughness))
                set.TextureFlags |= WebGPUTextureFlags.MetallicRoughness;
            if (surface.HasTexture(SurfaceTextureSlot.Normal))
                set.TextureFlags |= WebGPUTextureFlags.Normal;
            if (surface.HasTexture(SurfaceTextureSlot.Occlusion))
                set.TextureFlags |= WebGPUTextureFlags.Occlusion;
            if (surface.HasTexture(SurfaceTextureSlot.Emissive))
                set.TextureFlags |= WebGPUTextureFlags.Emissive;

            resolvedMap[surface] = set;
        }
    }

    /// <summary>Remove the metadata for all synthesized procedural textures registered under every Surface
    /// slot of a single mesh. JS-side textures follow device lifetime and have no separate destroy channel,
    /// consistent with the existing Sprite convention.</summary>
    void ReleaseProcSurfaceTextures(string meshName, long meshId, int surfaceCount)
    {
        lock (DictionaryWGPUTexture)
        {
            for (int i = 0; i < surfaceCount; i++)
            {
                DictionaryWGPUTexture.Remove(ProcTextureName(meshName, meshId, i, SurfaceTextureSlot.BaseColor));
                DictionaryWGPUTexture.Remove(ProcTextureName(meshName, meshId, i, SurfaceTextureSlot.Normal));
                DictionaryWGPUTexture.Remove(ProcTextureName(meshName, meshId, i, SurfaceTextureSlot.MetallicRoughness));
                DictionaryWGPUTexture.Remove(ProcTextureName(meshName, meshId, i, SurfaceTextureSlot.Occlusion));
                DictionaryWGPUTexture.Remove(ProcTextureName(meshName, meshId, i, SurfaceTextureSlot.Emissive));
            }
        }
    }

    public async Task<bool> LoadMesh3D(Mesh3D mesh)
    {
        lock (DictionaryMesh3D)
        {
            if (DictionaryMesh3D.ContainsKey((mesh.Name, mesh.ID)))
                return true;
        }

        var wgpuMesh = new WGPUMesh3D(mesh.Name, mesh);

        // 1. Pre-resolve every texture source referenced by the Surface list: pixel sources go through
        //    createTextureFromPixels directly with no temp file, path sources reuse LoadTextureAsync,
        //    and empty sources fall back to "White". Store the resolved snapshot for use during per-frame Draw.
        await ResolveMeshSurfaceTextures(mesh.Name, mesh.ID, mesh.Surfaces, wgpuMesh.ResolvedTextures);

        // 2. Clear TextureOverride after Load completes (single-consumption contract).
        foreach (var surface in mesh.Surfaces)
            ClearSurfaceOverrides(surface);

        lock (DictionaryMesh3D)
        {
            if (!DictionaryMesh3D.ContainsKey((mesh.Name, mesh.ID)))
                DictionaryMesh3D.Add((mesh.Name, mesh.ID), wgpuMesh);
        }

        return true;
    }

    public void UpdateMesh3D(Mesh3D mesh, float time)
    {
        WGPUMesh3D wgpuMesh = null;
        lock (DictionaryMesh3D)
        {
            DictionaryMesh3D.TryGetValue((mesh.Name, mesh.ID), out wgpuMesh);
        }

        if (wgpuMesh == null) return;

        // Unified transform convention: converge on BuildWorldMatrix
        // (anchor pivot: Scale → anchor translation → Rotation → Position; see Mesh3DBase).
        var world = mesh.BuildWorldMatrix();

        // 2-3 Contract Clause 6: roll the shadow copy first, then overwrite the current-frame world
        // (same ordering as DXMesh3D.UpdateTransform).
        if (wgpuMesh.TransformInitialized)
            wgpuMesh.PrevWorldMatrix = wgpuMesh.World;

        wgpuMesh.World = world;
        wgpuMesh.View = Camera3D.View;
        wgpuMesh.Projection = Camera3D.Projection;
        wgpuMesh.TransformInitialized = true;
        wgpuMesh.MeshAlpha = mesh.Alpha;
        // Mirror Mesh3D.ColorTint (used to adjust skybox brightness/color temperature over the day-night cycle):
        // the Web backend rebuilds uniforms per draw, so no extra dirty gate is needed.
        wgpuMesh.ColorTint = mesh.ColorTint;
        // 2-2 Contract Clause 7: mirror the GTAO exemption bit. It can change at runtime, and per-draw
        // uniform rebuilds naturally keep it in sync.
        wgpuMesh.ExcludeFromAo = mesh.ExcludeFromAo;

        // Unified highlight: sync the Bounds box. Box geometry is created lazily on the first enabled frame.
        // Face/edge dual colors are independent of the model alpha chain and are written every frame.
        // Do not enable it when Extents is near zero (unloaded / degenerate box).
        wgpuMesh.BoundsActive = mesh.Highlight.Bounds;
        if (wgpuMesh.BoundsActive)
        {
            var bounds = mesh.GetWorldBoundsRaw();
            if (bounds.Extents.LengthSquared() >= 1e-12f)
            {
                wgpuMesh.BoundsBox ??= WebBoundsBox.Create($"{mesh.Name}:{mesh.ID}:HOST");
                var box = wgpuMesh.BoundsBox;
                box.PrevWorld = box.World;
                box.World = Matrix4x4.CreateScale(bounds.Extents * 2f) * Matrix4x4.CreateTranslation(bounds.Center);
                box.FaceColor = mesh.Highlight.SurfaceColor;
                box.FaceAlpha = mesh.Highlight.SurfaceColor.W;
                box.EdgeColor = mesh.Highlight.EdgeColor;
            }
        }

        // Unified highlight: sync the wireframe shell. Per-surface shell boxes are created lazily on the
        // first enabled frame. PrevWorld is rolled forward, then current-frame world/colors are written every frame.
        // Ordering matches the host box: roll the shadow copy first, then overwrite current-frame data.
        // The first-frame Identity acts as the zero-velocity sentinel.
        // Unified highlight (Outline2D): forward host-level state directly. Color/width are frozen during
        // Update, and mask rendering happens in RenderOutlineMask, mirroring the Update hooks on DX/VK/Metal.
        wgpuMesh.Outline2DActive = mesh.Highlight.Outline;
        wgpuMesh.Outline2DMaskColor = mesh.Highlight.OutlineColor;
        wgpuMesh.Outline2DMaskWidth = mesh.Highlight.OutlineWidth;

        wgpuMesh.WireframeActive = mesh.Highlight.Wireframe;
        if (wgpuMesh.WireframeActive)
        {
            EnsureMesh3DShells(wgpuMesh, mesh);
            if (wgpuMesh.ShellBoxes != null)
            {
                for (int i = 0; i < wgpuMesh.ShellBoxes.Count; i++)
                {
                    var shell = wgpuMesh.ShellBoxes[i];
                    if (shell == null)
                        continue;
                    shell.PrevWorld = shell.World;
                    shell.World = world;
                    shell.FaceColor = mesh.Highlight.SurfaceColor;
                    shell.FaceAlpha = mesh.Highlight.SurfaceColor.W;
                    shell.EdgeColor = mesh.Highlight.EdgeColor;
                }
            }
        }
    }

    public void DrawMesh3D(Mesh3D mesh)
    {
        if (mesh.Alpha == 0f || !_initialized)
        {
            return;
        }

        WGPUMesh3D wgpuMesh = null;
        lock (DictionaryMesh3D)
        {
            DictionaryMesh3D.TryGetValue((mesh.Name, mesh.ID), out wgpuMesh);
        }

        if (wgpuMesh == null || !wgpuMesh.TransformInitialized)
        {
            return;
        }

        var matrixData = _scratchMatrix48;
        CopyMatrixTransposed(wgpuMesh.World, matrixData, 0);
        CopyMatrixTransposed(wgpuMesh.View, matrixData, 16);
        CopyMatrixTransposed(wgpuMesh.Projection, matrixData, 32);

        foreach (var surface in mesh.Surfaces)
        {
            DrawMesh3DSurface(wgpuMesh, surface, matrixData);
        }

        // Unified highlight (host Bounds box): face uses BLEND translucency and edges use OPAQUE depth-writing.
        // Flush after all surfaces have finished.
        if (wgpuMesh.BoundsActive && wgpuMesh.BoundsBox != null)
            DrawBoundsBox(wgpuMesh.BoundsBox, wgpuMesh.View, wgpuMesh.Projection);

        // Unified highlight (wireframe shell): one shell box per surface, with BLEND translucent faces and
        // OPAQUE depth-writing edges. Flush after Bounds.
        if (wgpuMesh.WireframeActive && wgpuMesh.ShellBoxes != null)
        {
            for (int i = 0; i < wgpuMesh.ShellBoxes.Count; i++)
            {
                var shell = wgpuMesh.ShellBoxes[i];
                if (shell != null)
                    DrawShellBox(shell, wgpuMesh.View, wgpuMesh.Projection);
            }
        }
    }

    // 1-3: bounding-box computation converges on the shared Season.Rendering.Bounds3D.FromVertices path
    // (same source across all four backends).

    WGPUMesh3D.SurfaceCacheEntry GetOrCreateSurfaceCache(Dictionary<object, WGPUMesh3D.SurfaceCacheEntry> surfaceCaches, string ownerName, Surface surface)
    {
        if (surfaceCaches.TryGetValue(surface, out var cache))
            return cache;

        var vertices = surface.Vertices;
        var vData = new float[vertices.Length * 20];
        for (int i = 0; i < vertices.Length; i++)
        {
            int off = i * 20;
            vData[off + 0] = vertices[i].Position.X;
            vData[off + 1] = vertices[i].Position.Y;
            vData[off + 2] = vertices[i].Position.Z;
            vData[off + 3] = vertices[i].TexCoord.X;
            vData[off + 4] = vertices[i].TexCoord.Y;
            vData[off + 5] = vertices[i].Normal.X;
            vData[off + 6] = vertices[i].Normal.Y;
            vData[off + 7] = vertices[i].Normal.Z;
            vData[off + 8] = vertices[i].Tangent.X;
            vData[off + 9] = vertices[i].Tangent.Y;
            vData[off + 10] = vertices[i].Tangent.Z;
            vData[off + 11] = vertices[i].Tangent.W;
            vData[off + 12] = vertices[i].Joints.X;
            vData[off + 13] = vertices[i].Joints.Y;
            vData[off + 14] = vertices[i].Joints.Z;
            vData[off + 15] = vertices[i].Joints.W;
            vData[off + 16] = vertices[i].Weights.X;
            vData[off + 17] = vertices[i].Weights.Y;
            vData[off + 18] = vertices[i].Weights.Z;
            vData[off + 19] = vertices[i].Weights.W;
        }

        var iData = new ushort[surface.Indices.Length];
        Array.Copy(surface.Indices, iData, surface.Indices.Length);

        var localBounds = Season.Rendering.Bounds3D.FromVertices(surface.Vertices);
        cache = new WGPUMesh3D.SurfaceCacheEntry
        {
            VertexData = vData,
            VertexBytes = ToByteArray(vData),
            IndexData = iData,
            IndexBytes = ToByteArray(iData),
            LocalBoundsCenter = localBounds.Center,
            LocalBoundsExtents = localBounds.Extents,
            CacheKey = $"M3D:{ownerName}:{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(surface):X}",
            Uploaded = false,
        };
        surfaceCaches[surface] = cache;
        return cache;
    }

    static (string textureName, string normalTextureName, string metallicRoughnessTextureName, string occlusionTextureName, string emissiveTextureName, int textureFlags) GetSurfaceTextureInfo(Surface surface)
    {
        string textureName = !string.IsNullOrEmpty(surface.BaseColorTexturePath) ? surface.BaseColorTexturePath : "White";
        string normalTextureName = !string.IsNullOrEmpty(surface.NormalTexturePath) ? surface.NormalTexturePath : "White";
        string metallicRoughnessTextureName = !string.IsNullOrEmpty(surface.MetallicRoughnessTexturePath) ? surface.MetallicRoughnessTexturePath : "White";
        string occlusionTextureName = !string.IsNullOrEmpty(surface.OcclusionTexturePath) ? surface.OcclusionTexturePath : "White";
        string emissiveTextureName = !string.IsNullOrEmpty(surface.EmissiveTexturePath) ? surface.EmissiveTexturePath : "White";

        int textureFlags = 0;
        if (!string.IsNullOrEmpty(surface.MetallicRoughnessTexturePath))
            textureFlags |= WebGPUTextureFlags.MetallicRoughness;
        if (!string.IsNullOrEmpty(surface.NormalTexturePath))
            textureFlags |= WebGPUTextureFlags.Normal;
        if (!string.IsNullOrEmpty(surface.OcclusionTexturePath))
            textureFlags |= WebGPUTextureFlags.Occlusion;
        if (!string.IsNullOrEmpty(surface.EmissiveTexturePath))
            textureFlags |= WebGPUTextureFlags.Emissive;

        return (textureName, normalTextureName, metallicRoughnessTextureName, occlusionTextureName, emissiveTextureName, textureFlags);
    }

    /// <summary>Prefer the snapshot resolved at Load time (including synthesized names for pixel sources and
    /// the "declared means enabled" flags). Fall back to path-based inference when no snapshot exists,
    /// for compatibility with callers that did not go through the new loading path.</summary>
    static (string textureName, string normalTextureName, string metallicRoughnessTextureName, string occlusionTextureName, string emissiveTextureName, int textureFlags) GetSurfaceTextureInfo(Surface surface, WGPUMesh3D.ResolvedTextureSet resolved)
    {
        if (resolved != null)
            return (resolved.BaseColor, resolved.Normal, resolved.MetallicRoughness, resolved.Occlusion, resolved.Emissive, resolved.TextureFlags);

        return GetSurfaceTextureInfo(surface);
    }

    void EnsureStaticMeshUploaded(WGPUMesh3D.SurfaceCacheEntry cache, Surface surface,
        string textureName, string normalTextureName, string metallicRoughnessTextureName, string occlusionTextureName, string emissiveTextureName)
    {
        if (!cache.Uploaded)
        {
            WebGPUInterop.UploadStaticMesh(
                cache.CacheKey, cache.VertexBytes, cache.IndexBytes,
                textureName, normalTextureName, metallicRoughnessTextureName, occlusionTextureName, emissiveTextureName,
                20, "uint16", surface.DoubleSided, false,
                Span<byte>.Empty, 0, 0);
            cache.Uploaded = true;
            cache.LastTextureName = textureName;
            cache.LastNormalTextureName = normalTextureName;
            cache.LastMetallicRoughnessTextureName = metallicRoughnessTextureName;
            cache.LastOcclusionTextureName = occlusionTextureName;
            cache.LastEmissiveTextureName = emissiveTextureName;
            cache.LastDoubleSided = surface.DoubleSided;
            return;
        }

        if (cache.LastTextureName != textureName
            || cache.LastNormalTextureName != normalTextureName
            || cache.LastMetallicRoughnessTextureName != metallicRoughnessTextureName
            || cache.LastOcclusionTextureName != occlusionTextureName
            || cache.LastEmissiveTextureName != emissiveTextureName
            || cache.LastDoubleSided != surface.DoubleSided)
        {
            // Rebind only: pass empty spans for bytes so the JS side can early-return and only update texture bindings.
            WebGPUInterop.UploadStaticMesh(
                cache.CacheKey, Span<byte>.Empty, Span<byte>.Empty,
                textureName, normalTextureName, metallicRoughnessTextureName, occlusionTextureName, emissiveTextureName,
                20, "uint16", surface.DoubleSided, false,
                Span<byte>.Empty, 0, 0);
            cache.LastTextureName = textureName;
            cache.LastNormalTextureName = normalTextureName;
            cache.LastMetallicRoughnessTextureName = metallicRoughnessTextureName;
            cache.LastOcclusionTextureName = occlusionTextureName;
            cache.LastEmissiveTextureName = emissiveTextureName;
            cache.LastDoubleSided = surface.DoubleSided;
        }
    }

    void BuildSurfaceUniform(float[] uniformData, float[] matrixData, Surface surface, float meshAlpha, bool isInstanced = false, int prevDataFlags = 0, Vector4 colorTint = default, bool excludeFromAo = false, WGPUMesh3D.ResolvedTextureSet resolvedTextures = null)
    {
        Array.Clear(uniformData, 48, WebGPUUniformLayout.TotalFloats - 48);
        Array.Copy(matrixData, 0, uniformData, 0, 48);

        float finalAlpha = surface.BaseColor.W * surface.Alpha * meshAlpha;
        // Mesh-level color multiplier: multiply RGB component-wise into BaseColor.
        // Leave W untouched so the alpha chain is unaffected. default(0) means no tint.
        var baseColor = surface.BaseColor;
        if (colorTint != default)
        {
            baseColor.X *= colorTint.X;
            baseColor.Y *= colorTint.Y;
            baseColor.Z *= colorTint.Z;
        }
        var w = new WebGPUUniformWriter(uniformData);
        w.SetBaseColor(baseColor);

        uint alphaMode = surface.Mode switch
        {
            SurfaceBlendMode.Mask => 1u,
            SurfaceBlendMode.Blend => 2u,
            _ => 0u,
        };
        float alphaCutoff = alphaMode == 1u ? surface.AlphaCutoff * meshAlpha : 0.5f;
        var surfaceTextureInfo = GetSurfaceTextureInfo(surface, resolvedTextures);
        // 2-2 Contract Clause 7: fold the GTAO exemption bit into flags.w so JS _selectPipelineMode can route the Nd pipeline variant.
        int textureFlags = surfaceTextureInfo.textureFlags;
        if (excludeFromAo)
            textureFlags |= WebGPUTextureFlags.NoDepthWrite;

        WriteLightUniform(
            uniformData,
            // 2-5: procedural sky takes priority over Unlit
            // (renderMode 3 selects the sky branch in WebGPUPipeline and consumes the SkyView LUT).
            renderMode: surface.ProceduralSky ? 3 : (surface.Unlit ? 0 : 1),
            metallic: surface.MetallicFactor,
            roughness: surface.RoughnessFactor,
            alpha: finalAlpha,
            emissive: new Vector3(surface.EmissiveFactor.X, surface.EmissiveFactor.Y, surface.EmissiveFactor.Z),
            alphaMode: alphaMode,
            alphaCutoff: alphaCutoff,
            textureFlags: textureFlags,
            isInstanced: isInstanced,
            prevDataFlags: prevDataFlags);

        // [SkyDebug] Invisible-starfield investigation: log render mode and material color on the first skybox surface draw.
        if (surface.ProceduralSky && !_skyDebugBoxLogged)
        {
            _skyDebugBoxLogged = true;
            DeviceServices.BaseApp.AddLog(LogType.Backend,
                $"[SkyDebug] Web skybox surface draw: ProceduralSky=true renderMode=3 " +
                $"baseColor=({baseColor.X:F3},{baseColor.Y:F3},{baseColor.Z:F3},{baseColor.W:F3}) alpha={finalAlpha:F3} texFlags={textureFlags}");
        }
    }

    static float ComputeTransparentDepth(Matrix4x4 world, Vector3 localCenter)
    {
        var center = Vector3.Transform(localCenter, world);
        var app = DeviceServices.BaseApp;
        if (app == null)
            return center.Z;

        var forward = app.CameraTarget - app.CameraPos;
        if (forward.LengthSquared() < 1e-6f)
            forward = Vector3.UnitZ;
        else
            forward = Vector3.Normalize(forward);

        return Vector3.Dot(center - app.CameraPos, forward);
    }

    void DrawMesh3DSurface(WGPUMesh3D wgpuMesh, Surface surface, float[] matrixData)
    {
        if (surface.Vertices == null || surface.Indices == null || surface.Vertices.Length == 0)
            return;

        var cache = GetOrCreateSurfaceCache(wgpuMesh.SurfaceCaches, wgpuMesh.Name, surface);
        wgpuMesh.ResolvedTextures.TryGetValue(surface, out var resolvedTextures);

        var uniformData = _scratchUniform;
        BuildSurfaceUniform(uniformData, matrixData, surface, wgpuMesh.MeshAlpha, colorTint: wgpuMesh.ColorTint, excludeFromAo: wgpuMesh.ExcludeFromAo, resolvedTextures: resolvedTextures);
        // 2-3 Contract Clause 6: this must run after BuildSurfaceUniform, because it clears the prev slots internally.
        // Only the main pass calls this method. The shadow path does not inject prev data, so the prev slots
        // stay all-zero and the VS sentinel skips motion-vector logic directly.
        WritePrevMatrices(uniformData, wgpuMesh.PrevWorldMatrix);
        var surfaceTextureInfo = GetSurfaceTextureInfo(surface, resolvedTextures);
        EnsureStaticMeshUploaded(cache, surface,
            surfaceTextureInfo.textureName,
            surfaceTextureInfo.normalTextureName,
            surfaceTextureInfo.metallicRoughnessTextureName,
            surfaceTextureInfo.occlusionTextureName,
            surfaceTextureInfo.emissiveTextureName);

        EnqueueDrawMesh3D(cache.CacheKey, uniformData);
    }

    // ── Unified highlight: Bounds box (Web backend uses separate cacheKey uploads + batched draws;
    // face/edge dual colors are independent of the host alpha chain) ──

    /// <summary>Lazily create the GPU resources for face/edge primitives. Each uses its own cacheKey, the
    /// geometry is uploaded once, and only uniforms change per frame afterward.</summary>
    void EnsureBoundsBoxUploaded(WebBoundsBox box)
    {
        if (box.Uploaded)
            return;

        WebGPUInterop.UploadStaticMesh(
            box.FaceCacheKey, box.FaceVertexBytes, box.FaceIndexBytes,
            "White", "White", "White", "White", "White",
            20, "uint16", true, false,
            Span<byte>.Empty, 0, 0);
        WebGPUInterop.UploadStaticMesh(
            box.EdgeCacheKey, box.EdgeVertexBytes, box.EdgeIndexBytes,
            "White", "White", "White", "White", "White",
            20, "uint16", true, false,
            Span<byte>.Empty, 0, 0);

        box.Uploaded = true;
    }

    /// <summary>Draw a single highlight box. If face alpha (SurfaceColor.W) is &gt; 0, use a BLEND translucent
    /// batch; otherwise it automatically becomes edge-only. Edges always use the OPAQUE batch with depth
    /// writes so the solid thin bars cover both the face and inner geometry. The world matrix is baked into
    /// the uniform and drawn per box. PrevWorld comes from the CPU-side shadow copy rolled in Update
    /// (first-frame Identity acts as the zero-velocity sentinel).</summary>
    internal void DrawBoundsBox(WebBoundsBox box, Matrix4x4 view, Matrix4x4 projection)
    {
        EnsureBoundsBoxUploaded(box);

        var matrixData = _scratchMatrix48;
        CopyMatrixTransposed(box.World, matrixData, 0);
        CopyMatrixTransposed(view, matrixData, 16);
        CopyMatrixTransposed(projection, matrixData, 32);

        if (box.FaceAlpha > 0f)
        {
            var uniformData = _scratchUniform;
            Array.Clear(uniformData, 48, WebGPUUniformLayout.TotalFloats - 48);
            Array.Copy(matrixData, 0, uniformData, 0, 48);
            // 2-3 Contract Clause 6: the prev slot must be written after Clear
            // (Clear overwrites the whole history region starting at float 48).
            WritePrevMatrices(uniformData, box.PrevWorld);
            new WebGPUUniformWriter(uniformData).SetBaseColor(box.FaceColor);
            WriteLightUniform(uniformData,
                renderMode: (int)WebGPURenderMode.Unlit,
                metallic: 0f, roughness: 1f,
                alpha: box.FaceAlpha,
                emissive: Vector3.Zero,
                alphaMode: (uint)WebGPUAlphaMode.Blend,
                alphaCutoff: 0.5f,
                textureFlags: 0);
            EnqueueDrawMesh3D(box.FaceCacheKey, uniformData);
        }

        {
            var uniformData = _scratchUniform;
            Array.Clear(uniformData, 48, WebGPUUniformLayout.TotalFloats - 48);
            Array.Copy(matrixData, 0, uniformData, 0, 48);
            WritePrevMatrices(uniformData, box.PrevWorld);
            new WebGPUUniformWriter(uniformData).SetBaseColor(box.EdgeColor);
            WriteLightUniform(uniformData,
                renderMode: (int)WebGPURenderMode.Unlit,
                metallic: 0f, roughness: 1f,
                alpha: 1f,
                emissive: Vector3.Zero,
                alphaMode: (uint)WebGPUAlphaMode.Opaque,
                alphaCutoff: 0.5f,
                textureFlags: 0);
            EnqueueDrawMesh3D(box.EdgeCacheKey, uniformData);
        }
    }

    /// <summary>Unified highlight: draw all instance boxes whose Bounds highlight is enabled this frame
    /// (DrawBoundsBox per box, then flush the batch).</summary>
    void DrawInstanceBoundsBoxes(List<WebBoundsBox> boxes, List<int> drawList, Matrix4x4 view, Matrix4x4 projection)
    {
        for (int i = 0; i < drawList.Count; i++)
            DrawBoundsBox(boxes[drawList[i]], view, projection);
    }

    /// <summary>Unified highlight (wireframe shell): lazily create GPU resources for face/edge primitives.
    /// Each has an independent cacheKey, geometry is uploaded once, and only uniforms change per frame.
    /// Shells use the full 20-float vertex payload, including joints/weights, so skinned shells go through
    /// the same VS skinning path. Morph shells carry expanded delta buffers matching shell-vertex layout,
    /// with separate vertex counts for face and edge.</summary>
    void EnsureShellBoxUploaded(WebShellBox box)
    {
        if (box.Uploaded)
            return;

        WebGPUInterop.UploadStaticMesh(
            box.FaceCacheKey, box.FaceVertexBytes, box.FaceIndexBytes,
            "White", "White", "White", "White", "White",
            20, box.Use32BitFaceIndices ? "uint32" : "uint16", true, box.HasSkinning,
            box.FaceMorphDeltaBytes, (int)box.MorphTargetCount, (int)box.FaceMorphVertexCount);
        WebGPUInterop.UploadStaticMesh(
            box.EdgeCacheKey, box.EdgeVertexBytes, box.EdgeIndexBytes,
            "White", "White", "White", "White", "White",
            20, box.Use32BitEdgeIndices ? "uint32" : "uint16", true, box.HasSkinning,
            box.EdgeMorphDeltaBytes, (int)box.MorphTargetCount, (int)box.EdgeMorphVertexCount);

        // [ShellDiag] One-time upload-contract diagnostics (the Uploaded gate naturally makes this once per box):
        // cacheKey / index width / byte sizes
        DeviceServices.BaseApp.AddLog(LogType.Backend,
            $"{DateTime.UtcNow} [ShellDiag] UploadStaticMesh face={box.FaceCacheKey} fVtxB={box.FaceVertexBytes.Length} fIdxB={box.FaceIndexBytes.Length} fFmt={(box.Use32BitFaceIndices ? "uint32" : "uint16")} | edge={box.EdgeCacheKey} eVtxB={box.EdgeVertexBytes.Length} eIdxB={box.EdgeIndexBytes.Length} eFmt={(box.Use32BitEdgeIndices ? "uint32" : "uint16")} skin={box.HasSkinning}");

        box.Uploaded = true;
    }

    /// <summary>Unified highlight: draw a single wireframe shell box. If face alpha (SurfaceColor.W) is
    /// &gt; 0, use a BLEND translucent batch; otherwise it automatically becomes edge-only. Edges always use an
    /// OPAQUE depth-writing batch so the solid thin bars cover the face and interior geometry. Skinned shells
    /// use the skinned batch for exact skeletal animation matching, while non-skinned shells use the normal
    /// batch (EnqueueShellDraw flushes the other channel internally as needed). Morph shells upload
    /// morphWeights through the uniform every frame; the delta buffers are already prepared during
    /// UploadStaticMesh. The world matrix is baked into the uniform and drawn per box. PrevWorld comes from
    /// the CPU-side shadow copy rolled in Update (first-frame Identity acts as the zero-velocity sentinel).</summary>
    internal void DrawShellBox(WebShellBox box, Matrix4x4 view, Matrix4x4 projection, Vector4? morphWeights = null)
    {
        EnsureShellBoxUploaded(box);

        var matrixData = _scratchMatrix48;
        CopyMatrixTransposed(box.World, matrixData, 0);
        CopyMatrixTransposed(view, matrixData, 16);
        CopyMatrixTransposed(projection, matrixData, 32);

        if (box.FaceAlpha > 0f)
        {
            var uniformData = _scratchUniform;
            Array.Clear(uniformData, 48, WebGPUUniformLayout.TotalFloats - 48);
            Array.Copy(matrixData, 0, uniformData, 0, 48);
            // 2-3 Contract Clause 6: the prev slot must be written after Clear
            // (Clear overwrites the whole history region starting at float 48).
            WritePrevMatrices(uniformData, box.PrevWorld);
            new WebGPUUniformWriter(uniformData).SetBaseColor(box.FaceColor);
            WriteLightUniform(uniformData,
                renderMode: (int)WebGPURenderMode.Unlit,
                metallic: 0f, roughness: 1f,
                alpha: box.FaceAlpha,
                emissive: Vector3.Zero,
                alphaMode: (uint)WebGPUAlphaMode.Blend,
                alphaCutoff: 0.5f,
                textureFlags: 0,
                isSkinned: box.HasSkinning,
                isMorph: morphWeights.HasValue,
                morphWeights: morphWeights ?? default);
            EnqueueShellDraw(box.FaceCacheKey, uniformData, box.HasSkinning);
        }

        {
            var uniformData = _scratchUniform;
            Array.Clear(uniformData, 48, WebGPUUniformLayout.TotalFloats - 48);
            Array.Copy(matrixData, 0, uniformData, 0, 48);
            WritePrevMatrices(uniformData, box.PrevWorld);
            new WebGPUUniformWriter(uniformData).SetBaseColor(box.EdgeColor);
            WriteLightUniform(uniformData,
                renderMode: (int)WebGPURenderMode.Unlit,
                metallic: 0f, roughness: 1f,
                alpha: 1f,
                emissive: Vector3.Zero,
                alphaMode: (uint)WebGPUAlphaMode.Opaque,
                alphaCutoff: 0.5f,
                textureFlags: 0,
                isSkinned: box.HasSkinning,
                isMorph: morphWeights.HasValue,
                morphWeights: morphWeights ?? default);
            EnqueueShellDraw(box.EdgeCacheKey, uniformData, box.HasSkinning);
        }
    }

    /// <summary>Unified highlight (wireframe shell): enqueue by channel. Skinned shells flush the normal
    /// batch first and then enter the skinned batch (must be inside a BeginSkinnedModelDraw session), while
    /// non-skinned shells do the reverse, matching the non-skinned branch ordering in DrawPrimitive.</summary>
    void EnqueueShellDraw(string cacheKey, float[] uniformData, bool isSkinned)
    {
        if (isSkinned)
        {
            FlushDrawMesh3DBatch();
            EnqueueDrawSkinnedMesh(cacheKey, uniformData);
        }
        else
        {
            FlushDrawSkinnedMeshBatch();
            EnqueueDrawMesh3D(cacheKey, uniformData);
        }
    }

    /// <summary>Unified highlight: draw all instance shells whose wireframe highlight is enabled this frame.
    /// Each ShellDrawEntry already bakes the world/prev/color captured during Update, so Draw only writes
    /// uniforms and does not mutate box state. Shared box geometry is reused per instance, then the batch is
    /// flushed. This is the non-skinned / rigid InstancedMesh3D path.</summary>
    void DrawInstanceShells(WebShellBox shell, List<ShellDrawEntry> drawList, Matrix4x4 view, Matrix4x4 projection)
    {
        for (int i = 0; i < drawList.Count; i++)
        {
            var entry = drawList[i];
            shell.World = entry.World;
            shell.PrevWorld = entry.PrevWorld;
            shell.FaceColor = entry.FaceColor;
            shell.FaceAlpha = entry.FaceColor.W;
            shell.EdgeColor = entry.EdgeColor;
            DrawShellBox(shell, view, projection);
        }
    }

    /// <summary>Unified highlight: draw all instance shells whose wireframe highlight is enabled this frame
    /// on the InstancedModel path. Rigid shells consume ShellDrawEntry data captured during Update and only
    /// write uniforms during Draw, reusing shared box geometry per instance and flushing afterward. Skinned
    /// shells use instanced drawing (count=1 + firstInstance=slot), with the bone palette addressed by
    /// instanceIndex×100, exactly mirroring the per-slot OutlineMask path. world/prev both come from the
    /// instance byte stream, so the uniform world slot stays Identity. Flush the two batches before the
    /// direct JS call, matching the ordering discipline at the start of DrawInstancedModel.</summary>
    void DrawInstancedModelShells(WGPUInstancedModel wgpuModel, Matrix4x4 view, Matrix4x4 projection)
    {
        var drawList = wgpuModel.ShellDrawList;
        if (drawList.Count == 0)
            return;

        bool isSkinned = !string.IsNullOrEmpty(wgpuModel.SkinKey);
        byte[]? instanceBytes = null;
        byte[]? prevInstanceBytes = null;
        if (isSkinned)
        {
            if (!wgpuModel.TryGetShellStreamBytes(out var ib, out var pib))
                isSkinned = false;
            else
            {
                instanceBytes = ib;
                // 2-3 Step C: the prev-instance byte stream must have the same length as the current frame,
                // using the same rule as the main pass; otherwise treat it as no history.
                prevInstanceBytes = pib.Length == ib.Length && pib.Length > 0 ? pib : null;
            }
        }

        // Skinned-shell world matrices come from the instance byte stream, so the uniform world slot stays
        // Identity. _scratchMatrix48 is overwritten by Bounds / rigid-shell paths, so rebuild into a
        // separate array to avoid cross-draw interference.
        var skinnedMatrixData = new float[48];
        if (isSkinned)
        {
            CopyMatrixTransposed(Matrix4x4.Identity, skinnedMatrixData, 0);
            CopyMatrixTransposed(view, skinnedMatrixData, 16);
            CopyMatrixTransposed(projection, skinnedMatrixData, 32);
        }

        // [ShellDiag] One-time diagnostics at the draw entry point (for wireframe-not-visible investigation;
        // remove after confirmation): drawList / instance-stream state / shell inventory, answering
        // "was drawing actually issued, and which branch did it take?"
        if (!wgpuModel.ShellDrawLogged)
        {
            wgpuModel.ShellDrawLogged = true;
            var shellsDesc = string.Join(" | ", wgpuModel.ShellGeometries.ConvertAll(s =>
                $"key={s.FaceCacheKey} skin={s.HasSkinning} uploaded={s.Uploaded}"));
            DeviceServices.BaseApp.AddLog(LogType.Backend,
                $"{DateTime.UtcNow} [ShellDiag] DrawInstancedModelShells model={wgpuModel.Name} drawList={drawList.Count} skinKey={wgpuModel.SkinKey} streamBytes={(isSkinned ? instanceBytes!.Length.ToString() : "n/a")} shells={wgpuModel.ShellGeometries.Count} [{shellsDesc}]");
        }

        foreach (var shell in wgpuModel.ShellGeometries)
        {
            // Skinned shells call JS drawInstancedMesh3D directly instead of going through DrawShellBox,
            // so geometry must be explicitly uploaded here. If missing, the JS side silently skips the draw
            // as missing-static-mesh. Rigid shells are uploaded inside DrawShellBox, and repeated entry
            // just returns through the Uploaded gate.
            EnsureShellBoxUploaded(shell);
            for (int i = 0; i < drawList.Count; i++)
            {
                var entry = drawList[i];
                if (shell.HasSkinning && isSkinned && instanceBytes != null)
                {
                    // Skinned shells: BLEND translucent faces + OPAQUE depth-writing edges
                    // (existing semantics unchanged). The prev sentinel bits are driven by
                    // prevInstanceBytes / PrevBonesReady; the cold all-zero sentinel falls back to the current
                    // frame, same as the main pass.
                    int prevDataFlags = 0;
                    if (prevInstanceBytes != null)
                        prevDataFlags |= WebGPUPrevDataFlags.PrevInstanceWorld;
                    if (wgpuModel.PrevBonesReady)
                        prevDataFlags |= WebGPUPrevDataFlags.PrevBones;

                    if (entry.FaceColor.W > 0f)
                    {
                        var faceUniform = BuildShellUniform(skinnedMatrixData, entry.FaceColor, entry.FaceColor.W,
                            WebGPUAlphaMode.Blend, prevDataFlags);
                        FlushDrawMesh3DBatch();
                        FlushDrawSkinnedMeshBatch();
                        _jsRuntime.InvokeVoid("seasonWebGPU.drawInstancedMesh3D", shell.FaceCacheKey, faceUniform,
                            instanceBytes, 1, wgpuModel.SkinKey, prevInstanceBytes, entry.WriteIndex);
                    }

                    var edgeUniform = BuildShellUniform(skinnedMatrixData, entry.EdgeColor, 1f,
                        WebGPUAlphaMode.Opaque, prevDataFlags);
                    FlushDrawMesh3DBatch();
                    FlushDrawSkinnedMeshBatch();
                    _jsRuntime.InvokeVoid("seasonWebGPU.drawInstancedMesh3D", shell.EdgeCacheKey, edgeUniform,
                        instanceBytes, 1, wgpuModel.SkinKey, prevInstanceBytes, entry.WriteIndex);
                }
                else
                {
                    shell.World = entry.World;
                    shell.PrevWorld = entry.PrevWorld;
                    shell.FaceColor = entry.FaceColor;
                    shell.FaceAlpha = entry.FaceColor.W;
                    shell.EdgeColor = entry.EdgeColor;
                    DrawShellBox(shell, view, projection);
                }
            }
        }
    }

    /// <summary>Unified highlight (wireframe shell): instanced uniform for skinned shells. Uses Unlit +
    /// ShellColor/FaceAlpha/EdgeAlpha, sets textureFlags bit4(instanced)+bit5(skinned), and drives the prev
    /// sentinel bits from prevInstanceBytes / PrevBonesReady. The world slot always stays Identity because
    /// matrices come from the instance byte stream. prev world is supplied through binding 14 instance bytes,
    /// while WritePrevMatrices leaves the zero sentinel, meaning per-instance history is not baked into the
    /// uniform, matching main-pass Clause 8(d).</summary>
    static float[] BuildShellUniform(float[] matrixData, Vector4 color, float alpha, WebGPUAlphaMode alphaMode, int prevDataFlags)
    {
        var uniformData = new float[WebGPUUniformLayout.TotalFloats];
        Array.Copy(matrixData, 0, uniformData, 0, 48);
        Array.Clear(uniformData, 48, WebGPUUniformLayout.TotalFloats - 48);
        // 2-3 Contract Clause 6: the prev slot must be written after Clear
        // (Clear overwrites the whole history region starting at float 48).
        WritePrevMatrices(uniformData);
        new WebGPUUniformWriter(uniformData).SetBaseColor(color);
        WriteLightUniform(uniformData,
            renderMode: (int)WebGPURenderMode.Unlit,
            metallic: 0f, roughness: 1f,
            alpha: alpha,
            emissive: Vector3.Zero,
            alphaMode: (uint)alphaMode,
            alphaCutoff: 0.5f,
            textureFlags: 0,
            isInstanced: true,
            isSkinned: true,
            prevDataFlags: prevDataFlags);
        return uniformData;
    }

    /// <summary>Unified highlight (wireframe shell): lazily build per-surface shell boxes for non-instanced
    /// Mesh3D at runtime. On the first frame where wireframe is enabled, build one shell per surface in
    /// surface order, using null placeholders for surfaces with no valid triangles / degenerate geometry.
    /// Memory stays at zero when fully disabled. Once built, shells stay resident and are neither rebuilt nor
    /// released on runtime toggle. edgeWidth comes from host Highlight.EdgeWidth as a model-size ratio.
    /// localSizeMax is the largest local-space model dimension used as the scale reference, so per-surface
    /// local thickness is baked as h = edgeWidth × localSizeMax. Mesh3D has no node chain, so NodeScaleOf = 1.
    /// If this diverges from the host configuration, release and rebuild immediately in the same frame.</summary>
    void EnsureMesh3DShells(WGPUMesh3D wgpuMesh, Mesh3D mesh)
    {
        if (wgpuMesh.ShellBoxes != null)
        {
            if (wgpuMesh.BuiltShellEdgeWidth == mesh.Highlight.EdgeWidth)
                return;
            // Edge width changed: invalidate the old shell geometry
            // (JS-side GPU resources are reclaimed by GC) and rebuild with the new width immediately.
            wgpuMesh.ShellBoxes = null;
        }
        if (mesh.Surfaces.Count == 0)
            return;
        var localSizeMax = MathF.Max(mesh.LocalSize.X, MathF.Max(mesh.LocalSize.Y, mesh.LocalSize.Z));
        wgpuMesh.ShellBoxes = new List<WebShellBox?>(mesh.Surfaces.Count);
        for (int i = 0; i < mesh.Surfaces.Count; i++)
        {
            var surface = mesh.Surfaces[i];
            wgpuMesh.ShellBoxes.Add(surface.Vertices != null && surface.Indices != null && surface.Vertices.Length > 0 && surface.Indices.Length >= 3
                ? WebShellBox.Create($"{mesh.Name}:{mesh.ID}:HOST:{i}",
                    surface.Vertices, Array.ConvertAll(surface.Indices, static i => (uint)i),
                    HighlightGeometry.ComputeShellThickness(mesh.Highlight.EdgeWidth, localSizeMax, null))
                : null);
        }
        wgpuMesh.BuiltShellEdgeWidth = mesh.Highlight.EdgeWidth;
    }

    public void DisposeMesh3D(Mesh3D mesh)
    {
        var key = (mesh.Name, mesh.ID);
        lock (DictionaryMesh3D)
        {
            if (DictionaryMesh3D.TryGetValue(key, out var wgpuMesh))
                DictionaryMesh3D.Remove(key);
        }

        // wgpuMesh is only a C# bookkeeping wrapper. GPU resources are owned by JS-side GC, and the browser
        // already delays reclaiming references that are still in flight at destroy time, so no fence-gated
        // delayed release is required (contrast DX/VK; see WGPUModel.cs).
        // Remove synthesized procedural-texture metadata slot by slot (registered under mesh-private names).
        ReleaseProcSurfaceTextures(mesh.Name, mesh.ID, mesh.Surfaces.Count);

        mesh.Ready = false;
    }

    public async Task<bool> LoadInstancedMesh3D(InstancedMesh3D mesh)
    {
        return await LoadInstancedMesh3DCore(mesh);
    }

    async Task<bool> LoadInstancedMesh3DCore(InstancedMesh3D mesh)
    {
        lock (DictionaryInstancedMesh3D)
        {
            if (DictionaryInstancedMesh3D.ContainsKey((mesh.Name, mesh.ID)))
                return true;
        }

        var wgpuMesh = new WGPUInstancedMesh3D(mesh.Name);

        // 1. Pre-resolve every texture source referenced by the Surface list
        //    (pixel sources upload directly without temp files; path sources reuse LoadTextureAsync),
        //    and store the resolved snapshot for per-frame Draw.
        await ResolveMeshSurfaceTextures(mesh.Name, mesh.ID, mesh.Surfaces, wgpuMesh.ResolvedTextures);

        // 2. Clear TextureOverride after Load completes (single-consumption contract).
        foreach (var surface in mesh.Surfaces)
            ClearSurfaceOverrides(surface);

        lock (DictionaryInstancedMesh3D)
        {
            if (!DictionaryInstancedMesh3D.ContainsKey((mesh.Name, mesh.ID)))
                DictionaryInstancedMesh3D.Add((mesh.Name, mesh.ID), wgpuMesh);
        }

        return true;
    }

    public void UpdateInstancedMesh3D(InstancedMesh3D mesh, float time)
    {
        WGPUInstancedMesh3D wgpuMesh = null;
        lock (DictionaryInstancedMesh3D)
        {
            DictionaryInstancedMesh3D.TryGetValue((mesh.Name, mesh.ID), out wgpuMesh);
        }

        wgpuMesh?.Update(mesh, Camera3D);
    }

    public void DrawInstancedMesh3D(InstancedMesh3D mesh)
    {
        if (mesh.Alpha == 0f || !_initialized)
            return;

        WGPUInstancedMesh3D wgpuMesh = null;
        lock (DictionaryInstancedMesh3D)
        {
            DictionaryInstancedMesh3D.TryGetValue((mesh.Name, mesh.ID), out wgpuMesh);
        }

        if (wgpuMesh == null || !wgpuMesh.TransformInitialized || wgpuMesh.EnabledInstanceCount <= 0)
            return;

        #region debug-point G:poll-instanced-diag
        if ((++_instancedDiagPollCounter % 30) == 0)
        {
            var diag = _jsRuntime.Invoke<WebInstancedDiagState?>("seasonWebGPU.getInstancedDiagState");
            if (diag != null)
            {
                string signature = $"drawCalls={diag.drawCalls}|cache={diag.lastCacheKey}|count={diag.lastInstanceCount}|bytes={diag.lastInstanceBytes}|mode={diag.lastModeKey}|lost={diag.deviceLost}|lostReason={diag.deviceLostReason}|uncaptured={diag.uncapturedError}|error={diag.lastError}";
                if (!string.Equals(signature, _lastInstancedDiagSignature, StringComparison.Ordinal))
                {
                    _lastInstancedDiagSignature = signature;
                    DeviceServices.BaseApp.AddLog(LogType.Backend, $"{DateTime.UtcNow} [WebInstancedDiag] {signature}");
                }
            }
        }
        #endregion

        FlushDrawMesh3DBatch();
        FlushDrawSkinnedMeshBatch();

        var matrixData = _scratchMatrix48;
        CopyMatrixTransposed(Matrix4x4.Identity, matrixData, 0);
        CopyMatrixTransposed(wgpuMesh.View, matrixData, 16);
        CopyMatrixTransposed(wgpuMesh.Projection, matrixData, 32);

        foreach (var surface in mesh.Surfaces)
        {
            if (surface.Vertices == null || surface.Indices == null || surface.Vertices.Length == 0)
                continue;

            var cache = GetOrCreateSurfaceCache(wgpuMesh.SurfaceCaches, wgpuMesh.Name, surface);
            wgpuMesh.ResolvedTextures.TryGetValue(surface, out var resolvedTextures);
            var surfaceTextureInfo = GetSurfaceTextureInfo(surface, resolvedTextures);
            EnsureStaticMeshUploaded(cache, surface,
                surfaceTextureInfo.textureName,
                surfaceTextureInfo.normalTextureName,
                surfaceTextureInfo.metallicRoughnessTextureName,
                surfaceTextureInfo.occlusionTextureName,
                surfaceTextureInfo.emissiveTextureName);

            // 2-3 Step C (structural fallback for Contract Clause 8(b)): per-instance world-matrix history
            // travels through the prev-instance-world SB at binding 14.
            // When transparent surfaces are split into one draw per instance, only one world matrix
            // (16 floats) is uploaded, which does not match the prev stream's per-instance 5×vec4 stride, so
            // prev is not fed there and behavior falls back to Step A (camera-motion velocity only).
            bool blendSurface = surface.Mode == SurfaceBlendMode.Blend;
            byte[]? prevInstanceBytes = (!blendSurface
                && wgpuMesh.PrevInstanceBytes.Length > 0
                && wgpuMesh.PrevInstanceBytes.Length == wgpuMesh.InstanceBytes.Length)
                ? wgpuMesh.PrevInstanceBytes
                : null;
            int prevDataFlags = prevInstanceBytes != null ? WebGPUPrevDataFlags.PrevInstanceWorld : 0;

            var uniformData = new float[WebGPUUniformLayout.TotalFloats];
            BuildSurfaceUniform(uniformData, matrixData, surface, wgpuMesh.MeshAlpha, isInstanced: true, prevDataFlags: prevDataFlags, resolvedTextures: resolvedTextures);
            // 2-3 Contract Clause 8(d): on instanced paths PrevWorld stays all-zero because world comes from
            // instance attributes. After Step C, per-instance history is carried by binding 14, and the VS
            // prefers it when hasPrevInstanceWorld is set. If not set, it still falls back to the current
            // frame's world, yielding camera-motion velocity only.
            WritePrevMatrices(uniformData);

            if (blendSurface)
            {
                var orderedInstances = Enumerable.Range(0, wgpuMesh.EnabledInstanceCount)
                    .OrderByDescending(index => ComputeTransparentDepth(wgpuMesh.InstanceWorlds[index], cache.LocalBoundsCenter));

                foreach (var instanceIndex in orderedInstances)
                {
                    var singleMatrixBytes = ToByteArray(new[] { wgpuMesh.InstanceWorlds[instanceIndex] });
                    _jsRuntime.InvokeVoid("seasonWebGPU.drawInstancedMesh3D", cache.CacheKey, uniformData, singleMatrixBytes, 1);
                }
            }
            else
            {
                _jsRuntime.InvokeVoid("seasonWebGPU.drawInstancedMesh3D", cache.CacheKey, uniformData, wgpuMesh.InstanceBytes, wgpuMesh.EnabledInstanceCount, null, prevInstanceBytes);
            }
        }

        // Unified highlight (per-instance Bounds boxes): for instances enabled this frame, use BLEND faces +
        // OPAQUE edges, and flush after all surfaces.
        if (wgpuMesh.BoundsActive)
            DrawInstanceBoundsBoxes(wgpuMesh.InstanceBoundsBoxes, wgpuMesh.BoundsBoxDrawList, wgpuMesh.View, wgpuMesh.Projection);

        // Unified highlight (per-instance wireframe shells): for instances enabled this frame, use BLEND
        // faces + OPAQUE edges, and flush after Bounds.
        if (wgpuMesh.WireframeActive && wgpuMesh.ShellGeometry != null)
            DrawInstanceShells(wgpuMesh.ShellGeometry, wgpuMesh.ShellDrawList, wgpuMesh.View, wgpuMesh.Projection);
    }

    public void DisposeInstancedMesh3D(InstancedMesh3D mesh)
    {
        var key = (mesh.Name, mesh.ID);
        lock (DictionaryInstancedMesh3D)
        {
            if (DictionaryInstancedMesh3D.ContainsKey(key))
                DictionaryInstancedMesh3D.Remove(key);
        }

        // Remove synthesized procedural-texture metadata slot by slot (registered under mesh-private names).
        ReleaseProcSurfaceTextures(mesh.Name, mesh.ID, mesh.Surfaces.Count);

        mesh.Ready = false;
    }

    // ── InstancedModel (GLB GPU instancing) ──

    public async Task<bool> LoadInstancedModel(InstancedModel model)
    {
        lock (DictionaryInstancedModel)
        {
            if (DictionaryInstancedModel.ContainsKey((model.ModelName, model.ID)))
            {
                return true;
            }
        }

        WGPUModel? template;
        try
        {
            template = await GetOrCreateSharedModelAsync(model.ModelName);
        }
        catch (Exception ex)
        {
            DeviceServices.BaseApp.AddLog(LogType.Error, $"{DateTime.UtcNow} [WebGPUGraphics] Failed to prepare model resource for InstancedModel {model.ModelName}: {ex}");
            return true;
        }

        var wgpuInstancedModel = new WGPUInstancedModel(model.ModelName);
        wgpuInstancedModel.Load(template, model);
        wgpuInstancedModel.View = Camera3D.View;
        wgpuInstancedModel.Projection = Camera3D.Projection;

        // v2 picking: inject the instanced GltfAsset. The cloned node tree / animation / bone palette comes
        // from the same source as instance rendering, matching the runtime-asset injection model on VK/Metal/DX.
        // The template Asset's player is never evaluated, and narrow-phase skinning would otherwise skip
        // everything because of empty bones.
        model.Asset = wgpuInstancedModel.Asset;

        // 1-3: copy the shared-template local bounds back to the control
        // as the per-instance sphere broad-phase source, once during load.
        model.TemplateLocalBounds = template.Asset.Model.LocalBounds;
        // Unified transform convention: likewise copy back the raw bounds
        // for instance anchors / per-axis scaling before animation expansion.
        model.TemplateLocalBoundsRaw = template.Asset.Model.LocalBoundsRaw;
        model.AnimationNames = wgpuInstancedModel.GetAnimationNames();
        model.AnimationClipCount = model.AnimationNames.Count;

        lock (DictionaryInstancedModel)
        {
            if (!DictionaryInstancedModel.ContainsKey((model.ModelName, model.ID)))
                DictionaryInstancedModel.Add((model.ModelName, model.ID), wgpuInstancedModel);
        }

        return true;
    }

    public void UpdateInstancedModel(InstancedModel model, float time)
    {
        WGPUInstancedModel? wgpuModel = null;
        lock (DictionaryInstancedModel)
        {
            DictionaryInstancedModel.TryGetValue((model.ModelName, model.ID), out wgpuModel);
        }

        wgpuModel?.Update(model, time, Camera3D);
    }

    public void DrawInstancedModel(InstancedModel model)
    {
        if (model.Alpha == 0f || !_initialized)
            return;

        WGPUInstancedModel? wgpuModel = null;
        lock (DictionaryInstancedModel)
        {
            DictionaryInstancedModel.TryGetValue((model.ModelName, model.ID), out wgpuModel);
        }

        if (wgpuModel == null || !wgpuModel.TransformInitialized || wgpuModel.EnabledInstanceCount <= 0)
            return;

        FlushDrawMesh3DBatch();
        FlushDrawSkinnedMeshBatch();

        var matrixData = _scratchMatrix48;
        CopyMatrixTransposed(Matrix4x4.Identity, matrixData, 0);
        CopyMatrixTransposed(wgpuModel.View, matrixData, 16);
        CopyMatrixTransposed(wgpuModel.Projection, matrixData, 32);

        var opaquePrimitives = new List<WGPUPrimitiveData>();
        var transparentPrimitives = new List<WGPUPrimitiveData>();

        foreach (var prim in wgpuModel.Primitives)
        {
            // Skip primitives with no data.
            if (prim.VertexData == null || prim.IndexData == null || prim.VertexData.Length == 0)
                continue;
            // v1 static models skip skinned primitives; v2 allows skinning when SkinKey is present.
            if (prim.HasSkinning && string.IsNullOrEmpty(wgpuModel.SkinKey))
                continue;

            // Ensure GPU upload.
            EnsureModelPrimitiveUploaded(prim);

            if (prim.IsTransparent)
                transparentPrimitives.Add(prim);
            else
                opaquePrimitives.Add(prim);
        }

        float modelAlpha = wgpuModel.ModelAlpha;
        bool isSkinned = !string.IsNullOrEmpty(wgpuModel.SkinKey);
        if (isSkinned && wgpuModel.BoneMatricesBytes.Length > 0)
            WebGPUInterop.UploadSkinnedBones(wgpuModel.SkinKey, wgpuModel.BoneMatricesBytes);

        // 1. Opaque
        foreach (var prim in opaquePrimitives)
        {
            if (!wgpuModel.TryGetPrimitiveInstanceData(prim, out _, out var instanceBytes, out var prevBytes) || instanceBytes.Length == 0)
                continue;

            // 2-3 Step C (Contract Clause 8(b)(c)): both per-instance world history and morph-weight history
            // are carried by the prev-instance byte stream
            // (the 5th vec4 stores previous-frame morph weights). Bone history is prepared automatically by
            // the JS-side shadow copy; this side only sets the flags.
            byte[]? prevInstanceBytes = prevBytes.Length == instanceBytes.Length && prevBytes.Length > 0 ? prevBytes : null;
            int prevDataFlags = 0;
            if (prevInstanceBytes != null)
            {
                prevDataFlags |= WebGPUPrevDataFlags.PrevInstanceWorld;
                if (prim.MorphTargetCount > 0)
                    prevDataFlags |= WebGPUPrevDataFlags.PrevMorph;
            }
            if (isSkinned && wgpuModel.PrevBonesReady)
                prevDataFlags |= WebGPUPrevDataFlags.PrevBones;

            var uniformData = BuildModelPrimitiveUniform(matrixData, prim, modelAlpha, isSkinned, prevDataFlags);
            // 2-3 Contract Clause 8(d): instanced models keep PrevWorld all-zero, and per-instance history
            // travels through binding 14.
            WritePrevMatrices(uniformData);
            _jsRuntime.InvokeVoid("seasonWebGPU.drawInstancedMesh3D", prim.CacheKey, uniformData, instanceBytes, wgpuModel.EnabledInstanceCount, wgpuModel.SkinKey, prevInstanceBytes);
        }

        // 2. Transparent: draw one instance at a time after depth sorting.
        foreach (var prim in transparentPrimitives)
        {
            if (!wgpuModel.TryGetPrimitiveInstanceData(prim, out var instanceWorlds, out _) || instanceWorlds.Length == 0)
                continue;

            var uniformData = BuildModelPrimitiveUniform(matrixData, prim, modelAlpha, isSkinned);
            // 2-3 Contract Clause 8(d): same rule as above
            // (transparent primitives are split into per-instance draws while sharing one uniform).
            WritePrevMatrices(uniformData);

            var orderedInstances = Enumerable.Range(0, wgpuModel.EnabledInstanceCount)
                .OrderByDescending(index => ComputeTransparentDepth(instanceWorlds[index], prim.LocalBoundsCenter));

            foreach (var instanceIndex in orderedInstances)
            {
                var singleMatrixBytes = ToByteArray(new[] { instanceWorlds[instanceIndex] });
                _jsRuntime.InvokeVoid("seasonWebGPU.drawInstancedMesh3D", prim.CacheKey, uniformData, singleMatrixBytes, 1, wgpuModel.SkinKey);
            }
        }

        // Unified highlight (per-instance Bounds boxes): for instances enabled this frame, use BLEND faces +
        // OPAQUE edges, and flush after all surfaces.
        if (wgpuModel.BoundsActive)
            DrawInstanceBoundsBoxes(wgpuModel.InstanceBoundsBoxes, wgpuModel.BoundsBoxDrawList, wgpuModel.View, wgpuModel.Projection);

        // Unified highlight (per-instance wireframe shells): for instances enabled this frame, use BLEND
        // faces + OPAQUE edges, and flush after Bounds.
        if (wgpuModel.WireframeActive && wgpuModel.ShellGeometries.Count > 0)
            DrawInstancedModelShells(wgpuModel, wgpuModel.View, wgpuModel.Projection);
    }

    /// <summary>Build the primitive uniform data used for instanced drawing.</summary>
    static float[] BuildModelPrimitiveUniform(float[] matrixData, WGPUPrimitiveData prim, float modelAlpha, bool isSkinned = false, int prevDataFlags = 0)
    {
        var uniformData = new float[WebGPUUniformLayout.TotalFloats];
        Array.Copy(matrixData, 0, uniformData, 0, 48);
        Array.Clear(uniformData, 48, WebGPUUniformLayout.TotalFloats - 48);

        float finalAlpha = prim.OriginalBaseColorAlpha * modelAlpha;
        var w = new WebGPUUniformWriter(uniformData);
        w.SetBaseColor(new Vector4(prim.BaseColor.X, prim.BaseColor.Y, prim.BaseColor.Z, prim.BaseColor.W));

        int textureFlags = 0;
        if (!string.IsNullOrEmpty(prim.MetallicRoughnessTextureName)) textureFlags |= WebGPUTextureFlags.MetallicRoughness;
        if (!string.IsNullOrEmpty(prim.NormalTextureName)) textureFlags |= WebGPUTextureFlags.Normal;
        if (!string.IsNullOrEmpty(prim.OcclusionTextureName)) textureFlags |= WebGPUTextureFlags.Occlusion;
        if (!string.IsNullOrEmpty(prim.EmissiveTextureName)) textureFlags |= WebGPUTextureFlags.Emissive;

        float alphaCutoff = prim.AlphaMode == 1u ? prim.AlphaCutoff * modelAlpha : prim.AlphaCutoff;

        WriteLightUniform(
            uniformData,
            renderMode: (int)prim.RenderMode,
            metallic: prim.MetallicFactor,
            roughness: prim.RoughnessFactor,
            alpha: finalAlpha,
            emissive: prim.EmissiveFactor,
            ao: 1f,
            alphaMode: prim.AlphaMode,
            alphaCutoff: alphaCutoff,
            textureFlags: textureFlags,
            isInstanced: true,
            isSkinned: isSkinned,
            isMorph: prim.MorphTargetCount > 0,
            prevDataFlags: prevDataFlags);

        return uniformData;
    }

    /// <summary>Ensure that the model primitive's GPU resources have been uploaded.</summary>
    void EnsureModelPrimitiveUploaded(WGPUPrimitiveData prim)
    {
        if (prim.Uploaded)
            return;

        if (prim.VertexBytes == null || prim.IndexBytes == null || prim.VertexBytes.Length == 0)
            return;

        string textureName = !string.IsNullOrEmpty(prim.BaseColorTextureName) ? prim.BaseColorTextureName : "White";
        string normalName = prim.NormalTextureName ?? "White";
        string mrName = prim.MetallicRoughnessTextureName ?? "White";
        string aoName = prim.OcclusionTextureName ?? "White";
        string emissiveName = prim.EmissiveTextureName ?? "White";

        if (prim.HasSkinning)
        {
            _jsRuntime.InvokeVoid("seasonWebGPU.uploadStaticSkinnedMesh",
                prim.CacheKey,
                prim.VertexBytes,
                prim.IndexBytes,
                textureName,
                normalName,
                mrName,
                aoName,
                emissiveName,
                prim.VertexStrideFloats,
                prim.Use32BitIndices ? "uint32" : "uint16",
                prim.DoubleSided,
                prim.MorphDeltasBytes,
                (int)prim.MorphTargetCount,
                (int)prim.MorphVertexCount);
        }
        else
        {
            _jsRuntime.InvokeVoid("seasonWebGPU.uploadStaticMesh",
                prim.CacheKey,
                prim.VertexBytes,
                prim.IndexBytes,
                textureName,
                normalName,
                mrName,
                aoName,
                emissiveName,
                prim.VertexStrideFloats,
                prim.Use32BitIndices ? "uint32" : "uint16",
                prim.DoubleSided,
                prim.MorphDeltasBytes,
                (int)prim.MorphTargetCount,
                (int)prim.MorphVertexCount);
        }

        prim.Uploaded = true;
        prim.LastTextureName = textureName;
        prim.LastNormalTextureName = normalName;
        prim.LastMRTextureName = mrName;
        prim.LastAOTextureName = aoName;
        prim.LastEmissiveTextureName = emissiveName;
        _uploadedStaticMeshKeys.Add(prim.CacheKey);
    }

    public void DisposeInstancedModel(InstancedModel model)
    {
        var key = (model.ModelName, model.ID);
        lock (DictionaryInstancedModel)
        {
            if (DictionaryInstancedModel.ContainsKey(key))
                DictionaryInstancedModel.Remove(key);
        }

        model.Ready = false;
    }

    public void DisposeModel(Model model)
    {
        WGPUModel wgpuModel = null;
        lock (DictionaryModel)
        {
            var key = (model.Name, model.ID);
            if (DictionaryModel.TryGetValue(key, out wgpuModel))
                DictionaryModel.Remove(key);
        }

        // WGPUModel.Dispose only clears the nodeMap cloned for this instance.
        // Shared templates (DictionaryModelResource) are unaffected.
        wgpuModel?.Dispose();

        model.Ready = false;
    }

    // ============================================================
    // 1-5 Shadow pass: per-control shadow dispatch + pass-orchestration entry
    // (mirrors Apple Graphics, with a non-isomorphic difference in Contract 8:
    // draw routing is handled implicitly by JS-side _passDepthOnly, and the light matrix goes through the
    // Projection slot in the uniform).
    // ============================================================

    public void DrawModelShadow(Model model)
    {
        WGPUModel? wgpuModel = null;
        lock (DictionaryModel)
        {
            DictionaryModel.TryGetValue((model.Name, model.ID), out wgpuModel);
        }
        wgpuModel?.DrawShadow(this);
    }

    public void DrawMesh3DShadow(Mesh3D mesh)
    {
        if (mesh.Alpha == 0f || !_initialized)
            return;

        WGPUMesh3D wgpuMesh = null;
        lock (DictionaryMesh3D)
        {
            DictionaryMesh3D.TryGetValue((mesh.Name, mesh.ID), out wgpuMesh);
        }

        if (wgpuMesh == null || !wgpuMesh.TransformInitialized)
            return;

        var matrixData = _scratchMatrix48;
        CopyMatrixTransposed(wgpuMesh.World, matrixData, 0);
        CopyMatrixTransposed(Matrix4x4.Identity, matrixData, 16);
        CopyMatrixTransposed(_shadowViewProj, matrixData, 32);

        foreach (var surface in mesh.Surfaces)
        {
            if (surface.Vertices == null || surface.Indices == null || surface.Vertices.Length == 0)
                continue;
            // Contract 7: true BLEND transparent surfaces do not cast shadows.
            // Unuploaded caches are not uploaded during the shadow pass; missing shadows on the first frame are acceptable.
            if (surface.Mode == SurfaceBlendMode.Blend)
                continue;
            if (!wgpuMesh.SurfaceCaches.TryGetValue(surface, out var cache) || !cache.Uploaded)
                continue;

            wgpuMesh.ResolvedTextures.TryGetValue(surface, out var resolvedTextures);
            var uniformData = _scratchUniform;
            BuildSurfaceUniform(uniformData, matrixData, surface, wgpuMesh.MeshAlpha, resolvedTextures: resolvedTextures);
            EnqueueDrawMesh3D(cache.CacheKey, uniformData);
        }
    }

    public void DrawInstancedMesh3DShadow(InstancedMesh3D mesh)
    {
        if (mesh.Alpha == 0f || !_initialized)
            return;

        WGPUInstancedMesh3D wgpuMesh = null;
        lock (DictionaryInstancedMesh3D)
        {
            DictionaryInstancedMesh3D.TryGetValue((mesh.Name, mesh.ID), out wgpuMesh);
        }

        if (wgpuMesh == null || !wgpuMesh.TransformInitialized || wgpuMesh.EnabledInstanceCount <= 0)
            return;

        FlushDrawMesh3DBatch();
        FlushDrawSkinnedMeshBatch();

        var matrixData = _scratchMatrix48;
        CopyMatrixTransposed(Matrix4x4.Identity, matrixData, 0);
        CopyMatrixTransposed(Matrix4x4.Identity, matrixData, 16);
        CopyMatrixTransposed(_shadowViewProj, matrixData, 32);

        foreach (var surface in mesh.Surfaces)
        {
            if (surface.Vertices == null || surface.Indices == null || surface.Vertices.Length == 0)
                continue;
            if (surface.Mode == SurfaceBlendMode.Blend)
                continue;
            if (!wgpuMesh.SurfaceCaches.TryGetValue(surface, out var cache) || !cache.Uploaded)
                continue;

            wgpuMesh.ResolvedTextures.TryGetValue(surface, out var resolvedTextures);
            var uniformData = new float[WebGPUUniformLayout.TotalFloats];
            BuildSurfaceUniform(uniformData, matrixData, surface, wgpuMesh.MeshAlpha, isInstanced: true, resolvedTextures: resolvedTextures);
            _jsRuntime.InvokeVoid("seasonWebGPU.drawInstancedMesh3D", cache.CacheKey, uniformData, wgpuMesh.InstanceBytes, wgpuMesh.EnabledInstanceCount);
        }
    }

    public void DrawInstancedModelShadow(InstancedModel model)
    {
        if (model.Alpha == 0f || !_initialized)
            return;

        WGPUInstancedModel? wgpuModel = null;
        lock (DictionaryInstancedModel)
        {
            DictionaryInstancedModel.TryGetValue((model.ModelName, model.ID), out wgpuModel);
        }

        if (wgpuModel == null || !wgpuModel.TransformInitialized || wgpuModel.EnabledInstanceCount <= 0)
            return;

        FlushDrawMesh3DBatch();
        FlushDrawSkinnedMeshBatch();

        var matrixData = _scratchMatrix48;
        CopyMatrixTransposed(Matrix4x4.Identity, matrixData, 0);
        CopyMatrixTransposed(Matrix4x4.Identity, matrixData, 16);
        CopyMatrixTransposed(_shadowViewProj, matrixData, 32);

        float modelAlpha = wgpuModel.ModelAlpha;
        bool isSkinned = !string.IsNullOrEmpty(wgpuModel.SkinKey);
        if (isSkinned && wgpuModel.BoneMatricesBytes.Length > 0)
            WebGPUInterop.UploadSkinnedBones(wgpuModel.SkinKey, wgpuModel.BoneMatricesBytes);

        foreach (var prim in wgpuModel.Primitives)
        {
            if (prim.VertexData == null || prim.IndexData == null || prim.VertexData.Length == 0)
                continue;
            if (prim.HasSkinning && string.IsNullOrEmpty(wgpuModel.SkinKey))
                continue;
            // Contract 7: transparent primitives do not cast shadows, and the shadow pass does not trigger uploads.
            if (prim.IsTransparent || !prim.Uploaded)
                continue;
            if (!wgpuModel.TryGetPrimitiveInstanceData(prim, out _, out var instanceBytes) || instanceBytes.Length == 0)
                continue;

            var uniformData = BuildModelPrimitiveUniform(matrixData, prim, modelAlpha, isSkinned);
            _jsRuntime.InvokeVoid("seasonWebGPU.drawInstancedMesh3D", prim.CacheKey, uniformData, instanceBytes, wgpuModel.EnabledInstanceCount, wgpuModel.SkinKey);
        }
    }

    /// <summary>
    /// 1-5 Shadow-pass contents (FrameSchedule.RenderShadow callback): set viewport + light-space matrix for
    /// each atlas quadrant and replay the shared-layer DrawShadow traversal once per cascade / spotlight.
    /// This mirrors Apple RenderShadowPass.
    /// Web-specific difference (ordering contract): batch submission is deferred while setViewport takes effect
    /// immediately, so both batches must be flushed after each quadrant before switching to the next one.
    /// Routing into the shadow pipeline is handled implicitly by JS-side _passDepthOnly, so no PSO switch is needed.
    /// </summary>
    internal void RenderShadowPass(Season.Basic.IGraphics g)
    {
        if (!RenderQuality.Current.ShadowsEnabled)
            return;
        if (!Season.Rendering.CascadedShadow.SunActive && !Season.Rendering.CascadedShadow.SpotActive)
            return;

        var app = DeviceServices.BaseApp;
        if (app == null)
            return;

        if (Season.Rendering.CascadedShadow.SunActive)
        {
            for (int slot = 0; slot < Season.Rendering.CascadedShadow.ActiveCascadeCount; slot++)
            {
                Season.Rendering.CascadedShadow.GetAtlasViewport(slot, out int x, out int y, out int size);
                WebGPUInterop.SetShadowViewport(x, y, size);
                // Clause 7: BeginSlot emits both the matrix and the culling frustum from the same source,
                // and this must not be bypassed.
                // Culling is decided during app.DrawShadow() enqueue time and is independent of the two flushes at the end of the quadrant.
                _shadowViewProj = Season.Rendering.CascadedShadow.BeginSlot(slot);
                app.DrawShadow();
                FlushDrawMesh3DBatch();
                FlushDrawSkinnedMeshBatch();
            }
        }

        if (Season.Rendering.CascadedShadow.SpotActive)
        {
            Season.Rendering.CascadedShadow.GetAtlasViewport(Season.Rendering.CascadedShadow.SpotSlot, out int sx, out int sy, out int ssize);
            WebGPUInterop.SetShadowViewport(sx, sy, ssize);
            _shadowViewProj = Season.Rendering.CascadedShadow.BeginSlot(Season.Rendering.CascadedShadow.SpotSlot);
            app.DrawShadow();
            FlushDrawMesh3DBatch();
            FlushDrawSkinnedMeshBatch();
        }

        Season.Rendering.CascadedShadow.EndPass();
    }

    // ============================================================
    // Phase 4 Outline pass: mask RT + frame-level aggregation + RenderOutlineMask
    // (mirrors VK/Metal; draw routing is handled implicitly by JS-side _passOutlineMask, and outline color is
    // carried per draw through the hdrParams slot at floats 104-107).
    // Per-instance masks use drawInstancedMesh3D firstInstance, mirroring VK Pipeline.DrawPrimitive.
    // Bones, morph data, and instance streams all index by instance_index, so compressed subsets would misalign.
    // ============================================================

    WGPURenderTarget EnsureOutlineMaskTarget()
    {
        if (_outlineMaskTarget != null)
            return _outlineMaskTarget;

        _outlineMaskTarget = (WGPURenderTarget)CreateRenderTarget(new Season.Rendering.RenderTargetDesc
        {
            ColorFormat = Season.Rendering.RtFormat.BackbufferCompatible,
            MatchBackbufferSize = true,
            SampleCount = 1,
        });
        return _outlineMaskTarget;
    }

    /// <summary>Frame-level aggregation: color is carried per draw inside the mask, so multiple colors can
    /// coexist in the same frame. At the frame level, only activation and width are accumulated, taking the
    /// maximum width to ensure the widest outline remains fully visible. This mirrors VK TryAccumulateOutline2D.
    /// The Web backend has no shared primitive-group base type across its four object kinds, so aggregation
    /// operates on the (active, width) value pair instead.</summary>
    bool AccumulateOutline2D(bool active, float width)
    {
        if (!active)
            return false;

        _outline2DFrameActive = true;
        _outline2DFrameWidth = MathF.Max(_outline2DFrameWidth, width);
        return true;
    }

    public void RenderOutlineMask()
    {
        _outline2DFrameActive = false;
        _outline2DFrameWidth = 0f;

        if (!_initialized)
            return;

        var drawList = new List<object>();

        lock (DictionaryModel)
        {
            foreach (var pair in DictionaryModel)
            {
                if (pair.Value != null && AccumulateOutline2D(pair.Value.Outline2DActive, pair.Value.Outline2DMaskWidth))
                    drawList.Add(pair.Value);
            }
        }

        lock (DictionaryMesh3D)
        {
            foreach (var pair in DictionaryMesh3D)
            {
                if (pair.Value != null && AccumulateOutline2D(pair.Value.Outline2DActive, pair.Value.Outline2DMaskWidth))
                    drawList.Add(pair.Value);
            }
        }

        // Instanced controls (InstancedMesh3D / InstancedModel): Outline2D also supports per-instance masks.
        // Activation state is aggregated from each instance / host Highlight.Outline2D during platform Update.
        lock (DictionaryInstancedMesh3D)
        {
            foreach (var pair in DictionaryInstancedMesh3D)
            {
                if (pair.Value != null && AccumulateOutline2D(pair.Value.Outline2DActive, pair.Value.Outline2DMaskWidth))
                    drawList.Add(pair.Value);
            }
        }

        lock (DictionaryInstancedModel)
        {
            foreach (var pair in DictionaryInstancedModel)
            {
                if (pair.Value != null && AccumulateOutline2D(pair.Value.Outline2DActive, pair.Value.Outline2DMaskWidth))
                    drawList.Add(pair.Value);
            }
        }

        if (!_outline2DFrameActive || drawList.Count == 0)
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

        for (int i = 0; i < drawList.Count; i++)
        {
            switch (drawList[i])
            {
                case WGPUModel model:
                    model.DrawOutlineMask(this);
                    break;
                case WGPUMesh3D mesh:
                    DrawMesh3DOutlineMask(mesh);
                    break;
                case WGPUInstancedMesh3D instancedMesh:
                    DrawInstancedMesh3DOutlineMask(instancedMesh);
                    break;
                case WGPUInstancedModel instancedModel:
                    DrawInstancedModelOutlineMask(instancedModel);
                    break;
            }
        }

        EndPass();
    }

    void DrawMesh3DOutlineMask(WGPUMesh3D wgpuMesh)
    {
        if (!wgpuMesh.TransformInitialized || !wgpuMesh.Outline2DActive)
            return;

        var matrixData = _scratchMatrix48;
        CopyMatrixTransposed(wgpuMesh.World, matrixData, 0);
        CopyMatrixTransposed(wgpuMesh.View, matrixData, 16);
        CopyMatrixTransposed(wgpuMesh.Projection, matrixData, 32);

        foreach (var pair in wgpuMesh.SurfaceCaches)
        {
            var surface = (Surface)pair.Key;
            var cache = pair.Value;
            if (surface.Vertices == null || surface.Indices == null || surface.Vertices.Length == 0)
                continue;
            // Mirror Contract 7: true BLEND transparent surfaces are not outlined
            // (the JS mask branch also defensively skips transparent draws).
            if (surface.Mode == SurfaceBlendMode.Blend)
                continue;
            // Unuploaded caches are not uploaded during the mask pass, consistent with the shadow path.
            if (!cache.Uploaded)
                continue;

            wgpuMesh.ResolvedTextures.TryGetValue(surface, out var resolvedTextures);
            var uniformData = _scratchUniform;
            BuildSurfaceUniform(uniformData, matrixData, surface, wgpuMesh.MeshAlpha, resolvedTextures: resolvedTextures);
            // Outline color is carried through the hdrParams slot for the mask fragment shader to read.
            // This must run after BuildSurfaceUniform because its internal Array.Clear overwrites the whole
            // region starting at 48, including this slot.
            new WebGPUUniformWriter(uniformData).SetOutlineMaskColor(wgpuMesh.Outline2DMaskColor);
            EnqueueDrawMesh3D(cache.CacheKey, uniformData);
        }
    }

    void DrawInstancedMesh3DOutlineMask(WGPUInstancedMesh3D wgpuMesh)
    {
        if (!wgpuMesh.TransformInitialized || !wgpuMesh.Outline2DActive || wgpuMesh.EnabledInstanceCount <= 0)
            return;

        FlushDrawMesh3DBatch();
        FlushDrawSkinnedMeshBatch();

        var matrixData = _scratchMatrix48;
        CopyMatrixTransposed(Matrix4x4.Identity, matrixData, 0);
        CopyMatrixTransposed(wgpuMesh.View, matrixData, 16);
        CopyMatrixTransposed(wgpuMesh.Projection, matrixData, 32);

        foreach (var pair in wgpuMesh.SurfaceCaches)
        {
            var surface = (Surface)pair.Key;
            var cache = pair.Value;
            if (surface.Vertices == null || surface.Indices == null || surface.Vertices.Length == 0)
                continue;
            if (surface.Mode == SurfaceBlendMode.Blend)
                continue;
            if (!cache.Uploaded)
                continue;

            wgpuMesh.ResolvedTextures.TryGetValue(surface, out var resolvedTextures);
            var uniformData = new float[WebGPUUniformLayout.TotalFloats];
            BuildSurfaceUniform(uniformData, matrixData, surface, wgpuMesh.MeshAlpha, isInstanced: true, resolvedTextures: resolvedTextures);
            new WebGPUUniformWriter(uniformData).SetOutlineMaskColor(wgpuMesh.Outline2DMaskColor);

            if (wgpuMesh.Outline2DHostActive)
            {
                _jsRuntime.InvokeVoid("seasonWebGPU.drawInstancedMesh3D", cache.CacheKey, uniformData, wgpuMesh.InstanceBytes, wgpuMesh.EnabledInstanceCount);
            }
            else
            {
                // Per-instance activation: use the full instance byte stream + count=1 + firstInstance=writeIndex,
                // mirroring VK. Rewrite this instance's own OutlineColor per slot; uniform marshalling is
                // synchronous, so calls do not interfere with one another.
                var slots = wgpuMesh.Outline2DInstances;
                for (int k = 0; k < slots.Count; k++)
                {
                    if (slots[k] >= wgpuMesh.EnabledInstanceCount)
                        continue;
                    new WebGPUUniformWriter(uniformData).SetOutlineMaskColor(wgpuMesh.Outline2DInstanceColors[k]);
                    _jsRuntime.InvokeVoid("seasonWebGPU.drawInstancedMesh3D", cache.CacheKey, uniformData, wgpuMesh.InstanceBytes, 1, null, null, slots[k]);
                }
            }
        }
    }

    void DrawInstancedModelOutlineMask(WGPUInstancedModel wgpuModel)
    {
        if (!wgpuModel.TransformInitialized || !wgpuModel.Outline2DActive || wgpuModel.EnabledInstanceCount <= 0)
            return;

        FlushDrawMesh3DBatch();
        FlushDrawSkinnedMeshBatch();

        var matrixData = _scratchMatrix48;
        CopyMatrixTransposed(Matrix4x4.Identity, matrixData, 0);
        CopyMatrixTransposed(wgpuModel.View, matrixData, 16);
        CopyMatrixTransposed(wgpuModel.Projection, matrixData, 32);

        float modelAlpha = wgpuModel.ModelAlpha;
        bool isSkinned = !string.IsNullOrEmpty(wgpuModel.SkinKey);
        if (isSkinned && wgpuModel.BoneMatricesBytes.Length > 0)
            WebGPUInterop.UploadSkinnedBones(wgpuModel.SkinKey, wgpuModel.BoneMatricesBytes);

        foreach (var prim in wgpuModel.Primitives)
        {
            if (prim.VertexData == null || prim.IndexData == null || prim.VertexData.Length == 0)
                continue;
            if (prim.HasSkinning && string.IsNullOrEmpty(wgpuModel.SkinKey))
                continue;
            // Mirror Contract 7: transparent primitives are not outlined, and the mask pass does not trigger uploads.
            if (prim.IsTransparent || !prim.Uploaded)
                continue;
            if (!wgpuModel.TryGetPrimitiveInstanceData(prim, out _, out var instanceBytes) || instanceBytes.Length == 0)
                continue;

            var uniformData = BuildModelPrimitiveUniform(matrixData, prim, modelAlpha, isSkinned);
            new WebGPUUniformWriter(uniformData).SetOutlineMaskColor(wgpuModel.Outline2DMaskColor);

            if (wgpuModel.Outline2DHostActive)
            {
                _jsRuntime.InvokeVoid("seasonWebGPU.drawInstancedMesh3D", prim.CacheKey, uniformData, instanceBytes, wgpuModel.EnabledInstanceCount, wgpuModel.SkinKey);
            }
            else
            {
                // Per-instance activation: full stream + count=1 + firstInstance=writeIndex.
                // The bone palette is addressed by instanceIndex*100, and firstInstance keeps slot alignment,
                // mirroring VK. Rewrite this instance's own OutlineColor per slot; uniform marshalling is
                // synchronous, so calls do not interfere with one another.
                var slots = wgpuModel.Outline2DInstances;
                for (int k = 0; k < slots.Count; k++)
                {
                    if (slots[k] >= wgpuModel.EnabledInstanceCount)
                        continue;
                    new WebGPUUniformWriter(uniformData).SetOutlineMaskColor(wgpuModel.Outline2DInstanceColors[k]);
                    _jsRuntime.InvokeVoid("seasonWebGPU.drawInstancedMesh3D", prim.CacheKey, uniformData, instanceBytes, 1, wgpuModel.SkinKey, null, slots[k]);
                }
            }
        }
    }

    public static void UpdateCamera3D(Vector3 cameraPos, Vector3 cameraTarget, SceneLightParams sceneLights)
    {
        // 1-3: matrix construction converges on the shared Camera3D path
        // (Changed-gated, zero rebuild for a still camera; FOV/near/far are driven by BaseApp.Camera).
        // The cameraPos/cameraTarget parameters are simply forwarded from BaseApp.Camera.Position/Target,
        // and the signature is kept for frame-loop compatibility.
        var camera3D = DeviceServices.BaseApp.Camera;
        var res = DeviceServices.BaseApp.DeviceResolution;
        var aspectRatio = res.X / (float)res.Y;

        // 2-3 Contract Clauses 4/6 (mirroring the dual-branch flow in MTLPrimitiveGroup.Update):
        // when enabled, use UpdateTemporal. Jitter is injected once into ProjectionJittered
        // (the main chain uses the jittered matrix, while historical VP remains non-jittered), and
        // PrevViewProjection is forwarded into Camera3D each frame for draw sites to source the prev slots.
        // When disabled, keep the original 1-3 path and zero the history (sentinel = no history).
        if (RenderQuality.Current.MotionVectors)
        {
            camera3D.UpdateTemporal(aspectRatio, res.X, res.Y);
            Camera3D.View = camera3D.View;
            Camera3D.Projection = camera3D.ProjectionJittered;
            Camera3D.PrevViewProjection = camera3D.PrevViewProjection;
        }
        else
        {
            camera3D.UpdateIfChanged(aspectRatio);
            Camera3D.View = camera3D.View;
            Camera3D.Projection = camera3D.Projection;
            Camera3D.PrevViewProjection = default;
        }

        // 1-5: shadow-matrix chain (mirrors MTLPrimitiveGroup.Update):
        // BeginFrame reset → directional/spot light computation → Apply writes shadow fields into
        // SceneLightParams at a single point. The full 1152B block is then uploaded, so Contract 8 adds no new UBO.
        // Shadow sources are now selected by the indices stored in Params0.Z/W (written by the authorized bake layer).
        if (RenderQuality.Current.ShadowsEnabled)
        {
            Season.Rendering.CascadedShadow.BeginFrame();
            int dirIdx = (int)sceneLights.Params0.Z;
            if (dirIdx >= 0 && dirIdx < sceneLights.LightCount)
            {
                var dirType = sceneLights.Lights[dirIdx].DirType;
                Season.Rendering.CascadedShadow.ComputeSun(camera3D, new Vector3(dirType.X, dirType.Y, dirType.Z));
            }
            int spotIdx = (int)sceneLights.Params0.W;
            if (spotIdx >= 0 && spotIdx < sceneLights.LightCount
                && sceneLights.Lights[spotIdx].DirType.W == GpuLight.TypeSpot)
                Season.Rendering.CascadedShadow.ComputeSpot(in sceneLights.Lights[spotIdx]);
            Season.Rendering.CascadedShadow.Apply(ref sceneLights);
        }

        // 1-2 Contract 7: inject exposure once into Params0.Y
        // (same semantics as SetLighting on DX/VK/Metal; app-side writes are ineffective),
        // then upload the whole block into the shared lighting UBO at JS binding(10) once per frame (Contract 8).
        sceneLights.Params0.Y = RenderQuality.Current.HdrExposure;

        // 2-3 Contract Clause 6: same one-point injection rule as exposure
        // with xy = current-frame jitter in NDC and zw = 1 / screen size. The fragment shader uses it to
        // reconstruct NDC from @builtin(position). When disabled, JitterNdc stays zero, and writing it is harmless
        // because shaders with VELOCITY_OUTPUT=false do not read the field. App-side writes are ineffective.
        var jitter = camera3D.JitterNdc;
        sceneLights.VelocityParams = new Vector4(
            jitter.X, jitter.Y,
            res.X > 0 ? 1f / res.X : 0f,
            res.Y > 0 ? 1f / res.Y : 0f);

        // 1-7 Contract Clause 4: inject environment parameters and resolve the current-frame radiance cube
        // once per frame, so draw paths avoid doing per-draw lookups. This is equivalent to the same-named
        // block in MTLPrimitiveGroup.Update. If SceneEnvironment is null, EnvParams stay all-zero, so the
        // shader falls back per pixel to the 1-2 constant ambient light, and SetEnvCube(null) makes binding 15
        // fall back to the all-black cube. This must be written before the full block upload below because
        // EnvParams/IrradianceSH9 occupy the tail of the same 1136B UBO.
        var env = DeviceServices.BaseApp.SceneEnvironment;
        env?.Apply(ref sceneLights);
        WebGPUInterop.SetEnvCube(env?.RadianceName);

        // 2-4 Clause 10: one-point injection of DDGI GiParams0/1/2
        // (leave untouched when not ready and let the consumer side fall back).
        Season.Rendering.Effects.DdgiEffect.Apply(ref sceneLights);

        // 2-5 Step B (b11): resolve sun disk / moon disk + starfield into a one-point injection of SkyParams0..3.
        // The StaticCube tier early-outs as a whole, leaving all four fields at zero, so the pixel shader's
        // skyParams0.w > 0 gate stays false with no residue.
        Season.Rendering.SkyLighting.Apply(ref sceneLights);

        // 2-4 Clause 10: push this frame's DDGI irradiance-atlas name using the same pattern as SetEnvCube.
        // Pass null when not ready so binding 16 falls back to a 1×1 White texture; real sampling is gated by
        // WGSL DDGI_ENABLED + giParams.
        WebGPUInterop.SetDdgiAtlas(Season.Rendering.Effects.DdgiEffect.Ready
            ? Season.Rendering.Effects.DdgiEffect.ActiveIrradianceName
            : null);

        // 2-4 Step 3: push this frame's DDGI depth-moment atlas name with the same pattern.
        // Pass null when not ready so binding 17 falls back to a 1×1 White texture; real Chebyshev sampling
        // is gated at runtime by giParams2.y.
        WebGPUInterop.SetDdgiDepth(Season.Rendering.Effects.DdgiEffect.Ready
            ? Season.Rendering.Effects.DdgiEffect.ActiveDepthName
            : null);

        // 2-5 Step C/E: push this frame's cloud-noise / AP 3D LUT names using the same pattern as SetDdgiAtlas.
        // Pass null when not ready so bindings 18/19 fall back to their defaults; real sampling is gated by
        // WGSL cloudParams0.w (layer count) / apParams0.x (max distance in km).
        WebGPUInterop.SetCloudNoise(Season.Rendering.FrameSchedule.CloudNoiseTexture);
        WebGPUInterop.SetAerialLut(Season.Rendering.FrameSchedule.AerialLutTexture);

        Light3D = sceneLights;
        WebGPUInterop.UpdateSceneLights(MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref sceneLights, 1)));

        // [SkyDebug] Invisible-starfield investigation: log the injected values before the Web-side full-block upload every 180 frames.
        _skyDebugUploadFrame++;
        if (_skyDebugUploadFrame % 180 == 0)
            DeviceServices.BaseApp.AddLog(LogType.Backend,
                $"[SkyDebug] Web upload f={_skyDebugUploadFrame} uboBytes={Unsafe.SizeOf<Season.Rendering.SceneLightParams>()} " +
                $"SkyP0.w={sceneLights.SkyParams0.W:F5} SkyP1.w={sceneLights.SkyParams1.W:F4} " +
                $"SkyP2.w={sceneLights.SkyParams2.W:F5} SkyP3.w={sceneLights.SkyParams3.W:F3} " +
                $"SkyViewTex={Season.Rendering.FrameSchedule.SkyViewTexture ?? "null"}");
    }

    /// <summary>
    /// 2-3 Contract Clause 6: inject history matrices into the prev-data region of the 432B uniform,
    /// reusing the retired 1-2 slots.
    /// This must be called after <c>Array.Clear(uniformData, 48, ...)</c> at each draw site, because Clear
    /// zeros the prev slots.
    /// When MotionVectors is disabled, return immediately and leave the prev slots all-zero, so the shader's
    /// sentinel outputs zero velocity.
    /// Passing default prevWorld (all-zero, M44==0) falls back to the current-frame world. Instanced paths
    /// always take that branch under Clause 8(d), which yields only camera-motion velocity, while 2D/UI/text
    /// and shadow passes do not call this method at all.
    /// </summary>
    internal static void WritePrevMatrices(float[] uniformData, Matrix4x4 prevWorld = default)
    {
        if (!RenderQuality.Current.MotionVectors)
            return;
        // All-zero data (no history, such as the first frame) can be written as-is. The shader-side
        // prevViewProjection[3] all-zero test handles the fallback.
        CopyMatrixTransposed(Camera3D.PrevViewProjection, uniformData, WebGPUUniformLayout.PrevViewProjection);
        CopyMatrixTransposed(prevWorld, uniformData, WebGPUUniformLayout.PrevWorld);
    }

    public static void WriteLightUniform(
        float[] uniformData,
        int renderMode,
        float metallic,
        float roughness,
        float alpha,
        Vector3 emissive,
        float ao = 1f,
        uint alphaMode = 0u,
        float alphaCutoff = 0.5f,
        int textureFlags = 0,
        bool isInstanced = false,
        bool isSkinned = false,
        bool isMorph = false,
        Vector4 morphWeights = default,
        int prevDataFlags = 0)
    {
        var w = new WebGPUUniformWriter(uniformData);

        // 1-2 Contract 8: camera / light / exposure are no longer written per draw and are instead read from
        // the shared UBO at binding(10). UpdateCamera3D uploads SceneLightParams every frame, including the
        // Params0.Y exposure value, while the Contract 7 one-point injection rule remains unchanged.
        // The old slots are retired into reserved space, with layout/stride preserved.
        w.SetEmissive(emissive, ao);
        w.SetMaterial(metallic, roughness, alpha, alphaCutoff);
        // Write unconditionally: scratch buffers are reused across calls, so this prevents stale weights from
        // the previous primitive from leaking into the next one.
        w.SetMorphWeights(morphWeights);

        int effectiveFlags = textureFlags;
        if (isInstanced) effectiveFlags |= WebGPUTextureFlags.Instanced;
        if (isSkinned) effectiveFlags |= WebGPUTextureFlags.Skinned;
        if (isMorph) effectiveFlags |= WebGPUTextureFlags.Morph;

        // 2-3 Step C: force prev sentinel bits to zero when the feature is disabled, matching the fallback
        // behavior of WritePrevMatrices.
        // SetFlags always writes flags.x, so call sites that do not pass this argument
        // (text / Sprite2D / Sprite3D) always get 0, and scratch reuse will not leak stale values.
        if (!RenderQuality.Current.MotionVectors) prevDataFlags = 0;

        w.SetFlags((WebGPURenderMode)renderMode, (WebGPUAlphaMode)alphaMode, effectiveFlags, prevDataFlags);
    }

    public static void CopyMatrixTransposed(Matrix4x4 m, float[] dst, int offset)
    {
        dst[offset + 0] = m.M11; dst[offset + 1] = m.M12; dst[offset + 2] = m.M13; dst[offset + 3] = m.M14;
        dst[offset + 4] = m.M21; dst[offset + 5] = m.M22; dst[offset + 6] = m.M23; dst[offset + 7] = m.M24;
        dst[offset + 8] = m.M31; dst[offset + 9] = m.M32; dst[offset + 10] = m.M33; dst[offset + 11] = m.M34;
        dst[offset + 12] = m.M41; dst[offset + 13] = m.M42; dst[offset + 14] = m.M43; dst[offset + 15] = m.M44;
    }

    public void ExecuteUpload()
    {
        // On the Web backend, resource creation is submitted immediately through JS, so no extra upload phase is needed.
    }

    public void DisposeSprite2D(Sprite2D sprite)
    {
        WGPUSprite2D wgpuSprite = null;
        var key = (sprite.Name, sprite.ID);
        lock (DictionarySprite)
        {
            if (DictionarySprite.TryGetValue(key, out wgpuSprite))
                DictionarySprite.Remove(key);
        }

        if (wgpuSprite?.WGPUTexture != null)
        {
            var tex = wgpuSprite.WGPUTexture;
            tex.Release();
            // Remove metadata from the dictionary when the ref count reaches zero.
            if (tex.RefCount == 0 && !string.IsNullOrEmpty(tex.Name))
            {
                lock (DictionaryWGPUTexture)
                {
                    DictionaryWGPUTexture.Remove(tex.Name);
                }
            }
        }

        sprite.Ready = false;
    }

    // ── Shape (procedural geometry) ──

    public async Task<bool> LoadShape(Season.Controls.Shape shape)
    {
        // Width/Height can be null when AddControl runs. Casting (int)(float?)null would throw and make Load fail.
        int shapeW = Math.Max(1, (int)(shape.Width ?? 1f));
        int shapeH = Math.Max(1, (int)(shape.Height ?? 1f));

        // RectFrame textures are keyed by the tuple (Type, W, H, Border); Border is always 0 for other types.
        // Clamp the same way as CreateImageRectFrame to [1, min(W, H) / 2] to avoid duplicate keys producing multiple copies of the same texture.
        int shapeBorder = shape.Type == Season.Controls.ShapeType.RectFrame
            ? Math.Clamp((int)shape.Border, 1, Math.Min(shapeW, shapeH) / 2)
            : 0;

        var textureKey = shape.Type == Season.Controls.ShapeType.Dot
            ? (shape.Type, 1, 1, 0)
            : (shape.Type, shapeW, shapeH, shapeBorder);
        var instanceKey = (shape.Type, shape.ID);

        // Dot textures are always 1×1, so JS upload parameters must also use 1 rather than shape.Width/Height,
        // which can still be 0 on first load.
        var uploadW = shape.Type == Season.Controls.ShapeType.Dot ? 1 : shapeW;
        var uploadH = shape.Type == Season.Controls.ShapeType.Dot ? 1 : shapeH;

        WGPUSprite2D wgpuSprite2D = null;

        lock (DictionaryShape)
        {
            if (shape.IsDisposed) return false;

            if (DictionaryShape.TryGetValue(instanceKey, out wgpuSprite2D))
            {
                if (wgpuSprite2D == null || wgpuSprite2D.WGPUTexture == null)
                {
                }
                else
                {
                    shape.OriginWidth = (int)wgpuSprite2D.WGPUTexture.Width;
                    shape.OriginHeight = (int)wgpuSprite2D.WGPUTexture.Height;
                }
            }
            else
            {
                // Get or create the shared shape texture, cached by Type + Width + Height.
                WGPUTexture wgpuTexture = null;

                lock (DictionaryShapeTexture)
                {
                    if (DictionaryShapeTexture.TryGetValue(textureKey, out wgpuTexture!))
                    {

                    }
                    else
                    {
                        var imageDecoder = Season.Models.ImageUtils.CreateShapeImage(shape.Type, shapeW, shapeH, shapeBorder);

                        if (imageDecoder != null)
                        {
                            var rgbaData = imageDecoder.PixelSpan.ToArray();

                            // The texture name must include Border, because shapes of the same type and size
                            // but different thickness must not share the same JS-side texture name.
                            var textureName = $"shape_{shape.Type}_{uploadW}_{uploadH}_{shapeBorder}";

                            var uploadResult = _jsRuntime.Invoke<WebTextureUploadResult>(
                                "seasonWebGPU.uploadGlyphTexture",
                                textureName,
                                rgbaData,
                                uploadW,
                                uploadH);

                            if (uploadResult?.success == true)
                            {
                                wgpuTexture = new WGPUTexture
                                {
                                    Name = textureName,
                                    Width = (uint)(uploadResult?.width ?? uploadW),
                                    Height = (uint)(uploadResult?.height ?? uploadH),
                                };
                            }
                        }

                        if (wgpuTexture == null)
                        {
                            DeviceServices.BaseApp.AddLog(LogType.Backend, $"{DateTime.UtcNow} [WebGPUGraphics] LoadShape WGPUTexture=null type={shape.Type}");
                        }
                        else
                        {
                            // Only cache successful results so a null value does not poison later requests for the same key.
                            DictionaryShapeTexture[textureKey] = wgpuTexture;
                        }
                    }
                }

                if (wgpuTexture == null)
                {
                    return false;
                }

                try
                {
                    wgpuSprite2D = new WGPUSprite2D(wgpuTexture);

                    shape.OriginWidth = (int)wgpuSprite2D.WGPUTexture.Width;
                    shape.OriginHeight = (int)wgpuSprite2D.WGPUTexture.Height;
                }
                catch (Exception ex)
                {
                    DeviceServices.BaseApp.AddLog(LogType.Error, $"{DateTime.UtcNow} [WebGPUGraphics] LoadShape new WGPUSprite2D error: {ex}");

                    return false;
                }

                lock (DictionaryShape)
                {
                    if (!DictionaryShape.ContainsKey(instanceKey))
                    {
                        DictionaryShape.Add(instanceKey, wgpuSprite2D);
                    }
                }
            }
        }

        return true;
    }

    public void UpdateShape(Season.Controls.Shape shape)
    {
        WGPUSprite2D? wgpuSprite = null;

        lock (DictionaryShape)
        {
            DictionaryShape.TryGetValue((shape.Type, shape.ID), out wgpuSprite);
        }

        if (wgpuSprite == null || wgpuSprite.WGPUTexture == null)
        {
            return;
        }

        shape.Ready = true;

        if (shape.Changed)
        {
            shape.Changed = false;
        }
    }

    public void DrawShape(Season.Controls.Shape shape)
    {
        WGPUSprite2D? wgpuSprite = null;

        lock (DictionaryShape)
        {
            DictionaryShape.TryGetValue((shape.Type, shape.ID), out wgpuSprite);
        }

        if (wgpuSprite == null || wgpuSprite.WGPUTexture == null || !_initialized)
        {
            return;
        }

        var app = DeviceServices.BaseApp;
        float scaleX = app.Scale;
        float scaleY = app.Scale;
        float screenW = app.DeviceResolution.X;
        float screenH = app.DeviceResolution.Y;

        // Read properties directly from shape, matching the DrawSprite2D pattern and avoiding default values
        // from Controls.Texture.
        float x = shape.PosX * scaleX;
        float y = shape.PosY * scaleY;
        float w = ((float)shape.Width > 0 ? (float)shape.Width : shape.OriginWidth) * scaleX;
        float h = ((float)shape.Height > 0 ? (float)shape.Height : shape.OriginHeight) * scaleY;

        float ndcX = (x / screenW) * 2f - 1f;
        float ndcY = 1f - (y / screenH) * 2f;
        float ndcW = (w / screenW) * 2f;
        float ndcH = -(h / screenH) * 2f;

        Vector4 color = shape.Color;

        FlushDrawMesh3DBatch();
        FlushDrawSkinnedMeshBatch();

        _jsRuntime.InvokeVoid("seasonWebGPU.drawSprite2D",
            wgpuSprite.WGPUTexture.Name,
            ndcX, ndcY, ndcW, ndcH,
            shape.Alpha,
            color.X, color.Y, color.Z, color.W,
            shape.FlipX, shape.FlipY,
            0f, 0f,
            shape.Clock,
            shape.SourceX, shape.SourceY, shape.SourceWidth, shape.SourceHeight);
    }

    public void DisposeShape(Season.Controls.Shape shape)
    {
        WGPUSprite2D? wgpuSprite = null;

        lock (DictionaryShape)
        {
            var key = (shape.Type, shape.ID);
            if (DictionaryShape.TryGetValue(key, out wgpuSprite))
                DictionaryShape.Remove(key);
        }

        shape.Ready = false;
    }
}
