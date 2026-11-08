// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering.Effects;

/// <summary>
/// L2.1 occlusion pyramid: the Hi-Z downsample chain rebuilt from SceneDepth, doubled into two ping-ponged
/// layers and restricted to the main view.
///
/// <para><b>Why a copy instead of a shared source.</b> <see cref="HiZEffect"/> keeps its four shader sources in
/// private <c>const string</c>s, and L2.1's change surface is "four kernels copied, one new effect, one
/// scheduling hook" - rewriting HiZ to expose them would touch a pass that already ships and is already
/// validated. The four sources below are therefore taken character for character from HiZ.cs, down to the
/// <c>HiZParams</c> block name. That name never crosses a kernel boundary (an HLSL cbuffer, a GLSL
/// push_constant block, an MSL struct and a WGSL struct are all translation-unit scoped), so keeping it costs
/// nothing at runtime and lets <c>.dbg/occpyramid_parity.ps1</c> prove the copy with a plain string compare
/// rather than a normalization rule that could itself be wrong.</para>
///
/// <para><b>Why two layers.</b> The consumer of this pyramid is occlusion culling, which has to test a caster
/// before the occluders of the same frame have been drawn, so it can only ever read the previous frame's
/// pyramid while the current one is being rebuilt. Ping-ponging two complete layers makes "write this frame" and
/// "read last frame" two different textures: no in-place hazard, no copy-on-write. <see cref="HistoryValid"/>
/// stays false until a second frame has landed, so a consumer that reads too early shows up as "skip the
/// occlusion test this frame" rather than as a pyramid full of the clear value.</para>
///
/// <para><b>Why the main view only.</b> A minimap or a rear-view mirror has its own camera, its own depth, and
/// no consumer for this chain; running there would overwrite the main view's history with a pyramid nobody can
/// use. <see cref="Scope"/> stays <see cref="ComputeScope.View"/> because that is the only way to see
/// <see cref="FrameSchedule.ActiveTargets"/> at all, and <see cref="Record"/> returns on every view that is not
/// the primary one before it touches <see cref="Ready"/> - a secondary view must not revoke a publication the
/// main view already made this frame.</para>
///
/// <para><b>Why it is off by default.</b> L2.1 ships the producer with no consumer yet: four dispatches per
/// frame that nothing samples. <see cref="Requested"/> is read from <c>SEASON_OCCLUSION_PYRAMID</c> at startup,
/// so the acceptance script opens it explicitly and a shipped frame never pays for it.</para>
///
/// <para><b>What "texel-for-texel equal to HiZ" reduces to.</b> Same level count (bound at compile time through
/// <see cref="HiZEffect.LevelCount"/>), same identical shader text, same input (the main view's SceneDepth,
/// same D32Float gate), same level-0 rule (2x2 minimum at half resolution), same <c>max(1, (n + 1) / 2)</c>
/// rounding, same R16Float storage, same 16-byte parameter block, same 8x8x1 workgroup and same clamped-corner
/// edge handling. What remains different is only the resource names each binding points at, which is exactly the
/// layer prefix.</para>
/// </summary>
public sealed class OcclusionPyramidEffect : ComputeEffect
{
    /// <summary>Level count, bound to HiZ at compile time: the acceptance criterion is per-texel equality with that chain.</summary>
    public const int LevelCount = HiZEffect.LevelCount;

    /// <summary>Two complete pyramids, ping-ponged one per frame: write this frame, read the previous one.</summary>
    public const int LayerCount = 2;

    /// <summary>Registry prefix for the storage textures; the full name adds the layer and the level.</summary>
    public const string TextureNamePrefix = "compute://occlusionpyramid/";

    /// <summary>Storage texture name of one level of one layer; layer 0/1 and level 0..<see cref="LevelCount"/>-1.</summary>
    public static string LevelName(int layer, int level) => TextureNamePrefix + layer + "/" + level;

    /// <summary>The layer this frame's rebuild wrote; a same-frame consumer would race the partial chain and should not use it.</summary>
    public static string CurrentName(int level) => LevelName(CurrentLayer, level);

    /// <summary>The layer a same-frame consumer may read, valid when <see cref="HistoryValid"/>.</summary>
    public static string HistoryName(int level) => LevelName(HistoryLayer, level);

    /// <summary>
    /// Whether the pyramid was requested at startup (<c>SEASON_OCCLUSION_PYRAMID=1</c>). Registration is skipped
    /// when false, so an unrequested run keeps its zero dispatch count and <see cref="Ready"/> stays false.
    /// </summary>
    public static bool Requested { get; set; } = ReadRequested();

    /// <summary>This frame's rebuild completed for the main view; consumers treat false as "do not sample".</summary>
    public static bool Ready { get; private set; }

    /// <summary>Whether <see cref="HistoryLayer"/> holds a complete pyramid from an earlier frame.</summary>
    public static bool HistoryValid { get; private set; }

    /// <summary>Layer written by the most recent completed frame.</summary>
    public static int CurrentLayer { get; private set; }

    /// <summary>Layer written by the frame before <see cref="CurrentLayer"/>; the only safe read for a same-frame consumer.</summary>
    public static int HistoryLayer { get; private set; } = 1;

    static bool ReadRequested() => ReadSwitch("SEASON_OCCLUSION_PYRAMID");

