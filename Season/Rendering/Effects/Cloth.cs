// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering.Effects;

/// <summary>
/// C1: what one cloth sheet is made of. The grid is row-major, <c>j * Columns + i</c>, with i running hoist to fly and j top to
/// bottom; every array here is indexed that way, and so is every vertex the kernel writes. Positions are in simulation space,
/// whose Y axis is the pole: the swivel rotates the pins about it and the pole collision is a cylinder around it.
/// </summary>
public sealed class ClothDesc
{
    /// <summary>Particles hoist to fly (i).</summary>
    public int Columns;

    /// <summary>Particles top to bottom (j).</summary>
    public int Rows;

    /// <summary>Grid spacing in metres. Every constraint's rest length is a multiple of it (1, sqrt 2, 2), i.e. the sheet's rest
    /// state is flat - <see cref="Rest"/> is only where it starts and where the pins hold it.</summary>
    public float Spacing;

    /// <summary>Initial position of every particle, and the hold point of the pinned ones before the swivel rotates it.</summary>
    public Vector3[] Rest = Array.Empty<Vector3>();

    /// <summary>True for particles that follow the swivel instead of the solver (inverse mass 0).</summary>
    public bool[] Pinned = Array.Empty<bool>();

    /// <summary>Subtracted from every output vertex, so the streams land in the owning Mesh3D's local space.</summary>
    public Vector3 Origin;

    /// <summary>The front and back streams sit this far either side of the mid-surface, along its normal.</summary>
    public float HalfThickness;

    /// <summary>Radius of the pole cylinder free particles are pushed out of.</summary>
    public float CollideRadius;

    /// <summary>XPBD compliance of the diagonal (shear) constraints; the structural ones are rigid. Particle mass is
    /// normalised to 1, so this is in m/N with a 1 kg particle.</summary>
    public float ShearCompliance = 1e-4f;

    /// <summary>XPBD compliance of the two-cell (bend) constraints, same units as <see cref="ShearCompliance"/>.</summary>
    public float BendCompliance = 2e-2f;

    /// <summary>Aerodynamic split on the relative wind, in 1/s: the normal part pushes the sheet, the tangential part pulls it
    /// out along the wind - which is what flattens a flag, since a sheet edge-on to the wind has almost no normal area.</summary>
    public float NormalDrag = 6f;

    /// <summary>Tangential half of the aerodynamic split, in 1/s; see <see cref="NormalDrag"/>.</summary>
    public float TangentDrag = 3f;

    /// <summary>Velocity damping in 1/s, applied as exp(-k h) per substep so it does not depend on the substep count.</summary>
    public float Damping = 1f;

    /// <summary>The front stream's u runs fly to hoist instead of hoist to fly, for a picture viewed from the +normal side.</summary>
    public bool FlipFrontU;
}

/// <summary>
/// C1: GPU XPBD cloth whose output is three ordinary vertex streams (<see cref="Vertex"/>, 80 bytes each) that a Mesh3D surface
/// draws from through <see cref="Surface.VertexStream"/>. Because the streams replace the surfaces' static vertices one for one,
/// the scene pass and the shadow pass rasterise the same sheet with the ordinary shaders - there is no cloth shader at all.
///
/// <para><b>Schedule.</b> FrameStart, App scope: the dispatch must be outside every Pass and before the shadow pass, and the cloth
/// is app-global input. The producer calls <see cref="Step"/> from its Update; Record turns the latest call into one dispatch and
/// then hands the three streams to the input assembler (<see cref="IGraphics.TryPrepareVertexInput"/>). A frame with no Step
/// dispatches nothing and the streams keep last frame's vertices, still in the readable state.</para>
///
/// <para><b>Solver.</b> One workgroup holds the whole sheet in groupshared memory. Each frame is split into
/// ceil(dt x <see cref="SubstepRate"/>) substeps (Small Steps: one iteration per substep, no lambda carried), each of which
/// predicts with gravity plus the aerodynamic split, solves twelve disjoint constraint colours with a barrier between them, and
/// pushes the sheet out of the pole. The substep length, not the count, is what is held fixed, so a 30 Hz host gets twice the
/// substeps of a 60 Hz one and the same stiffness.</para>
///
/// <para><b>Buffers.</b> rest: float4 (position, inverse mass) per particle, uploaded once. state: float4 position + float4
/// velocity per particle, resident across frames - velocity rather than the previous position, so a change of dt between frames
/// does not inject energy. front/back: one vertex per particle. rim: two vertices (front, back) per boundary point, in the
/// boundary order of <see cref="RimLoopLength"/>.</para>
///
/// <para><b>Failure.</b> All four dialects ship, so RegisterCompute binds wherever the backend compiles compute. A backend that
/// cannot hand the streams to the input assembler declines through <see cref="IGraphics.TryPrepareVertexInput"/> and the producer
/// keeps its static mesh. A non-finite or runaway particle resets the whole sheet to its pinned rest pose on the GPU, without a
/// readback.</para>
/// </summary>
public sealed partial class ClothSimulationEffect : ComputeEffect, IDisposable
{
    /// <summary>Particles one workgroup can hold: 128 threads x 4 particles, which is also what the shared arrays are sized for.
    /// Positions and weights alone are about 10 KB, so the three dialects without groupshared float3 keep clear of the 16 KB
    /// floor every Vulkan and WebGPU implementation guarantees.</summary>
    public const int MaxParticles = 512;

