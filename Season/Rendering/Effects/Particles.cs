// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering.Effects;

/// <summary>
/// A0.1 end-to-end validation case and the seed of the A6 GPU particle system: a GPU-driven
/// billboard field submitted with one indirect indexed draw.
///
/// Per-frame chain, all of it in the FrameStart phase so nothing touches a render pass:
/// 1. reset kernel, one thread, writes the five indirect arguments with InstanceCount = 0;
/// 2. compaction kernel, one thread per capacity slot, derives a particle position, rejects it
///    against the density gate and the camera frustum, and for survivors claims a slot with an
///    atomic add on InstanceCount and appends a <see cref="ParticleInstance"/>.
/// The instance count the rasterizer consumes is therefore produced entirely on the GPU and never
/// travels back to the CPU. That is the whole point of the exercise: with a CPU-sourced count the
/// cull would have to be a readback and would cost a full frame of latency.
///
/// Motion is stateless by design at this stage. A particle's position is a pure function of its
/// index hash plus one shared normalized phase, so there is no persistent state buffer, no spawn
/// list, no free list and nothing to reset on resize or on camera teleport. The phase is accumulated
/// from delta time rather than computed as elapsed * speed, because the latter makes every speed
/// change jump the phase of the entire field. Phase wraps at 1 and the shader only ever uses it
/// inside frac() or a 2*pi sine, so the wrap is exactly continuous.
///
/// Acceleration and lifetime fit inside that stateless model rather than breaking it: the phase is
/// normalized by the lifetime, so a particle's age in seconds is age * lifetime and the acceleration
/// integrates in closed form to 0.5 * a * t^2 on the position and a * t on the velocity. Nothing has
/// to be remembered between frames, which is what keeps rain, snow, smoke and fire sharing one
/// kernel. What this cannot express is anything path-dependent -- collision response, event-driven
/// bursts, trails -- and that is where a real state buffer will have to arrive later.
///
/// Several instances coexist on screen. Each owns its own instance buffer, indirect argument block
/// and backend pipeline, and registers itself into <see cref="FrameSchedule.ParticleSubmitters"/>,
/// so rain, snow and smoke are separate systems rather than modes of one.
/// Each system allocates and dispatches by the ParticleBudget given at construction; Capacity is only the per-system budget ceiling.
/// Lowering Density only reduces the survivor count, not the candidate thread count.
///
/// The shader single source of truth for all stages lives beside this file, one partial per backend
/// language (Particles.Hlsl.cs and friends), including the draw-side vertex and pixel shaders consumed
/// by the backend pipeline, per the engine's shader externalization rule. A backend whose language is
/// absent returns null from CreateComputeKernel / CreateParticleDrawPipeline and registration declines
/// cleanly.
/// </summary>
public sealed partial class ParticleSystemEffect : ComputeEffect, IParticleSubmitter, IDisposable
{
    /// <summary>Upper bound on simultaneously simulated particles. One compaction thread per slot, so this is also the dispatch width.</summary>
    public const uint Capacity = 32768;

    /// <summary>Actual allocation and dispatch budget, immutable after construction; defaults to retaining the legacy capacity.</summary>
    public uint ParticleBudget { get; }

    public ParticleSystemEffect() : this(Capacity) { }

    public ParticleSystemEffect(uint particleBudget)
    {
        if (particleBudget == 0 || particleBudget > Capacity) throw new ArgumentOutOfRangeException(nameof(particleBudget));
        ParticleBudget = particleBudget;
    }

    /// <summary>When disabled, neither dispatches nor submits the legacy indirect arguments; enabled by default to preserve the legacy call behavior.</summary>
    public bool Enabled = true;
    Vector3? _worldOrigin;
    /// <summary>When non-null, the spawn box is fixed at a world position; when null, keeps the legacy camera-following behavior.</summary>
    public Vector3? WorldOrigin
    {
        get => _worldOrigin;
        set
        {
            if (value is { } origin) ValidateVector(origin, nameof(WorldOrigin));
            _worldOrigin = value;
        }
    }
    /// <summary>The world-anchored periodic field selects a window around the camera; it only wraps edge particles rather than translating the whole field.</summary>
    public bool CameraWindow;
    float _viewDistance;
    /// <summary>Near-view visible distance (meters); smoothly fades out over the final quarter of the distance. 0 keeps the original infinite distance.</summary>
    public float ViewDistance
    {
        get => _viewDistance;
        set
        {
            if (!float.IsFinite(value) || value < 0) throw new ArgumentOutOfRangeException(nameof(ViewDistance));
            _viewDistance = value;
        }
    }
    /// <summary>Fades in over the 0.02 band ahead of the density threshold, preventing particles from popping in wholesale during weather transitions; disabled by default.</summary>
    public bool SoftDensity;
    Vector3 _windVelocity;
    /// <summary>Constant drift velocity (meters/second), distinct from the acceleration integrated against age.</summary>
    public Vector3 WindVelocity
    {
        get => _windVelocity;
        set { ValidateVector(value, nameof(WindVelocity)); _windVelocity = value; }
    }