    /// <summary>Shared reader for the boolean <c>SEASON_*</c> switches this effect owns.</summary>
    static bool ReadSwitch(string variable)
    {
        var raw = Environment.GetEnvironmentVariable(variable)?.Trim();

        return raw is "1" or "on" or "true" or "yes";
    }

    // Precomputed because ComputeEffect.Record's contract is "zero allocations": the per-frame path indexes
    // these instead of concatenating a name string for every binding update.
    static readonly string[,] Names = BuildNames();

    static string[,] BuildNames()
    {
        var names = new string[LayerCount, LevelCount];

        for (int layer = 0; layer < LayerCount; layer++)
            for (int level = 0; level < LevelCount; level++)
                names[layer, level] = LevelName(layer, level);

        return names;
    }

    readonly uint[] _widths = new uint[LevelCount];
    readonly uint[] _heights = new uint[LevelCount];
    IGraphics? _graphics;
    ComputeKernel? _fromDepth;
    ComputeKernel? _reduce;
    ComputeResourceRef[]? _fromDepthResources;
    ComputeResourceRef[]? _reduceResources;
    uint _sourceWidth, _sourceHeight;
    int _writeLayer;
    bool _hasHistory;

    /// <summary>Registry name of this effect; it has no consumer by name, so it only shows up in logs.</summary>
    public override string Name => "occlusionPyramid";

    /// <summary>AfterOpaque: SceneDepth is only a clean full-frame depth after the scene opaque stage ends.</summary>
    public override ComputePhase Phase => ComputePhase.AfterOpaque;

    /// <summary>View scope so the effect sees <see cref="FrameSchedule.ActiveTargets"/> once per view and can pick the main one.</summary>
    public override ComputeScope Scope => ComputeScope.View;

    /// <summary>Level 0's width (half of full resolution); zero means "not allocated". Published statically because
    /// the effect is registered anonymously and the sidecar has no instance to ask.</summary>
    public static uint Level0Width { get; private set; }

    /// <summary>Level 0's height; see <see cref="Level0Width"/>.</summary>
    public static uint Level0Height { get; private set; }

    /// <summary>
    /// L2.3: the unjittered main-view ViewProjection whose SceneDepth <see cref="CurrentLayer"/> was reduced from -
    /// the camera this pyramid is a snapshot of. Meaningful only while <see cref="HasBuiltViewProjection"/>.
    ///
    /// <para><b>Why the producer stamps this instead of the consumer remembering it.</b> A consumer one frame later
    /// could carry its own previous-frame matrix, but only if it knew the pyramid had been rebuilt on every frame in
    /// between. <see cref="Record"/> returns early on a resize, on a missed dispatch and on every view that is not
    /// the primary one, and each of those leaves the published layer more than one frame old. Stamping the camera
    /// beside the layer rotation makes the two inseparable: whichever frame is being read, the matrix next to it is
    /// the one that produced it.</para>
    ///
    /// <para>Same source as the cull's slot matrix (<see cref="DeviceServices.BaseApp"/>'s MainView), so "the
    /// pyramid was built for the camera that is testing against it" is one opinion read twice rather than two
    /// opinions that could disagree.</para>
    /// </summary>
    public static Matrix4x4 BuiltViewProjection { get; private set; }

    /// <summary>
    /// Whether <see cref="BuiltViewProjection"/> describes <see cref="CurrentLayer"/>. Revoked together with
    /// <see cref="Ready"/> - never separately - on every path that stops short of publishing a whole chain, and
    /// cleared by <see cref="Allocate"/> because a reallocation replaces the textures the stamp described.
    /// </summary>
    public static bool HasBuiltViewProjection { get; private set; }

    /// <summary>
    /// L2.3: the screen budget of <see cref="GateOpen"/>, in level-0 texels. One texel is two screen pixels.
    ///
    /// <para><b>Why six.</b> <c>SEASON_OCCLUSION_GATE_SWEEP</c> measures the exchange rate on real geometry and real
    /// level-0 dimensions: about 20 level-0 texels per degree of yaw at 1139x628. Six texels is therefore roughly 0.3
    /// degrees per frame, or 18 degrees per second at 60 Hz - the band a mouse drag lives in and a slow pan does
    /// not. The value this shipped with first was two, which shuts the gate at 0.1 degrees per frame (6 degrees per
    /// second): safe, but slow enough that a stationary camera with a hand tremor would count as motion, so it would
    /// have thrown away most of what the step after this one exists to earn.</para>
    ///
    /// <para><b>What it costs.</b> A budget of N texels lets the pyramid be consulted while every texel in it sits up
    /// to 2N screen pixels away from the surface it described. Nothing can go wrong today, because L2.2's
    /// min-pyramid can only keep instances. Once the reduce flips to max, this number becomes the width of the hole
    /// a fast-moving thin occluder can punch, and six is the largest value that still reads as "small compared to an
    /// occluder worth testing" - 12 screen pixels is well inside a wall and well outside a fence rail. Raise it
    /// further only against a measurement of how many instances the flip actually culls; it is a parameter rather
    /// than a constant precisely so that measurement can move it without a rebuild.</para>
    ///
    /// <para>A negative value means "never open", which is the fault injection the acceptance script uses to take
    /// the pyramid-free path on a frame where the camera happens to be still.</para>
    /// </summary>
    public static float GateTexels { get; } = ReadGate("SEASON_OCCLUSION_GATE_TEXELS", 6f);

