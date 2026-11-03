// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering.Effects;

/// <summary>
/// Draw side of lightning channel V2: feeds <see cref="LightningChannel"/>'s polyline into the
/// particle ribbon pipeline, drawing a whole bolt with a single indirect draw.
///
/// Why reuse the particle pipeline instead of building a new one: every capability a ribbon needs --
/// per-instance data from a storage buffer, instance count from GPU-side indirect args, premultiplied
/// alpha transparency, depth test on and depth write off -- the particle pipeline already has. The
/// vertex stage's ribbon branch (see the contract at lines 12/13 of <see cref="ParticleDraw"/>) only
/// runs when the frame buffer's ribbon flag is set, so every byte of the particle path is unchanged
/// from before the expansion: the two paths share one PSO without interfering with each other.
///
/// Per-frame chain, all inside the FrameStart phase and outside any pass:
/// 1. Strike edge: write each segment of the channel as a <see cref="ParticleInstance"/> and upload once;
/// 2. args kernel (single thread) writes indexCount=6, instanceCount=segment count, and 0 for the rest;
/// 3. Prepare (before BeginPass): write the frame buffer for this frame's viewport, hand the args over
///    to the indirect args state, and resolve resources;
/// 4. Same place (Prepare): project the channel into a screen AABB and write it into
///    <see cref="ViewTargets.ReactiveRect"/>; TAA disables history reuse inside the rect -- the channel
///    only lives one or two frames, and the 0.97 static feedback would swallow the whole bolt.
/// Afterwards <see cref="FrameSchedule.SubmitParticles"/> submits it once at the end of the scene
/// transparent stage.
///
/// Args go through the GPU rather than a CPU upload: the compute-write -> indirect-read sync chain has
/// already been validated by the particle path on all four backends, whereas a CPU upload on Vulkan
/// needs a copy->drawIndirect barrier, and that chain has never been validated.
///
/// Brightness has two levels: the instance's Color.a is the per-segment **static** weight (the top
/// fades out inside the cloud, branches are dimmer than the main channel), and the intensity in frame
/// buffer row 13 is the **per-frame** envelope (the four-stage decay of a return stroke). The product
/// is the final coverage, so a change in the envelope does not require rebuilding the instances.
/// </summary>
public sealed class LightningEffect : ComputeEffect, IParticleSubmitter, IDisposable
{
    /// <summary>Linear RGB of the lightning. Slightly blue-shifted cold white: a real channel's core is incandescent, only the edges lean blue-violet.</summary>
    public Vector3 Tint = new(0.78f, 0.85f, 1.0f);

    /// <summary>Coverage multiplier at full envelope. Lightning is an emitter rather than a surface, so its brightness must far exceed 1 to burn to white in HDR.</summary>
    public float Intensity = 3.2f;

    /// <summary>
    /// Fog extinction coefficient (per meter). Deliberately not the 0.006 of the rain curtain: that one
    /// describes "scattering of the rain droplet curtain off the background", whereas lightning is its
    /// own radiation source and its light penetrates far deeper in a downpour than background
    /// visibility. At 0.0012 it retains 55% at 500 m and 16% at 1500 m, so a bright line is still
    /// visible far away.
    /// </summary>
    public float FogExtinction = 0.0012f;

    /// <summary>
    /// Minimum half-width (pixels). A distant channel's world half-width computes to sub-pixel, and
    /// rasterization at that width flickers on and off: some frames produce fragments, others drop the
    /// whole segment, showing up as the entire channel blinking intermittently. The reactive rect
    /// (<see cref="ViewTargets.ReactiveRect"/>) only protects pixels that were already drawn from being
    /// dragged by history; it cannot make up for a fragment that never existed, so this floor must stay.
    /// </summary>
    public float MinHalfWidthPixels = 1.4f;

    /// <summary>When disabled, neither dispatched nor submitted; used by the panel or quality tiers.</summary>
    public bool Enabled = true;

    readonly ParticleInstance[] _instances = new ParticleInstance[LightningChannel.MaxSegments];
    readonly Vector4[] _frame = new Vector4[ParticleDraw.FrameVectorCount];
    readonly ComputeResourceRef[] _argsResources = new ComputeResourceRef[1];

