// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace Season.Platforms.Web;

/// <summary>
/// Direct [JSImport] bindings to seasonWebGPU.js
/// (Phase 1: hot per-frame paths using only primitive types).
/// Compared to IJSRuntime.InvokeVoid
/// (JSON serialization on the C# side + JSON.parse + reflection-based dispatch on the JS side),
/// [JSImport] generates source bindings, writes primitive values directly into the interop buffer,
/// reduces per-call overhead by 1~2 orders of magnitude, and avoids JSON string allocations
/// (which helps reduce wasm-side GC stalls).
/// seasonWebGPU is attached to the global window object and bound through the globalThis. prefix,
/// so JSHost.ImportAsync is unnecessary.
/// These bindings are callable only in the browser wasm runtime
/// (both hosted net10.0 and net10.0-browser TFMs run in the browser).
/// JS-side function signatures remain fully aligned with the IJSRuntime path, so Phase 1 requires no changes to seasonWebGPU.js.
/// </summary>
[SupportedOSPlatform("browser")]
internal static partial class WebGPUInterop
{
    [JSImport("globalThis.seasonWebGPU.beginFrame")]
    internal static partial void BeginFrame(float r, float g, float b, float a);

    [JSImport("globalThis.seasonWebGPU.endFrame")]
    internal static partial void EndFrame();

    // ── 1-1 Pass orchestration (Step 0+1): flatten PassDesc into scalar parameters
    // because struct JSInterop serialization has known issues ──

    /// <summary>targetName != null means an offscreen RT
    /// (color or depth-only, with the JS side choosing the attachment set based on RT shape); null means the backbuffer.
    /// depthTargetName != null means a dual-target Scene pass (2-2), where the depth plane is rebound to explicit SceneDepth
    /// (formatKind 3 form).
    /// velocityTargetName != null means a 2-3 MRT Scene pass: append SceneVelocity as color attachment slot 1
    /// (formatKind 4), while the JS side also sets _passVelocity so draws are implicitly routed to the MRT
    /// pipeline variant, following the same routing pattern as 1-5 _passDepthOnly.</summary>
    [JSImport("globalThis.seasonWebGPU.beginPass")]
    internal static partial void BeginPass(
        int passId,
        string? targetName,
        bool clearColorEnable,
        float clearR, float clearG, float clearB, float clearA,
        bool clearDepthEnable,
        bool storeDepth,
        string? depthTargetName,
        string? velocityTargetName);

    [JSImport("globalThis.seasonWebGPU.endPass")]
    internal static partial void EndPass();

    // ── 1-1 Offscreen RT / FinalBlit (Step 2/3): name-as-handle, with real resources owned by the JS side ──

    /// <summary>formatKind: 0 = BackbufferCompatible color, 1 = Rgba16Float color, 2 = depth-only D32Float,
    /// 3 = depth-only SceneDepth (2-2: depth24plus + TEXTURE_BINDING, matching the depth format already baked into the pipeline).</summary>
    [JSImport("globalThis.seasonWebGPU.createRenderTarget")]
    internal static partial void CreateRenderTarget(string name, int width, int height, bool matchBackbuffer, int formatKind);

    [JSImport("globalThis.seasonWebGPU.disposeRenderTarget")]
    internal static partial void DisposeRenderTarget(string name);

    /// <summary>2-1 Step D expansion: pass exposure/bloomIntensity with the blit
    /// (HDR sources writeBuffer them into the params uniform, while LDR sources ignore them).
    /// When bloomName is not null and resolves on the JS side, switch to the tonemap+bloom variant.
    /// fxaa=true selects the FXAA variant (PostColor→backbuffer), which is mutually exclusive with tonemap/bloom.
    /// 2-2 Step C: when aoName is not null and resolves on the JS side, switch to the AO variant
    /// (HDR sources only, with aoIntensity using a dedicated AO params uniform).
    /// 2-3 Contract Clause 12: when sceneOverrideName is not null and resolves on the JS side,
    /// replace the scene source at binding 0 with that texture (TAA resolve output,
    /// always full-size rgba16float, therefore always using the point tonemap family variant).
    /// When null, fall back to the RT's own colorView with no residue.
    /// Phase 4: when outlineMaskName is not null and resolves on the JS side, append outline composite
    /// after the main blit (8-neighborhood expansion with alpha-blend overlay, mirroring DX BlitToBackbuffer).
    /// outlineWidth is in pixels and is clamped to 1 when below 1.</summary>
    [JSImport("globalThis.seasonWebGPU.blitToBackbuffer")]
    internal static partial void BlitToBackbuffer(string name, float exposure, string? bloomName, float bloomIntensity, bool fxaa, string? aoName, float aoIntensity, string? sceneOverrideName, string? outlineMaskName, float outlineWidth);

