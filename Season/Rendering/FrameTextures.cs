// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

/// <summary>
/// Declarative vocabulary of the backend texture entries one frame publishes.
///
/// Stage 2B: before this, every published intermediate was a plain string field, either on
/// <see cref="FrameSchedule"/> (SkyView / CloudNoise / AerialLut) or on <see cref="ViewTargets"/>
/// (bloom, AO, TAA resolve, the five fog volumes). Three consequences followed from that shape:
/// a string can be null at any point (the MacCatalyst / Web first-frame null-key crashes), two
/// producers can silently overwrite each other's entry, and a consumer cannot tell "still the same
/// binding" from "rebuilt in place", because the name is byte-identical across a rebuild.
///
/// A slot is the *identity* of the entry; the string is only its spelling in the backend
/// dictionary. Producers write through the slot and consumers read through it, so nobody has to
/// know which view's chain happens to be live, and <see cref="FrameTextureHandle.Generation"/>
/// makes a rebuild observable.
///
/// Not a frame graph: pass order stays hard-coded in the scheduler (see the non-goals of the
/// optimization roadmap). This enum is a resource vocabulary, nothing more.
/// </summary>
public enum FrameTextureSlot : byte
{
    /// <summary>Bloom chain output of one view (last up-chain level, or down[0] on the single-level shape).</summary>
    Bloom = 0,

    /// <summary>GTAO chain output of one view (the blurred AO image sampled by the final composite).</summary>
    Ao,

    /// <summary>TAA resolve output of one view; replaces the scene source downstream while it is published.</summary>
    SceneColorResolve,

    /// <summary>Procedural-sky Sky-View LUT. Frame-global: it only depends on the sun direction, not on the camera.</summary>
    SkyView,

    /// <summary>Baked cloud-noise volume. Frame-global and constant for its whole lifetime.</summary>
    CloudNoise,

    /// <summary>
    /// Aerial-perspective 3D LUT. Frame-global only in the sense that exactly one volume is baked
    /// per frame; its contents are **not** view-independent. <c>FillAerialParams</c> rebuilds the
    /// direction basis from <c>RenderView.Current.Camera</c>, and the effect runs at
    /// <c>ComputeScope.App</c> / <c>ComputePhase.FrameStart</c> - before the view loop - so every
    /// view samples a volume baked from the main view's basis.
    ///
    /// Measured by the Vulkan <c>--aerial</c> host (32^3, eight screen uvs, basis swapped for a
    /// rolled rear view): at w = 0.30 (about 2.9 km) 7/8 samples moved, by at most 0.00316, i.e.
    /// below 1/255; at w = 0.80 (about 20 km) 8/8 moved, by up to 0.0549, i.e. about 14/255.
    /// Rebaking with an identical basis is bit-identical (max delta 0), so the difference is the
    /// basis and nothing else. Disabling the feature writes <c>ApParams0 = 0</c>, the consumer's
    /// only gate, so the delta is exactly 0.
    ///
    /// A second distant view must therefore get its own bake; near-range minimaps can keep sharing
    /// this one.
    /// </summary>
    AerialLut,

    /// <summary>DDGI irradiance atlas surface consumed this frame (ping-pong read side).</summary>
    DdgiIrradiance,

    /// <summary>DDGI depth-moment atlas surface consumed this frame (ping-pong read side).</summary>
    DdgiDepth,

    /// <summary>Per-view fog injection volume.</summary>
    FogInjection,

    /// <summary>Per-view fog integrated volume.</summary>
    FogIntegrated,

    /// <summary>Per-view fog local (filtered) volume: the current history write slot, swapped every frame.</summary>
    FogLocal,

    /// <summary>Per-view fog temporal history ping slot 0.</summary>
    FogHistory0,

    /// <summary>Per-view fog temporal history ping slot 1.</summary>
    FogHistory1,
}

/// <summary>
/// Who owns a slot's value: one view, or the frame.
///
/// Only the slots that are genuinely camera-independent are global. Bloom / AO / TAA-resolve and the
/// fog volumes are built from a specific view's image and must never be shared; SkyView and
/// CloudNoise depend on the sun and the noise bake only. AerialLut sits in the global table because
/// exactly one volume is baked per frame, but that is an implementation fact, not a guarantee of
/// view independence: the bake reads the current view's basis, and the measured error is below
/// 1/255 up to about 3 km and around 14/255 at about 20 km (see the AerialLut slot note above).
/// Moving it to <see cref="FrameTextureScope.Value"/> is a one-line change in
/// <see cref="FrameTextureScopeRules.IsGlobal"/> once a distant second view needs its own bake.
/// </summary>
public enum FrameTextureScope : byte
{
    /// <summary>Owned by one view; resolved through <see cref="ViewTargets.Textures"/>.</summary>
    Value,

    /// <summary>Owned by the frame; resolved through <see cref="FrameState.Textures"/>.</summary>
    Global,
}

