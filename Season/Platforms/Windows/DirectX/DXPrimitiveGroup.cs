// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Silk.NET.Direct3D12;

namespace Season.Platforms.Windows.DirectX;

/// <summary>
/// Common base class for primitive groups rendered as a PrimitiveData list on
/// the Pbr3D path.
/// Derived classes (DXModel / DXMesh3D) mainly differ in:
///   - geometry / material source (glTF node tree vs Mesh3D.Surfaces)
///   - how primitives are collected (derived CollectPrimitives implementation)
///
/// Shared responsibilities:
///   - static: camera, shared lighting CB
///     (dedicated to the Pbr3D path, also reused by DXSpriteQuad)
///   - per-instance: Matrix/Material CB creation, SyncAlpha, three-bucket Draw,
///     and DrawPrimitive
/// </summary>
internal unsafe abstract class DXPrimitiveGroup : PrimitiveGroupCore<PrimitiveData, MaterialParams, DXTexture>, IDisposable
{
    // === Globally shared: lighting CB (N-buffered) ===
    // The Pbr3D path and the DXSpriteQuad/Sprite family all read the same CB
    // from here to avoid duplicate copies.
    internal static ID3D12Resource*[] lightConstantBuffers;
    static byte*[] mappedLightConstantBuffers;

    // === Per-instance state ===
    // Group-level state (alpha, tint, transform, outline, the reusable draw list, the shadow-pass cache) and the
    // highlight boxes all live in PrimitiveGroupCore. What stays here is D3D12 resource creation and submission.

    // ============================================================
    // Static: lighting-CB lifetime and global camera / lighting updates
    // ============================================================

    public static void InitLights()
    {
        int n = (int)Device.frameCount;
        lightConstantBuffers = new ID3D12Resource*[n];
        mappedLightConstantBuffers = new byte*[n];

        for (int i = 0; i < n; i++)
        {
            lightConstantBuffers[i] = Device.ResourceManager.CreateConstantBuffer(
                (uint)Unsafe.SizeOf<SceneLightParams>(),
                out mappedLightConstantBuffers[i]);
        }

        var defaultLight = new SceneLightParams
        {
            CameraPos = new Vector4(0, 0, -1, 1),
            Ambient = new Vector4(0.5f, 0.5f, 0.5f, 1f),
            Params0 = new Vector4(0, Device.HdrExposure, 0, 0),
        };
        for (int i = 0; i < n; i++)
            Unsafe.Write(mappedLightConstantBuffers[i], defaultLight);
    }

    public static void InitLightsDispose()
    {
        if (lightConstantBuffers != null)
        {
            for (int i = 0; i < lightConstantBuffers.Length; i++)
            {
                if (lightConstantBuffers[i] != null)
                {
                    lightConstantBuffers[i]->Unmap(0, null);
                    lightConstantBuffers[i]->Release();
                    lightConstantBuffers[i] = null;
                }
            }
            lightConstantBuffers = null;
            mappedLightConstantBuffers = null;
        }
    }