    /// <summary>
    /// L2.3: the depth budget of <see cref="GateOpen"/>, in NDC depth units. This engine's projections map near/far
    /// onto 0/1, so the number is a fraction of the whole depth range and is comparable across resolutions and
    /// fields of view.
    ///
    /// <para><b>Held at exactly one thousandth of <see cref="GateTexels"/></b> because that is the ratio the sweep
    /// measures: 0.001 NDC of depth drift per level-0 texel of screen drift, steady to within 5% across a hundred-fold
    /// range of yaw rates. Leaving it at the old 0.002 while the screen budget grew to six would have made this the
    /// only bound that ever fires - the gate would have stayed shut at 0.1 degrees per frame and the screen budget
    /// would have been unreachable, which is the one way to widen this gate and get nothing for it.</para>
    ///
    /// <para>The two bounds are not redundant even at a fixed ratio. A pure dolly moves depth with almost no corner
    /// drift, and a field-of-view change moves depth with no screen drift at all; those are exactly the cases where a
    /// min-pyramid's "nearest surface at this texel" stops describing the same surface, so the second bound earns its
    /// keep precisely where the first one goes quiet.</para>
    ///
    /// <para>Negative means "never open".</para>
    /// </summary>
    public static float GateDepth { get; } = ReadGate("SEASON_OCCLUSION_GATE_DEPTH", 0.006f);