    static void ValidateVector(Vector3 value, string name)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z))
            throw new ArgumentOutOfRangeException(name, "Particle space parameters must be finite.");
    }
    /// <summary>Independent seed; only the low 24 bits participate, ensuring the float constant transfers exactly across all four backends.</summary>
    public uint Seed;
    /// <summary>Regional constraint; null means unconstrained, an empty field means no precipitation.</summary>
    public WeatherField? Weather;
    /// <summary>Precipitation channel of the regional field: 1 = rain, 2 = snow.</summary>
    public int WeatherChannel = 1;
    /// <summary>Enables external frame time; frozen until Advance is called, and never consumes the previous frame's time twice.</summary>
    public bool ExternalTime;
    float _pendingDelta;

    public void Advance(float seconds)
    {
        if (!float.IsFinite(seconds) || seconds < 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        _pendingDelta = MathF.Min(0.1f, _pendingDelta + seconds);
    }

    /// <summary>
    /// Zeroes the accumulated phase. Its only caller is the per-pixel A/B forensics hook.
    ///
    /// The phase answers "how long has this system been recording", and recording starts the moment the particle system is created --
    /// the creation timing depends on asynchronous loading, so two runs can differ by dozens of frames. This offset does not
    /// converge with frame count: by any given frame, both sides' rain and snow are shifted by the same distance, so that large
    /// precipitation region in the image never lines up.
    /// Zeroing makes particles teleport once, but forensics wants reproducibility, not continuity. The normal running path does not call it.
    /// </summary>
    public void ResetPhase()
    {
        _phase = 0f;
        _pendingDelta = 0f;
        // When time is fed externally, _lastSeconds is not used in the computation, but the internal-timing path uses it to compute delta;
        // without synchronizing it, the first frame after zeroing would count in "the entire wall-clock time since the last recording".
        _lastSeconds = _clock.Elapsed.TotalSeconds;
    }

    const uint CompactGroupSize = 64;

    readonly Vector4[] _frame = new Vector4[ParticleDraw.FrameVectorCount];
    readonly ComputeResourceRef[] _resetResources = new ComputeResourceRef[1];
    readonly ComputeResourceRef[] _compactResources = new ComputeResourceRef[4];
    readonly Vector4[] _regionData = new Vector4[WeatherField.MaxRegions * WeatherField.RegionVectorCount];
    StorageBuffer? _regionBuffer;
    readonly Stopwatch _clock = Stopwatch.StartNew();

    IGraphics? _graphics;
    ComputeKernel? _reset, _compact;
    /// <summary>Buffers for a view slot that has no <see cref="ViewTargets"/> registered (the single-view shape);
    /// a registered view keeps one set per submitter on the view. See <see cref="ViewTargets.ParticleBuffers"/>.</summary>
    StorageBuffer? _instances, _args, _frameBuffer;
    /// <summary>The three buffers of the view being recorded, handed to <see cref="IParticleSubmitter.Prepare"/>.</summary>
    StorageBuffer? _currentInstances, _currentArgs, _currentFrame;
    ParticleDrawPipeline? _pipeline;
    double _lastSeconds;
    float _phase;
    /// <summary>Frame token whose phase advance was already applied: the clock is a property of the frame, not of
    /// a view, and a second view would otherwise advance the whole field twice per frame. Starts out of range so the
    /// very first frame (token 0) still advances. See <see cref="Record"/> for how the token is formed.</summary>
    ulong _timeFrame = ulong.MaxValue;
    /// <summary>Record counter used as the frame token when no prepared view identifies the frame itself.</summary>
    ulong _recordSerial;
    bool _ready;
    // Whether the previous frame's submission failed. Draw returning false has several legitimate causes (pass identity mismatch, PSO still being asynchronously compiled),
    // a single-frame failure is not worth logging, but consecutive failures mean this system has not drawn a single frame all session -- and reporting every frame would flood the log.
    // So log once on each flip.
    bool _submitFailed;
    // Whether the system has entered FrameSchedule's submitter table. Initialize may fail and bail out before registering,
    // and in that case Dispose must not remove an entry that was never added.
    bool _routed;

    /// <summary>Fraction of the capacity allowed to survive the density gate, in [0,1]. The rejection happens on the GPU, so lowering it costs no draw calls.</summary>
    public float Density = 1f;

    /// <summary>Half-extent of the horizontal spawn box around the camera, in world units.</summary>
    public float BoxRadius = 28f;

    /// <summary>Vertical span of the fall cycle, in world units. Particles wrap from the bottom back to the top.</summary>
    public float BoxHeight = 30f;

    /// <summary>Fall speed in world units per second.</summary>
    public float FallSpeed = 2.4f;

    /// <summary>Horizontal sway amplitude in world units.</summary>
    public float Sway = 0.35f;

    /// <summary>Billboard half-extent in world units.</summary>
    public float ParticleSize = 0.055f;

    /// <summary>Linear RGB plus peak coverage of a single billboard.</summary>
    public Vector4 Color = new(1.6f, 1.7f, 1.85f, 0.75f);

    /// <summary>
    /// Lifetime of one fall cycle in seconds. Zero means derive it from <see cref="BoxHeight"/> and
    /// <see cref="FallSpeed"/>, which is what rain and snow want; smoke and fire set it explicitly
    /// because their rise speed is governed by <see cref="Acceleration"/> rather than by the box.
    /// A lifetime of zero with a zero fall speed freezes the field, by design.
    /// </summary>
    public float Lifetime;

    /// <summary>
    /// Constant world-space acceleration in units per second squared. Integrated in closed form
    /// against the particle's own age, so it stays a pure function of the index hash: negative Y is
    /// heavier rain, positive Y is rising smoke, and a horizontal component is wind.
    /// </summary>
    public Vector3 Acceleration;

    /// <summary>Fraction of the lifetime spent fading in, in [0,1]. Zero means appear at full coverage.</summary>
    public float FadeIn;

    /// <summary>Fraction of the lifetime spent fading out, in [0,1]. Zero means disappear at full coverage.</summary>
    public float FadeOut;

    /// <summary>
    /// Seconds of travel the billboard is sheared along its velocity, turning the quad into a motion
    /// streak whose total length is |velocity| * this. Zero leaves the quad exactly square, which is
    /// what snow and smoke want; rain sets a small positive value to get falling threads.
    /// </summary>
    public float Stretch;

    /// <summary>
    /// Curve mode: appearance varies with normalized age rather than sharing one constant across the whole system.
    /// When enabled, the compaction stage rewrites the instance record's rgb into two stable random numbers (the vertex stage uses them to pick the two-segment color gradient and the initial spin angle),
    /// so <see cref="Color"/>'s rgb becomes the gradient start, and only alpha remains the coverage.
    /// When disabled, both ends take the original path and a frame's output is bit-identical to before the extension.
    /// </summary>
    public bool CurveMode;

    /// <summary>Spawn points land in a disk uniformly distributed on the horizontal plane rather than in a box. Independent of curve mode.</summary>
    public bool SpawnDisk;

    /// <summary>Disk radius (meters); only participates in compaction when <see cref="SpawnDisk"/> is enabled.</summary>
    public float SpawnRadius = 1f;

    /// <summary>
    /// Radial velocity (meters/second), continuously expanding outward along the line from the spawn point to the disk center: smoke columns therefore widen as they rise,
    /// and fire uses a negative value to pull the flame tongues toward the center axis. Like wind velocity, it is a constant velocity, not an acceleration.
    /// </summary>
    public float Spread;

    /// <summary>
    /// Amplitude of low-frequency sway (meters/second); one sine of different frequency per axis, with displacement growing linearly with age.
    /// It is still a closed-form function of age and introduces no cross-frame state.
    /// </summary>
    public float Turbulence;

    /// <summary>Billboard half-extent at the end of the lifetime (meters); the start is still <see cref="ParticleSize"/>.</summary>
    public float SizeEnd;

    /// <summary>Color at the lifetime midpoint; together with <see cref="Color"/> (start) and <see cref="ColorEnd"/> it forms a two-segment linear gradient.</summary>
    public Vector3 ColorMid = Vector3.One;

    /// <summary>Color at the end of the lifetime.</summary>
    public Vector3 ColorEnd = Vector3.One;

    /// <summary>
    /// Additive blend: keeps rgb and drives alpha to 0, which under the existing One/InvSrcAlpha PSO is pure additive.
    /// Fire and sparks use it to build a saturated white core in the highlights without needing a second PSO.
    /// </summary>
    public bool Additive;

    /// <summary>Sub-pixel noise strength; 0 means a smooth radial falloff; only participates in shading under curve mode.</summary>
    public float NoiseAmount;

    /// <summary>
    /// Rectangular spawn: spawn points land on a band centered on the spawn point and extending along <see cref="SpawnAxis">,
    /// rather than in a box or disk. Used for the kind of emitting surface -- such as a waterfall lip -- where "one edge hangs down as a whole".
    /// When opened together with <see cref="SpawnDisk">, the disk takes priority; the two do not stack.
    /// </summary>
    public bool SpawnRect;

    /// <summary>Direction of the spawn axis within the horizontal plane (x/z), not required to be normalized; falls back to (0,1), i.e. extending along +Z, when too short.</summary>
    public Vector2 SpawnAxis = new(0, 1);

    /// <summary>Half-length of the rectangular spawn along the axis (meters); only participates in compaction when <see cref="SpawnRect"> is enabled.</summary>
    public float SpawnHalfLength = 1f;

    /// <summary>Half-depth of the rectangular spawn perpendicular to the axis (meters); only participates in compaction when <see cref="SpawnRect"> is enabled.</summary>
    public float SpawnHalfDepth = 0.25f;

    /// <summary>
    /// Initial velocity of the rectangular spawn (meters/second): the whole band shares one direction, so a waterfall is a whole sheet of water flung downward.
    /// The phase's closed-form velocity -- "deriving speed from the drop height" -- is no longer added on top, so this value is the actual initial velocity;
    /// acceleration and wind velocity are still integrated as usual. The other spawn shapes do not read it.
    /// </summary>
    public Vector3 InitialVelocity;

    /// <summary>
    /// Cone jitter: randomly deflects the initial velocity direction in 3D, by an amount of |InitialVelocity| x this value.
    /// 0 means the whole band is perfectly aligned (crisp edges); above 0 the water curtain gradually spreads out.
    /// </summary>
    public float ConeJitter;

    /// <summary>Speed jitter: each particle's velocity magnitude is randomly scaled within 1 +/- this value/2; 0 means no jitter.</summary>
    public float SpeedJitter;

    /// <summary>Floor height (meters); only participates in compaction when <see cref="DieOnFloor"> is enabled.</summary>
    public float FloorY;

    /// <summary>
    /// Die on floor: particles whose integrated position falls below <see cref="FloorY"> are not emitted that frame, so splashes vanish
    /// as soon as they hit the ground rather than passing beneath it. Disabled by default; the legacy path's instance set is bit-identical.
    /// </summary>
    public bool DieOnFloor;

    /// <summary>
    /// Toon edge width (as a fraction of the billboard radius, 0..0.5): compresses the smooth radial falloff into a crisp circular edge.
    /// 0 means disabled, the fragment stage takes no new branch, so the default render output is bit-identical to before the extension.
    /// </summary>
    public float ToonEdge;

    public override string Name => "particles";

    public override ComputePhase Phase => ComputePhase.FrameStart;

    /// <summary>
    /// Per view: the compaction kernel culls against the current camera and the instance table is the result of that
    /// cull, so every view needs its own record. The feature switch keeps a view that turned particles off from even
    /// dispatching - no compaction, no indirect draw.
    /// </summary>
    public override ComputeScope Scope => ComputeScope.View;

    public override ViewFeature RequiredFeature => ViewFeature.Particles;

    public override bool Initialize(IGraphics g)
    {
        if (_graphics != null) throw new InvalidOperationException("Particle system is already initialized.");
        if (!g.ComputeSupported || !g.IndirectDrawSupported) return false;
        _graphics = g;
        try
        {
            _pipeline = g.CreateParticleDrawPipeline();
            if (_pipeline == null) { Dispose(); return false; }
            _reset = g.CreateComputeKernel(CreateArgsKernel("particleReset"));
            _compact = g.CreateComputeKernel(CreateCompactKernel());
            if (_reset == null || _compact == null) { Dispose(); return false; }
            _regionBuffer = g.CreateStorageBuffer((uint)_regionData.Length * 16);
            if (_regionBuffer == null) { Dispose(); return false; }
            // The instance table, its indirect arguments and the frame block are per view and per submitter, so they are
            // allocated on the first Record of each pair rather than here: the main view no longer owns buffers that the
            // secondary view would have to fight over (see ViewTargets.ParticleBuffers).
            FrameSchedule.ParticleSubmitters.Add(this);
            _routed = true;
            return true;
        }
        catch (Exception ex)
        {
            Dispose();
            DeviceServices.BaseApp.AddLog(LogType.Error, $"[Particles] Initialization failed: {ex.Message}");
            return false;
        }
    }

    public override void Record(IGraphics g)
    {
        _ready = false;
        if (!ReferenceEquals(g, _graphics) || _reset == null || _compact == null
            || _regionBuffer == null) return;

        // The view this block is packed for: every camera culls, packs and draws with its own matrices and its own
        // instance table. A view slot without targets falls back to the effect's own buffers, which is the shape the
        // single-view path had before secondary views existed.
        //
        // A host that never prepares a view (the headless acceptance harness) has no camera of its own, and skipping the
        // record there would leave it with nothing to submit. The frame snapshot the backends publish is the only camera
        // such a host has, and it is exactly the pair the single-view path packed; a prepared view always wins, because
        // its own matrices are what a second camera has to cull and draw with.
        var view = RenderView.CurrentOrNull;
        bool perView = view is { Prepared: true };
        var targets = perView ? view!.Targets : null;
        StorageBuffer? instances, args, frame;
        if (targets != null)
        {
            // Per (view, submitter): the pass chain records every particle system of the frame before it draws any of
            // them, so one table per view would be overwritten in turn and each draw would render whichever system
            // happened to record last - the campfire drawing the waterfall mist's instances, and no smoke anywhere.
            var buffers = targets.GetParticleBuffers(this);
            instances = buffers.Instances ??= g.CreateStorageBuffer(ParticleBudget * ParticleInstance.SizeInBytes);
            // Usage must be declared at creation and cannot be widened afterwards: written by compute, read by an indirect draw.
            args = buffers.Args ??= g.CreateStorageBuffer(IndirectDrawIndexedArgs.SizeInBytes,
                StorageBufferUsage.ComputeAndIndirectArgs);
            frame = buffers.Frame ??= g.CreateStorageBuffer(ParticleDraw.FrameBytes);
        }
        else
        {
            instances = _instances ??= g.CreateStorageBuffer(ParticleBudget * ParticleInstance.SizeInBytes);
            args = _args ??= g.CreateStorageBuffer(IndirectDrawIndexedArgs.SizeInBytes,
                StorageBufferUsage.ComputeAndIndirectArgs);
            frame = _frameBuffer ??= g.CreateStorageBuffer(ParticleDraw.FrameBytes);
        }
        if (instances == null || args == null || frame == null) return;
        _currentInstances = instances; _currentArgs = args; _currentFrame = frame;

        // The clock and the weather region table belong to the frame rather than to a view: advancing the phase once
        // per view would double the fall speed of the whole field as soon as a second camera renders.
        //
        // The frame token is the scheduler's counter only where several records really can share a frame, i.e. when a
        // prepared view is being recorded. A host that drives effects by hand (the four acceptance harnesses) has no
        // prepared view and never advances that counter, so there each record is a frame of its own -- gating on the
        // counter alone froze the phase and the region table after the first record.
        ulong frameToken = perView ? FrameSchedule.FrameIndex : ++_recordSerial;
        bool newFrame = frameToken != _timeFrame;
        double now = _clock.Elapsed.TotalSeconds;
        float delta = 0f;
        if (newFrame)
        {
            // The first frame and long hitches both produce anomalous dt; after clamping, phase advance is at most one fall cycle, so the whole field never skips.
            delta = ExternalTime ? _pendingDelta : (float)Math.Clamp(now - _lastSeconds, 0, 0.1);
            _pendingDelta = 0;
            _lastSeconds = now;
            _timeFrame = frameToken;
        }
        if (!Enabled) return;
        if (Weather != null)
        {
            if (WeatherChannel is not (1 or 2)) throw new InvalidOperationException("Weather channel must be rain or snow.");
            // Every view makes the same decision; only the first one of the frame has to repack the table, because the
            // regions are frame state and the upload would otherwise be repeated (and lost) per view.
            bool active = false;
            foreach (var region in Weather.Regions)
                active |= WeatherChannel == 1 ? region.State.Rain > 0 : region.State.Snow > 0;
            if (!active) return;
            if (newFrame)
            {
                int vectors = Weather.WriteParticleData(_regionData);
                if (!g.TryUpdateStorageBuffer(_regionBuffer, MemoryMarshal.AsBytes(_regionData.AsSpan(0, vectors)))) return;
            }
        }
        float height = MathF.Max(BoxHeight, 0.01f);
        // Lifetime is the sole timebase for phase: an explicit Lifetime takes priority, otherwise it is derived from the drop height and fall speed, and if both are missing the phase freezes.
        // This lets the shader side multiply normalized age back into seconds for the closed-form integral, while the phase itself remains a pure [0,1) scalar.
        float fallSpeed = MathF.Max(FallSpeed, 0);
        float life = Lifetime > 0 ? Lifetime : (fallSpeed > 0 ? height / fallSpeed : 0);
        // Phase accumulates in normalized lifetime and wraps at 1; the shader only uses it inside frac() and a 2*pi sine, so the wrap is exactly continuous.
        if (newFrame)
        {
            _phase += life > 0 ? delta / life : 0;
            if (_phase >= 1f) _phase -= MathF.Floor(_phase);
        }

        // The camera terms come from the view being recorded, not from the frame snapshot: FrameSchedule.RenderView is
        // captured once per frame at the main camera's update point, so a secondary view projecting through it would
        // cull and draw the field where the *main* camera sees it (a mist bank in front of the player showing up in the
        // rear mirror, and vanishing when it is behind him). view.View / RenderViewProjection are that view's own
        // unjittered pair, the same values the snapshot holds for the main view.
        if (!Matrix4x4.Invert(perView ? view!.View : FrameSchedule.RenderView, out var inverseView)) return;
        var position = inverseView.Translation;
        var right = new Vector3(inverseView.M11, inverseView.M12, inverseView.M13);
        var up = new Vector3(inverseView.M21, inverseView.M22, inverseView.M23);
        if (right.LengthSquared() < 1e-12f || up.LengthSquared() < 1e-12f) return;
        right = Vector3.Normalize(right);
        up = Vector3.Normalize(up);

        // Use the unjittered VP for culling and projection: jitter belongs to TAA, and particle positions themselves should not wobble with it.
        var vp = perView ? view!.RenderViewProjection : FrameSchedule.UnjitteredRenderViewProjection;
        _frame[0] = new Vector4(vp.M11, vp.M12, vp.M13, vp.M14);
        _frame[1] = new Vector4(vp.M21, vp.M22, vp.M23, vp.M24);
        _frame[2] = new Vector4(vp.M31, vp.M32, vp.M33, vp.M34);
        _frame[3] = new Vector4(vp.M41, vp.M42, vp.M43, vp.M44);
        _frame[4] = new Vector4(right, 0);
        _frame[5] = new Vector4(up, 0);
        _frame[6] = new Vector4(position, 0);
        // row 7: lifetime fade-in/fade-out, stretch in seconds, near-view distance; the latter two also participate in compaction.
        _frame[7] = new Vector4(Math.Clamp(FadeIn, 0, 1), Math.Clamp(FadeOut, 0, 1),
            MathF.Max(Stretch, 0), ViewDistance);
        // rows 8..11: appearance curve and spawn shape, see the row-meaning table in ParticleDraw.FrameVectorCount.
        // Under default values the flag bits in these four rows are all 0, so neither the vertex nor the fragment end takes a new branch.
        _frame[8] = new Vector4(Color.X, Color.Y, Color.Z, Additive ? 1f : 0f);
        _frame[9] = new Vector4(ColorMid.X, ColorMid.Y, ColorMid.Z, Math.Clamp(NoiseAmount, 0f, 1f));
        _frame[10] = new Vector4(ColorEnd.X, ColorEnd.Y, ColorEnd.Z,
            float.IsFinite(SizeEnd) ? SizeEnd : 0f);
        _frame[11] = new Vector4(MathF.Max(SpawnRadius, 0f), MathF.Max(Spread, -100f),
            MathF.Max(Turbulence, 0f), CurveMode ? 1f : 0f);
        // rows 12..15 stay zero: those four rows are the viewport and fog parameters of the ribbon
        // strip (lightning channel), which the particle path never fills, so the frame buffer's
        // ribbon flag is 0, the vertex stage takes no new branch, and every byte of the particle
        // path matches the pre-expansion layout.
        // rows 16..18: spawn-shape extensions (rect spawn / initial velocity and jitter / die on floor
        // / toon edge), see the row-meaning table.
        // Under default values bits 32 and 64 of flags are 0 and the hard-edge width is 0, so none of
        // the four backends read these rows.
        Vector2 axis = SpawnAxis;
        float axisLength = axis.Length();
        // A non-finite or degenerate axis falls back to +Z: the shader assumes the axis is
        // normalized, otherwise the rectangle collapses into an infinitely thin sliver.
        if (!(axisLength > 1e-4f) || !float.IsFinite(axisLength)) axis = new Vector2(0, 1);
        else axis /= axisLength;
        _frame[16] = new Vector4(InitialVelocity, Math.Clamp(ConeJitter, 0f, 4f));
        _frame[17] = new Vector4(axis.X, axis.Y, MathF.Max(SpawnHalfLength, 0f), MathF.Max(SpawnHalfDepth, 0f));
        // The jitter amount is clamped to [0,1], so the speed-magnitude scale lands in [0.5, 1.5]
        // and can never flip the velocity into the opposite direction.
        _frame[18] = new Vector4(Math.Clamp(SpeedJitter, 0f, 1f),
            float.IsFinite(FloorY) ? FloorY : 0f, Math.Clamp(ToonEdge, 0f, 0.5f), 0f);
        if (!g.TryUpdateStorageBuffer(frame, MemoryMarshal.AsBytes(_frame.AsSpan()))) return;

        // Bind the buffers of the view being recorded: the reset kernel zeroes this view's argument block, and the
        // compaction kernel appends into this view's instance table.
        _resetResources[0] = args;
        _compactResources[0] = frame;
        _compactResources[1] = instances;
        _compactResources[2] = args;
        _compactResources[3] = _regionBuffer;

        Span<float> reset = stackalloc float[4];
        reset.Clear();
        reset[0] = ParticleDraw.QuadIndexCount;
        g.DispatchCompute(new ComputeDispatchArgs
        {
            Kernel = _reset,
            Params = MemoryMarshal.AsBytes(reset),
            Resources = _resetResources,
            GroupsX = 1,
            GroupsY = 1,
            GroupsZ = 1,
        });

        float size = MathF.Max(ParticleSize, 1e-4f);
        var origin = WorldOrigin ?? position;
        Span<float> p = stackalloc float[24];
        p.Clear();
        p[0] = _phase;
        p[1] = MathF.Max(BoxRadius, 0.01f);
        p[2] = height;
        p[3] = ParticleBudget;
        p[4] = origin.X;
        p[5] = origin.Y;
        p[6] = origin.Z;
        p[7] = size;
        p[8] = Color.X;
        p[9] = Color.Y;
        p[10] = Color.Z;
        p[11] = Color.W;
        // Density also determines the dispatch width of compact: the gate is index-ordered on the
        // shader side, so indices before the ceil(density x budget)-th are always admitted and those
        // after are always rejected; dispatching the latter would only make the GPU spin idly.
        // p[12] must be the same value used to compute the dispatch width below, otherwise the
        // CPU's dispatch width and the shader's decided prefix would disagree.
        float density = Math.Clamp(Density, 0f, 1f);
        p[12] = density;
        p[13] = MathF.Max(Sway, 0);
        // Lifetime is sent to the shader in seconds so the kernel can convert the [0,1) age back to
        // real time for closed-form integration; 0 means the phase is frozen, and the shader zeroes
        // out the entire time term, so position and velocity fall back to the pre-expansion result.
        p[14] = life;
        p[15] = Seed & 0x00FFFFFFu;
        p[16] = Acceleration.X;
        p[17] = Acceleration.Y;
        p[18] = Acceleration.Z;
        p[19] = Weather?.Count ?? 0;
        p[20] = WindVelocity.X; p[21] = WindVelocity.Y; p[22] = WindVelocity.Z;
        // Low two bits of the channel: 0 = unconstrained, 1 = rain, 2 = snow; +4 world-anchored
        // window, +8 density fade-in, +16 disk spawn, +32 rect spawn, +64 die on floor. The last two
        // are off by default, so the legacy path's decisions and instance set stay unchanged.
        p[23] = (Weather == null ? 0 : WeatherChannel) + (CameraWindow && WorldOrigin.HasValue ? 4 : 0)
            + (SoftDensity ? 8 : 0) + (SpawnDisk ? 16 : 0) + (SpawnRect ? 32 : 0) + (DieOnFloor ? 64 : 0);
        g.DispatchCompute(new ComputeDispatchArgs
        {
            Kernel = _compact,
            Params = MemoryMarshal.AsBytes(p),
            Resources = _compactResources,
            // Dispatch only the index prefix that will be admitted: the candidate thread count
            // narrows linearly with density, so the dispatch width also changes continuously as clear
            // weather transitions to light rain, with no idle spinning where "density 0.02 still runs
            // at full budget". Rounded up to whole groups; the surplus indices in the last group are
            // rejected by the shader's own index gate.
            GroupsX = (uint)MathF.Ceiling(density * ParticleBudget / CompactGroupSize),
            GroupsY = 1,
            GroupsZ = 1,
        });
        // When density is 0, GroupsX is 0 as well, so the backend skips the whole dispatch and args
        // stays at the 0 written by reset: this frame draws nothing explicitly, rather than drawing a
        // field of stale instances already culled last frame.
        _ready = true;
    }

    /// <summary>Hands off parameters outside the pass; called by FrameSchedule before the scene draw target is opened; accepted only once per frame.</summary>
    bool IParticleSubmitter.Prepare(IGraphics g, RenderTarget color)
    {
        if (!_ready) return false;
        _ready = false;
        if (!ReferenceEquals(g, _graphics) || _pipeline == null) return false;
        if (_currentInstances == null || _currentArgs == null || _currentFrame == null) return false;
        if (!g.TryPrepareIndirectArgs(_currentArgs)) return false;
        bool prepared = _pipeline.Prepare(new ParticleDrawInputs(color, FrameSchedule.SceneDepth,
            _currentInstances, _currentArgs, _currentFrame));
        return prepared;
    }

    bool IParticleSubmitter.Submit(IGraphics g)
    {
        if (!ReferenceEquals(g, _graphics)) return false;
        bool drawn = _pipeline?.Draw() == true;
        if (drawn == _submitFailed)
        {
            _submitFailed = !drawn;
            // Recovery logs Backend, entering failure logs Error: the former is expected, the latter
            // needs someone to look into it.
            DeviceServices.BaseApp.AddLog(drawn ? LogType.Backend : LogType.Error, drawn
                ? $"[Particles] Indirect draw resumed submission (budget {ParticleBudget})."
                : $"[Particles] Indirect draw repeatedly failed to submit (budget {ParticleBudget}): this frame's pipeline state is inconsistent with Prepare, and this particle set is missing for the entire frame.");
        }
        return drawn;
    }

    public override void OnUnregistered() => Dispose();

    public void Dispose()
    {
        _ready = false;
        if (_routed)
        {
            FrameSchedule.ParticleSubmitters.Remove(this);
            _routed = false;
        }
        _pipeline?.Dispose();
        _pipeline = null;
        _reset?.Dispose();
        _reset = null;
        _compact?.Dispose();
        _compact = null;
        _instances?.Dispose();
        _instances = null;
        _args?.Dispose();
        _args = null;
        _frameBuffer?.Dispose();
        _frameBuffer = null;
        // The per-view buffers are owned by their ViewTargets; dropping the references here only stops this effect
        // from handing a stale one to the next Prepare. The entries themselves die with the view, and this release
        // covers the other case: the effect is disposed while the views that drew it live on.
        FrameSchedule.ForEachTargets(targets => targets.ReleaseParticleBuffers(this));
        _currentInstances = null;
        _currentArgs = null;
        _currentFrame = null;
        _regionBuffer?.Dispose();
        _regionBuffer = null;
        _pendingDelta = 0;
        _graphics = null;
    }

    // ── Kernel declarations ──
    // Binding order is the cross-backend slot contract; see the ComputeBindingType summary.
    // args:    [0] Params 16B -> b0 / [1] StorageBufferReadWrite args -> u0
    // compact: [0] Params 96B -> b0 / [1] StorageBufferRead regions -> t0
    //          [2] StorageBufferReadWrite instances -> u0 / [3] StorageBufferReadWrite args -> u1
    //          [4] StorageBufferRead frame -> t1
    // 96B is below the cross-backend 128B limit of Vulkan push constants / WebGPU uniforms.

    /// <summary>
    /// Single-threaded kernel that rewrites five indirect arguments. The parameters are four floats:
    /// indexCount / instanceCount / startIndex / baseVertex; the fifth field startInstance is always
    /// written as 0.
    ///
    /// Each frame the particle path uses it to zero the instance count (later raised by the compaction
    /// stage via atomic add), and the lightning channel uses it to write a fixed segment count.
    /// Lightning does not go through CPU upload: the compute-write -> indirect-read synchronization
    /// chain of the indirect argument buffer has already been verified by the particle path on all
    /// four backends, whereas CPU upload would require a copy->drawIndirect barrier on Vulkan whose
    /// chain has never been verified; writing it as a CPU upload would leave a visibility gap on VK
    /// that only shows up on real hardware.
    ///
    /// Name is used only for compile-error attribution and debug markers; each backend passes its own,
    /// so logs can tell which system is writing the arguments.
    /// </summary>
    internal static ComputeKernelDesc CreateArgsKernel(string name) => new()
    {
        Name = name,
        Source = new ShaderSourceSet
        {
            Hlsl = ResetHlsl, Glsl = ResetGlsl, Msl = ResetMsl, Wgsl = ResetWgsl, EntryPoint = "CSMain",
        },
        Bindings = new[]
        {
            new ComputeBindingDesc { Type = ComputeBindingType.Params, SizeInBytes = 16 },
            new ComputeBindingDesc { Type = ComputeBindingType.StorageBufferReadWrite },
        },
        WorkgroupX = 1,
        WorkgroupY = 1,
        WorkgroupZ = 1,
    };

    static ComputeKernelDesc CreateCompactKernel() => new()
    {
        Name = "particleCompact",
        Source = new ShaderSourceSet
        {
            Hlsl = CompactHlsl, Glsl = CompactGlsl, Msl = CompactMsl, Wgsl = CompactWgsl, EntryPoint = "CSMain",
        },
        Bindings = new[]
        {
            new ComputeBindingDesc { Type = ComputeBindingType.Params, SizeInBytes = 96 },
            new ComputeBindingDesc { Type = ComputeBindingType.StorageBufferRead },
            new ComputeBindingDesc { Type = ComputeBindingType.StorageBufferReadWrite },
            new ComputeBindingDesc { Type = ComputeBindingType.StorageBufferReadWrite },
            new ComputeBindingDesc { Type = ComputeBindingType.StorageBufferRead },
        },
        WorkgroupX = CompactGroupSize,
        WorkgroupY = 1,
        WorkgroupZ = 1,
    };

}
