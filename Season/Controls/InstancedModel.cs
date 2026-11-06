// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Controls;

/// <summary>
/// GPU instancing control for GLB models.
/// v2 supports skeletal animation and morph targets, with independent animation state per instance.
/// For the shared framework (instance collection / template-box bounding-sphere broad-phase culling /
/// shadow gating / Draw gating), see <see cref="InstancedMesh3DBase"/>.
/// </summary>
public partial class InstancedModel : InstancedMesh3DBase
{
    /// <summary>GLB model path, relative to `Raw/`.</summary>
    public string ModelName { get; init; } = string.Empty;

    /// <summary>Number of available animation clips, populated after loading.</summary>
    public int AnimationClipCount { get; internal set; }

    /// <summary>List of animation names, populated after loading.</summary>
    public IReadOnlyList<string> AnimationNames { get; internal set; } = Array.Empty<string>();

    /// <summary>
    /// Cross-platform animation data source, semantically aligned with <see cref="Model.Asset"/>.
    /// The platform LoadInstancedModel path injects the instantiated GltfAsset, whose node tree,
    /// animations, and bone palettes share the same source as instance rendering.
    /// Per-instance picking snapshots (InstancePickNodeWorlds/InstancePickBones) are written by the
    /// platform on every frame where that instance's pose is sampled, and left in place on a throttled
    /// frame, where they keep describing the pose still being rendered. This is null before loading and
    /// after disposal.
    /// </summary>
    internal Season.Models.GltfAsset Asset { get; set; }

    /// <summary>1-5 clause 12: reports the batch's pose generation for shadow-atlas reuse. This used to be the shared asset's
    /// animation clock, which was one value for the whole batch because every instance replayed the same clip from the same
    /// player; once P1 throttles sampling per instance, that clock only belongs to whichever instance was evaluated last, so it
    /// no longer describes the batch. The backend increments <see cref="AnimationPoseGeneration"/> on exactly the frames where at
    /// least one instance was evaluated -- the frames on which the set of rendered poses can change -- so throttled and held
    /// frames keep the cached atlas and any real motion redraws it. Per-instance transforms are folded in separately by
    /// <see cref="InstancedMesh3DBase.DrawShadow"/>.</summary>
    protected override int ShadowPoseKey => AnimationPoseGeneration;

    protected override bool HasContent => !string.IsNullOrEmpty(ModelName);

    /// <summary>
    /// P0: optional GPU-resident palette ring. When set and fully allocated, t6/t8 read it instead of the backend's CPU-written ring, and
    /// the backend stages the same rows into it so the two routes render identically. A producer (a compute effect) owns this ring: it
    /// writes the palette on the GPU and calls <see cref="InstanceBoneStorage.Publish(IGraphics, int)"/> to record the vertex-read handoff.
    /// The control only hands it to the backend every frame, and never allocates or disposes it; the backend may only grow it to the shape
    /// it can bind (see the type's ownership clause).
    /// <para>
    /// It is per model, not per backend, so an app sets it the same way on all four platforms and a platform that cannot bind storage into
    /// the vertex stage simply leaves it unused (see <see cref="Graphics.VertexShaderStorageReadSupported"/>).
    /// </para>
    /// </summary>
    public InstanceBoneStorage? BoneStorage { get; set; }

    /// <summary>
    /// P1: which route computes this model's bone palette. <see cref="InstanceSkinningMode.Cpu"/> is the default and is the historical
    /// path unchanged: the clock and the clip replay both run on the CPU, and one palette upload per frame carries the result across.
    /// <see cref="InstanceSkinningMode.Gpu"/> keeps only the clock on the CPU and lets the FrameStart kernel evaluate from the
    /// load-time bake.
    /// <para>
    /// It is a property rather than a global because it is a per-model decision: it pays only when every animated node is a joint and
    /// the primitive tree hangs off static nodes, and a model that fails that test falls back to the CPU no matter what this says. The
    /// environment variable <c>SEASON_INSTANCE_SKINNING=gpu|cpu</c> overrides it process-wide, which is what the pixel-for-pixel A/B
    /// comparison flips between runs.
    /// </para>
    /// <para>
    /// Set it in the object initializer, next to <see cref="Model.ModelName"/>: the bake is built once during <see cref="Load"/>, so
    /// assigning this afterwards selects nothing - the route already taken stands until the model is reloaded.
    /// </para>
    /// </summary>
    public InstanceSkinningMode SkinningMode { get; set; } = InstanceSkinningMode.Cpu;