    IGraphics? _graphics;
    ComputeKernel? _argsKernel;
    StorageBuffer? _instancesBuffer, _args, _frameBuffer;
    ParticleDrawPipeline? _pipeline;
    // Strike serial for which instances have already been uploaded. The geometry of the same bolt does
    // not change, so upload only once at the edge.
    int _builtSerial = int.MinValue;
    bool _ready;
    bool _routed;
    bool _submitFailed;

    public override string Name => "lightning";

    public override ComputePhase Phase => ComputePhase.FrameStart;

    /// <summary>
    /// Per view: the bolt geometry and its segment count are view independent, but the frame block carries this
    /// camera's VP, viewport pixels and minimum line width, so a secondary view has to prepare its own draw.
    /// </summary>
    public override ComputeScope Scope => ComputeScope.View;

    /// <summary>The ribbon is drawn by the particle pipeline and shares that feature switch.</summary>
    public override ViewFeature RequiredFeature => ViewFeature.Particles;

    public override bool Initialize(IGraphics g)
    {
        if (_graphics != null) throw new InvalidOperationException("Lightning effect is already initialized.");
        if (!g.ComputeSupported || !g.IndirectDrawSupported) return false;
        _graphics = g;
        try
        {
            _pipeline = g.CreateParticleDrawPipeline();
            if (_pipeline == null) { Dispose(); return false; }
            _argsKernel = g.CreateComputeKernel(ParticleSystemEffect.CreateArgsKernel("lightningArgs"));
            if (_argsKernel == null) { Dispose(); return false; }
            _frameBuffer = g.CreateStorageBuffer(ParticleDraw.FrameBytes);
            _instancesBuffer = g.CreateStorageBuffer(LightningChannel.MaxSegments * ParticleInstance.SizeInBytes);
            // usage must be declared at creation time: this buffer is both written by compute and read
            // by an indirect draw.
            _args = g.CreateStorageBuffer(IndirectDrawIndexedArgs.SizeInBytes,
                StorageBufferUsage.ComputeAndIndirectArgs);
            if (_frameBuffer == null || _instancesBuffer == null || _args == null) { Dispose(); return false; }
            _argsResources[0] = _args;
            FrameSchedule.ParticleSubmitters.Add(this);
            _routed = true;
            return true;
        }
        catch (Exception ex)
        {
            Dispose();
            DeviceServices.BaseApp.AddLog(LogType.Error, $"[Lightning] initialization failed: {ex.Message}");
            return false;
        }
    }

    public override void Record(IGraphics g)
    {
        _ready = false;
        if (!Enabled || !ReferenceEquals(g, _graphics) || _argsKernel == null
            || _instancesBuffer == null || _args == null || _frameBuffer == null) return;

        var visuals = FrameSchedule.WeatherVisuals;
        if (visuals == null) return;
        var channel = visuals.Channel;
        if (!channel.Visible) return;

        var flash = visuals.Flash;
        if (!(flash.W > 0)) return;

        // The brightest flash this frame may not belong to this channel: when two clouds each strike,
        // <see cref="WeatherVisualState.Flash"/> takes the brighter one. If the coordinates do not
        // match, explicitly draw nothing rather than lighting cloud A's channel at cloud B's brightness.
        Vector3 source = new(flash.X, flash.Y, flash.Z);
        if (Vector3.DistanceSquared(source, channel.CloudPoint) > 1e-4f) return;

        int count = channel.Count;
        if (count == 0 || count > _instances.Length) return;

        if (visuals.StrikeSerial != _builtSerial)
        {
            var segments = channel.Segments;
            for (int i = 0; i < count; i++)
            {
                var segment = segments[i];
                _instances[i] = new ParticleInstance
                {
                    // Segment midpoint; with Velocity = half the segment vector, these are the two
                    // endpoints the ribbon vertex stage recognizes.
                    Position = (segment.A + segment.B) * 0.5f,
                    Size = segment.HalfWidth,
                    Color = new Vector4(Tint.X, Tint.Y, Tint.Z, segment.Brightness),
                    Velocity = (segment.B - segment.A) * 0.5f,
                    Age = 0f,
                };
            }

            if (!g.TryUpdateStorageBuffer(_instancesBuffer,
                MemoryMarshal.AsBytes(_instances.AsSpan(0, count)))) return;
            _builtSerial = visuals.StrikeSerial;
        }

        // The segment count is a CPU-known constant, but we still let the GPU write the args: this
        // compute-write -> indirect-read chain is the one the particle path has already validated, and a
        // CPU upload on Vulkan still lacks a visibility guarantee.
        Span<float> args = stackalloc float[4];
        args[0] = ParticleDraw.QuadIndexCount;
        args[1] = count;
        args[2] = 0;
        args[3] = 0;
        g.DispatchCompute(new ComputeDispatchArgs
        {
            Kernel = _argsKernel,
            Params = MemoryMarshal.AsBytes(args),
            Resources = _argsResources,
            GroupsX = 1,
            GroupsY = 1,
            GroupsZ = 1,
        });
        _ready = true;
    }