    /// <summary>Threads in the single workgroup the kernel runs as.</summary>
    public const uint GroupSize = 128;

    /// <summary>Substeps per second of simulated time; tuned in .dbg/_cloth_check/cloth_ref.py so the structural stretch of a
    /// 1.6 m sheet stays under 3 % at the strongest wind.</summary>
    public const float SubstepRate = 1800f;

    /// <summary>Fewest substeps one dispatch runs, however short the frame.</summary>
    public const int MinSubsteps = 4;

    /// <summary>Most substeps one dispatch runs; see <see cref="MaxStep"/>.</summary>
    public const int MaxSubsteps = 64;

    /// <summary>Longest frame one dispatch simulates. Longer frames are clamped rather than split, so a hitch slows the cloth
    /// down for one frame instead of stretching it.</summary>
    public const float MaxStep = MaxSubsteps / SubstepRate;

    /// <summary>Bytes per particle in the rest buffer: float4 (position, inverse mass).</summary>
    public const int RestStride = 16;

    /// <summary>Bytes per particle in the state buffer: float4 position + float4 velocity.</summary>
    public const int StateStride = 32;

    /// <summary>Bytes per vertex in every output stream: one <see cref="Vertex"/>.</summary>
    public const int VertexStride = 80;

    /// <summary>Size of the kernel's root-constant block, seven float4s.</summary>
    public const uint ParamsBytes = 112;

    /// <summary>Gravity along the simulation Y axis, m/s^2.</summary>
    public const float Gravity = -9.8f;

    /// <inheritdoc/>
    public override string Name => "clothSimulation";

    /// <inheritdoc/>
    public override ComputePhase Phase => ComputePhase.FrameStart;

    readonly ClothDesc _desc;
    readonly byte[] _restBytes;
    readonly uint[] _params = new uint[ParamsBytes / 4];
    readonly ComputeResourceRef[] _refs = new ComputeResourceRef[5];

    IGraphics? _graphics;
    ComputeKernel? _kernel;
    StorageBuffer? _rest;
    StorageBuffer? _state;
    StorageBuffer? _front;
    StorageBuffer? _back;
    StorageBuffer? _rim;

    bool _restUploaded;
    bool _resetPending = true;
    bool _stepPending;
    float _dt;
    Vector3 _wind;
    float _gust;
    float _phase;
    float _wavenumber;
    float _cosYaw = 1f;
    float _sinYaw;

    /// <summary>Particles in the sheet, and vertices in the front and back streams.</summary>
    public int ParticleCount { get; }

    /// <summary>Vertices in the rim stream: two per boundary point.</summary>
    public int RimVertexCount => 2 * RimLoopLength(_desc.Columns, _desc.Rows);

    /// <summary>The +normal side, one vertex per particle; null until Initialize succeeds.</summary>
    public StorageBuffer? FrontVertices => _front;

    /// <summary>The -normal side, one vertex per particle; null until Initialize succeeds.</summary>
    public StorageBuffer? BackVertices => _back;

    /// <summary>The edge strip, <see cref="RimVertexCount"/> vertices; null until Initialize succeeds.</summary>
    public StorageBuffer? RimVertices => _rim;

    /// <summary>Dispatches recorded since registration. The only evidence that the GPU sheet, not the static fallback, is drawing:
    /// both look like a flag.</summary>
    public int Dispatches { get; private set; }

    /// <summary>True when the most recent dispatch also handed all three streams to the input assembler.</summary>
    public bool Live { get; private set; }