    /// <summary>Writes the lighting CB for the current frame using the 1-2
    /// SceneLightParams layout. Params0.Y carries HDR exposure
    /// (shader-side params0.y, used for inverse-ACES text compensation),
    /// VelocityParams carries this frame's sub-pixel jitter and inverse screen
    /// size (2-3 contract rule 6), and EnvParams + IrradianceSH9 carry 1-7
    /// environment lighting (contract rule 4). All three are injected once per
    /// frame here; writes from the app layer are ignored.</summary>
    public static void SetLighting(SceneLightParams lightParams)
    {
        int fi = (int)Device.FrameIndex;

        // Exposure, velocity parameters, environment, DDGI and sky are resolved by the shared frame setup, so
        // VK and Metal cannot drift from this backend.
        SceneFrameSetup.PrepareLighting(ref lightParams, Device.HdrExposure);

        // 1-7 rule 4: resolve this frame's radiance cube once so DrawPrimitive does not look it up per draw.
        var env = DeviceServices.BaseApp.SceneEnvironment;
        DXTextureCube.Active = env != null ? DXTextureCube.Find(env.RadianceName) : null;

        // Resolve the irradiance atlas written this frame once per frame.
        // Compute 2D textures are registered in the Graphics instance's
        // DictionaryDXTexture under their full names, which is the same
        // dictionary used by CreateComputeTexture. The Device static dictionary
        // only contains file textures. This avoids per-draw lookups in
        // DrawPrimitive. When not ready or not found, set null so DrawPrimitive
        // falls back to White.
        if (Season.Rendering.Effects.DdgiEffect.Ready
            && Season.Basic.Graphics.Instance is Season.Platforms.Windows.Graphics winGraphics)
        {
            winGraphics.TryGetTexture(Season.Rendering.Effects.DdgiEffect.ActiveIrradianceName, out Pipeline.DdgiAtlasActive);
            winGraphics.TryGetTexture(Season.Rendering.Effects.DdgiEffect.ActiveDepthName, out Pipeline.DdgiDepthActive);
        }
        else
        {
            Pipeline.DdgiAtlasActive = null;
            Pipeline.DdgiDepthActive = null;
        }

        // 2-5 Step C: resolve this frame's cloud-noise texture through the same
        // Graphics instance dictionary. It is baked only once in its lifetime,
        // but still resolved every frame so Active is cleared when
        // FrameSchedule.CloudNoiseTexture becomes null after Dispose or quality
        // downgrade. Otherwise a released texture handle could remain bound.
        // When null, DrawPrimitive falls back to White and the layer-count gate
        // keeps it out of shading.
        if (Season.Rendering.FrameSchedule.CloudNoiseTexture is string cloudNoiseName
            && Season.Basic.Graphics.Instance is Season.Platforms.Windows.Graphics cloudGraphics)
            cloudGraphics.TryGetTexture(cloudNoiseName, out Pipeline.CloudNoiseActive);
        else
            Pipeline.CloudNoiseActive = null;

        // 2-5 Step E: resolve this frame's aerial-perspective volume.
        // Do not use TryGetTexture here. Compute 3D textures are registered in
        // DXTexture3D's own static dictionary, separate from the 2D
        // DictionaryDXTexture (see 1-8), so only Find can be used.
        // This is also resolved every frame so Active is cleared when
        // FrameSchedule.AerialLutTexture becomes null after downgrade or Dispose.
        Pipeline.AerialLutActive = Season.Rendering.FrameSchedule.AerialLutTexture is string aerialName
            ? DXTexture3D.Find(aerialName)
            : null;

        // 2-7 P0: resolve this frame's Sky-View LUT, the PBR environment specular source under procedural sky.
        // Resolved every frame for the same reason as cloud noise: after a downgrade or Dispose the schedule
        // name goes null and a stale handle must not survive into the next frame's bindings.
        if (Season.Rendering.FrameSchedule.SkyViewTexture is string skyViewName
            && Season.Basic.Graphics.Instance is Season.Platforms.Windows.Graphics skyViewGraphics)
            skyViewGraphics.TryGetTexture(skyViewName, out Pipeline.SkyViewActive);
        else
            Pipeline.SkyViewActive = null;

        // The snapshot is read by fog / weather / particles, so it must come from the view that is actually
        // being rendered, not from the legacy camera mirror.
        var snapshotView = RenderView.Current;
        FrameSchedule.CaptureLighting(lightParams, snapshotView.View, snapshotView.RenderProjection,
            snapshotView.RenderViewProjection);
        Unsafe.Write(mappedLightConstantBuffers[fi], lightParams);
    }

    /// <summary>
    /// Called once per frame by the main loop: refreshes the camera
    /// view/projection and writes the lighting CB.
    /// The engine follows the glTF/OpenGL camera convention:
    /// left-handed space, with the camera standing on the +Z side and looking
    /// at the origin to see the model front face.
    /// </summary>
    public static void Update(float time, Vector3 cameraPos, Vector3 cameraTarget, SceneLightParams lightParams)
    {
        // 1-3: matrix construction is centralized in the shared Camera3D layer.
        // It is change-gated, so static cameras rebuild nothing, while
        // FOV/near/far are still driven by BaseApp.Camera.
        // cameraPos/cameraTarget are forwarded from BaseApp.Camera.Position/Target
        // and kept in the signature for frame-loop compatibility.
        var res = DeviceServices.BaseApp.DeviceResolution;

        // DPI compensation, matching the 2D rendering rule 1/CompositionScale.X:
        // multiply NDC xy by n and translate toward the upper-left corner.
        // This exactly reproduces the 2D "layout coordinates / scale" behavior,
        // meaning content scales around the screen's upper-left corner
        // (shrinking and shifting up-left).
        // Math: scaling by n around NDC upper-left (-1, +1) yields
        // x' = n*x + (n-1)*w, y' = n*y + (1-n)*w.
        // This only affects render matrices and does not pollute camera3D:
        // Frustum and shadow cascades still use the uncompensated matrices.
        // Multiplication order is fixed as Projection * DpiTransform under the
        // row-vector convention: project first, then apply the NDC affine transform.
        float n = 1f / DeviceServices.BaseApp.CompositionScale.X;
        var dpiTransform = Matrix4x4.CreateScale(n, n, 1f);
        dpiTransform.M41 = n - 1f;
        dpiTransform.M42 = 1f - n;

        // 2-3 contract rule 4: the main view is the only jitter injection point.
        // Prepare snapshots the previous unjittered ViewProjection, rebuilds the
        // camera matrices and bakes jitter only into the render projection.
        // Frustum culling and CSM cascades keep the unjittered matrices. The DPI
        // compensation is applied to the current and history render matrices
        // alike, so the TAA / MotionVectors velocity field stays aligned; with
        // MotionVectors off the history is all zero (no history).
        var view = DeviceServices.BaseApp.MainView;
        view.Prepare(res.X, res.Y, dpiTransform);
        FrameSchedule.CurrentView = view;
        Pipeline.WriteMainView(view.Constants);

        // 1-5: the CPU shadow-matrix chain runs after the camera update and before the lighting CB is written.
        // The rule itself is shared with VK and Metal; this call site only decides when to run it.
        SceneFrameSetup.ApplyShadowChain(ref lightParams, view.Camera);

        // Must go through SetLighting because it injects HdrExposure.
        // Writing the CB directly would leave hdrExposure at 0 in the shader, so
        // inverse-ACES text compensation would divide by 1e-4 and saturate all
        // text to white.
        SetLighting(lightParams);
    }