    /// <summary>2-1 Step D: uber composition inside the Post pass
    /// (SceneColor exposure×bloom accumulation → ACES+gamma → LDR, with luma written into alpha),
    /// rendered to the current pass target (PostColor).
    /// Parameters use a dedicated JS-side uniform separate from FinalBlit.
    /// 2-2 Step C: when aoName is not null and resolves on the JS side, switch to the uber AO variant
    /// (multiply AO occlusion before ACES, then add bloom).
    /// 2-3 Contract Clause 12: sceneOverrideName has the same meaning as in BlitToBackbuffer
    /// (override the scene source).</summary>
    [JSImport("globalThis.seasonWebGPU.renderPost")]
    internal static partial void RenderPost(string sceneName, float exposure, string? bloomName, float bloomIntensity, string? aoName, float aoIntensity, string? sceneOverrideName);

    /// <summary>Direct Sprite2D/Shape draw path.
    /// clock and source*/pixelRange semantics are aligned with DX/VK/MTL TextCoords.GetTransforms:
    /// flip is applied in source space first, then UVs are rotated clockwise by clock (90/180/270).
    /// When sourceWidth &gt; 0, UVs are mapped to a subregion.
    /// renderMode (0=Sprite) and pixelRange are only used by the MSDF variant; Sprite2D/Shape always passes 0.</summary>
    [JSImport("globalThis.seasonWebGPU.drawSprite2D")]
    internal static partial void DrawSprite2D(
        string name,
        float ndcX, float ndcY, float ndcW, float ndcH,
        float alpha,
        float colorR, float colorG, float colorB, float colorA,
        bool flipX, bool flipY,
        float renderMode, float pixelRange,
        int clock,
        float sourceX, float sourceY, float sourceWidth, float sourceHeight);

    /// <summary>1-2 Contract 8: text inverse-ACES exposure compensation now reads uLights.params0.y from binding(10),
    /// so the old per-draw bypass has been removed.</summary>
    [JSImport("globalThis.seasonWebGPU.drawTextInstanced")]
    internal static partial void DrawTextInstanced(
        string key,
        float alpha,
        float colorR, float colorG, float colorB, float colorA,
        float pixelRange);

    [JSImport("globalThis.seasonWebGPU.updateMeshMaterialParams")]
    internal static partial void UpdateMeshMaterialParams(
        string cacheKey,
        float metallic, float roughness,
        float emissiveX, float emissiveY, float emissiveZ);

    // ── Phase 2: fixed per-frame calls (rAF wait / resize / input polling) ──

    /// <summary>Wait for the next rAF. JS requestFrame returns Promise&lt;timestamp&gt;, marshaled directly as a Task.</summary>
    [JSImport("globalThis.seasonWebGPU.requestFrame")]
    internal static partial Task<double> RequestFrame();

    /// <summary>Returns [width, height]; returns [0, 0] when there is no pending resize to apply.</summary>
    [JSImport("globalThis.seasonWebGPU.applyPendingResizePacked")]
    internal static partial int[] ApplyPendingResize();

    /// <summary>Returns [isDown(0/1), poX, poY, poZDelta]; JS clears poZDelta after the call.</summary>
    [JSImport("globalThis.seasonWebGPU.pollInputPacked")]
    internal static partial double[] PollInput();

    // ── Phase 3: hot byte-data paths (Span<byte> → MemoryView, zero-copy views into wasm linear memory) ──
    // MemoryView is valid only during the synchronous call.
    // Each JS consumer has been verified to consume it immediately through writeBuffer
    // (_interopToU8 copies via slice()), with nothing retained across calls.
    // Compared to the IJSRuntime byte[] path, this avoids ToByteArray/BlockCopy allocations on the C# side,
    // binary-channel interop transport, and JSON dispatch.

    [JSImport("globalThis.seasonWebGPU.uploadSkinnedBones")]
    internal static partial void UploadSkinnedBones(
        string skinKey,
        [JSMarshalAs<JSType.MemoryView>] Span<byte> boneBytes);

    /// <summary>1-2 Contract 8: upload the full SceneLightParams block once per frame
    /// (expanded from 960B in 1-5 to 976B in 2-3) into the shared lighting UBO at JS binding(10),
    /// with the same semantics as SetLighting on the other three backends. JS consumes it immediately through writeBuffer.
    /// Note: the JS-side SCENE_LIGHT_BYTES check requires an exact size match, so shared-layer struct expansions
    /// must be synchronized or the upload will be silently dropped.</summary>
    [JSImport("globalThis.seasonWebGPU.updateSceneLights")]
    internal static partial void UpdateSceneLights(
        [JSMarshalAs<JSType.MemoryView>] Span<byte> lightBytes);