    /// <summary>
    /// Parameter handoff outside the pass. The frame buffer is written here rather than in Record
    /// because the minimum screen width needs a **pixel** size, and the RenderTarget only becomes
    /// available in Prepare; it also means the whole frame buffer is uploaded only once.
    /// </summary>
    bool IParticleSubmitter.Prepare(IGraphics g, RenderTarget color)
    {
        if (!_ready) return false;
        _ready = false;
        if (!ReferenceEquals(g, _graphics) || _pipeline == null) return false;
        if (color.Desc.Width == 0 || color.Desc.Height == 0) return false;

        // The frame block carries this view's VP, viewport pixels and minimum line width, so it lives on the view:
        // on backends whose staging keeps only a frame's last write, a shared block would leave the main view drawing
        // with the secondary view's matrices (see ViewTargets.ParticleBuffers).
        var view = RenderView.Current;
        if (view == null) return false;
        var frameBuffer = view.Targets != null
            ? view.Targets.GetParticleBuffers(this).Frame ??= g.CreateStorageBuffer(ParticleDraw.FrameBytes)
            : _frameBuffer;
        if (frameBuffer == null) return false;

        // Same reason as the frame block: the snapshot matrices are captured once per frame at the main camera's update
        // point, so projecting through them would place a secondary view's bolt wherever the main camera sees it.
        if (!Matrix4x4.Invert(view.View, out var inverseView)) return false;
        var position = inverseView.Translation;
        var right = new Vector3(inverseView.M11, inverseView.M12, inverseView.M13);
        var up = new Vector3(inverseView.M21, inverseView.M22, inverseView.M23);
        if (right.LengthSquared() < 1e-12f || up.LengthSquared() < 1e-12f) return false;
        right = Vector3.Normalize(right);
        up = Vector3.Normalize(up);

        var vp = view.RenderViewProjection;
        _frame[0] = new Vector4(vp.M11, vp.M12, vp.M13, vp.M14);
        _frame[1] = new Vector4(vp.M21, vp.M22, vp.M23, vp.M24);
        _frame[2] = new Vector4(vp.M31, vp.M32, vp.M33, vp.M34);
        _frame[3] = new Vector4(vp.M41, vp.M42, vp.M43, vp.M44);
        _frame[4] = new Vector4(right, 0);
        _frame[5] = new Vector4(up, 0);
        _frame[6] = new Vector4(position, 0);
        // row 7's lifetime fade and stretch are consumed by the billboard path; the ribbon does not read
        // it, so keep it zero to avoid anyone assuming it takes effect.
        _frame[7] = default;
        _frame[8] = new Vector4(Tint.X, Tint.Y, Tint.Z, 1f);
        _frame[9] = default;
        _frame[10] = default;
        _frame[11] = default;
        // row 12 = viewport width / viewport height / minimum half-width in pixels / ribbon flag.
        _frame[12] = new Vector4(color.Desc.Width, color.Desc.Height, MathF.Max(MinHalfWidthPixels, 0.05f), 1f);
        // row 13 = intensity (envelope x constant) / fog extinction coefficient / reserved / reserved.
        float envelope = FrameSchedule.WeatherVisuals?.Flash.W ?? 0f;
        _frame[13] = new Vector4(MathF.Max(Intensity, 0f) * envelope, MathF.Max(FogExtinction, 0f), 0f, 0f);
        _frame[14] = default;
        _frame[15] = default;
        if (!g.TryUpdateStorageBuffer(frameBuffer, MemoryMarshal.AsBytes(_frame.AsSpan()))) return false;

        if (!g.TryPrepareIndirectArgs(_args!)) return false;
        if (!_pipeline.Prepare(new ParticleDrawInputs(color, FrameSchedule.SceneDepth,
            _instancesBuffer!, _args!, frameBuffer))) return false;

        // Only once the draw will really happen may TAA disable history reuse inside this frame's
        // reactive rect. Placed after all failure paths: a frame that was not drawn should not cost any
        // pixel its temporal accumulation.
        PublishReactiveRect(vp, color);
        return true;
    }