    // ============================================================
    // Instance: CB creation used during derived PrimitiveData initialization
    // ============================================================

    protected void CreateMatrixBuffer(PrimitiveData primitiveData)
    {
        int n = (int)Device.frameCount;
        primitiveData.MatrixBuffers = new ID3D12Resource*[n];
        primitiveData.MappedMatrixBuffers = new byte*[n];
        for (int i = 0; i < n; i++)
            primitiveData.MatrixBuffers[i] = Device.ResourceManager.CreateConstantBuffer(
                (uint)Unsafe.SizeOf<MatrixBuffer>(),
                out primitiveData.MappedMatrixBuffers[i]);
    }

    protected static void CreateMaterialBuffer(PrimitiveData primitiveData)
    {
        int n = (int)Device.frameCount;
        primitiveData.MaterialBuffers = new ID3D12Resource*[n];
        primitiveData.MappedMaterialBuffers = new byte*[n];
        for (int i = 0; i < n; i++)
            primitiveData.MaterialBuffers[i] = Device.ResourceManager.CreateConstantBuffer(
                (uint)Unsafe.SizeOf<MaterialParams>(),
                out primitiveData.MappedMaterialBuffers[i]);
    }

    // ============================================================
    // Instance: material-texture replacement
    // (derived classes provide primitive lists through CollectPrimitives)
    // ============================================================

    /// <summary>
    /// 2-6 clause 1: mip policy implied by a material slot. Runtime replacement must resolve this the same way the
    /// glTF load path does, otherwise an overridden texture would silently lose the chain its neighbours have.
    /// </summary>
    static TextureMipPolicy MipPolicyForSlot(TextureSlot slot) => slot switch
    {
        TextureSlot.Normal => TextureMipPolicy.Normal,
        TextureSlot.MetallicRoughness => TextureMipPolicy.Linear,
        TextureSlot.Occlusion => TextureMipPolicy.Linear,
        _ => TextureMipPolicy.Color,
    };

    /// <summary>
    /// Replaces the texture on the specified slot for all primitives.
    /// </summary>
    internal void ReplaceTextureBySlot(TextureSlot slot, INativeImageDecoder decoder)
    {
        _drawList.Clear();
        CollectPrimitives(_drawList);
        if (_drawList.Count == 0) return;

        var oldTex = _drawList[0].Texture(slot);
        if (oldTex == null) return;

        bool sameSize = (uint)decoder.Width == oldTex.Width
                     && (uint)decoder.Height == oldTex.Height;
        bool exclusive = oldTex.RefCount == 1;

        if (sameSize && exclusive)
        {
            // Fast path. UploadPixels regenerates the chain from the policy the texture was created with, so the
            // lower levels stay consistent with the new level 0.
            oldTex.UploadPixels(decoder.PixelSpan);
        }
        else
        {
            var newTex = DXTexture.CreateFromDecoder(decoder, MipPolicyForSlot(slot));
            foreach (var primitive in _drawList)
                primitive.Texture(slot) = newTex;
        }
    }

    // ============================================================
    // Instance: derived hooks
    // ============================================================

