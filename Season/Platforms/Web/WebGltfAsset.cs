// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using SharpGLTF.Schema2;
using SharpGLTF.Validation;
using Season.Models;
using SharpGLTF.Runtime;

namespace Season.Platforms.Web;

internal class WebGltfAsset : GltfAsset
{
    byte[] _glbBytes;

    public void SetGlbBytes(byte[] bytes) { _glbBytes = bytes; }

    public override void Load(Season.Controls.Model model, Season.Basic.Camera camera)
    {
        Model = model;
        var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();

        using var stream = new MemoryStream(_glbBytes);
        var readGlbStopwatch = System.Diagnostics.Stopwatch.StartNew();
        // Keep Web behavior aligned with native loaders: tolerate non-critical glTF validation issues
        // such as non-normalized skin weights, and let the runtime normalize/consume the data.
        var glb = ModelRoot.ReadGLB(stream,
            new ReadSettings()
            {
                Validation = ValidationMode.Skip
            });

        var sceneRootNodes = glb.LogicalNodes
            .Where(n => n.VisualParent == null && n.VisualScenes.Contains(glb.DefaultScene))
            .ToList();

        var boundsStopwatch = System.Diagnostics.Stopwatch.StartNew();
        // Model rest bounds: use the same ComputeRestBounds path as the other four backends
        // (rest pose + replayed skin rest pose + initial morph-weight deltas, without animation sampling).
        // The old EvaluateBoundingBox path sampled animated tracks at 1-second intervals across the full
        // timeline and unioned the results, which expanded the raw bounds with morph motion ranges.
        // That made LocalSize too large, so the same Width/Height/Depth ended up visibly smaller on Web than
        // on DX/VK (confirmed with the MorphStressTest bee case, 2026-08).
        ComputeRestBounds(glb, out var min, out var max, out _);

        model.Size = max - min;
        model.OriginalScale = 1 / new float[] { model.Size.X, model.Size.Y, model.Size.Z }.Max();

        var processNodesStopwatch = System.Diagnostics.Stopwatch.StartNew();
        foreach (var node in sceneRootNodes)
        {
            ProcessNode(node, Matrix4x4.Identity, camera, model);
        }
        BindSkins(glb);
        int primitiveCount = 0;
        foreach (var nodeBase in gltfNodes)
        {
            if (nodeBase is WGPUGLTFNode webNode)
                primitiveCount += webNode.Primitives.Count;
        }
        int rootNodeCount = sceneRootNodes.Count;

        var animationStopwatch = System.Diagnostics.Stopwatch.StartNew();
        LoadAnimations(glb);
        _animationPlayer.Initialize(_animations);
        int animationChannels = _animations.Sum(a => a.Channels.Count);

        // 1-3: control-level local bounds, computed once at load time with the same logic as shared-layer GltfAsset.Load.
        // For RH→LH conversion, negate center.Z while keeping extents symmetric. Conservatively enlarge when animation exists (Contract Clause 2).
        var localBounds = Season.Rendering.Bounds3D.FromMinMax(min, max);
        localBounds.Center.Z = -localBounds.Center.Z;
        // Unified transform convention: keep the raw bounds, before conservative animation expansion,
        // for anchor and per-axis scaling. The setter triggers OnBoundsEstablished
        // (where the default size is finalized), so this must happen after Size/OriginalScale.
        model.LocalBoundsRaw = localBounds;
        if (_animations.Count > 0)
            localBounds = localBounds.Scaled(RenderQuality.Current.AnimatedBoundsScale);
        model.LocalBounds = localBounds;

        // 1-2: align with shared-layer GltfAsset.Load by passing the KHR imported lights collected in
        // ProcessNode back to Model. Otherwise, Web-side CreateInstance would still receive an empty list.
        model.ImportedPunctualLights = ImportedLights;
    }
}