    // ── 1-5 Shadows (Contract 8 is non-isomorphic here): draw routing is handled implicitly by JS-side _passDepthOnly,
    // so C# only needs two control interfaces: quadrant viewport switching and atlas-name registration ──

    /// <summary>Viewport+scissor for a shadow-atlas quadrant (square size×size); valid only inside the Shadow pass.</summary>
    [JSImport("globalThis.seasonWebGPU.setShadowViewport")]
    internal static partial void SetShadowViewport(int x, int y, int size);

    /// <summary>Registers the shadow-atlas RT name (name-as-handle); main-pass binding 11 resolves from it.</summary>
    [JSImport("globalThis.seasonWebGPU.setShadowAtlas")]
    internal static partial void SetShadowAtlas(string name);

    /// <summary>
    /// 1-7: creates a six-layer rgba8unorm cube (viewDimension:'cube', single mip) and registers it by name in JS-side _textureCubes.
    /// faceBytes must be a tightly packed contiguous RGBA8 block, concatenated in CubeFace declaration order
    /// (+X,-X,+Y,-Y,+Z,-Z), with exact length size×size×4×6.
    /// The JS side validates this strictly, logs to the console on mismatch, and returns false.
    /// </summary>
    [JSImport("globalThis.seasonWebGPU.createTextureCube")]
    internal static partial bool CreateTextureCube(string name, int size,
        [JSMarshalAs<JSType.MemoryView>] Span<byte> faceBytes);

    /// <summary>1-7: registers the currently active environment radiance cube name
    /// (name-as-handle, same pattern as SetShadowAtlas).
    /// Main-pass binding 15 resolves from it. Passing null/empty falls back to the 1×1 all-black fallback cube.</summary>
    [JSImport("globalThis.seasonWebGPU.setEnvCube")]
    internal static partial void SetEnvCube(string? name);

    /// <summary>2-4 Clause 10: registers the DDGI irradiance-atlas name active for this frame
    /// (name-as-handle, same pattern as SetEnvCube).
    /// Main-pass binding 16 resolves from it. Passing null/empty falls back to a 1×1 White texture.</summary>
    [JSImport("globalThis.seasonWebGPU.setDdgiAtlas")]
    internal static partial void SetDdgiAtlas(string? name);

    /// <summary>2-4 Step 3: registers the DDGI depth-moment atlas name active for this frame
    /// (same pattern as SetDdgiAtlas).
    /// Main-pass binding 17 resolves from it. Passing null/empty falls back to a 1×1 White texture.</summary>
    [JSImport("globalThis.seasonWebGPU.setDdgiDepth")]
    internal static partial void SetDdgiDepth(string? name);

    /// <summary>2-5 Step C: registers the cloud-noise 2D texture name active for this frame
    /// (name-as-handle, same pattern as SetDdgiAtlas).
    /// Main-pass binding 18 resolves from it. Passing null/empty falls back to a 1×1 White texture
    /// (a potentially dangerous value, but actual sampling is gated to zero by WGSL cloudParams0.w layer count).</summary>
    [JSImport("globalThis.seasonWebGPU.setCloudNoise")]
    internal static partial void SetCloudNoise(string? name);

    /// <summary>2-5 Step E: registers the AP 3D LUT name active for this frame
    /// (same pattern as SetCloudNoise).
    /// Main-pass binding 19 resolves from it. Passing null/empty falls back to a 1×1×1 all-zero 3D texture
    /// (the additive identity).</summary>
    [JSImport("globalThis.seasonWebGPU.setAerialLut")]
    internal static partial void SetAerialLut(string? name);

    [JSImport("globalThis.seasonWebGPU.updateStaticMeshVertices")]
    internal static partial void UpdateStaticMeshVertices(
        string cacheKey,
        [JSMarshalAs<JSType.MemoryView>] Span<byte> vertexBytes);

    [JSImport("globalThis.seasonWebGPU.updateTextInstance")]
    internal static partial void UpdateTextInstance(
        string key,
        [JSMarshalAs<JSType.MemoryView>] Span<byte> instanceBytes,
        [JSMarshalAs<JSType.MemoryView>] Span<byte> glyphBytes,
        int instanceCount);

    /// <summary>Pass null for skinKey on non-skinned batches; the JS side already handles skinKey ? ... correctly.</summary>
    [JSImport("globalThis.seasonWebGPU.drawMesh3DBatch")]
    internal static partial void DrawMesh3DBatch(
        string[] cacheKeys,
        [JSMarshalAs<JSType.MemoryView>] Span<byte> uniformBytes,
        int count,
        string? skinKey);

    // ── Phase 4: large byte uploads during load time
    // (MB-scale vertex/index/morph data + dirty rectangles for the glyph atlas) ──