    /// <summary>
    /// Projects the entire channel (main channel + all branches) into normalized screen space and
    /// computes an AABB to hand to TAA.
    ///
    /// Why it is needed: the channel only survives 1-3 frames, whereas TAA's static feedback is
    /// 0.97 -- a newly appeared bright line gets only 3% weight on its first frame, and by the time
    /// history accumulates to a visible brightness the event is over; conversely, when it goes out it
    /// takes thirty-some frames to fade. Pixels inside the rect skip history entirely, so the channel is
    /// effectively "appearing for the first time" every frame.
    ///
    /// Why an AABB rather than a per-pixel mask: the CPU only has the polyline's world coordinates, and
    /// covering it exactly would need another rasterization pass and a full-screen attachment; the
    /// channel's screen footprint is a thin polyline anyway, so the bounding box wastes limited area.
    /// The cost is stated plainly: background pixels inside the rect lose temporal accumulation over the
    /// few frames the channel is alive and briefly return to per-frame noise.
    ///
    /// Each segment is dilated by its own metric half-width rather than dilating the whole rect by a
    /// constant pixel count: a near channel's half-width can reach a dozen pixels, and dilating only by
    /// the minimum screen width would cut off the sides of the line -- exactly the part that falls
    /// inside TAA's neighborhood clipping view.
    /// </summary>
    void PublishReactiveRect(in Matrix4x4 viewProjection, RenderTarget color)
    {
        var visuals = FrameSchedule.WeatherVisuals;
        if (visuals == null) return;
        var channel = visuals.Channel;
        if (!channel.Visible) return;

        float width = color.Desc.Width;
        float height = color.Desc.Height;
        if (width < 1f || height < 1f) return;

        // The effect of a unit-direction world offset on clip.x / clip.y is determined by the three
        // scale terms of these two rows respectively; the sum of absolute values is an upper bound. With
        // it, converting a metric half-width into a uv offset is a single multiply -- no need to invert
        // fov or aspect, and no assumption that the projection is symmetric.
        float scaleX = MathF.Abs(viewProjection.M11) + MathF.Abs(viewProjection.M21) + MathF.Abs(viewProjection.M31);
        float scaleY = MathF.Abs(viewProjection.M12) + MathF.Abs(viewProjection.M22) + MathF.Abs(viewProjection.M32);
        // The minimum screen width is handled by the vertex stage; the extra 2 pixels is TAA's 3x3
        // neighborhood radius -- as long as one pixel in the neighborhood sees the channel, clipping
        // pulls the whole band along, so the reactive region must be wider than the line itself.
        float minHalf = MathF.Max(MinHalfWidthPixels, 0.05f) + 2f;
        float minPadX = minHalf / width;
        float minPadY = minHalf / height;

        float minX = 1f, minY = 1f, maxX = 0f, maxY = 0f;
        bool any = false;

        var segments = channel.Segments;
        for (int i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            Vector4 a = Vector4.Transform(new Vector4(segment.A, 1f), viewProjection);
            Vector4 b = Vector4.Transform(new Vector4(segment.B, 1f), viewProjection);
            // If either endpoint falls behind the camera plane, the bounding box becomes meaningless (it
            // would cut the channel in half). In that case fall back to a full-screen rect: this frame's
            // TAA effectively stops accumulating, but the image is correct; computing a wrong rect would
            // leave an afterimage of half the channel.
            if (a.W <= 1e-4f || b.W <= 1e-4f)
            {
                FrameSchedule.ActiveTargets.ReactiveRect = new Vector4(0f, 0f, 1f, 1f);
                return;
            }

            // uv.y must be flipped: the engine's shared projection convention puts NDC +1 at the top of
            // the screen (see Picking.ProjectToScreen's screenY = (1 - ndcY) * 0.5 * height), and TAA's
            // texture row 0 (id.y = 0) is also at the top. Writing it with +0.5 would flip the reactive
            // region upside down -- the lightning band would be unprotected while the sky on the other
            // side of the screen would lose its history.
            Vector2 ua = new(a.X / a.W * 0.5f + 0.5f, 0.5f - a.Y / a.W * 0.5f);
            Vector2 ub = new(b.X / b.W * 0.5f + 0.5f, 0.5f - b.Y / b.W * 0.5f);
            // View depth of the segment midpoint. Averaging the two endpoints is good enough: the ribbon's
            // width is far smaller than its distance to the camera.
            float depth = (a.W + b.W) * 0.5f;
            float padX = MathF.Max(segment.HalfWidth * scaleX / depth * 0.5f, minPadX);
            float padY = MathF.Max(segment.HalfWidth * scaleY / depth * 0.5f, minPadY);

            minX = MathF.Min(minX, MathF.Min(ua.X, ub.X) - padX);
            minY = MathF.Min(minY, MathF.Min(ua.Y, ub.Y) - padY);
            maxX = MathF.Max(maxX, MathF.Max(ua.X, ub.X) + padX);
            maxY = MathF.Max(maxY, MathF.Max(ua.Y, ub.Y) + padY);
            any = true;
        }

        if (!any) return;
        minX = Math.Clamp(minX, 0f, 1f);
        minY = Math.Clamp(minY, 0f, 1f);
        maxX = Math.Clamp(maxX, 0f, 1f);
        maxY = Math.Clamp(maxY, 0f, 1f);
        // When the entire channel is off-viewport both components get clamped equal; leave the rect empty
        // then and let TAA take its normal path.
        if (minX >= maxX || minY >= maxY) return;

        FrameSchedule.ActiveTargets.ReactiveRect = new Vector4(minX, minY, maxX, maxY);
    }

