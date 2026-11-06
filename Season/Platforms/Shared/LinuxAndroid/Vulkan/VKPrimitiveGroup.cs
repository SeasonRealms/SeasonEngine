// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Season.Models;
using Silk.NET.Vulkan;
using System.Runtime.CompilerServices;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Season.Platforms.Shared.LinuxAndroid.Vulkan;

/// <summary>
/// Common base class on the Pbr3D path for primitive groups rendered from a PrimitiveData list
/// (Vulkan backend).
/// Aligned 1:1 with DXPrimitiveGroup:
///   - Static: camera, shared lighting UBOs (N-buffered)
///   - Instance: Matrix/Material UBO creation, AllocateAndWriteDescriptorSets, SyncAlpha,
///     and three-bucket grouped drawing
/// Derived differences:
///   - Geometry/material source (VKModel uses the glTF node tree, VKMesh3D uses Mesh3D.Surfaces)
///   - Whether skinning palettes exist (VKModel supplies the binding-9 storage ring and uploads it in OnBeforeDraw)
/// </summary>
internal unsafe abstract class VKPrimitiveGroup : PrimitiveGroupCore<PrimitiveData, MaterialParams, Texture>, IDisposable
{
    /// <summary>
    /// Capacity of the previous-frame bone-palette SSBO. The current pose no longer has a fixed bone ceiling
    /// (it lives in the binding-9 storage buffer), but the prev ring keeps this bound so its allocation and
    /// the CPU shadow copy can be size-checked against one constant.
    /// </summary>
    public const int MaxBones = 100;

    // Global shared state: lighting UBOs (N-buffered)
    internal static BufferResource[] LightConstantBuffers = null!;

    static byte*[] _mappedLightConstantBuffers = null!;

    // 3B/B2: the five frame-level lighting inputs (DDGI irradiance/depth, cloud noise, AP volume, Sky-View LUT) live on
    // SceneFrameInputs together with their binding numbers. They are resolved once per frame from SetLighting and written
    // into the per-view globals set 3, so no primitive group owns them any more.
    /// <summary>
    /// 3B/B2: the binding numbers the set-0 writes below cover, ascending. The Vulkan harness holds this list against
    /// <c>Pipeline.SceneSetBindings</c>, so a write added or dropped on one side without the other turns red instead of
    /// reaching a driver as a wild or dead descriptor.
    /// </summary>
    internal static readonly uint[] GroupSetBindings =
    {
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 21, 26,
    };

    // === Per-instance state ===
    // Group-level state (alpha, tint, transform, the reusable draw list, the shadow-pass cache) and the
    // highlight boxes all live in PrimitiveGroupCore. What stays here is Vulkan resource creation and submission.

    // ============================================================
    // Static: lighting / identity-bone UBO lifetime + global camera/lighting updates
    // ============================================================

    public static void InitLights()
    {
        int n = (int)Device.frameCount;
        LightConstantBuffers = new BufferResource[n];
        _mappedLightConstantBuffers = new byte*[n];

        for (int i = 0; i < n; i++)
        {
            LightConstantBuffers[i] = Device.ResourceManager.CreateConstantBuffer(
                (uint)Unsafe.SizeOf<SceneLightParams>(),
                out _mappedLightConstantBuffers[i]);
        }

        var defaultLight = new SceneLightParams
        {
            CameraPos = new Vector4(0, 0, -1, 1),
            Ambient = new Vector4(0.5f, 0.5f, 0.5f, 1f),
            Params0 = new Vector4(0, Device.HdrExposure, 0, 0),
        };
        for (int i = 0; i < n; i++)
            Unsafe.Write(_mappedLightConstantBuffers[i], defaultLight);
    }

    public static void InitLightsDispose()
    {
        var vk = Device.Vk;
        var d = Device.LogicalDevice;
        var rm = Device.ResourceManager;

        if (LightConstantBuffers != null)
        {
            for (int i = 0; i < LightConstantBuffers.Length; i++)
            {
                if (LightConstantBuffers[i].Memory.Handle != 0)
                    vk.UnmapMemory(d, LightConstantBuffers[i].Memory);
                rm?.DestroyBuffer(LightConstantBuffers[i]);
            }
            LightConstantBuffers = null!;
            _mappedLightConstantBuffers = null!;
        }
    }

    /// <summary>Write the lighting UBO for the current frame
    /// (1-2 layout `SceneLightParams`, 976B).
    /// `Params0.Y` carries HDR exposure (shader-side `params0.y`, used for text inverse-ACES compensation),
    /// and `VelocityParams` carries the current-frame subpixel jitter plus inverse screen size
    /// (2-3 contract clause 6).
    /// Both are injected once per frame at a single point; writes from the app side are ineffective.</summary>
    public static void SetLighting(SceneLightParams lightParams)
    {
        int fi = (int)Device.FrameIndex;

        // Exposure, velocity parameters, environment, DDGI and sky are resolved by the shared frame setup, so
        // DX and Metal cannot drift from this backend.
        SceneFrameSetup.PrepareLighting(ref lightParams, Device.HdrExposure);

        // 1-7 clause 4: resolve this frame's radiance cube once so DrawPrimitive does not look it up per draw.
        var env = DeviceServices.BaseApp.SceneEnvironment;
        VKTextureCube.Active = env != null ? VKTextureCube.Find(env.RadianceName) : null;

        // 3B/B2: the five frame-level lighting inputs are resolved once per frame in one place (SceneFrameInputs),
        // before this frame's view hooks write the globals set 3. Nothing here is per-primitive or per-view.
        SceneFrameInputs.Resolve();

        // The snapshot is read by fog / weather / particles, so it must come from the view that is actually
        // being rendered, not from the legacy camera mirror.
        var snapshotView = RenderView.Current;
        FrameSchedule.CaptureLighting(lightParams, snapshotView.View, snapshotView.RenderProjection,
            snapshotView.RenderViewProjection);
        Unsafe.Write(_mappedLightConstantBuffers[fi], lightParams);
    }