    /// <summary>Derived classes append all PrimitiveData that should be drawn
    /// this frame into `result`.</summary>
    protected abstract override void CollectPrimitives(List<PrimitiveData> result);

    /// <summary>
    /// Derived classes may override this to return the bone StructuredBuffer
    /// SRV handle. Both regular DXModel and DXInstancedModel use the same
    /// dynamic bone-palette path.
    /// </summary>
    protected virtual GpuDescriptorHandle GetBoneSrvHandle() => default;

    /// <summary>2-3 Step C (tier B): derived classes may override this to
    /// return the previous bone-palette SB SRV handle.</summary>
    protected virtual GpuDescriptorHandle GetPrevBoneSrvHandle() => default;

    /// <summary>2-3 Step C (tier C-b completion): derived classes may override
    /// this to return the previous morph-weights SB SRV handle.</summary>
    protected virtual GpuDescriptorHandle GetPrevMorphSrvHandle() => default;

    public abstract void Dispose();

    // ============================================================
    // Unified highlighting: D3D12 resources and draw hooks
    // (the box lifetime, the shell geometry and the draw order live in
    // PrimitiveGroupCore.Highlight; only GPU objects are built here)
    // ============================================================

    /// <summary>Unified highlighting: attaches the morph path to a shell
    /// primitive by expanding source deltas to the shell-vertex layout. The
    /// shell-vertex to source-vertex mapping is recorded when the shell is
    /// built. This sets HasMorphTargets, MorphTargetCount, and
    /// MorphVertexCount (= shell vertex count), then writes them back to the
    /// Material CB for all frames. Weights are shared with the source and synced
    /// every frame by DXModel.ApplyMorphTargetsIfNeeded.</summary>
    protected override void AttachShellMorph(PrimitiveData shell, PrimitiveData source, IReadOnlyList<int> sourceIndices)
    {
        shell.MaterialParams.HasMorphTargets = 1;
        shell.MaterialParams.MorphTargetCount = source.MaterialParams.MorphTargetCount;
        shell.MaterialParams.MorphVertexCount = (uint)shell.Vertices.Count;
        for (int i = 0; i < Device.frameCount; i++)
            Unsafe.Write(shell.MappedMaterialBuffers[i], shell.MaterialParams);
        CreateMorphDeltaBuffer(shell, null!, source.MorphTargets!, sourceIndices);
    }

    /// <summary>
    /// Phase 3: packs morph-target deltas into the
    /// StructuredBuffer&lt;float&gt; layout
    /// [targetIndex * vertexCount + vertexIndex] * 9 floats = pos.xyz + normal.xyz + tangent.xyz
    /// When vertexMap is not null, deltas are expanded through that mapping.
    /// In the shell-vertex layout, the delta for shell vertex v comes from
    /// source delta [vertexMap[v]], so vertexCount = vertexMap.Count.
    /// On the source-primitive path, no mapping is passed and the layout is the
    /// identity mapping, so vertexCount = baseVertices.Length.
    /// 2-3 Step C: the previous position used by velocity reuses the same delta
    /// data, because morph deltas are static per-target geometric differences.
    /// There is no concept of a "previous-frame delta"; only weights change.
    /// So capacity does not need to be doubled, matching VK/Metal.
    /// </summary>
    protected static void CreateMorphDeltaBuffer(PrimitiveData primitiveData, Vertex[] baseVertices, List<GLTFMorphTarget> morphTargets, IReadOnlyList<int>? vertexMap = null)
    {
        // The [target][vertex] × 9 layout, the sparse-target zero fill, and the shell vertex map all live in
        // MorphDelta.Pack, shared with VK/Metal/Web. Only the upload below is backend-specific.
        var deltaData = MorphDelta.Pack(baseVertices, morphTargets, vertexMap);

        // Create upload buffer
        primitiveData.MorphDeltasBuffer = Device.ResourceManager.CreateBuffer(
            HeapType.Upload, (ulong)(deltaData.Length * sizeof(float)),
            ResourceStates.GenericRead);
        fixed (float* pSrc = deltaData)
        {
            void* pDst;
            primitiveData.MorphDeltasBuffer->Map(0, null, &pDst);
            Unsafe.CopyBlock(pDst, pSrc, (uint)(deltaData.Length * sizeof(float)));
            primitiveData.MorphDeltasBuffer->Unmap(0, null);
        }

        // Allocate SRV descriptor
        primitiveData.MorphDescriptorId = Device.DescriptorAllocator.Allocate(DescriptorKind.ModelData);
        var cpuHandle = Device.SrvHeapManager.GetCpuHandle(primitiveData.MorphDescriptorId);
        var srvDesc = new ShaderResourceViewDesc
        {
            Format = Silk.NET.DXGI.Format.FormatUnknown,
            ViewDimension = SrvDimension.Buffer,
            Shader4ComponentMapping = 0x00001688u,
            Buffer = new BufferSrv
            {
                FirstElement = 0,
                NumElements = (uint)deltaData.Length,
                StructureByteStride = (uint)sizeof(float),
                Flags = BufferSrvFlags.None
            }
        };
        Device.D3dDevice->CreateShaderResourceView(primitiveData.MorphDeltasBuffer, &srvDesc, cpuHandle);
        primitiveData.MorphDeltasSrvHandle = Device.SrvHeapManager.GetGpuHandle(primitiveData.MorphDescriptorId);
    }