    /// <summary>
    /// Part keys whose primitives take <see cref="MeshInstanceTransform.Color"/> as a multiplicative tint. A key matches a
    /// glTF material name first and the owning node name second, case-insensitively -- the same rule
    /// <see cref="Model.SetPartColor"/> uses.
    /// <para>
    /// Two things happen for a matching primitive at load, and both are required for the color to be usable. First, its
    /// authored base-color RGB is rewritten to white, because the tint multiplies: a saturated authored factor (this
    /// asset's sail is 0.97/0.93/0.0003) can only be darkened by a multiply, never turned to another hue. Second, the
    /// primitive's material constant carries <c>TintMode = 1</c>, which is what lets the pixel shader apply the color
    /// there and nowhere else -- the hull, glass, steel and deck paint of the same instance keep their authored look.
    /// </para>
    /// <para>
    /// Empty by default, so an existing InstancedModel is untouched: every primitive keeps <c>TintMode = 0</c> and the
    /// pixel shader never reads the per-instance color.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> InstanceTintParts { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Whether <paramref name="partName"/> / <paramref name="nodeName"/> names a part in <see cref="InstanceTintParts"/>.
    /// The material name wins, the node name is the fallback, and both are compared case-insensitively -- the same
    /// precedence <see cref="Model.TryGetPartColor"/> resolves with, so one asset can be described by either key.
    /// </summary>
    internal bool IsInstanceTintPart(string? partName, string? nodeName)
    {
        var keys = InstanceTintParts;
        for (int i = 0; i < keys.Count; i++)
        {
            string key = keys[i];
            if (string.IsNullOrEmpty(key))
                continue;
            if (string.Equals(key, partName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, nodeName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Whether this model may take the GPU route this frame. Three things have to agree: the backend compiled the kernel
    /// (<see cref="InstanceSkinning.Enabled"/>), the effective mode asks for it, and the asset actually produced a bake. The last one
    /// is where "every animated node is a joint" and the table size ceiling are enforced, once, at load time.
    /// </summary>
    internal bool GpuSkinning
        => InstanceSkinning.Enabled
        && (InstanceSkinning.Override ?? SkinningMode) == InstanceSkinningMode.Gpu
        && Asset?.SkinBake != null;

    Bounds3D _templateCullBounds;

    /// <summary>
    /// A7: model-space box the GPU instance cull tests against each instance's root world - <see cref="InstancedMesh3DBase.TemplateLocalBounds"/>
    /// united with every pose the clips reach (see <c>GltfAsset.ComputeClipUnionBounds</c>). It is kept apart from the template box
    /// because the two decide different things: the template box gates whole-batch submission and picking, where a slightly tight
    /// box costs nothing visible, while this one removes individual instances, where a limb outside it is a limb that vanishes.
    /// Filled once by each backend's LoadInstancedModel; until then (or for an asset without clips or pick meshes) it reads as the
    /// template box, so a cull that runs before loading finishes never tests a zero box and drops everything.
    /// </summary>
    internal Bounds3D TemplateCullBounds
    {
        get => _templateCullBounds.Extents == Vector3.Zero ? TemplateLocalBounds : _templateCullBounds;
        set => _templateCullBounds = value;
    }

    /// <summary>
    /// Load-time handoff of the three template boxes every backend consumes. Each platform used to repeat the same
    /// three assignments (carrying the same 1-3 comment); they live here so the contract has one source of truth.
    /// <paramref name="cullBounds"/> is the caller's clip-union expansion over
    /// <c>templateAsset.Model.LocalBounds</c> (see <see cref="Season.Models.GltfAsset.ComputeClipUnionBounds"/>), which
    /// has to be computed from the instance's own asset because it samples poses on it.
    /// </summary>
    internal void AdoptTemplateBounds(Season.Models.GltfAsset templateAsset, Bounds3D cullBounds)
    {
        TemplateLocalBounds = templateAsset.Model.LocalBounds;
        TemplateLocalBoundsRaw = templateAsset.Model.LocalBoundsRaw;
        TemplateCullBounds = cullBounds;
    }

    public override async Task<bool> Load()
    {
        await Graphics.Instance.LoadInstancedModel(this);

        return true;
    }

    public bool Update(float time, float? alpha = null)
    {
        var result = base.Update(time, alpha: alpha);

        // Latched before the backend call so the renderer and the history flags below agree on which frame submitted streams.
        bool submitting = Ready && HasContent && Enable;
        RefreshInstanceHistory(submitting);

        if (submitting)
        {
            // Unified positioning model: settle per-instance default dimensions
            // (zero dimension means template-local size) before each backend calls
            // UpdateInstancedModel, after matrices have already converged in BuildInstanceMatrix.
            for (int i = 0; i < Instances.Count; i++)
                SettleInstanceDimensions(Instances[i]);

            // P1: start the throttling frame immediately before the backend replays the batch. The enabled count is what
            // forces a full-rate frame after an enable/disable change, and the frame delta is what the static hold reads.
            int enabledCount = 0;
            for (int i = 0; i < Instances.Count; i++)
            {
                if (Instances[i].Enable)
                    enabledCount++;
            }

            BeginAnimationLodFrame(enabledCount, time);

            // P2: composes every instance's root world once and decides which ones skip their pose evaluation. It has to run
            // after the throttle frame (it stamps itself with that frame) and before the backend replays the batch, which is
            // what reads the verdicts back.
            BeginInstanceCullFrame();

            Graphics.Instance.UpdateInstancedModel(this, time);
        }

        return result;
    }

    /// <summary>
    /// Per-instance surface-accurate picking at v2 mesh granularity:
    /// broad-phase template-box culling followed by per-node PickMesh ray-triangle tests.
    /// Skinned hit candidates are skinned on the fly using the instance bones. The DX backend uses
    /// per-instance snapshots (node worlds + bone palette), matching rendering exactly bit for bit.
    /// Other backends fall back to the current-frame bones from the shared animation player,
    /// which is exact for static hosts and approximate for animated hosts, by documented boundary.
    /// When Asset has not been injected or there is no PickMesh data at all, the base class falls back to OBB.
    /// If data exists but the narrow phase misses, that is a real miss: empty template space is not selected,
    /// and overlapping instances are resolved by nearest surface distance, meaning the one closest to the screen wins.
    /// </summary>
    public override bool TryPickInstanceSurface(Vector3 rayOrigin, Vector3 rayDirection, out MeshInstanceTransform hit, out float distance)
    {
        hit = null;
        distance = 0f;

        if (Asset == null)
            return base.TryPickInstanceSurface(rayOrigin, rayDirection, out hit, out distance);

        if (!Ready || !Enable || TemplateLocalBounds.Extents == Vector3.Zero)
            return false;

        bool hasShadow = Asset.InstancePickNodeWorlds.Length > 0;
        bool hasData = false;
        float bestDistance = float.MaxValue;

        for (int i = 0; i < Instances.Count; i++)
        {
            var instance = Instances[i];
            if (!instance.Enable)
                continue;

            var instanceWorld = BuildInstanceMatrix(instance);
            if (!Picking.RayIntersectsObb(rayOrigin, rayDirection, instanceWorld, TemplateLocalBounds, out _))
                continue;

            var nodeWorlds = hasShadow ? Asset.InstancePickNodeWorlds[i] : null;
            var instanceBones = hasShadow ? Asset.InstancePickBones[i] : null;

            for (int n = 0; n < Asset.gltfNodes.Count; n++)
            {
                var node = Asset.gltfNodes[n];
                var meshes = node.PickMeshes;
                if (meshes.Count == 0)
                    continue;

                for (int m = 0; m < meshes.Count; m++)
                {
                    var mesh = meshes[m];
                    if (mesh.Positions.Length < 3 || mesh.Indices.Length < 3)
                        continue;

                    hasData = true;

                    if (mesh.IsSkinned)
                    {
                        int paletteOffset = Asset.GetSkinPaletteOffset(node.Skin);
                        if (paletteOffset < 0)
                            continue;

                        // Per-instance bones: prefer the snapshot path for exact DX behavior;
                        // otherwise fall back to the shared player's current frame, which is approximate on other backends.
                        var bones = instanceBones;
                        if (bones == null)
                        {
                            bones = Asset._animationPlayer.GetBoneMatricesArray();
                            if (bones.Length == 0)
                                continue;
                        }

                        // Skinned mesh space is node-local. The world matrix shares the same source as rendering:
                        // evaluated per-instance node world multiplied by the instance matrix.
                        var skinWorld = (nodeWorlds != null ? nodeWorlds[n] : node.WorldTransform) * instanceWorld;
                        var scratch = ArrayPool<Vector3>.Shared.Rent(mesh.Positions.Length);
                        try
                        {
                            Picking.SkinPositions(mesh.Positions, mesh.Joints, mesh.Weights, bones, paletteOffset,
                                scratch.AsSpan(0, mesh.Positions.Length));

                            if (Picking.RayIntersectsTriangles(rayOrigin, rayDirection, skinWorld,
                                    scratch.AsSpan(0, mesh.Positions.Length), mesh.Indices, out var d)
                                && d < bestDistance)
                            {
                                bestDistance = d;
                                hit = instance;
                            }
                        }
                        finally
                        {
                            ArrayPool<Vector3>.Shared.Return(scratch);
                        }
                    }
                    else
                    {
                        var world = (nodeWorlds != null ? nodeWorlds[n] : node.WorldTransform) * instanceWorld;
                        if (Picking.RayIntersectsTriangles(rayOrigin, rayDirection, world, mesh.Positions, mesh.Indices, out var d)
                            && d < bestDistance)
                        {
                            bestDistance = d;
                            hit = instance;
                        }
                    }
                }
            }
        }

        if (!hasData)
            return base.TryPickInstanceSurface(rayOrigin, rayDirection, out hit, out distance);

        if (hit != null)
            distance = bestDistance;

        return hit != null;
    }

    public override bool Draw()
    {
        var result = false;

        if (base.Draw())
        {
            Graphics.Instance.DrawInstancedModel(this);

            result = true;
        }

        return result;
    }

    protected override void DrawShadowCore() => Graphics.Instance.DrawInstancedModelShadow(this);

    public override void Dispose()
    {
        base.Dispose();
        Graphics.Instance.DisposeInstancedModel(this);
    }
}