    static float ReadGate(string variable, float fallback)
    {
        var raw = Environment.GetEnvironmentVariable(variable)?.Trim();

        return float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) ? value : fallback;
    }

    /// <summary>
    /// L2.3: whether <see cref="BuiltViewProjection"/> is close enough to <paramref name="viewProjection"/> that this
    /// pyramid's depths still describe the same screen.
    ///
    /// <para><b>What is being gated.</b> The pyramid is a snapshot of the previous frame's depth, taken with the
    /// previous frame's camera; the consumer runs before this frame's opaque stage, so it cannot have a fresher one.
    /// Under a slow camera the two frusta nearly coincide and a confirmation means what it says. Under a fast drag
    /// they do not: a block that held the nearest surface at one screen position now holds whatever the drag swept
    /// into that position, and the confirmation would be answering about a picture that is no longer on screen. This
    /// gate is the check that the two pictures coincide, expressed in the only two units the consumer cares about -
    /// where things landed (level-0 texels) and how deep they are (NDC depth).</para>
    ///
    /// <para><b>How it measures.</b> The previous frame's NDC cube is pushed back to world through the inverse of
    /// <see cref="BuiltViewProjection"/> and projected forward through the candidate. Each corner's distance from
    /// where it used to be, in each unit, is a lower bound on how far that part of the view has moved. A projective
    /// map is linear along edges, so the eight corners bound the six faces. This is a probe rather than a proof, and
    /// it does not have to be one: a false "open" only lets a confirmation fire, and under L2.2's min-pyramid a
    /// confirmation can only keep an instance that the plane test might have dropped. The proof obligation belongs
    /// to the step that flips the reduce, and that step inherits this gate as a precondition rather than as a
    /// substitute.</para>
    ///
    /// <para><b>Why it runs on the CPU.</b> The previous matrix would have to be a second push-constant block to
    /// reach the kernel, and 80 bytes of cull parameters plus 64 bytes of matrix already exceed Vulkan's 128-byte
    /// guaranteed push-constant budget; turning it into a storage buffer would put a per-frame upload and a fifth
    /// binding in front of a decision that is made once per frame rather than once per instance. A gate is by
    /// definition the cheap side of the trade: it either lets the whole frame's pyramid through or refuses it.</para>
    ///
    /// <para><paramref name="texels"/> and <paramref name="depth"/> are NaN when no comparison could be made at
    /// all - no stamp, no published size, a singular inverse, or a corner behind the candidate camera. NaN rather
    /// than a large number on purpose: a capture has to be able to tell "the gate refused to measure" from "the gate
    /// measured twelve texels and closed", and those are different bugs.</para>
    /// </summary>
    public static bool GateOpen(in Matrix4x4 viewProjection, out float texels, out float depth)
    {
        texels = float.NaN;
        depth = float.NaN;

        if (!HasBuiltViewProjection) return false;

        texels = MeasureStaleness(BuiltViewProjection, viewProjection, Level0Width, Level0Height, out depth);

        // NaN is the honest value for "the comparison could not be made", and a gate that cannot compare is a gate
        // that refuses: the caller falls back to the plane test, which is what shipped before L2.2.
        if (float.IsNaN(texels)) return false;

        return texels <= GateTexels && depth <= GateDepth;
    }

    /// <summary>
    /// L2.3: the measurement behind <see cref="GateOpen"/>, with the provenance and the budgets taken out.
    ///
    /// <para>Split out because it is the only part of this step that can be swept without dragging a mouse: given two
    /// view matrices and a level-0 size, "how far did the old frustum corner move" is a closed-form number, and
    /// <c>SEASON_OCCLUSION_GATE_SWEEP</c> sweeps it over yaw rates to show where the shipped budgets land. A single
    /// combined function would have forced that sweep to copy the loop, and a copy is the one thing a staleness
    /// threshold must not have.</para>
    ///
    /// <para>Returns NaN when the two matrices cannot be compared at all: no invertible <paramref name="built"/>, a
    /// corner with no finite pre-image, or a corner on or behind <paramref name="current"/>'s eye plane. All three
    /// mean the same thing to a caller - nothing here can be vouched for.</para>
    /// </summary>
    public static float MeasureStaleness(in Matrix4x4 built, in Matrix4x4 current, uint width, uint height, out float depth)
    {
        depth = float.NaN;

        if (width == 0u || height == 0u) return float.NaN;
        if (!Matrix4x4.Invert(built, out var fromNdc)) return float.NaN;

        // One NDC unit is half the texture, hence the halving: the cube spans [-1, 1] and the texel grid spans
        // [0, width]. Depth needs no scaling because near/far already sit on 0/1 (see Frustum.FromViewProjection).
        float halfWidth = width * 0.5f;
        float halfHeight = height * 0.5f;
        float worstTexels = 0f;
        float worstDepth = 0f;

        for (int corner = 0; corner < 8; corner++)
        {
            // x/y on the cube's faces, z on its near and far planes: the old view's whole frustum boundary.
            var previous = new Vector4(
                (corner & 1) != 0 ? 1f : -1f,
                (corner & 2) != 0 ? 1f : -1f,
                (corner & 4) != 0 ? 1f : 0f,
                1f);

            var world = Vector4.Transform(previous, fromNdc);
            if (MathF.Abs(world.W) < 1e-6f) return float.NaN;
            world /= world.W;

            var clip = Vector4.Transform(world, current);

            // On or behind the new eye plane the point has no finite screen position: the projection sends it to
            // infinity and then wraps it back into the frame. Nothing about it can be compared, and a drag fast
            // enough to do this is precisely the case this gate exists to refuse.
            if (clip.W <= 1e-6f) return float.NaN;

            float x = clip.X / clip.W;
            float y = clip.Y / clip.W;
            float z = clip.Z / clip.W;

            // `previous` is where this world point used to land, so each difference is how far it has moved.
            worstTexels = MathF.Max(worstTexels, MathF.Max(
                MathF.Abs(x - previous.X) * halfWidth,
                MathF.Abs(y - previous.Y) * halfHeight));
            worstDepth = MathF.Max(worstDepth, MathF.Abs(z - previous.Z));
        }

        depth = worstDepth;
        return worstTexels;
    }

    /// <summary>
    /// L2.3: diagnostic. <c>SEASON_OCCLUSION_GATE_SWEEP=1</c> makes every published frame compute what the gate would
    /// say at a range of camera yaw rates, and publishes it as the sidecar's <c>gateSweep=</c> token.
    ///
    /// It exists because the budgets are otherwise unreadable: a capture harness parks the camera, so the sidecar's
    /// texels are near zero on every frame of every run and say nothing about where "too stale" begins. Rotating
    /// the recorded view about its own Y axis and feeding the result back through the very same
    /// <see cref="MeasureStaleness"/> turns "six texels" into "this many degrees per frame", which is a sentence
    /// that can be checked against a mouse drag.
    /// </summary>
    static bool GateSweep { get; } = ReadSwitch("SEASON_OCCLUSION_GATE_SWEEP");

    /// <summary>Yaw rates in degrees per frame: a still camera, a slow pan, a drag, and a flick.</summary>
    static readonly float[] GateSweepRates = { 0.05f, 0.1f, 0.25f, 0.5f, 1f, 2f, 5f };

    /// <summary>
    /// L2.3: the sweep's text, empty unless <c>SEASON_OCCLUSION_GATE_SWEEP</c> is set. Published statically for the same
    /// reason <see cref="Level0Width"/> is: the effect is registered anonymously, so a sidecar has no instance to ask.
    /// </summary>
    public static string GateSweepReport { get; private set; } = "";

    bool _gateSweepLogged;

    /// <summary>
    /// Rebuilds <see cref="GateSweepReport"/> for the frame that just published.
    ///
    /// Rebuilt every frame rather than once, because the text is only useful if its candidate camera is the frame
    /// whose sidecar carries it; the log line is written once, because a hundred identical lines would bury the one
    /// that matters. This is the only allocation on <see cref="Record"/>'s path and it exists only when the diagnostic
    /// is armed - the shipped path stays at the "zero allocations" the base class asks for.
    /// </summary>
    void ReportGateSweep(in Matrix4x4 view, in Matrix4x4 projection)
    {
        var log = new StringBuilder();
        // One whitespace-free token: these lines go into a space-separated key=value sidecar, so a nested list has to
        // live inside parentheses with comma separators, exactly as the "a7=" and "skinning=" fields already do.
        log.Append("gateSweep=(at=").Append(Level0Width).Append('x').Append(Level0Height)
            .Append(",budgets=").Append(GateTexels.ToString("0.###", CultureInfo.InvariantCulture)).Append('/')
            .Append(GateDepth.ToString("0.#####", CultureInfo.InvariantCulture));

        for (int i = 0; i < GateSweepRates.Length; i++)
        {
            float yaw = GateSweepRates[i] * MathF.PI / 180f;
            // Right-multiplied: the rotation happens in view space, i.e. about the camera's own origin, which leaves
            // its position fixed and turns only its facing. That is what a view drag is; a left-multiplied rotation
            // would orbit the camera around the world origin instead.
            var candidate = view * Matrix4x4.CreateRotationY(yaw) * projection;
            float texels = MeasureStaleness(BuiltViewProjection, candidate, Level0Width, Level0Height, out float depth);
            bool open = !float.IsNaN(texels) && texels <= GateTexels && depth <= GateDepth;
            log.Append(',').Append(GateSweepRates[i].ToString("0.##", CultureInfo.InvariantCulture)).Append("deg=")
                .Append(texels.ToString("0.##", CultureInfo.InvariantCulture)).Append('/')
                .Append(depth.ToString("0.####", CultureInfo.InvariantCulture))
                .Append(open ? "/open" : "/shut");
        }

        log.Append(')');
        GateSweepReport = log.ToString();

        if (_gateSweepLogged) return;
        _gateSweepLogged = true;
        DeviceServices.BaseApp?.AddLog(LogType.Backend, GateSweepReport);
    }

    /// <summary>Compiles the two kernels and allocates both layers. False means no depth attachment, a narrower depth format
    /// than D32Float, or a backend that cannot compile the sources.</summary>
    public override bool Initialize(IGraphics g)
    {
        if (_graphics != null) throw new InvalidOperationException("Occlusion pyramid is already initialized.");
        if (FrameSchedule.SceneDepth == null) return false;
        var depth = FrameSchedule.SceneDepth;
        // D32Float for the same reason HiZ asks for it: the acceptance criterion compares against that chain texel
        // for texel, so both must read the same attachment at the same precision. A backend that fell back to a
        // narrower depth simply builds neither pyramid, and this effect's absence is then not a regression.
        if (depth.Desc.DepthFormat != RtFormat.D32Float || depth.Desc.Width == 0 || depth.Desc.Height == 0) return false;
        if (!Allocate(g, depth.Desc.Width, depth.Desc.Height)) return false;

        _fromDepth = g.CreateComputeKernel(new ComputeKernelDesc
        {
            Name = "occPyramidFromDepth",
            Source = new ShaderSourceSet
            {
                Hlsl = SourceFromDepthHlsl,
                Glsl = SourceFromDepthGlsl,
                Msl = SourceFromDepthMsl,
                Wgsl = SourceFromDepthWgsl,
                EntryPoint = "CSMain",
            },
            Bindings = new[]
            {
                new ComputeBindingDesc { Type = ComputeBindingType.Params, SizeInBytes = 16 },
                new ComputeBindingDesc { Type = ComputeBindingType.DepthTexture },
                new ComputeBindingDesc { Type = ComputeBindingType.StorageTextureWrite, StorageFormat = ComputeStorageFormat.R16Float },
            },
        });
        _reduce = g.CreateComputeKernel(new ComputeKernelDesc
        {
            Name = "occPyramidReduce",
            Source = new ShaderSourceSet
            {
                Hlsl = SourceReduceHlsl,
                Glsl = SourceReduceGlsl,
                Msl = SourceReduceMsl,
                Wgsl = SourceReduceWgsl,
                EntryPoint = "CSMain",
            },
            Bindings = new[]
            {
                new ComputeBindingDesc { Type = ComputeBindingType.Params, SizeInBytes = 16 },
                new ComputeBindingDesc { Type = ComputeBindingType.SampledTexture },
                new ComputeBindingDesc { Type = ComputeBindingType.StorageTextureWrite, StorageFormat = ComputeStorageFormat.R16Float },
            },
        });
        if (_fromDepth == null || _reduce == null)
        {
            Dispose();
            return false;
        }
        _graphics = g;
        _fromDepthResources = new ComputeResourceRef[] { depth, Names[0, 0] };
        _reduceResources = new ComputeResourceRef[] { Names[0, 0], Names[0, 1] };
        return true;
    }

    bool Allocate(IGraphics g, uint width, uint height)
    {
        _sourceWidth = width;
        _sourceHeight = height;
        uint w = width, h = height;
        for (int level = 0; level < LevelCount; level++)
        {
            // Round up: when the source is odd, the last row/column must still be covered by the last block,
            // otherwise the edge would retain the previous frame's depth.
            w = Math.Max(1u, (w + 1u) / 2u);
            h = Math.Max(1u, (h + 1u) / 2u);
            _widths[level] = w;
            _heights[level] = h;
            for (int layer = 0; layer < LayerCount; layer++)
                g.CreateComputeTexture(Names[layer, level], w, h, ComputeStorageFormat.R16Float);
        }
        // A rebuilt layer holds nothing from before: the next frame writes layer 0, and its "history" is a lie
        // until two frames have landed. Publishing that as HistoryValid=false is what keeps a culler from
        // testing against freshly cleared 1.0 depth and deciding everything is occluded.
        _writeLayer = 0;
        _hasHistory = false;
        Ready = false;
        HistoryValid = false;
        CurrentLayer = 0;
        HistoryLayer = 1;
        Level0Width = _widths[0];
        Level0Height = _heights[0];
        // A reallocation replaces the textures the stamp described, so the stamp goes with them.
        BuiltViewProjection = default;
        HasBuiltViewProjection = false;
        return true;
    }

    /// <summary>Rebuilds both layers against the resized depth attachment. Kernels are size-independent and survive.</summary>
    public override void OnResize(IGraphics g)
    {
        if (!ReferenceEquals(g, _graphics)) return;
        var depth = FrameSchedule.SceneDepth;
        if (depth == null || depth.Desc.Width == 0 || depth.Desc.Height == 0) return;
        Allocate(g, depth.Desc.Width, depth.Desc.Height);
        _fromDepthResources![0] = depth;
    }

    /// <summary>Records the four dispatches for the main view only. Every other view returns before touching any publication.</summary>
    public override void Record(IGraphics g)
    {
        // Main view only, and this is the very first test: by the time a minimap or mirror runs, the main view has
        // already published this frame's pyramid, and clearing Ready here would revoke a valid publication.
        if (!ReferenceEquals(FrameSchedule.ActiveTargets, FrameSchedule.PrimaryTargets)) return;

        // Revoked together with the publication, never separately: a consumer reads the two as a pair, and "the layer
        // is current but I cannot say from which camera" is exactly the state a stale read would come from.
        Ready = false;
        HasBuiltViewProjection = false;
        if (!ReferenceEquals(g, _graphics) || _fromDepth == null || _reduce == null) return;
        var depth = FrameSchedule.SceneDepth;
        if (depth == null || depth.Desc.Width != _sourceWidth || depth.Desc.Height != _sourceHeight) return;

        int write = _writeLayer;

        Span<float> p = stackalloc float[4];
        p[0] = _sourceWidth;
        p[1] = _sourceHeight;
        p[2] = _widths[0];
        p[3] = _heights[0];
        _fromDepthResources![1] = Names[write, 0];
        if (!g.TryDispatchCompute(new ComputeDispatchArgs
        {
            Kernel = _fromDepth,
            Params = MemoryMarshal.AsBytes(p),
            Resources = _fromDepthResources,
            GroupsX = (_widths[0] + 7) / 8,
            GroupsY = (_heights[0] + 7) / 8,
            GroupsZ = 1,
        })) return;

        for (int level = 1; level < LevelCount; level++)
        {
            _reduceResources![0] = Names[write, level - 1];
            _reduceResources[1] = Names[write, level];
            p[0] = _widths[level - 1];
            p[1] = _heights[level - 1];
            p[2] = _widths[level];
            p[3] = _heights[level];
            if (!g.TryDispatchCompute(new ComputeDispatchArgs
            {
                Kernel = _reduce,
                Params = MemoryMarshal.AsBytes(p),
                Resources = _reduceResources,
                GroupsX = (_widths[level] + 7) / 8,
                GroupsY = (_heights[level] + 7) / 8,
                GroupsZ = 1,
            }))
            {
                // A half-written layer is not a history anybody may read, so _writeLayer is not advanced: the next
                // frame overwrites the same layer from the top, and the previously published pair stays intact.
                return;
            }
        }

        // Publish only after the whole chain landed.
        CurrentLayer = write;
        HistoryLayer = 1 - write;
        HistoryValid = _hasHistory;
        _hasHistory = true;
        _writeLayer = 1 - write;

        // L2.3: the camera this layer is a snapshot of. A frame with no prepared main view still publishes the
        // pyramid - the depth is real - but without a stamp, and the gate then refuses to vouch for it rather than
        // guessing that the camera did not move.
        if (DeviceServices.BaseApp?.MainView is { Prepared: true } mainView)
        {
            BuiltViewProjection = mainView.ViewProjection;
            HasBuiltViewProjection = true;
        }

        Ready = true;

        // Last, so the sweep reads the stamp and the size this frame just published rather than the previous pair.
        if (GateSweep && DeviceServices.BaseApp?.MainView is { Prepared: true } swept)
            ReportGateSweep(swept.View, swept.Projection);
    }

    /// <summary>Unregistration is a full teardown: the kernels and the published names go away with the effect.</summary>
    public override void OnUnregistered() => Dispose();

    void Reset()
    {
        Ready = false;
        HistoryValid = false;
        CurrentLayer = 0;
        HistoryLayer = 1;
        Level0Width = 0;
        Level0Height = 0;
        BuiltViewProjection = default;
        HasBuiltViewProjection = false;
        _graphics = null;
    }

    /// <summary>Releases the kernels and revokes every published name and flag, so a re-registered effect starts without history.</summary>
    public void Dispose()
    {
        _fromDepth?.Dispose();
        _fromDepth = null;
        _reduce?.Dispose();
        _reduce = null;
        _fromDepthResources = null;
        _reduceResources = null;
        Array.Clear(_widths);
        Array.Clear(_heights);
        _sourceWidth = _sourceHeight = 0;
        _writeLayer = 0;
        _hasHistory = false;
        Reset();
    }

    // Shader sources: taken character for character from HiZ.cs (see the class summary). The slot convention is
    // the binding declaration order above; the workgroup is fixed at 8x8x1.

    /// <summary>D3D12 cs_5_0 (fxc; single exit avoids X4000). Level 0 reads the depth attachment directly and takes the minimum of the four corners.</summary>
    const string SourceFromDepthHlsl = @"
