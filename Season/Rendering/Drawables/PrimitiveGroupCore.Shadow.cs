// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering.Drawables;

/// <summary>
/// The instanced shadow pass, shared by the generated-geometry group and the glTF model.
///
/// The skeleton is the same on all three backends: bind the group-invariant state once, decide whether the
/// shadow material can be shared, then walk the opaque bucket and submit each primitive with the material
/// bound only on the first real submit. Transparent primitives are skipped because true BLEND materials do
/// not cast shadows under contract clause 7.
///
/// Two things stay backend code:
/// <list type="bullet">
///   <item>the submit itself — D3D12 takes a stack <c>VertexBufferView*</c>, Vulkan a <c>VkBuffer</c> plus a
///   descriptor set, Metal an encoder plus instance and bone buffer offsets;</item>
///   <item>the indirect-versus-direct decision, because each backend asks its own cull resources
///   (<c>TryGetShadowSegment</c>) and each spells the args offset differently. A generated group always
///   queries stream 0; a glTF model queries the primitive's own stream.</item>
/// </list>
/// </summary>
internal abstract partial class PrimitiveGroupCore<TPrim, TMat, TTex>
    where TPrim : PrimitiveCpuData<TMat, TTex>, IDisposable
    where TMat : unmanaged, IMaterialFields
    where TTex : class
{
    /// <summary>
    /// Walks the opaque bucket for the shadow pass. The material bind is shared across the group when the
    /// backend reports that every primitive carries the same shadow material, which is why the flag advances
    /// only after a real submit: a skipped primitive would otherwise consume the one bind for the whole group.
    /// </summary>
    protected void DrawInstancedShadowPass()
    {
        if (!_transformInitialized || _instanceCount == 0)
            return;

        OnBeginShadowPass();

        bool shareMaterial = CanShareShadowMaterial(_opaquePrimitives);
        bool materialBound = false;

        for (int i = 0; i < _opaquePrimitives.Count; i++)
        {
            var primitive = _opaquePrimitives[i];

            // A glTF primitive whose per-primitive stream is not resident submits nothing this frame.
            if (!TryBeginInstancePrimitive(primitive))
                continue;

            DrawShadowInstance(primitive, i, !shareMaterial || !materialBound);
            materialBound = true;
        }
    }

    /// <summary>
    /// Binds the group-invariant shadow state once per pass: DX publishes the four previous-frame SRVs here,
    /// Vulkan refreshes binding 9, and Metal binds nothing beyond the PSO the shadow pass already set. A
    /// generated group also writes its trace group here; a glTF model has never traced this pass.
    /// </summary>
    protected virtual void OnBeginShadowPass()
    {
    }

    /// <summary>
    /// Resolves the primitive's instance stream. Returning false skips it without submitting, which is what a
    /// glTF model does for a primitive whose stream is not resident this frame. The main pass and the shadow
    /// pass ask exactly the same question, so they share this one hook rather than each carrying a copy.
    /// </summary>
    protected virtual bool TryBeginInstancePrimitive(TPrim primitive) => true;

    /// <summary>
    /// Submits one primitive for every instance, taking the indirect route when this frame's cull chain covers
    /// it. <paramref name="streamIndex"/> is the primitive's index in the opaque bucket, which is the gather
    /// stream a glTF model owns and the <c>(slot, primitive)</c> args row every backend addresses.
    /// </summary>
    protected virtual void DrawShadowInstance(TPrim primitive, int streamIndex, bool bindMaterial)
        => throw new NotSupportedException(
            $"{GetType().Name} called DrawInstancedShadowPass without implementing its backend hooks.");
}