    /// <summary>Unified highlighting: derives a shared-geometry box from a
    /// template primitive by copying references to CPU vertex/index arrays,
    /// VB/IB views, and material / texture references, while leaving held GPU
    /// pointers null. PrimitiveData.Dispose is null-safe, so no double release
    /// occurs. Vertices and indices are immutable shared data, and Dispose only
    /// releases GPU buffers, so aliasing is safe. DrawPrimitive reads
    /// Indices.Length every draw to determine index count.
    /// A local N-buffered Material CB is created and initialized for all frames.
    /// Matrix CB is unnecessary because instanced rendering uses the group-level
    /// shared matrix CB plus instance-stream slots.</summary>
    PrimitiveData CreateSharedShellPrimitive(PrimitiveData template)
    {
        var primitive = new PrimitiveData
        {
            SharedGeometrySource = template.SharedGeometrySource ?? template,
            Vertices = template.Vertices,
            Indices = template.Indices,
            VertexBufferView = template.VertexBufferView,
            IndexBufferView = template.IndexBufferView,
            Use32BitIndices = template.Use32BitIndices,
            DoubleSided = template.DoubleSided,
            IsTransparent = template.IsTransparent,
            LocalBoundsCenter = template.LocalBoundsCenter,
            LocalBoundsExtents = template.LocalBoundsExtents,
            MaterialParams = template.MaterialParams,
            BaseColorTexture = template.BaseColorTexture,
            NormalTexture = template.NormalTexture,
            MetallicRoughnessTexture = template.MetallicRoughnessTexture,
            OcclusionTexture = template.OcclusionTexture,
            EmissiveTexture = template.EmissiveTexture,
        };
        CreateMaterialBuffer(primitive);
        for (int i = 0; i < Device.frameCount; i++)
            Unsafe.Write(primitive.MappedMaterialBuffers[i], primitive.MaterialParams);
        return primitive;
    }

    /// <summary>The core's template hook, typed to this backend's PrimitiveData. The factory keeps its own name
    /// because the DirectX rendering tests reflect <see cref="CreateSharedShellPrimitive"/> by name.</summary>
    protected override PrimitiveData CreateSharedGeometryPrimitive(PrimitiveData template)
        => CreateSharedShellPrimitive(template);

    /// <summary>
    /// Unified highlighting: writes the box world matrix plus the dual face/edge
    /// colors into the box's own N-buffered CB every frame for the current fi.
    /// There is no change gate because face alpha, taken from SurfaceColor.W,
    /// pulses every frame and the steady write cost matches normal primitives.
    /// PrevWorld comes from the CPU shadow copy in box.PrevWorld, and after
    /// writing, the current world is rolled into the shadow for the next frame.
    /// `world` is supplied by the caller:
    /// Bounds = Scale(Extents*2) x Translate(Center), after the caller checks
    /// for degenerate boxes;
    /// Wireframe = model / instance world matrix, which is always valid.
    /// </summary>
    protected override void WriteHighlightBox(HighlightBox box, Matrix4x4 world, Vector4 faceColor, Vector4 edgeColor)
    {
        int fi = (int)Device.FrameIndex;
        var matrices = new MatrixBuffer
        {
            World = Matrix4x4.Transpose(world),
            PrevWorld = Matrix4x4.Transpose(box.PrevWorld),
        };
        Unsafe.Write(box.Face.MappedMatrixBuffers[fi], matrices);
        Unsafe.Write(box.Edges.MappedMatrixBuffers[fi], matrices);

        box.Face.MaterialParams.BaseColor = faceColor;
        Unsafe.Write(box.Face.MappedMaterialBuffers[fi], box.Face.MaterialParams);
        box.Edges.MaterialParams.BaseColor = edgeColor;
        Unsafe.Write(box.Edges.MappedMaterialBuffers[fi], box.Edges.MaterialParams);

        box.PrevWorld = world;
        box.FaceAlpha = faceColor.W;
    }