    bool IParticleSubmitter.Submit(IGraphics g)
    {
        if (!ReferenceEquals(g, _graphics)) return false;
        bool drawn = _pipeline?.Draw() == true;
        if (drawn == _submitFailed)
        {
            _submitFailed = !drawn;
            DeviceServices.BaseApp.AddLog(drawn ? LogType.Backend : LogType.Error, drawn
                ? "[Lightning] Indirect draw resumed submitting."
                : "[Lightning] Indirect draw missing submissions in a row: this frame's pipeline state does not match Prepare, and the channel is absent for the entire frame.");
        }
        return drawn;
    }

    public override void OnUnregistered() => Dispose();

    public void Dispose()
    {
        _ready = false;
        _builtSerial = int.MinValue;
        if (_routed)
        {
            FrameSchedule.ParticleSubmitters.Remove(this);
            _routed = false;
        }
        _pipeline?.Dispose();
        _pipeline = null;
        _argsKernel?.Dispose();
        _argsKernel = null;
        _instancesBuffer?.Dispose();
        _instancesBuffer = null;
        _args?.Dispose();
        _args = null;
        // The per-view frame blocks are owned by their ViewTargets; this release covers the case where the
        // lightning channel retires while the views that drew it live on.
        FrameSchedule.ForEachTargets(targets => targets.ReleaseParticleBuffers(this));
        _frameBuffer?.Dispose();
        _frameBuffer = null;
        _graphics = null;
    }
}