cbuffer HiZParams : register(b0)
{
    float uSrcWidth;
    float uSrcHeight;
    float uDstWidth;
    float uDstHeight;
};

Texture2D<float> uDepth : register(t0);
RWTexture2D<float4> uOutput : register(u0);

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    if (id.x < (uint)uDstWidth && id.y < (uint)uDstHeight)
    {
        // When the source has odd dimensions the bottom-right corner goes out of bounds; clamp to the
        // last row/column: sampling the edge repeatedly will not raise the minimum, whereas dropping it
        // would make the pyramid's edge farther than the true depth and the ray would slip through the gap.
        int2 first = int2(id.xy) * 2;
        int2 last = int2((uint)uSrcWidth - 1u, (uint)uSrcHeight - 1u);
        float d0 = uDepth.Load(int3(min(first, last), 0));
        float d1 = uDepth.Load(int3(min(first + int2(1, 0), last), 0));
        float d2 = uDepth.Load(int3(min(first + int2(0, 1), last), 0));
        float d3 = uDepth.Load(int3(min(first + int2(1, 1), last), 0));
        uOutput[id.xy] = float4(min(min(d0, d1), min(d2, d3)), 0.0, 0.0, 0.0);
    }
}
";

    /// <summary>Vulkan GLSL 450 (glslang -> SPIR-V; Params use push_constant, binding follows declaration order).</summary>
    const string SourceFromDepthGlsl = @"#version 450
layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(push_constant) uniform HiZParams
{
    float uSrcWidth;
    float uSrcHeight;
    float uDstWidth;
    float uDstHeight;
};

layout(binding = 1) uniform sampler2D uDepth;
layout(binding = 2, r16f) uniform writeonly image2D uOutput;

void main()
{
    uvec2 id = gl_GlobalInvocationID.xy;
    if (id.x < uint(uDstWidth) && id.y < uint(uDstHeight))
    {
        ivec2 first = ivec2(id) * 2;
        ivec2 last = ivec2(int(uint(uSrcWidth)) - 1, int(uint(uSrcHeight)) - 1);
        float d0 = texelFetch(uDepth, min(first, last), 0).r;
        float d1 = texelFetch(uDepth, min(first + ivec2(1, 0), last), 0).r;
        float d2 = texelFetch(uDepth, min(first + ivec2(0, 1), last), 0).r;
        float d3 = texelFetch(uDepth, min(first + ivec2(1, 1), last), 0).r;
        imageStore(uOutput, ivec2(id), vec4(min(min(d0, d1), min(d2, d3)), 0.0, 0.0, 0.0));
    }
}
";

    /// <summary>Metal MSL kernel.</summary>
    const string SourceFromDepthMsl = @"
#include <metal_stdlib>
using namespace metal;