    /// <summary>
    /// Boundary points of a columns x rows grid, walked clockwise in (c, t): along the top from the hoist, down the fly, back along
    /// the bottom, up the hoist. Rim vertex 2k is boundary point k on the front side, 2k + 1 the same point on the back.
    /// </summary>
    public static int RimLoopLength(int columns, int rows) => 2 * (columns - 1) + 2 * (rows - 1);

    /// <summary>Grid index of boundary point k, in the order of <see cref="RimLoopLength"/>. Mirrors the kernel's LoopParticle.</summary>
    public static int RimLoopParticle(int columns, int rows, int k)
    {
        int top = columns - 1;
        int side = rows - 1;
        if (k < top) return k;
        if (k < top + side) return (k - top) * columns + (columns - 1);
        if (k < 2 * top + side) return (rows - 1) * columns + (columns - 1 - (k - top - side));
        return (rows - 1 - (k - 2 * top - side)) * columns;
    }

    /// <summary>Validates the sheet and packs its rest block; GPU resources wait for Initialize.</summary>
    public ClothSimulationEffect(ClothDesc desc)
    {
        ArgumentNullException.ThrowIfNull(desc);
        if (desc.Columns < 3 || desc.Rows < 3)
            throw new ArgumentException("ClothDesc: the grid needs at least 3 x 3 particles for the bend constraints.");
        int count = desc.Columns * desc.Rows;
        if (count > MaxParticles)
            throw new ArgumentException($"ClothDesc: {count} particles exceed the single-workgroup limit of {MaxParticles}.");
        if (desc.Rest.Length != count || desc.Pinned.Length != count)
            throw new ArgumentException("ClothDesc: Rest and Pinned must hold Columns x Rows entries.");
        if (!(desc.Spacing > 0f))
            throw new ArgumentException("ClothDesc: Spacing must be positive.");

        _desc = desc;
        ParticleCount = count;

        _restBytes = new byte[count * RestStride];
        var rest = MemoryMarshal.Cast<byte, Vector4>(_restBytes.AsSpan());
        for (int i = 0; i < count; i++)
            rest[i] = new Vector4(desc.Rest[i], desc.Pinned[i] ? 0f : 1f);
    }

    /// <summary>
    /// Queues one simulation step for the next FrameStart. Call once per frame from the producer's Update; a second call before
    /// the dispatch adds its dt (up to <see cref="MaxStep"/>) and replaces the inputs.
    /// </summary>
    /// <param name="dt">Seconds to simulate. Zero or negative queues nothing.</param>
    /// <param name="wind">Wind velocity in simulation space, m/s.</param>
    /// <param name="gustAmplitude">Sideways gust in m/s, applied across the wind as a wave travelling hoist to fly.</param>
    /// <param name="flutterPhase">Phase of that wave in radians; the producer integrates it so a frequency change never jumps.</param>
    /// <param name="wavenumber">Radians of phase across the sheet, hoist to fly.</param>
    /// <param name="swivelYaw">Rotation of the pins about the simulation Y axis, radians, same sense as Wind.DirectionRadians.</param>
    public void Step(float dt, Vector3 wind, float gustAmplitude, float flutterPhase, float wavenumber, float swivelYaw)
    {
        if (!(dt > 0f)) return;
        _dt = MathF.Min(_stepPending ? _dt + dt : dt, MaxStep);
        _wind = wind;
        _gust = gustAmplitude;
        _phase = flutterPhase;
        _wavenumber = wavenumber;
        _cosYaw = MathF.Cos(swivelYaw);
        _sinYaw = MathF.Sin(swivelYaw);
        _stepPending = true;
    }

    /// <summary>Puts the sheet back on its pinned rest pose at the next dispatch.</summary>
    public void Reset() => _resetPending = true;

    /// <summary>Substeps one dispatch runs for a frame of dt seconds.</summary>
    public static int SubstepsFor(float dt)
        => Math.Clamp((int)MathF.Ceiling(dt * SubstepRate - 1e-3f), MinSubsteps, MaxSubsteps);

