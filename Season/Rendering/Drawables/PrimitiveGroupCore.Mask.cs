// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering.Drawables;

/// <summary>
/// The instanced Outline2D mask pass, shared by the generated-geometry group and the glTF model.
///
/// The two differ in one place only: where the instance stream comes from. A generated group owns a single
/// group-wide stream, while a glTF model owns one stream per primitive — its primitives carry independent
/// bone palettes and morph weights — so it resolves the stream through <c>GetPrimitiveInstanceStream</c>
/// and skips the primitives whose stream is not resident this frame. Everything else is identical, and is
/// what this partial holds: the host-wide batch versus the per-instance fallback, the slot-range guard,
/// and the two-pass walk over the opaque bucket.
///
/// What stays backend code is the submit itself. D3D12 takes a stack <c>VertexBufferView*</c> plus four
/// previous-frame SRV handles, Vulkan a <c>VkBuffer</c> plus a descriptor set and a per-slot push constant,
/// Metal an encoder plus offsets into both the instance and the bone streams. Those three signatures share
/// no argument in common, so the hooks below are the narrowest seam that still removes the duplicated loop.
/// </summary>
internal abstract partial class PrimitiveGroupCore<TPrim, TMat, TTex>
    where TPrim : PrimitiveCpuData<TMat, TTex>, IDisposable
    where TMat : unmanaged, IMaterialFields
    where TTex : class
{
    /// <summary>
    /// Walks the opaque bucket and submits the mask. Host-wide activation covers the whole batch in one
    /// submit; otherwise each listed instance is submitted on its own. Transparent buckets are skipped,
    /// matching the non-instanced mask semantics.
    /// </summary>
    protected void DrawInstancedOutlineMask()
    {
        if (!_transformInitialized || _instanceCount == 0 || !Outline2DActive)
            return;

        OnBeginOutlineMaskPass();

        for (int i = 0; i < _opaquePrimitives.Count; i++)
        {
            var primitive = _opaquePrimitives[i];

            // The backend decides whether the primitive has a usable stream and binds the mask pipeline here.
            // A transient "no" (a glTF primitive whose stream is not resident) is a skip, not an early return.
            if (!TryBeginOutlineMaskPrimitive(primitive))
                continue;

            if (_outline2DHostActive)
            {
                DrawOutlineMaskBatch(primitive);
                continue;
            }

            for (int k = 0; k < _outline2DInstances.Count; k++)
            {
                int instanceIndex = _outline2DInstances[k];

                // The list is rebuilt during Update, so it can outlive a shrinking batch by one frame.
                if ((uint)instanceIndex >= (uint)_instanceCount)
                    continue;

                DrawOutlineMaskInstance(primitive, instanceIndex, k);
            }
        }
    }

    /// <summary>
    /// Writes the group-wide mask colour, plus anything the backend binds once per pass. Metal also primes
    /// the morph fallback and the instance bone buffers here; DX and Vulkan write only the colour.
    /// The hooks are virtual rather than abstract because the non-instanced groups derive from this same
    /// class and never take the mask through an instance stream; reaching them means a subclass called
    /// <see cref="DrawInstancedOutlineMask"/> without supplying the backend half.
    /// </summary>
    protected virtual void OnBeginOutlineMaskPass() => throw MissingOutlineMaskHooks();

    /// <summary>
    /// Resolves the primitive's instance stream and binds the mask pipeline. Returning false skips the
    /// primitive without submitting anything.
    /// </summary>
    protected virtual bool TryBeginOutlineMaskPrimitive(TPrim primitive) => throw MissingOutlineMaskHooks();

    /// <summary>One submit covering every instance of the primitive.</summary>
    protected virtual void DrawOutlineMaskBatch(TPrim primitive) => throw MissingOutlineMaskHooks();

    /// <summary>
    /// One submit covering a single instance. <paramref name="slot"/> indexes the parallel per-instance
    /// colour list, which is what a multi-colour mask fetches slot by slot.
    /// </summary>
    protected virtual void DrawOutlineMaskInstance(TPrim primitive, int instanceIndex, int slot)
        => throw MissingOutlineMaskHooks();

    private NotSupportedException MissingOutlineMaskHooks()
        => new($"{GetType().Name} called DrawInstancedOutlineMask without implementing its backend hooks.");
}