struct HiZParams
{
    float uSrcWidth;
    float uSrcHeight;
    float uDstWidth;
    float uDstHeight;
};

kernel void CSMain(
    constant HiZParams& params [[buffer(0)]],
    depth2d<float, access::read> uDepth [[texture(0)]],
    texture2d<float, access::write> uOutput [[texture(1)]],
    uint2 id [[thread_position_in_grid]])
{
    if (id.x < uint(params.uDstWidth) && id.y < uint(params.uDstHeight))
    {
        int2 first = int2(id) * 2;
        int2 last = int2(int(uint(params.uSrcWidth)) - 1, int(uint(params.uSrcHeight)) - 1);
        float d0 = uDepth.read(uint2(min(first, last)));
        float d1 = uDepth.read(uint2(min(first + int2(1, 0), last)));
        float d2 = uDepth.read(uint2(min(first + int2(0, 1), last)));
        float d3 = uDepth.read(uint2(min(first + int2(1, 1), last)));
        uOutput.write(float4(min(min(d0, d1), min(d2, d3)), 0.0, 0.0, 0.0), id);
    }
}
";

    /// <summary>WebGPU WGSL. The storage format is pinned to rgba16float by the engine's narrow-format fallback chain, and reads take only .x.</summary>
    const string SourceFromDepthWgsl = @"
struct HiZParams
{
    uSrcWidth : f32,
    uSrcHeight : f32,
    uDstWidth : f32,
    uDstHeight : f32,
};

@group(0) @binding(0) var<uniform> params : HiZParams;
@group(0) @binding(1) var uDepth : texture_depth_2d;
@group(0) @binding(2) var uOutput : texture_storage_2d<rgba16float, write>;

@compute @workgroup_size(8, 8, 1)
fn CSMain(@builtin(global_invocation_id) id : vec3<u32>)
{
    if (id.x < u32(params.uDstWidth) && id.y < u32(params.uDstHeight))
    {
        let first = vec2<i32>(i32(id.x), i32(id.y)) * 2;
        let last = vec2<i32>(i32(u32(params.uSrcWidth)) - 1, i32(u32(params.uSrcHeight)) - 1);
        let d0 = textureLoad(uDepth, min(first, last), 0);
        let d1 = textureLoad(uDepth, min(first + vec2<i32>(1, 0), last), 0);
        let d2 = textureLoad(uDepth, min(first + vec2<i32>(0, 1), last), 0);
        let d3 = textureLoad(uDepth, min(first + vec2<i32>(1, 1), last), 0);
        textureStore(uOutput, vec2<i32>(i32(id.x), i32(id.y)), vec4<f32>(min(min(d0, d1), min(d2, d3)), 0.0, 0.0, 0.0));
    }
}
";

    /// <summary>D3D12 cs_5_0 (fxc; single exit avoids X4000). Takes the minimum over the previous level's 2x2.</summary>
    const string SourceReduceHlsl = @"
cbuffer HiZParams : register(b0)
{
    float uSrcWidth;
    float uSrcHeight;
    float uDstWidth;
    float uDstHeight;
};