    /// <summary>Unified highlighting: draws a single highlight box. When face
    /// alpha (SurfaceColor.W) is &gt; 0, faces are drawn using the engine's
    /// double-sided transparent 2-pass convention (Front -> Back). When it is 0,
    /// the box automatically becomes edge-only and faces are skipped.
    /// Edges use the Opaque path with CullNone and depth writes, so solid thin
    /// strips cover the faces and any interior geometry.</summary>
    protected void DrawHighlightBox(HighlightBox box, ID3D12Resource* lightCB)
    {
        int fi = (int)Device.FrameIndex;
        var face = box.Face;
        var edges = box.Edges;

        if (box.FaceAlpha > 0f)
        {
            Pipeline.SetPipeline(PipelineMode.Transparent, PipelineCullVariant.Front);
            Pipeline.DrawPrimitive(face, lightCB, face.MatrixBuffers[fi], null, 1, 0,
                GetBoneSrvHandle(), GetPrevBoneSrvHandle(), default, GetPrevMorphSrvHandle());

            Pipeline.SetPipeline(PipelineMode.Transparent, PipelineCullVariant.Back);
            Pipeline.DrawPrimitive(face, lightCB, face.MatrixBuffers[fi], null, 1, 0,
                GetBoneSrvHandle(), GetPrevBoneSrvHandle(), default, GetPrevMorphSrvHandle());
        }

        Pipeline.SetPipeline(PipelineMode.Opaque, PipelineCullVariant.None, depthWrite: true);
        Pipeline.DrawPrimitive(edges, lightCB, edges.MatrixBuffers[fi], null, 1, 0,
            GetBoneSrvHandle(), GetPrevBoneSrvHandle(), default, GetPrevMorphSrvHandle());
    }

    /// <summary>Unified highlighting: draws all instance boxes whose Bounds
    /// highlighting is enabled this frame, one by one through DrawHighlightBox.</summary>
    protected void DrawBoundsBoxes(ID3D12Resource* lightCB)
    {
        for (int i = 0; i < _boundsBoxDrawList.Count; i++)
            DrawHighlightBox(_instanceBoundsBoxes[_boundsBoxDrawList[i]], lightCB);
    }

    /// <summary>
    /// Unified highlighting, Bounds-box geometry: face PrimitiveData.
    /// Eight corners are baked into [-0.5,0.5]^3, using bit-coded corner indices
    /// x + y*2 + z*4, with 36 indices total.
    /// RenderMode=0 (Unlit) and AlphaMode=2 (BLEND), so true transparency uses
    /// the Transparent PSO. DoubleSided is enabled, and all five textures are
    /// White with Use*Map=0 because Unlit does not sample them.
    /// </summary>
    protected override PrimitiveData CreateBoundsFacePrimitive()
    {
        var primitive = new PrimitiveData
        {
            Vertices = HighlightGeometry.BuildBoxFaceVertices(),
            Indices = HighlightGeometry.BuildBoxFaceIndices().ToArray(),
            Use32BitIndices = false,
            DoubleSided = true,
            IsTransparent = true,
            LocalBoundsCenter = Vector3.Zero,
            LocalBoundsExtents = new Vector3(0.5f),
            MaterialParams = new MaterialParams
            {
                RenderMode = 0u,
                AlphaMode = 2u,
                AlphaCutoff = 0.5f,
                BaseColor = new Vector4(1f, 1f, 1f, 0.3f),
            },
        };
        InitBoundsBoxGpuResources(primitive);
        return primitive;
    }

    /// <summary>
    /// Unified highlighting, Bounds-box geometry: edge PrimitiveData.
    /// Uses 12 thin boxes, four on each of the three axes. Each one extends by
    /// one thickness beyond the corners along its axis
    /// ([-0.5-h, 0.5+h]) so all eight corners join seamlessly.
    /// RenderMode=0 and AlphaMode=0 (OPAQUE), so solid edges use the Opaque PSO
    /// with depth writes and do not pulse with face alpha. EdgeColor stays
    /// semantically solid.
    /// </summary>
    protected override PrimitiveData CreateBoundsEdgesPrimitive()
    {
        var indices = new List<uint>(12 * 36);
        var primitive = new PrimitiveData
        {
            Vertices = HighlightGeometry.BuildBoxEdgesVertices(indices),
            Indices = indices.ToArray(),
            Use32BitIndices = false,
            DoubleSided = true,
            IsTransparent = false,
            LocalBoundsCenter = Vector3.Zero,
            LocalBoundsExtents = new Vector3(0.5f),
            MaterialParams = new MaterialParams
            {
                RenderMode = 0u,
                AlphaMode = 0u,
                AlphaCutoff = 0.5f,
                BaseColor = new Vector4(1f, 0f, 0f, 1f),
            },
        };
        InitBoundsBoxGpuResources(primitive);
        return primitive;
    }

