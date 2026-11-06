// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering.Drawables;

/// <summary>
/// The instanced main pass, shared by the generated-geometry group and the glTF model.
///
/// The skeleton is the same on all three backends and on both shapes of source data: bind the
/// group-invariant state once, walk the opaque bucket, then walk the transparent bucket in
/// back-to-front order drawing each instance twice when the material is double sided, and finally
/// paint the highlight overlays. Fade-vs-opaque is the only branch left in the loop and it is decided
/// once per pass, because the whole group shares one alpha.
///
/// What stays backend code:
/// <list type="bullet">
///   <item>the submit itself — D3D12 takes a stack <c>VertexBufferView*</c>, Vulkan a <c>VkBuffer</c>
///   plus a descriptor set, Metal an encoder plus instance and bone buffer offsets;</item>
///   <item>the indirect-versus-direct decision, because each backend asks its own cull resources
///   (<c>TryGetMainViewSegment</c>) and each spells the segment offset differently. A generated group
///   always queries stream 0; a glTF model queries the primitive's own stream;</item>
///   <item>the transparent sort key: Vulkan and Metal pass the per-instance depth to their collectors
///   while D3D12 has none to pass, and only the per-primitive shape has to be told which world array
///   to sort from;</item>
///   <item>the highlight geometry, because the boxes are built from backend primitives.</item>
/// </list>
/// </summary>
internal abstract partial class PrimitiveGroupCore<TPrim, TMat, TTex>
    where TPrim : PrimitiveCpuData<TMat, TTex>, IDisposable
    where TMat : unmanaged, IMaterialFields
    where TTex : class
{
    /// <summary>
    /// Runs the main pass. The transparent bucket is ordered from the world matrices the caller
    /// resolves, because a generated group keeps its worlds in <see cref="_instanceWorlds"/> while a
    /// glTF model keeps one array per primitive stream.
    /// </summary>
    protected void DrawInstancedMainPass()
    {
        if (!_transformInitialized || _instanceCount == 0)
            return;

        OnBeginMainPass();

        bool forceFadeByAlpha = _currentAlpha < 1.0f;

        for (int i = 0; i < _opaquePrimitives.Count; i++)
        {
            var primitive = _opaquePrimitives[i];

            // A glTF primitive whose per-primitive stream is not resident submits nothing this frame.
            if (!TryBeginInstancePrimitive(primitive))
                continue;

            DrawOpaqueInstances(primitive, i, forceFadeByAlpha);
        }

        for (int i = 0; i < _transparentPrimitives.Count; i++)
        {
            var primitive = _transparentPrimitives[i];
            if (!TryBeginInstancePrimitive(primitive))
                continue;

            Matrix4x4[]? worlds = ResolveTransparentWorlds(primitive);
            if (worlds == null)
                continue;

            BuildTransparentInstanceOrder(primitive, worlds);

            for (int orderIndex = 0; orderIndex < _transparentInstanceOrder.Count; orderIndex++)
            {
                int instanceIndex = _transparentInstanceOrder[orderIndex];

                // A double-sided material is split the same way on every backend: the pass that culls
                // front faces runs first, then the one that culls back faces. Keeping that order here
                // is what makes the split a single ordered pair instead of a backend choice.
                if (primitive.DoubleSided)
                    DrawTransparentInstance(primitive, instanceIndex, frontFace: true);

                DrawTransparentInstance(primitive, instanceIndex, frontFace: false);
            }
        }

        if (_boundsActive)
            DrawInstanceBoundsBoxes();
        if (_wireframeActive)
            DrawInstanceShellBoxes();
    }

    /// <summary>
    /// Publishes the group-invariant state once per pass: DX nothing beyond the trace group, Vulkan
    /// also pins the group on the snapshot page so replay can still reach its buffers, Metal nothing
    /// beyond the trace group. A glTF model has never traced this pass.
    /// </summary>
    protected virtual void OnBeginMainPass()
    {
    }

    /// <summary>
    /// The world array the transparent bucket sorts from. A generated group keeps one group-wide copy;
    /// a glTF model answers with the primitive's own stream so each node sorts against its own nodes.
    /// Returning null skips the primitive, which mirrors the "stream not resident" guard.
    /// </summary>
    protected virtual Matrix4x4[]? ResolveTransparentWorlds(TPrim primitive) => _instanceWorlds;

    /// <summary>
    /// Draws every instance of one opaque primitive, switching to the fade pipeline when the group alpha
    /// has dropped below one. <paramref name="streamIndex"/> is the primitive's index in the opaque
    /// bucket, which is the gather stream a glTF model owns and the <c>(MainViewSlot, primitive)</c> args
    /// row every backend addresses.
    /// </summary>
    protected virtual void DrawOpaqueInstances(TPrim primitive, int streamIndex, bool fadeInsteadOfOpaque)
        => throw new NotSupportedException(
            $"{GetType().Name} called DrawInstancedMainPass without implementing DrawOpaqueInstances.");

    /// <summary>
    /// Submits a single instance of a transparent primitive with one face culled. Called twice for a
    /// double-sided material, once per face; each backend owns the sort key it hands to its collector.
    /// </summary>
    protected virtual void DrawTransparentInstance(TPrim primitive, int instanceIndex, bool frontFace)
        => throw new NotSupportedException(
            $"{GetType().Name} called DrawInstancedMainPass without implementing DrawTransparentInstance.");

    /// <summary>Per-instance bounds boxes, drawn after every surface once the group has them enabled.</summary>
    protected virtual void DrawInstanceBoundsBoxes()
        => throw new NotSupportedException(
            $"{GetType().Name} called DrawInstancedMainPass without implementing DrawInstanceBoundsBoxes.");

    /// <summary>Wireframe shell boxes, addressed by the instance-stream slot after every surface.</summary>
    protected virtual void DrawInstanceShellBoxes()
        => throw new NotSupportedException(
            $"{GetType().Name} called DrawInstancedMainPass without implementing DrawInstanceShellBoxes.");
}