    /// <summary>
    /// Full-mesh upload, including skinning and morph data.
    /// In rebind-only scenarios, pass Span&lt;byte&gt;.Empty for vertex/index/morph data.
    /// The JS side normalizes empty spans to null and then takes the existing early-return/guard path in _uploadStaticMeshInternal.
    /// </summary>
    [JSImport("globalThis.seasonWebGPU.uploadStaticMeshInterop")]
    internal static partial void UploadStaticMesh(
        string cacheKey,
        [JSMarshalAs<JSType.MemoryView>] Span<byte> vertexBytes,
        [JSMarshalAs<JSType.MemoryView>] Span<byte> indexBytes,
        string textureName,
        string normalTextureName,
        string mrTextureName,
        string aoTextureName,
        string emissiveTextureName,
        int vertexStrideFloats,
        string indexFormat,
        bool doubleSided,
        bool skinned,
        [JSMarshalAs<JSType.MemoryView>] Span<byte> morphBytes,
        int morphTargetCount,
        int morphVertexCount);

    /// <summary>
    /// Compact upload for dirty glyph-atlas rectangles: packedBytes concatenates row data for each rect in rects order
    /// (bytesPerRow = rw*4), and rects is a flat [x,y,w,h] tuple array.
    /// This replaces the old uploadGlyphAtlasSubRects path that resent the full 16 MB atlas every time.
    /// </summary>
    [JSImport("globalThis.seasonWebGPU.uploadGlyphAtlasPackedRects")]
    internal static partial void UploadGlyphAtlasPackedRects(
        string atlasName,
        [JSMarshalAs<JSType.MemoryView>] Span<byte> packedBytes,
        int[] rects);

    // ── 1-6 Compute foundation
    // (kernel registration model: WGSL and binding layouts are provided by C#, with zero shader source in JS) ──
    // bindingsJson is encoded to match ComputeBindingType ([{"type":0,"size":16},...]).
    // resourcesJson is a prefix-encoded array using "t:textureName"/"b:bufferId", and the JS side also uses it as the bind-group cache key.

    /// <summary>false means creation failed; compilation errors are reported asynchronously to the console, per JS-side rule ③.</summary>
    [JSImport("globalThis.seasonWebGPU.createComputeKernel")]
    internal static partial bool CreateComputeKernel(string name, string wgslCode, string entryPoint, string bindingsJson);

    [JSImport("globalThis.seasonWebGPU.disposeComputeKernel")]
    internal static partial void DisposeComputeKernel(string name);

    /// <summary>Registers a storage texture (write-only storage + sampleable) into the JS _textures dictionary.
    /// formatKind is aligned with ComputeStorageFormat (0=rgba8unorm, 1=rgba16float, used by the 2-1 bloom chain).</summary>
    [JSImport("globalThis.seasonWebGPU.createComputeTexture")]
    internal static partial void CreateComputeTexture(string name, int width, int height, int formatKind);

    [JSImport("globalThis.seasonWebGPU.createStorageBuffer")]
    internal static partial bool CreateStorageBuffer(string id, int sizeInBytes);

    [JSImport("globalThis.seasonWebGPU.disposeStorageBuffer")]
    internal static partial void DisposeStorageBuffer(string id);

    /// <summary>1-8: registers a 3D storage texture into the dedicated JS _textures3d dictionary (**not** into _textures),
    /// otherwise drawSprite2D and material name lookups would hit a 3D view.
    /// formatKind is aligned with ComputeStorageFormat.
    /// The actual texture format is decided centrally by JS _mapStorageFormat, which is the single source of truth
    /// and also keeps bind-group layout formats consistent.</summary>
    [JSImport("globalThis.seasonWebGPU.createComputeTexture3D")]
    internal static partial bool CreateComputeTexture3D(string name, int width, int height, int depth, int formatKind);

    /// <summary>1-8: CPU → storage-buffer write path (StorageBufferRead constant-block channel).
    /// Must be called from the frame-loop thread and outside a render pass.
    /// On the JS side, queue.writeBuffer is ordered relative to later dispatches by submission order.</summary>
    [JSImport("globalThis.seasonWebGPU.updateStorageBuffer")]
    internal static partial void UpdateStorageBuffer(
        string id,
        [JSMarshalAs<JSType.MemoryView>] Span<byte> bytes);

    /// <summary>Must be called outside a render pass (FrameStart/AfterScene phases).
    /// The JS side encodes writeBuffer plus a standalone compute pass into the current frame encoder.</summary>
    [JSImport("globalThis.seasonWebGPU.dispatchCompute")]
    internal static partial void DispatchCompute(
        string name,
        [JSMarshalAs<JSType.MemoryView>] Span<byte> paramsBytes,
        string resourcesJson,
        int groupsX, int groupsY, int groupsZ);
}