/// <summary>Where each slot lives. Kept separate from the enum so the enum stays a pure vocabulary.</summary>
public static class FrameTextureScopeRules
{
    /// <summary>Owner kind of a slot.</summary>
    public static FrameTextureScope Of(FrameTextureSlot slot)
        => IsGlobal(slot) ? FrameTextureScope.Global : FrameTextureScope.Value;

    /// <summary>True when the slot is frame-global rather than per-view.</summary>
    public static bool IsGlobal(FrameTextureSlot slot) => slot switch
    {
        FrameTextureSlot.SkyView or FrameTextureSlot.CloudNoise or FrameTextureSlot.AerialLut
            or FrameTextureSlot.DdgiIrradiance or FrameTextureSlot.DdgiDepth => true,
        _ => false,
    };
}

/// <summary>
/// Typed reference to one published entry: which slot, which owner, and which generation of the
/// value. A handle whose generation no longer matches the table resolves to null instead of
/// silently binding a rebuilt resource, which is the failure mode plain strings cannot express.
/// </summary>
public readonly struct FrameTextureHandle : IEquatable<FrameTextureHandle>
{
    internal FrameTextureHandle(FrameTextureSlot slot, int view, uint generation)
    {
        Slot = slot;
        View = view;
        Generation = generation;
    }

    /// <summary>Slot this handle refers to.</summary>
    public FrameTextureSlot Slot { get; }

    /// <summary>Owning view index, or -1 for a frame-global slot.</summary>
    public int View { get; }

    /// <summary>Generation of the published value; 0 means "nothing published".</summary>
    public uint Generation { get; }

    /// <summary>False when no value has ever been published for this slot.</summary>
    public bool IsValid => Generation != 0;

    public bool Equals(FrameTextureHandle other)
        => Slot == other.Slot && View == other.View && Generation == other.Generation;

    public override bool Equals(object? obj) => obj is FrameTextureHandle other && Equals(other);

    public override int GetHashCode() => HashCode.Combine((byte)Slot, View, Generation);

    public static bool operator ==(FrameTextureHandle left, FrameTextureHandle right) => left.Equals(right);

    public static bool operator !=(FrameTextureHandle left, FrameTextureHandle right) => !left.Equals(right);

    public override string ToString() => IsValid
        ? $"{Slot}#v{View}@{Generation}"
        : $"{Slot}#v{View}@none";
}

/// <summary>
/// Slot table of one owner: either a view's set (<see cref="ViewTargets.Textures"/>) or the frame's
/// global set (<see cref="FrameState.Textures"/>). Fixed-size and allocation-free after construction.
///
/// Generation rules: publishing a value that differs from the current one (including publishing
/// null over a value) increments the generation, so a rebuild or a revocation invalidates every
/// outstanding handle. Publishing the same string again is a no-op, which keeps the steady state
/// free of version churn - the previous implementation compared strings in the backend every frame
/// precisely because it had no other way to tell "same" from "rebuilt".
/// </summary>
public sealed class FrameTextureTable
{
    struct Entry
    {
        public string? Name;
        public uint Generation;
    }

    readonly Entry[] _entries = new Entry[(int)FrameTextureSlot.FogHistory1 + 1];

    internal FrameTextureTable(int ownerView) => OwnerView = ownerView;

    /// <summary>View index this table belongs to, or -1 for the frame-global table.</summary>
    public int OwnerView { get; }

    /// <summary>Raw published name, or null. Consumers should normally go through a slot accessor so the scope rules stay in one place.</summary>
    public string? NameOf(FrameTextureSlot slot) => _entries[(int)slot].Name;

    /// <summary>Current handle of a slot; <see cref="FrameTextureHandle.IsValid"/> is false until something is published.</summary>
    public FrameTextureHandle Handle(FrameTextureSlot slot)
        => new(slot, OwnerView, _entries[(int)slot].Generation);

    /// <summary>
    /// Publishes (or revokes, with null) one slot. Returns true when the generation advanced, i.e.
    /// when a handle taken before this call is now stale.
    /// </summary>
    internal bool Publish(FrameTextureSlot slot, string? name)
    {
        ref var entry = ref _entries[(int)slot];
        if (string.Equals(entry.Name, name, StringComparison.Ordinal)) return false;
        entry.Name = name;
        entry.Generation++;
        return true;
    }

    /// <summary>Resolves a handle against this table; false when it is stale or belongs to another owner.</summary>
    public bool TryResolve(in FrameTextureHandle handle, out string? name)
    {
        name = null;
        if (handle.View != OwnerView) return false;
        ref var entry = ref _entries[(int)handle.Slot];
        if (handle.Generation == 0 || handle.Generation != entry.Generation) return false;
        name = entry.Name;
        return true;
    }

    /// <summary>Drops every entry, invalidating all outstanding handles. Used when a view retires.</summary>
    internal void Clear()
    {
        for (int i = 0; i < _entries.Length; i++)
        {
            ref var entry = ref _entries[i];
            if (entry.Name == null) continue;
            entry.Name = null;
            entry.Generation++;
        }
    }
}
