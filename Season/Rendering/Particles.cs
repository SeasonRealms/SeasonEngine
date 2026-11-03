// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

/// <summary>
/// A0.1 second half: layout of one GPU-written particle draw record, and the only thing the vertex
/// shader is allowed to assume about the instance buffer.
///
/// The record is deliberately flat and 16-byte aligned so that every backend can read it as a raw
/// (ByteAddressBuffer / typeless SSBO) buffer. Bindings carry no stride metadata on any of the four
/// backends, so a structured view would be a D3D12-only luxury that the other three cannot match.
///
/// Producers: the compaction kernel appends records at the slot returned by an atomic add on
/// <see cref="IndirectDrawIndexedArgs.InstanceCount"/>, so the live prefix of the buffer is exactly
/// as long as the instance count the GPU wrote. Nothing reads the tail, and nothing clears it.
///
/// A6 groundwork: the record carries the *observable products* of the simulation rather than its
/// inputs. Acceleration and lifetime are per-system constants and therefore live in the kernel's
/// Params block; what the vertex shader actually needs is the resulting velocity (to shear the quad
/// into a motion streak) and the normalized age (to fade in and out). Handing a vertex shader the
/// acceleration instead would tell it nothing it could use.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct ParticleInstance
{
    /// <summary>Stride in bytes. Shared by the C# allocation size and the shader addressing of every backend, so it cannot drift.</summary>
    public const uint SizeInBytes = 48;

    /// <summary>World-space center of the billboard.</summary>
    public Vector3 Position;

    /// <summary>Half-extent of the billboard in world units, applied along the camera right/up basis.</summary>
    public float Size;

    /// <summary>Linear RGB plus coverage. Alpha is pre-multiplied in the pixel shader, not here.</summary>
    public Vector4 Color;

    /// <summary>
    /// World-space velocity in units per second at this particle's current age, including the
    /// acceleration term. Consumed by the vertex shader to shear the billboard along the direction
    /// of travel; it is deliberately *not* written to the velocity attachment, which particles leave
    /// untouched so that opaque motion vectors survive underneath them.
    /// </summary>
    public Vector3 Velocity;

    /// <summary>Age as a fraction of the particle's lifetime, in [0,1). Drives the fade-in / fade-out curve.</summary>
    public float Age;
}

/// <summary>
/// Shared constants of the particle draw path. They are contract, not tuning: the simulation kernel,
/// the shared layer allocation and all four backend pipelines must agree on them byte for byte.
/// </summary>
public static class ParticleDraw
{
    /// <summary>
    /// Per-frame constants read by both the compaction kernel and the vertex shader:
    /// rows 0..3 are the unjittered view-projection in System.Numerics row order,
    /// row 4 is the camera right basis, row 5 the camera up basis, row 6 the camera position,
    /// row 7 is fadeIn, fadeOut, stretch, viewDistance (0 means no distance limit).
    /// Kept as one buffer rather than a Params block because the vertex shader has no access to
    /// compute push constants.
    ///
    /// The lifetime fade in/out of row 7 is consumed by the vertex stage; the stretch cull margin and
    /// the near-distance fade-out are consumed by compaction. Reusing the frame buffer avoids spending
    /// the cross-backend compute Params, whose limit is 128B.
    ///
    /// Rows 8..11 are the appearance curve and spawn shape, meaningful only in curve mode:
    /// row 8  = start colour rgb / additive-blend flag, row 9 = mid colour rgb / noise strength,
    /// row 10 = end colour rgb / end half-size, row 11 = disk radius / radial velocity / sway amplitude / curve-mode flag.
    /// With curve mode off these four rows are still written, but neither end reads them, so every byte of the old path matches the pre-extension state.
    /// They live in the frame buffer rather than Params because the vertex and fragment stages cannot read compute Params, yet exactly these values must cross stages.
    ///
    /// Rows 12..15 are used by the ribbon strip (lightning channel), and particle systems always write zero:
    /// row 12 = viewport width / viewport height / minimum half-width (pixels) / ribbon flag,
    /// row 13 = intensity / fog extinction (per metre) / reserved / reserved, rows 14 and 15 reserved.
    /// When the ribbon flag is 0 the vertex stage takes no new branch, so every byte of the particle path still matches the pre-extension state.
    /// The minimum half-width is given in pixels rather than world units: a distant lightning bolt must keep a guaranteed number of pixels of width,
    /// otherwise rasterization flickers on and off at sub-pixel widths -- some frames produce fragments, some frames the whole thing disappears.
    /// That is a loss where "the pixel never happens at all", and TAA's reactive rect cannot compensate: it only protects pixels that were already drawn.
    ///
    /// Rows 16..19 are the spawn-shape extension (rect spawn, initial-velocity cone jitter, speed jitter, die on floor, toon edge):
    /// row 16 = initial velocity xyz / cone jitter (as a fraction of the initial velocity length),
    /// row 17 = spawn axis x / spawn axis z / half-length along the axis / half-depth across it,
    /// row 18 = speed jitter (relative fraction) / floor height / toon edge width (0 means off) / reserved,
    /// row 19 = reserved.
    /// These rows are read by any stage only when the flags' rect-spawn bit (32) and die-on-floor bit (64) are set, or when row 18's edge width is non-zero;
    /// under the default parameters all four rows are zero and both bits are off, so every byte of the old path still matches the pre-extension state.
    /// They are not squeezed into 12..15 because those four rows already belong to the ribbon strip: once cone jitter writes row 12.w's ribbon flag to non-zero,
    /// it would wrongly route ordinary particles into the ribbon branch.
    /// </summary>
    public const uint FrameVectorCount = 20;