    /// <summary>Called once per frame from the main loop:
    /// refresh camera view/projection and write the lighting UBO.</summary>
    public static void Update(float time, Vector3 cameraPos, Vector3 cameraTarget, SceneLightParams lightParams)
    {
        // 1-3: matrix construction is unified through the shared Camera3D layer
        // (Changed-gated, zero rebuild for a stationary camera; FOV/near/far are driven by BaseApp.Camera).
        // The cameraPos/cameraTarget parameters are forwarded from BaseApp.Camera.Position/Target;
        // the signature is kept for frame-loop compatibility.
        var res = DeviceServices.BaseApp.DeviceResolution;

        // 2-3 contract clause 4: the main view is the only injection point for jitter.
        // Prepare snapshots the previous frame's unjittered ViewProjection,
        // then rebuilds matrices and bakes jitter only into the render projection.
        // Frustum culling and CSM cascades keep the unjittered matrices
        // to avoid edge flicker and shadow jitter; with MotionVectors off the history is all zero.
        var view = DeviceServices.BaseApp.MainView;
        view.Prepare(res.X, res.Y);
        FrameSchedule.CurrentView = view;
        Pipeline.WriteMainView(view.Constants);

        // 1-5: the CPU shadow-matrix chain runs after the camera update and before the lighting UBO is written.
        // The rule itself is shared with DX and Metal; this call site only decides when to run it.
        SceneFrameSetup.ApplyShadowChain(ref lightParams, view.Camera);

        // Must go through SetLighting, which includes HdrExposure injection.
        // Writing the UBO directly would make the shader read params0.y = 0,
        // causing text inverse-ACES compensation to divide by 1e-4
        // and saturate all text to white
        // (same failure seen on the DX side, RenderQuality contract 5).
        SetLighting(lightParams);
    }

    // ============================================================
    // Instance: UBO creation (called by derived classes during PrimitiveData initialization)
    // ============================================================

    protected void CreateMatrixBuffer(PrimitiveData primitiveData)
    {
        int n = (int)Device.frameCount;
        primitiveData.MatrixBuffers = new BufferResource[n];
        primitiveData.MappedMatrixBuffers = new byte*[n];
        for (int i = 0; i < n; i++)
            primitiveData.MatrixBuffers[i] = Device.ResourceManager.CreateConstantBuffer(
                (uint)Unsafe.SizeOf<MatrixBuffer>(),
                out primitiveData.MappedMatrixBuffers[i]);
    }

    protected void CreateMaterialBuffer(PrimitiveData primitiveData)
    {
        int n = (int)Device.frameCount;
        primitiveData.MaterialBuffers = new BufferResource[n];
        primitiveData.MappedMaterialBuffers = new byte*[n];
        for (int i = 0; i < n; i++)
            primitiveData.MaterialBuffers[i] = Device.ResourceManager.CreateConstantBuffer(
                (uint)Unsafe.SizeOf<MaterialParams>(),
                out primitiveData.MappedMaterialBuffers[i]);
    }

    /// <summary>Derived override: return the storage-buffer frame ring used for instanced skinning.
    /// Defaults to the global Identity buffers.</summary>
    protected virtual BufferResource[] InstanceBoneBuffers => Pipeline.IdentityInstanceBoneBuffers;

    protected virtual BufferResource[] MorphDeltasBuffers => Pipeline.DefaultMorphDeltasBuffers;

    /// <summary>
    /// Allocate one DescriptorSet per frame for the primitive and write it once:
    ///   binding 0: matrix UBO[fi]
    ///   binding 1: light UBO[fi]
    ///   binding 2: material UBO[fi]
    ///   binding 3: reserved placeholder UBO (unread since skinning moved to binding 9, still written for a gap-free prefix)
    ///   binding 4..8: five PBR texture ImageViews (CombinedImageSampler, sampler provided by PImmutableSamplers)
    ///   binding 9: instanced bone storage buffer[fi] (falls back to global Identity when no instance bones exist)
    ///   binding 10: morph-delta storage buffer (falls back to the global zero buffer when there is no morph)
    ///   binding 11: shared TextDrawParams UBO
    ///   binding 12: shadow atlas (when shadows are enabled)
    ///   binding 13/14/15: previous-frame SSBO data
    ///     (prev bones / instanceWorld / morphWeights, defaulting to zero placeholders)
    ///   binding 16 (1-7): environment radiance cube (1x1 all-black dummy when no environment map exists)
    ///   binding 17 (2-4): DDGI irradiance atlas (falls back to White when not ready)
    ///   binding 18 (2-4 Step 3): DDGI depth-moment atlas (falls back to White when not ready)
    ///   binding 19 (2-5 Step C): cloud noise (falls back to White when not ready, gated by layer count)
    ///   binding 20 (2-5 Step E): AP 3D LUT (falls back to 1x1x1 DummyBlack when not ready)
    /// </summary>
    protected void AllocateAndWriteDescriptorSets(PrimitiveData p)
    {
        // A model loaded on the background thread allocates and writes its sets here, including the 25-binding write
        // batch in WriteDescriptorSet. The frame that is already recording may have bound the same slots, so the whole
        // allocate-and-write stays under the GPU gate; see Device.EnterGpuAccessScope.
        using var gpuAccess = Device.EnterGpuAccessScope();

        int n = (int)Device.frameCount;
        p.DescriptorSets = new DescriptorSet[n];
        // 3B: the six frame-level lighting inputs moved to the per-view globals set 3, so a primitive's set 0
        // carries no version caches at all any more: nothing in it follows a per-frame ping-pong.
        var instanceBones = InstanceBoneBuffers;
        var morphDeltas = MorphDeltasBuffers;

        for (int fi = 0; fi < n; fi++)
        {
            p.DescriptorSets[fi] = Device.DescriptorAllocator.AllocateSet(Pipeline.SetLayout);
            WriteDescriptorSet(p, fi, instanceBones, morphDeltas);
        }
    }