    /// <inheritdoc/>
    public override bool Initialize(IGraphics g)
    {
        if (_graphics != null) throw new InvalidOperationException("Cloth simulation is already initialized.");
        // The vertex-input handoff is the hard gate: without it the kernel's vertices could be written but never drawn.
        if (!g.ComputeSupported || !g.VertexInputStorageSupported) return false;
        _graphics = g;
        try
        {
            _kernel = g.CreateComputeKernel(CreateClothKernel());
            if (_kernel == null) { Dispose(); return false; }

            uint count = (uint)ParticleCount;
            _rest = g.CreateStorageBuffer(count * RestStride);
            _state = g.CreateStorageBuffer(count * StateStride);
            _front = g.CreateStorageBuffer(count * VertexStride, StorageBufferUsage.ComputeAndVertexInput);
            _back = g.CreateStorageBuffer(count * VertexStride, StorageBufferUsage.ComputeAndVertexInput);
            _rim = g.CreateStorageBuffer((uint)RimVertexCount * VertexStride, StorageBufferUsage.ComputeAndVertexInput);
            if (_rest == null || _state == null || _front == null || _back == null || _rim == null) { Dispose(); return false; }
            return true;
        }
        catch (Exception ex)
        {
            Dispose();
            DeviceServices.BaseApp.AddLog(LogType.Error, $"[ClothSimulation] Initialization failed: {ex.Message}");
            return false;
        }
    }

    /// <inheritdoc/>
    public override void Record(IGraphics g)
    {
        Live = false;
        if (!ReferenceEquals(g, _graphics) || _kernel == null) return;
        if (_rest == null || _state == null || _front == null || _back == null || _rim == null) return;

        // The rest block goes up here rather than in Initialize: an upload needs the frame's command list open and no Pass in
        // flight, which only Record guarantees. The state buffer is uninitialised until then, so the first dispatch resets.
        if (!_restUploaded)
        {
            if (!g.TryUpdateStorageBuffer(_rest, _restBytes)) return;
            _restUploaded = true;
            _resetPending = true;
        }

        if (!_stepPending) return;
        _stepPending = false;

        var d = _desc;
        int substeps = SubstepsFor(_dt);
        SetFloat(0, _wind.X); SetFloat(1, _wind.Y); SetFloat(2, _wind.Z); SetFloat(3, _dt);
        SetFloat(4, _gust); SetFloat(5, _phase); SetFloat(6, _wavenumber); SetFloat(7, _resetPending ? 1f : 0f);
        SetFloat(8, _cosYaw); SetFloat(9, _sinYaw); SetFloat(10, d.CollideRadius); SetFloat(11, d.Damping);
        SetFloat(12, d.ShearCompliance); SetFloat(13, d.BendCompliance); SetFloat(14, d.NormalDrag); SetFloat(15, d.TangentDrag);
        SetFloat(16, Gravity); SetFloat(17, d.HalfThickness); SetFloat(18, d.Spacing); SetFloat(19, d.FlipFrontU ? 1f : 0f);
        SetFloat(20, d.Origin.X); SetFloat(21, d.Origin.Y); SetFloat(22, d.Origin.Z); SetFloat(23, 0f);
        _params[24] = (uint)d.Columns; _params[25] = (uint)d.Rows; _params[26] = (uint)substeps; _params[27] = 0u;

        _refs[0] = _rest;
        _refs[1] = _state;
        _refs[2] = _front;
        _refs[3] = _back;
        _refs[4] = _rim;

        if (!g.TryDispatchCompute(new ComputeDispatchArgs
        {
            Kernel = _kernel,
            Params = MemoryMarshal.AsBytes(_params.AsSpan()),
            Resources = _refs,
            GroupsX = 1,
            GroupsY = 1,
            GroupsZ = 1,
        }))
        {
            // Refused before recording anything: the streams keep last frame's vertices and no surface is handed over.
            return;
        }

        _resetPending = false;
        Dispatches++;

        // Not short-circuited: a stream that fails to transition is drawn from its static vertices by the backend, and the other
        // two must still be handed over so the sheet does not lose a face to one failure.
        bool front = g.TryPrepareVertexInput(_front);
        bool back = g.TryPrepareVertexInput(_back);
        bool rim = g.TryPrepareVertexInput(_rim);
        Live = front & back & rim;
    }

    void SetFloat(int index, float value) => _params[index] = BitConverter.SingleToUInt32Bits(value);

    /// <inheritdoc/>
    public override void OnUnregistered() => Dispose();

    /// <summary>Releases the kernel and every buffer. Surfaces still pointing at the streams fall back to their static
    /// vertices, because a released stream is never in the vertex-input state.</summary>
    public void Dispose()
    {
        Live = false;
        _kernel?.Dispose();
        _rest?.Dispose();
        _state?.Dispose();
        _front?.Dispose();
        _back?.Dispose();
        _rim?.Dispose();
        _kernel = null;
        _rest = null;
        _state = null;
        _front = null;
        _back = null;
        _rim = null;
        _graphics = null;
        _restUploaded = false;
        _resetPending = true;
        _stepPending = false;
    }
}