Texture2D<float> uSource : register(t0);
SamplerState uLinearClamp : register(s0);
RWTexture2D<float4> uOutput : register(u0);

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    if (id.x < (uint)uDstWidth && id.y < (uint)uDstHeight)
    {
        // The sample points land on the centers of the source's two rows and two columns of texels, where
        // linear filtering equals point sampling.
        float2 size = float2(uSrcWidth, uSrcHeight);
        float2 uv = (float2(id.xy) * 2.0 + 0.5) / size;
        float2 step = 1.0 / size;
        float d0 = uSource.SampleLevel(uLinearClamp, uv, 0.0);
        float d1 = uSource.SampleLevel(uLinearClamp, uv + float2(step.x, 0.0), 0.0);
        float d2 = uSource.SampleLevel(uLinearClamp, uv + float2(0.0, step.y), 0.0);
        float d3 = uSource.SampleLevel(uLinearClamp, uv + step, 0.0);
        uOutput[id.xy] = float4(min(min(d0, d1), min(d2, d3)), 0.0, 0.0, 0.0);
    }
}
";

    /// <summary>Vulkan GLSL 450.</summary>
    const string SourceReduceGlsl = @"#version 450
layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(push_constant) uniform HiZParams
{
    float uSrcWidth;
    float uSrcHeight;
    float uDstWidth;
    float uDstHeight;
};

layout(binding = 1) uniform sampler2D uSource;
layout(binding = 2, r16f) uniform writeonly image2D uOutput;

void main()
{
    uvec2 id = gl_GlobalInvocationID.xy;
    if (id.x < uint(uDstWidth) && id.y < uint(uDstHeight))
    {
        vec2 size = vec2(uSrcWidth, uSrcHeight);
        vec2 uv = (vec2(id) * 2.0 + 0.5) / size;
        vec2 step = 1.0 / size;
        float d0 = textureLod(uSource, uv, 0.0).r;
        float d1 = textureLod(uSource, uv + vec2(step.x, 0.0), 0.0).r;
        float d2 = textureLod(uSource, uv + vec2(0.0, step.y), 0.0).r;
        float d3 = textureLod(uSource, uv + step, 0.0).r;
        imageStore(uOutput, ivec2(id), vec4(min(min(d0, d1), min(d2, d3)), 0.0, 0.0, 0.0));
    }
}
";

    /// <summary>Metal MSL kernel.</summary>
    const string SourceReduceMsl = @"
#include <metal_stdlib>
using namespace metal;

struct HiZParams
{
    float uSrcWidth;
    float uSrcHeight;
    float uDstWidth;
    float uDstHeight;
};

kernel void CSMain(
    constant HiZParams& params [[buffer(0)]],
    texture2d<float, access::sample> uSource [[texture(0)]],
    texture2d<float, access::write> uOutput [[texture(1)]],
    sampler uLinearClamp [[sampler(0)]],
    uint2 id [[thread_position_in_grid]])
{
    if (id.x < uint(params.uDstWidth) && id.y < uint(params.uDstHeight))
    {
        float2 size = float2(params.uSrcWidth, params.uSrcHeight);
        float2 uv = (float2(id) * 2.0 + 0.5) / size;
        float2 step = 1.0 / size;
        float d0 = uSource.sample(uLinearClamp, uv, level(0.0)).r;
        float d1 = uSource.sample(uLinearClamp, uv + float2(step.x, 0.0), level(0.0)).r;
        float d2 = uSource.sample(uLinearClamp, uv + float2(0.0, step.y), level(0.0)).r;
        float d3 = uSource.sample(uLinearClamp, uv + step, level(0.0)).r;
        uOutput.write(float4(min(min(d0, d1), min(d2, d3)), 0.0, 0.0, 0.0), id);
    }
}
";

    /// <summary>WebGPU WGSL.</summary>
    const string SourceReduceWgsl = @"
struct HiZParams
{
    uSrcWidth : f32,
    uSrcHeight : f32,
    uDstWidth : f32,
    uDstHeight : f32,
};

@group(0) @binding(0) var<uniform> params : HiZParams;
@group(0) @binding(1) var uSource : texture_2d<f32>;
@group(0) @binding(2) var uOutput : texture_storage_2d<rgba16float, write>;
@group(0) @binding(15) var uLinearClamp : sampler;

@compute @workgroup_size(8, 8, 1)
fn CSMain(@builtin(global_invocation_id) id : vec3<u32>)
{
    if (id.x < u32(params.uDstWidth) && id.y < u32(params.uDstHeight))
    {
        let size = vec2<f32>(params.uSrcWidth, params.uSrcHeight);
        let uv = (vec2<f32>(f32(id.x), f32(id.y)) * 2.0 + vec2<f32>(0.5)) / size;
        let texel = vec2<f32>(1.0) / size;
        let d0 = textureSampleLevel(uSource, uLinearClamp, uv, 0.0).r;
        let d1 = textureSampleLevel(uSource, uLinearClamp, uv + vec2<f32>(texel.x, 0.0), 0.0).r;
        let d2 = textureSampleLevel(uSource, uLinearClamp, uv + vec2<f32>(0.0, texel.y), 0.0).r;
        let d3 = textureSampleLevel(uSource, uLinearClamp, uv + texel, 0.0).r;
        textureStore(uOutput, vec2<i32>(i32(id.x), i32(id.y)), vec4<f32>(min(min(d0, d1), min(d2, d3)), 0.0, 0.0, 0.0));
    }
}
";
}