    protected void RewriteDescriptorSets(PrimitiveData p)
    {
        if (p.DescriptorSets == null)
            return;

        var instanceBones = InstanceBoneBuffers;
        var morphDeltas = MorphDeltasBuffers;
        for (int fi = 0; fi < p.DescriptorSets.Length; fi++)
            WriteDescriptorSet(p, fi, instanceBones, morphDeltas);
    }

    /// <summary>Rewrite DescriptorSets for all shell primitives
    /// (both shared templates + both instance-box pools).
    /// After bone-buffer resize/grow, shell descriptor sets may still point to old buffers
    /// and read freed memory (plan risk 3), so they must be rewritten in the same batch
    /// as the main primitives.
    /// The binding 13/14/15 switch after prev SSBOs become ready is synchronized here as well.
    /// Safe when templates are not ready because RewriteDescriptorSets exits early internally.</summary>
    protected void RewriteShellDescriptorSets()
    {
        if (_shellGeometry != null)
            RewriteShellBoxDescriptorSets(_shellGeometry);
        if (_skinnedShellGeometry != null)
            RewriteShellBoxDescriptorSets(_skinnedShellGeometry);
        foreach (var box in _instanceShellBoxes)
            if (box != null)
                RewriteShellBoxDescriptorSets(box);
        foreach (var box in _skinnedInstanceShellBoxes)
            if (box != null)
                RewriteShellBoxDescriptorSets(box);
    }

    void RewriteShellBoxDescriptorSets(HighlightBox box)
    {
        RewriteDescriptorSets(box.Face);
        RewriteDescriptorSets(box.Edges);
    }