    /// <summary>Unified highlighting: builds a shell primitive from vertices and
    /// indices. Material settings are copied from the source primitive and then
    /// forced to Unlit plus transparent (BLEND) or opaque (OPAQUE). It uses
    /// DoubleSided and White for all five textures, since Unlit does not sample
    /// them. VB/IB and both CBs are created and initialized for all frames by
    /// InitBoundsBoxGpuResources.
    /// Skinning and instancing flags such as IsSkinned, BonePaletteStride, and
    /// IsInstanced are inherited from the source material, which is critical for
    /// keeping non-instanced per-primitive shell boxes tightly aligned to
    /// animation.</summary>
    protected override PrimitiveData CreateShellPrimitive(List<Vertex> vertices, uint[] indices, MaterialParams sourceParams, bool isTransparent)
    {
        var mp = sourceParams;
        mp.RenderMode = 0u;
        mp.AlphaMode = isTransparent ? 2u : 0u;
        mp.AlphaCutoff = 0.5f;

        var primitive = new PrimitiveData
        {
            Vertices = vertices,
            Indices = indices,
            Use32BitIndices = true,
            DoubleSided = true,
            IsTransparent = isTransparent,
            LocalBoundsCenter = Vector3.Zero,
            LocalBoundsExtents = new Vector3(0.5f),
            MaterialParams = mp,
        };
        InitBoundsBoxGpuResources(primitive);
        return primitive;
    }

    /// <summary>Unified highlighting: creates VB/IB/Material CB/Matrix CB for
    /// box primitives and initializes them for all frames to avoid garbage reads
    /// under N-buffering.</summary>
    void InitBoundsBoxGpuResources(PrimitiveData primitive)
    {
        primitive.VertexBuffer = Device.CreateVertexBuffer(primitive.Vertices.ToArray(), out primitive.VertexBufferView);
        primitive.IndexBuffer = Device.CreateIndexBuffer(primitive.Indices, out primitive.IndexBufferView);
        primitive.BaseColorTexture = Device.White;
        primitive.NormalTexture = Device.White;
        primitive.MetallicRoughnessTexture = Device.White;
        primitive.OcclusionTexture = Device.White;
        primitive.EmissiveTexture = Device.White;

        CreateMatrixBuffer(primitive);
        CreateMaterialBuffer(primitive);

        var matrices = new MatrixBuffer
        {
            World = Matrix4x4.Transpose(Matrix4x4.Identity),
        };
        for (int i = 0; i < Device.frameCount; i++)
        {
            Unsafe.Write(primitive.MappedMatrixBuffers[i], matrices);
            Unsafe.Write(primitive.MappedMaterialBuffers[i], primitive.MaterialParams);
        }
    }

    // ============================================================
    // Instance: main pass
    // (the three-bucket flow lives in PrimitiveGroupCore; only the sort key is DX-specific)
    // ============================================================

    static float ComputeTransparentSortDepth(PrimitiveData primitiveData)
    {
        int fi = (int)Device.FrameIndex;
        var matrices = Unsafe.Read<MatrixBuffer>(primitiveData.MappedMatrixBuffers[fi]);
        var world = Matrix4x4.Transpose(matrices.World);
        var center = Vector3.Transform(primitiveData.LocalBoundsCenter, world);

        if (DeviceServices.BaseApp != null)
            return RenderView.Current.ViewDepth(center);

        // No app (offline tooling): world Z, matching RenderView.SortDepth's fallback.
        return center.Z;
    }

    protected override string TraceBackend => "dx";

    protected override int FrameCount => (int)Device.frameCount;

    protected override ref MaterialParams MaterialSlot(PrimitiveData primitive, int frame)
        => ref Unsafe.AsRef<MaterialParams>(primitive.MappedMaterialBuffers[frame]);

    protected override TransparentOrder TransparentBucketOrder => TransparentOrder.SortedSplit;

    /// <summary>While a scene draw collection is recording, the order it captures is replayed later, so sorting here
    /// would produce an order the replay cannot reproduce.</summary>
    protected override bool SortTransparentNow => DXSceneDrawCollection.Active == null;