    /// <summary>Byte size of the frame buffer described by <see cref="FrameVectorCount"/>.</summary>
    public const uint FrameBytes = FrameVectorCount * 16;

    /// <summary>
    /// Index count of one billboard. The quad is two triangles over four corners, and this value is what
    /// the reset kernel writes into <see cref="IndirectDrawIndexedArgs.IndexCountPerInstance"/>.
    /// Where the six indices come from is a backend detail: the ones that already keep a shared unit-quad
    /// index buffer reuse it, and the ones that do not -- Metal expands its quads into six vertices
    /// elsewhere -- let the particle pipeline own a private one with the same winding.
    /// </summary>
    public const uint QuadIndexCount = 6;
}

/// <summary>
/// Resources consumed by one indirect particle submission. All three buffers are GPU-resident and
/// none of them is ever read back; Color and Depth are carried only so the backend can re-verify at
/// draw time that the pass it landed in is the pass that was prepared.
///
/// Depth is null when the scene pass has no explicit depth target and therefore runs on the
/// backend's default depth buffer. That is the common case outside the ambient-occlusion tier, so
/// treating it as a failure would silently restrict particles to one quality tier.
/// </summary>
public readonly record struct ParticleDrawInputs(RenderTarget Color, RenderTarget? Depth,
    StorageBuffer Instances, StorageBuffer IndirectArgs, StorageBuffer Frame);

/// <summary>
/// Backend pipeline that owns the particle PSO and issues the single indirect indexed draw.
/// Split in two the same way <see cref="FogCompositePipeline"/> is, and for the same reason:
/// PassDesc forbids state transitions inside a pass body, so resolving resources and moving the
/// instance buffer from compute-write to shader-read can only happen before BeginPass.
///
/// The submission verb lives here rather than on IGraphics on purpose. Only this object holds the
/// PSO, the root signature and the command signature that give the five numbers in the argument
/// buffer any meaning, so an IGraphics-level "draw indirect" would have to take all of that as
/// parameters and would describe nothing the shared layer can actually check.
/// </summary>
public abstract class ParticleDrawPipeline : IDisposable
{
    /// <summary>Resolves this frame's resources and performs the compute-write to shader-read handoff. Must be called outside any pass; false means skip submission for this frame.</summary>
    public abstract bool Prepare(in ParticleDrawInputs inputs);

    /// <summary>Issues one indirect indexed draw inside the scene pass. Consumes the Prepare result exactly once and returns false if the pass identity no longer matches.</summary>
    public abstract bool Draw();

    public abstract void Dispose();
}

/// <summary>
/// One particle system as seen by <see cref="FrameSchedule"/>. Several of these coexist on screen —
/// rain, snow and smoke are separate systems with their own buffers and their own indirect argument
/// block — so the schedule holds a list of them rather than a single routing slot.
///
/// The two verbs are deliberately split and must stay paired: Prepare runs outside the pass and
/// performs the compute-write to shader-read handoff, Submit runs inside it. Because BeginPass sits
/// between them, the schedule remembers which submitters succeeded instead of asking again.
///
/// This is an interface rather than a pair of delegates so that unregistration removes an entry by
/// reference identity, which does not depend on how method-group conversion compares delegates.
/// </summary>
public interface IParticleSubmitter
{
    /// <summary>Hands over this frame's parameters against the pass colour target. False means skip this system for this frame; Submit must then not be called.</summary>
    bool Prepare(IGraphics g, RenderTarget color);

    /// <summary>
    /// Submits the indirect draw inside the pass. Called only after Prepare returned true, exactly once per Prepare.
    /// Returning false means this frame was not actually drawn. There is more than one legitimate cause
    /// (the pass identity changed between Prepare and Draw, the PSO is still baking asynchronously, the
    /// resources went stale after the handoff), and the caller can take no remedial action from it, so
    /// this returns rather than throws: whoever holds the history state decides whether to log, avoiding
    /// the same line being spammed every frame.
    /// </summary>
    bool Submit(IGraphics g);
}