    void WriteDescriptorSet(PrimitiveData p, int fi, BufferResource[] instanceBones, BufferResource[] morphDeltas)
    {
        // Every descriptor write funnels through here, so this is the one place that catches a producer which reached
        // GPU state without the gate: AllocateAndWriteDescriptorSets and VKModel.CreateInstance both wrap their loops.
        Device.AssertGpuGateHeld("VKPrimitiveGroup.WriteDescriptorSet");

        // Rewrites happen from the render thread too (VKInstancedModel.Update on a bone capacity change rewrites every
        // slot), so never touch a set whose frame is still on the GPU; see Device.WaitForFrameSlot.
        Device.WaitForFrameSlot(fi);

        var matrixInfo = new DescriptorBufferInfo
        { Buffer = p.MatrixBuffers[fi].Buffer, Offset = 0, Range = Vk.WholeSize };
        var lightInfo = new DescriptorBufferInfo
        { Buffer = LightConstantBuffers[fi].Buffer, Offset = 0, Range = Vk.WholeSize };
        var materialInfo = new DescriptorBufferInfo
        { Buffer = p.MaterialBuffers[fi].Buffer, Offset = 0, Range = Vk.WholeSize };
        // Binding 3 is the retired b3 bone UBO slot: no shader declares it any more, but the write array must stay
        // contiguous, so it points at the shared default block.
        var placeholderInfo = new DescriptorBufferInfo
        { Buffer = Pipeline.DefaultTextDrawParamsBuffer.Buffer, Offset = 0, Range = Vk.WholeSize };
        var instanceBoneInfo = new DescriptorBufferInfo
        { Buffer = instanceBones[fi].Buffer, Offset = 0, Range = Vk.WholeSize };
        var morphInfo = new DescriptorBufferInfo
        {
            Buffer = p.MorphDeltasBuffer.Buffer.Handle != 0 ? p.MorphDeltasBuffer.Buffer : morphDeltas[fi].Buffer,
            Offset = 0,
            Range = Vk.WholeSize
        };
        var textDrawParamsInfo = new DescriptorBufferInfo
        { Buffer = Pipeline.DefaultTextDrawParamsBuffer.Buffer, Offset = 0, Range = Vk.WholeSize };

        var imgInfos = stackalloc DescriptorImageInfo[5];
        imgInfos[0] = new DescriptorImageInfo
        { ImageView = p.BaseColorTexture.View, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
        imgInfos[1] = new DescriptorImageInfo
        { ImageView = p.NormalTexture.View, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
        imgInfos[2] = new DescriptorImageInfo
        { ImageView = p.MetallicRoughnessTexture.View, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
        imgInfos[3] = new DescriptorImageInfo
        { ImageView = p.OcclusionTexture.View, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
        imgInfos[4] = new DescriptorImageInfo
        { ImageView = p.EmissiveTexture.View, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };

        var set = p.DescriptorSets[fi];
        // 3B: 18 writes -- the 16 prefix bindings plus 21 and the per-instance extras table. The six lighting
        // inputs that used to be 16..20/25 sit in the per-view globals set 3 now, so nothing here depends on this frame's
        // lighting state.
        var writes = stackalloc WriteDescriptorSet[18];
        writes[0] = MakeBufferWrite(set, 0, DescriptorType.UniformBuffer, &matrixInfo);
        writes[1] = MakeBufferWrite(set, 1, DescriptorType.UniformBuffer, &lightInfo);
        writes[2] = MakeBufferWrite(set, 2, DescriptorType.UniformBuffer, &materialInfo);
        writes[3] = MakeBufferWrite(set, 3, DescriptorType.UniformBuffer, &placeholderInfo);
        for (int i = 0; i < 5; i++)
            writes[4 + i] = MakeImageWrite(set, (uint)(4 + i), imgInfos + i);
        writes[9] = MakeBufferWrite(set, 9, DescriptorType.StorageBuffer, &instanceBoneInfo);
        writes[10] = MakeBufferWrite(set, 10, DescriptorType.StorageBuffer, &morphInfo);
        writes[11] = MakeBufferWrite(set, 11, DescriptorType.UniformBuffer, &textDrawParamsInfo);

        // 1-5: binding 12 shadow atlas.
        // ShadowMap is created during app initialization before any model is loaded
        // (same contract as WindowsApp), so it must be non-null when ShadowsEnabled is true.
        // The comparison sampler is immutable and already part of the layout,
        // so only DepthView is provided here.
        // When shadows are disabled, ShadowMap is null, so a placeholder must be supplied:
        // stackalloc memory is not zero-initialized,
        // and an unwritten writes[12] would contain stack garbage,
        // causing the WSL Vulkan driver to abort on an invalid descriptor.
        var fallback = Device.White;
        var shadowInfo = default(DescriptorImageInfo);
        if (FrameSchedule.ShadowMap is VKRenderTarget shadowRt && shadowRt.DepthView.Handle != 0)
        {
            shadowInfo = new DescriptorImageInfo
            { ImageView = shadowRt.DepthView, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
        }
        else
        {
            // Shadows disabled: fill binding 12 with the White placeholder texture
            // to avoid UB from stack garbage
            shadowInfo = new DescriptorImageInfo
            { ImageView = fallback.View, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
        }
        writes[12] = MakeImageWrite(set, 12, &shadowInfo);
        // 1-5 clause 15: binding 21 is the same view again, reached through the immutable point sampler so the blocker search
        // can read stored depth - binding 12's comparison sampler only ever yields pass/fail. Sharing shadowInfo is what makes
        // this a second descriptor rather than a second image, including the disabled-shadows placeholder case above.
        writes[16] = MakeImageWrite(set, 21, &shadowInfo);

        // 2-3 Step C: previous-frame SSBO data (binding 13/14/15).
        // Derived classes can override this to provide the actual prev buffers.
        // The base class returns default zero-value placeholders;
        // VKInstancedModel overrides them with the real prev bone/morph/instanceWorld SBs.
        var prevBoneInfo = GetPrevBoneBufferInfo(fi);
        var prevInstanceWorldInfo = GetPrevInstanceWorldBufferInfo(fi);
        var prevMorphWeightsInfo = GetPrevMorphWeightsBufferInfo(fi);
        writes[13] = MakeBufferWrite(set, 13, DescriptorType.StorageBuffer, &prevBoneInfo);
        writes[14] = MakeBufferWrite(set, 14, DescriptorType.StorageBuffer, &prevInstanceWorldInfo);
        writes[15] = MakeBufferWrite(set, 15, DescriptorType.StorageBuffer, &prevMorphWeightsInfo);

        // 3B: the six frame-level lighting inputs (bindings 16/17/18/19/20/25) are no longer written here.
        // They live in the per-view globals set 3 and are written once per view by Pipeline.PrepareViewGlobals,
        // which is what lets a primitive's set 0 stay untouched across frames and views.

        // B2: the 22/23/24 fog placeholders are gone as well. The transparent stage reads the fog trio from its own set 1
        // (Pipeline.BindFogSet), and no shader declares 22/23/24 in set 0, so those three writes bound nothing at all.

        // Generic per-instance extras (binding 26). The base returns the white placeholder for every non-instanced group;
        // a GPU-instanced group overrides it with its own per-frame extras ring, sized to its instance count.
        var extrasInfo = GetInstanceExtrasBufferInfo(fi);
        writes[17] = MakeBufferWrite(set, Pipeline.InstanceExtrasBinding, DescriptorType.StorageBuffer, &extrasInfo);

        Device.Vk.UpdateDescriptorSets(Device.LogicalDevice, 18, writes, 0, null);
    }

    /// <summary>2-3 Step C: derived classes may override this to return the prev bone SSBO
    /// for binding 13. The base class returns the default zero-value buffer.</summary>
    protected virtual DescriptorBufferInfo GetPrevBoneBufferInfo(int fi)
        => new() { Buffer = Pipeline.DefaultPrevBoneBuffers[fi].Buffer, Offset = 0, Range = Vk.WholeSize };

    /// <summary>2-3 Step C: derived classes may override this to return the prev instanceWorld SSBO
    /// for binding 14. The base class returns the default zero-value buffer.</summary>
    protected virtual DescriptorBufferInfo GetPrevInstanceWorldBufferInfo(int fi)
        => new() { Buffer = Pipeline.DefaultPrevInstanceWorldBuffers[fi].Buffer, Offset = 0, Range = Vk.WholeSize };

    /// <summary>2-3 Step C: derived classes may override this to return the prev morphWeights SSBO
    /// for binding 15. The base class returns the default zero-value buffer.</summary>
    protected virtual DescriptorBufferInfo GetPrevMorphWeightsBufferInfo(int fi)
        => new() { Buffer = Pipeline.DefaultPrevMorphWeightsBuffers[fi].Buffer, Offset = 0, Range = Vk.WholeSize };

    /// <summary>Generic per-instance extras SSBO for binding 26. The base class returns the shared white record, which is
    /// what a non-instanced draw binds: its VS never reads the table, but the descriptor must still be valid. A
    /// GPU-instanced group overrides this with the ring slot it uploaded for that frame.</summary>
    protected virtual DescriptorBufferInfo GetInstanceExtrasBufferInfo(int fi)
        => new() { Buffer = Pipeline.DefaultInstanceExtrasBuffers[fi].Buffer, Offset = 0, Range = Vk.WholeSize };

    static WriteDescriptorSet MakeBufferWrite(DescriptorSet set, uint binding, DescriptorType descriptorType, DescriptorBufferInfo* info)
    {
        return new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = set,
            DstBinding = binding,
            DstArrayElement = 0,
            DescriptorType = descriptorType,
            DescriptorCount = 1,
            PBufferInfo = info
        };
    }

    static WriteDescriptorSet MakeImageWrite(DescriptorSet set, uint binding, DescriptorImageInfo* info)
    {
        return new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = set,
            DstBinding = binding,
            DstArrayElement = 0,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            PImageInfo = info
        };
    }

    // ============================================================
    // Instance: sync path bindings
    // (the alpha / tint / material-parameter walks live in PrimitiveGroupCore)

    // ============================================================
    // Instance: material texture replacement
    // (derived classes provide the primitive list through CollectPrimitives)
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

    /// <summary>Replace the texture in the specified slot on all Primitives.
    /// The current implementation always uses the recreate path.</summary>
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
            // UploadPixels regenerates the whole chain from the policy the texture was created with, so the fast path
            // stays correct for mipmapped textures too.
            oldTex.UploadPixels(decoder.PixelSpan);
        }
        else
        {
            var newTex = Texture.CreateFromDecoder(decoder, MipPolicyForSlot(slot));
            foreach (var primitive in _drawList)
            {
                primitive.Texture(slot) = newTex;
                AllocateAndWriteDescriptorSets(primitive);
            }
        }
    }

    // ============================================================
    // Instance: derived hooks
    // ============================================================

    /// <summary>Derived classes append all PrimitiveData that need to be drawn in the current frame to result.</summary>
    protected abstract override void CollectPrimitives(List<PrimitiveData> result);

    /// <summary>
    /// Extra hook before Draw.
    /// VKModel overrides this to write bone matrices into the current frame's bone UBO.
    /// The default implementation is empty:
    /// VKMesh3D and other non-skinned primitive groups need no extra work here.
    /// </summary>
    protected virtual void OnBeforeDraw() { }

    // ============================================================
    // Unified highlighting: Vulkan resources and draw hooks
    // (the box lifetime, the shell geometry and the draw order live in
    // PrimitiveGroupCore.Highlight; only GPU objects are built here)
    // ============================================================

    /// <summary>Unified highlighting: attach a morph path to shell primitives.
    /// Source deltas are expanded by shell-vertex layout
    /// (the shell-vertex <-> source-vertex index mapping is recorded when the shell is built),
    /// then HasMorphTargets/MorphTargetCount/MorphVertexCount (= shell vertex count) are set
    /// and written back to the Material UBO for all frames.
    /// Weights are shared with the source and synchronized every frame by VKModel.ApplyMorphTargetsIfNeeded.
    /// DescriptorSet binding 10 (morph delta storage buffer) initially points to the default zero buffer
    /// through InitBoundsBoxGpuResources;
    /// after creating a custom delta buffer it must be rewritten
    /// because Vulkan descriptors are fixed at build time, unlike DX-style per-draw SRVs.</summary>
    protected override void AttachShellMorph(PrimitiveData shell, PrimitiveData source, IReadOnlyList<int> sourceIndices)
    {
        shell.MaterialParams.HasMorphTargets = 1;
        shell.MaterialParams.MorphTargetCount = source.MaterialParams.MorphTargetCount;
        shell.MaterialParams.MorphVertexCount = (uint)shell.Vertices.Count;
        for (int i = 0; i < Device.frameCount; i++)
            Unsafe.Write(shell.MappedMaterialBuffers[i], shell.MaterialParams);
        CreateMorphDeltaBuffer(shell, null!, source.MorphTargets!, sourceIndices);
        RewriteDescriptorSets(shell);
    }

    /// <summary>
    /// Phase 3: pack morph-target deltas into `storage buffer<float>` layout
    /// [targetIndex * vertexCount + vertexIndex] * 9 floats = pos.xyz + normal.xyz + tangent.xyz
    /// When vertexMap is non-null, expand by mapping
    /// (for shell-vertex layout: delta of shell vertex v = source delta[vertexMap[v]],
    /// vertex count = vertexMap.Count;
    /// source-primitive path without vertexMap = identity mapping, vertex count = baseVertices.Length).
    /// </summary>
    protected static void CreateMorphDeltaBuffer(PrimitiveData primitive, Vertex[] baseVertices, List<GLTFMorphTarget> morphTargets, IReadOnlyList<int>? vertexMap = null)
    {
        // The [target][vertex] × 9 layout, the sparse-target zero fill, and the shell vertex map all live in
        // MorphDelta.Pack, shared with DX/Metal/Web. Only the upload below is backend-specific.
        var deltaData = MorphDelta.Pack(baseVertices, morphTargets, vertexMap);
        int totalFloats = deltaData.Length;

        ulong size = (ulong)(sizeof(float) * totalFloats);
        primitive.MorphDeltasBuffer = Device.ResourceManager.CreateBuffer(
            size,
            BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

        void* mapped;
        if (Device.Vk.MapMemory(Device.LogicalDevice, primitive.MorphDeltasBuffer.Memory, 0, size, 0, &mapped) != Result.Success)
            throw new Exception("vkMapMemory (MorphDeltasBuffer) failed");

        fixed (float* src = deltaData)
            Unsafe.CopyBlock(mapped, src, (uint)size);

        Device.Vk.UnmapMemory(Device.LogicalDevice, primitive.MorphDeltasBuffer.Memory);
        primitive.OwnsMorphDeltasBuffer = true;
    }

    /// <summary>The core's template hook, typed to this backend's PrimitiveData. The factory keeps its own name
    /// because the existing call sites read the same way.</summary>
    protected override PrimitiveData CreateSharedGeometryPrimitive(PrimitiveData template)
        => CreateSharedShellPrimitive(template);

    /// <summary>Unified highlighting: derive a shared-geometry box from a template primitive
    /// by copying CPU-side vertex/index array references and material/texture references,
    /// while leaving owned GPU handles at default values.
    /// PrimitiveData.Dispose is null-safe, so there is no double free.
    /// Vertices/indices are immutable shared data and Dispose only releases GPU buffers,
    /// so aliasing is safe
    /// (DrawPrimitive reads Indices.Length each draw to determine index count).
    /// It creates its own N-buffered Matrix/Material UBOs,
    /// initializes all frames, and writes the DescriptorSet once.
    /// Matrices stay identity, and instanced drawing uses the instance-stream slot.</summary>
    PrimitiveData CreateSharedShellPrimitive(PrimitiveData template)
    {
        var primitive = new PrimitiveData
        {
            Vertices = template.Vertices,
            Indices = template.Indices,
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
        CreateMatrixBuffer(primitive);
        CreateMaterialBuffer(primitive);
        var matrices = new MatrixBuffer
        {
            World = Matrix4x4.Transpose(Matrix4x4.Identity),
            // 2-3 Step C (track C-a): per-instance previous world matrices already come from
            // the prev instance world SB,
            // so b0.PrevWorld stays all zero
            // because the instanced-path shader does not read b0 prevWorld.
        };
        for (int i = 0; i < Device.frameCount; i++)
        {
            Unsafe.Write(primitive.MappedMatrixBuffers[i], matrices);
            Unsafe.Write(primitive.MappedMaterialBuffers[i], primitive.MaterialParams);
        }
        AllocateAndWriteDescriptorSets(primitive);
        return primitive;
    }

    /// <summary>Unified highlighting (instanced): write face/edge dual colors plus current-frame matrices
    /// into the per-instance shell box's own N-buffered UBO
    /// (current frame fi only), and record this frame's face alpha.
    /// World stays identity
    /// (per-instance world matrices are addressed by instance-stream writeIndex slot),
    /// and View/Projection/PrevViewProjection come from the per-view set 2 (ViewConstants),
    /// so the shell always follows the current camera without per-box rewrites.
    /// PrevWorld stays all zero
    /// (2-3 Step C track C-a: instanced-path per-instance historical world matrices come from
    /// the prev instance world SB, and the shader does not read b0 prevWorld).</summary>
    protected void WriteInstanceShell(HighlightBox box, Vector4 faceColor, Vector4 edgeColor)
    {
        int fi = (int)Device.FrameIndex;
        var matrices = new MatrixBuffer
        {
            World = Matrix4x4.Transpose(Matrix4x4.Identity),
        };
        Unsafe.Write(box.Face.MappedMatrixBuffers[fi], matrices);
        Unsafe.Write(box.Edges.MappedMatrixBuffers[fi], matrices);
        box.Face.MaterialParams.BaseColor = faceColor;
        Unsafe.Write(box.Face.MappedMaterialBuffers[fi], box.Face.MaterialParams);
        box.Edges.MaterialParams.BaseColor = edgeColor;
        Unsafe.Write(box.Edges.MappedMaterialBuffers[fi], box.Edges.MaterialParams);
        box.FaceAlpha = faceColor.W;
    }

    /// <summary>Unified highlighting (instanced): draw one per-instance shell box
    /// with instanceCount=1 + startInstance slot.
    /// When face alpha (`SurfaceColor.W`) is > 0, draw faces with the double-sided transparent 2-pass rule
    /// (= 0 becomes edge-only automatically and skips faces);
    /// edges use Opaque (CullNone + depth write).
    /// Geometry comes from the shared template geo
    /// (_shellGeometry for the rigid pool, _skinnedShellGeometry for the skinned pool;
    /// the box itself does not own VB/IB, see CreateSharedShellPrimitive).
    /// For skinned shells, slot addressing through gl_InstanceIndex
    /// (including firstInstance) is automatically correct,
    /// so no extra base constant is needed on the draw side.</summary>
    protected void DrawInstanceShellBox(HighlightBox box, HighlightBox geo, BufferResource instanceBuffer, uint startInstance)
    {
        var cmd = Device.GraphicsCommandBuffer;
        int fi = (int)Device.FrameIndex;
        var face = box.Face;
        var edges = box.Edges;

        if (box.FaceAlpha > 0f)
        {
            Pipeline.SetPipeline(cmd, PipelineMode.Transparent, CullModeFlags.FrontBit);
            Pipeline.DrawPrimitive(cmd, face, geo.Face.VertexBuffer.Buffer, geo.Face.IndexBuffer.Buffer,
                face.DescriptorSets[fi], (uint)face.Indices.Length, instanceBuffer.Buffer, 1, startInstance);
            Pipeline.SetPipeline(cmd, PipelineMode.Transparent, CullModeFlags.BackBit);
            Pipeline.DrawPrimitive(cmd, face, geo.Face.VertexBuffer.Buffer, geo.Face.IndexBuffer.Buffer,
                face.DescriptorSets[fi], (uint)face.Indices.Length, instanceBuffer.Buffer, 1, startInstance);
        }

        Pipeline.SetPipeline(cmd, PipelineMode.Opaque, CullModeFlags.None, depthWrite: true);
        Pipeline.DrawPrimitive(cmd, edges, geo.Edges.VertexBuffer.Buffer, geo.Edges.IndexBuffer.Buffer,
            edges.DescriptorSets[fi], (uint)edges.Indices.Length, instanceBuffer.Buffer, 1, startInstance);
    }

    /// <summary>Unified highlighting (instanced): draw all instance shell boxes
    /// that enabled Wireframe in the current frame, box by box through DrawInstanceShellBox.
    /// instanceBuffer is the instance stream -
    /// either the base-class _instanceBuffer, or a per-primitive stream owned by a derived class
    /// such as VKInstancedModel.
    /// Slot layout is isomorphic (80B stride), so any such stream can be used.
    /// Mixed assets (rigid + skinned primitives) draw both shells:
    /// the same writeIndex is fetched once from the rigid pool and once from the skinned pool.</summary>
    protected void DrawShellBoxes(BufferResource instanceBuffer)
    {
        for (int i = 0; i < _shellBoxDrawList.Count; i++)
        {
            int idx = _shellBoxDrawList[i];
            if ((uint)idx < (uint)_instanceShellBoxes.Count)
            {
                var box = _instanceShellBoxes[idx];
                if (box != null && _shellGeometry != null)
                    DrawInstanceShellBox(box, _shellGeometry, instanceBuffer, (uint)idx);
            }
            if ((uint)idx < (uint)_skinnedInstanceShellBoxes.Count)
            {
                var box = _skinnedInstanceShellBoxes[idx];
                if (box != null && _skinnedShellGeometry != null)
                    DrawInstanceShellBox(box, _skinnedShellGeometry, instanceBuffer, (uint)idx);
            }
        }
    }

    /// <summary>Unified highlighting: build shell primitives from vertices/indices.
    /// Material parameters are copied from the source primitive, then forced to
    /// Unlit + transparent (BLEND) / opaque (OPAQUE),
    /// DoubleSided + five White textures (Unlit does not sample them).
    /// VB/IB/two UBOs are created and fully initialized through InitBoundsBoxGpuResources.
    /// Skinning and instancing flags such as IsSkinned/BonePaletteStride/IsInstanced
    /// are inherited from the source material,
    /// which is critical for non-instanced per-primitive shell boxes to stay perfectly aligned with skinned animation.</summary>
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
            // IB bit width must match the content detection in ResourceManager.CreateIndexBuffer
            // (store as 16-bit when all indices <= 65535).
            // Shell-face vertex count may be < 65536, so hardcoding 32-bit here would bind
            // a 16-bit IB as Uint32 and break indices, making faces invisible
            // (DX does not have this issue).
            Use32BitIndices = indices.Any(i => i > ushort.MaxValue),
            DoubleSided = true,
            IsTransparent = isTransparent,
            LocalBoundsCenter = Vector3.Zero,
            LocalBoundsExtents = new Vector3(0.5f),
            MaterialParams = mp,
        };
        InitBoundsBoxGpuResources(primitive);
        return primitive;
    }

    /// <summary>
    /// Unified highlighting (Bounds-box geometry): face PrimitiveData for the box.
    /// The 8 corner points are baked in [-0.5, 0.5]^3
    /// (corner-index bit encoding x + y*2 + z*4), with 36 indices.
    /// RenderMode=0 (Unlit) + AlphaMode=2 (BLEND) means truly transparent faces using the Transparent PSO,
    /// DoubleSided + five White textures (Use*Map=0, Unlit does not sample them).
    /// Geometry is reused from the shared HighlightGeometry layer
    /// and matches DX bit for bit.
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
    /// Unified highlighting (Bounds-box geometry): edge PrimitiveData for the box.
    /// Uses 12 thin boxes (3 axes x 4 edges per axis),
    /// extending one thickness past the corner points along the axis ([-0.5-h, 0.5+h])
    /// so all 8 corners connect seamlessly.
    /// RenderMode=0 + AlphaMode=0 (OPAQUE) means solid edges using the Opaque PSO with depth writes,
    /// independent from face-alpha pulsing
    /// (EdgeColor is always interpreted as solid).
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

    /// <summary>Unified highlighting: create VB/IB/Material UBO/Matrix UBO for box primitives,
    /// initialize all frames
    /// (to avoid reading garbage under N-buffering),
    /// and write the DescriptorSet once.</summary>
    void InitBoundsBoxGpuResources(PrimitiveData primitive)
    {
        primitive.VertexBuffer = Device.ResourceManager.CreateVertexBuffer(primitive.Vertices.ToArray());
        primitive.IndexBuffer = Device.ResourceManager.CreateIndexBuffer(primitive.Indices);
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

        AllocateAndWriteDescriptorSets(primitive);
    }

    /// <summary>
    /// Unified highlighting: each frame, write the box world matrix plus face/edge dual colors
    /// into the box's own N-buffered UBO (current frame fi only).
    /// There is no Changed gate:
    /// face alpha (`SurfaceColor.W`) pulses every frame, so the steady write cost matches normal primitives.
    /// PrevWorld comes from the CPU shadow copy (`box.PrevWorld`);
    /// after writing, the current-frame world is rolled into the shadow copy for the next frame.
    /// `world` is supplied by the caller
    /// (Bounds = Scale(Extents x 2) x Translate(Center), with degenerate-box checks done by the caller;
    /// Wireframe = model/instance world matrix, always valid).
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

    /// <summary>Unified highlighting: draw a single highlight box.
    /// When face alpha (`SurfaceColor.W`) is > 0, draw faces with the engine's double-sided transparent
    /// 2-pass rule (Front -> Back);
    /// when = 0, it becomes edge-only automatically and skips faces.
    /// Edges use Opaque (CullNone + depth writes),
    /// so the solid thin strips cover both faces and interior geometry.
    /// Bones/morph data are already bound through DescriptorSet,
    /// so OnBeforeDraw is not needed.</summary>
    protected override void DrawHighlightBox(HighlightBox box)
    {
        var face = box.Face;
        var edges = box.Edges;
        var cmd = Device.GraphicsCommandBuffer;

        if (box.FaceAlpha > 0f)
        {
            Pipeline.SetPipeline(cmd, PipelineMode.Transparent, CullModeFlags.FrontBit);
            DrawPrimitive(cmd, face);
            Pipeline.SetPipeline(cmd, PipelineMode.Transparent, CullModeFlags.BackBit);
            DrawPrimitive(cmd, face);
        }

        Pipeline.SetPipeline(cmd, PipelineMode.Opaque, CullModeFlags.None, depthWrite: true);
        DrawPrimitive(cmd, edges);
    }

    /// <summary>Unified highlighting: draw all instance boxes that enabled Bounds boxes in the current frame
    /// by calling DrawHighlightBox per box.</summary>
    protected void DrawBoundsBoxes()
    {
        for (int i = 0; i < _boundsBoxDrawList.Count; i++)
            DrawHighlightBox(_instanceBoundsBoxes[_boundsBoxDrawList[i]]);
    }

    // ============================================================
    // Core hooks
    // (the Opaque / Fade / Transparent flow and the shadow-pass cache live in PrimitiveGroupCore;
    // these bind it to Vulkan)
    // ============================================================

    protected override string TraceBackend => "vk";

    protected override int FrameCount => (int)Device.frameCount;

    protected override ref MaterialParams MaterialSlot(PrimitiveData primitive, int frame)
        => ref Unsafe.AsRef<MaterialParams>(primitive.MappedMaterialBuffers[frame]);

    /// <summary>Vulkan submits in collection order and never sorts the transparent bucket: a double-sided primitive
    /// is drawn front then back at its own position in the list.</summary>
    protected override TransparentOrder TransparentBucketOrder => TransparentOrder.Submission;

    protected override void ApplySurfaceState(in SurfaceState state)
    {
        // Explicit switches, never casts: PipelineMode orders Opaque/Transparent/Fade while SurfaceMode orders
        // Opaque/Fade/Transparent, so a cast would silently swap Fade and Transparent.
        Pipeline.SetPipeline(
            Device.GraphicsCommandBuffer,
            state.Mode switch
            {
                SurfaceMode.Opaque => PipelineMode.Opaque,
                SurfaceMode.Fade => PipelineMode.Fade,
                _ => PipelineMode.Transparent,
            },
            state.Cull switch
            {
                SurfaceCull.None => CullModeFlags.None,
                SurfaceCull.Front => CullModeFlags.FrontBit,
                _ => CullModeFlags.BackBit,
            },
            state.DepthWrite);
    }

    protected override void DrawMainPrimitive(PrimitiveData primitive)
        => DrawPrimitive(Device.GraphicsCommandBuffer, primitive);

    /// <summary>Pins the group on the snapshot page and refreshes the frame's bindings, both before the list is
    /// collected. These two calls used to open Draw.</summary>
    protected override void OnDrawBegin()
    {
        VKSceneDrawCollection.RetainOwner(this);
        OnBeforeDraw();
    }

    /// <summary>Same three groups of boxes as before, in the same order: the host Bounds box, then the per-instance
    /// Bounds boxes, then the per-primitive Wireframe shells.</summary>
    protected override void DrawHighlights()
    {
        if (_boundsActive && _boundsBox != null)
            DrawHighlightBox(_boundsBox);
        DrawBoundsBoxes();
        if (_wireframeEnabled && _wireframeBoxes != null)
        {
            for (int i = 0; i < _wireframeBoxes.Count; i++)
                DrawHighlightBox(_wireframeBoxes[i]);
        }
    }

    protected override void BeginShadowPass() => OnBeforeDraw();

    protected override void DrawShadowPrimitive(PrimitiveData primitive, bool bindMaterial)
        => DrawShadowPrimitive(Device.GraphicsCommandBuffer, primitive);

    /// <summary>Vulkan binds the material through each primitive's own descriptor set, so there is nothing a
    /// group-wide binding could share. Reporting false keeps every primitive on the same path.</summary>
    protected override bool CanShareShadowMaterial(List<PrimitiveData> primitives) => false;

    public abstract void Dispose();

    // ============================================================
    // Instance: per-primitive submit
    // (the Opaque / Fade / Transparent grouping lives in PrimitiveGroupCore)
    // ============================================================

    void DrawPrimitive(CommandBuffer cmd, PrimitiveData p)
    {
        int fi = (int)Device.FrameIndex;
        // Unified entry point: regular drawing uses instanceCount=1, instanceBuffer=default
        // and automatically falls back to IdentityInstanceBuffer
        Pipeline.DrawPrimitive(cmd, p, p.VertexBuffer.Buffer, p.IndexBuffer.Buffer,
            p.DescriptorSets[fi], (uint)p.Indices.Length, default, 1, 0);
    }

    // ============================================================
    // Instance: shadow-pass submit
    // (the epoch cache and the primitive loop live in PrimitiveGroupCore)
    // ============================================================

    void DrawShadowPrimitive(CommandBuffer cmd, PrimitiveData p)
    {
        int fi = (int)Device.FrameIndex;
        Pipeline.DrawShadowPrimitive(cmd, p, p.VertexBuffer.Buffer, p.IndexBuffer.Buffer,
            p.DescriptorSets[fi], (uint)p.Indices.Length, default, 1, 0);
    }

    // ============================================================
    // Outline2D mask-pass drawing
    // (called group by group from Graphics.RenderOutlineMask)
    // ============================================================

    /// <summary>
    /// Outline2D mask drawing (non-instanced):
    /// draw all opaque primitives and skip true BLEND materials.
    /// PSO/push constants (outline color) are already set uniformly by this method
    /// (Opaque + depthWrite=false, mirroring DX DrawOutlineMask's `depthWrite:false`).
    /// This path switches PSO per primitive according to DoubleSided
    /// and adds OnBeforeDraw (the binding-9 palette refresh) plus per-primitive draws.
    /// Mirrors DX DrawOutlineMask 1:1.
    /// </summary>
    public virtual void DrawOutlineMask()
    {
        if (!_transformInitialized || !_outline2DActive)
            return;

        _drawList.Clear();
        CollectPrimitives(_drawList);
        if (_drawList.Count == 0)
            return;

        DrawTrace.Group("vk", "outline", Name, _drawList.Count);

        // Write outline color per group through the FS push constant:
        // for multi-color cases in the same frame, use the group color fixed during Update
        var cmd = Device.GraphicsCommandBuffer;
        Pipeline.SetOutlineMaskColor(cmd, _outline2DColor);

        for (int i = 0; i < _drawList.Count; i++)
        {
            var p = _drawList[i];
            if (p.IsTransparent)
                continue;
            Pipeline.SetPipeline(cmd, PipelineMode.Opaque,
                p.DoubleSided ? CullModeFlags.None : CullModeFlags.BackBit, depthWrite: false);
            OnBeforeDraw();
            DrawPrimitive(cmd, p);
        }
    }
}