    protected override float TransparentSortDepth(PrimitiveData primitive)
        => ComputeTransparentSortDepth(primitive);

    protected override void ApplySurfaceState(in SurfaceState state)
    {
        // Explicit switches, never casts: PipelineMode orders Opaque/Transparent/Fade while SurfaceMode orders
        // Opaque/Fade/Transparent, so a cast would silently swap Fade and Transparent.
        Pipeline.SetPipeline(
            state.Mode switch
            {
                SurfaceMode.Opaque => PipelineMode.Opaque,
                SurfaceMode.Fade => PipelineMode.Fade,
                _ => PipelineMode.Transparent,
            },
            state.Cull switch
            {
                SurfaceCull.None => PipelineCullVariant.None,
                SurfaceCull.Front => PipelineCullVariant.Front,
                _ => PipelineCullVariant.Back,
            },
            state.DepthWrite);
    }

    protected override void DrawMainPrimitive(PrimitiveData primitive)
        => DrawPrimitive(primitive, lightConstantBuffers[(int)Device.FrameIndex]);

    /// <summary>Unified highlighting: the core decides which boxes to draw and in what order; this binds the
    /// frame's lighting block. Faces use transparent 2-pass rendering, edges use Opaque with depth writes, and a zero
    /// SurfaceColor.w turns the box edge-only through the FaceAlpha gate.</summary>
    protected override void DrawHighlightBox(HighlightBox box)
        => DrawHighlightBox(box, lightConstantBuffers[(int)Device.FrameIndex]);

    void DrawPrimitive(PrimitiveData primitiveData, ID3D12Resource* lightConstantBuffer)
    {
        int fi = (int)Device.FrameIndex;
        // Unified entry point: regular draws use instanceCount=1 and
        // instanceBuffer=null, which automatically selects the identity buffer.
        Pipeline.DrawPrimitive(primitiveData, lightConstantBuffer, primitiveData.MatrixBuffers[fi], null, 1, 0,
            GetBoneSrvHandle(), GetPrevBoneSrvHandle(), default, GetPrevMorphSrvHandle());
    }

    public virtual void DrawOutlineMask()
    {
        if (!_transformInitialized || !_outline2DActive)
            return;

        _drawList.Clear();
        CollectPrimitives(_drawList);
        if (_drawList.Count == 0)
            return;

        DrawTrace.Group("dx", "outline", Name, _drawList.Count);

        var lightCB = lightConstantBuffers[(int)Device.FrameIndex];
        bool pipelineSet = false;
        bool currentDoubleSided = false;

        // Rewrite outline color per group through root constant b6.
        // Mask pixels carry the group's color, and the composite pass picks
        // color per pixel from it, allowing multiple colors in the same frame.
        Pipeline.SetOutlineMaskColor(_outline2DColor);

        for (int i = 0; i < _drawList.Count; i++)
        {
            var p = _drawList[i];
            if (p.IsTransparent)
                continue;

            if (!pipelineSet || currentDoubleSided != p.DoubleSided)
            {
                Pipeline.SetPipeline(PipelineMode.Opaque,
                    p.DoubleSided ? PipelineCullVariant.None : PipelineCullVariant.Back, depthWrite: false);
                pipelineSet = true;
                currentDoubleSided = p.DoubleSided;
            }

            DrawPrimitive(p, lightCB);
        }
    }

    // ============================================================
    // Instance: 1-5 shadow pass
    // (the epoch cache and the primitive loop live in PrimitiveGroupCore)
    // ============================================================

    protected override void BeginShadowPass()
    {
        // Group-invariant t6/t8/t9/t10 are bound once for the group.
        // This class has no instance stream, so t9 always uses the default zero-value SB.
        Pipeline.SetShadowGroupBindings(
            GetBoneSrvHandle(), GetPrevBoneSrvHandle(), default, GetPrevMorphSrvHandle());
    }

    protected override void DrawShadowPrimitive(PrimitiveData p, bool bindMaterial)
        => Pipeline.DrawShadowPrimitive(p, p.MatrixBuffers[(int)Device.FrameIndex], null, 1, 0, bindMaterial);

    /// <summary>
    /// Whether the group can bind b2 (root parameter 2) and t5 (root parameter 9) once for the whole group
    /// instead of per primitive. The rule itself and the list of shader fields it depends on live in
    /// <see cref="ShadowMaterialSharing.CanShare"/>; this is only the DX-typed entry point.
    /// </summary>
    protected override bool CanShareShadowMaterial(List<PrimitiveData> primitives)
        => ShadowMaterialSharing.CanShare(primitives);
}
