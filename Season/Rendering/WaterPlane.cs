// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Collections.ObjectModel;
using System.Numerics;

namespace Season.Rendering;

/// <summary>
/// Data description of a P2 water surface. A water surface is an axis-aligned horizontal rectangle, centered at <see cref="Position"/> with edge lengths in <see cref="Size"/>,
/// and its normal is always +Y. The backend computes the geometry on the fly from SV_VertexID (two triangles, 6 vertices), so no mesh data is carried here.
///
/// It plays the same role as <see cref="DecalVolume"/>: the authoring layer only writes data, and the rendering layer collects it on demand each frame. The difference is that a water surface has no volume,
/// so it needs no local box coordinates, and the shader computes UVs and normal perturbation directly from world position.
/// </summary>
public sealed class WaterPlane
{
    internal SceneWaters? Owner;

    public bool Enabled { get; set; } = true;

    /// <summary>World position of the water surface center (the rectangle lies in the XZ plane, and Y is the surface height).</summary>
    public Vector3 Position { get; set; }

    /// <summary>World size in the XZ directions (meters).</summary>
    public Vector2 Size { get; set; } = new(8f, 8f);

    /// <summary>Transmitted color of the water body itself: the color reflected back from the bottom as thickness approaches infinity.</summary>
    public Vector4 Tint { get; set; } = new(0.055f, 0.20f, 0.235f, 1f);

    /// <summary>Base opacity where the normal is flat. Fresnel pushes it toward 1 at grazing angles.</summary>
    public float Opacity { get; set; } = 0.32f;

    /// <summary>Absorption coefficient (1/meter). Attenuates as e^(-absorption * thickness), with the color taken from the inverse spectrum of <see cref="Tint"/>.</summary>
    public float Absorption { get; set; } = 2.2f;

    /// <summary>Maximum pixel fraction of the screen-space refraction offset; 0 disables refraction and samples the bottom directly beneath.</summary>
    public float RefractionStrength { get; set; } = 0.035f;

    /// <summary>
    /// Amplitude multiplier for the normal perturbation. 1 means taking the gradient calibrated in the shader against real light wind waves (~3~4° of tilt),
    /// 0 is a perfectly flat surface (only Fresnel and rain ripples remain), and values above 1 are for an exaggerated "boiling" water surface.
    /// Note that 1 here corresponds to the gradient dimension, not the arbitrary normalized value of older versions.
    /// </summary>
    public float NormalStrength { get; set; } = 1f;

    /// <summary>UV scale of the normal map: the world spacing over which one texture tile repeats on the water surface.</summary>
    public float NormalTiling { get; set; } = 0.12f;

    /// <summary>Opposite scroll speed of the two normal layers (UV/second). The second layer uses 1.37× the frequency and 0.8× the speed, to avoid freezing when the beat frequency cancels out.</summary>
    public float ScrollSpeed { get; set; } = 0.035f;

    /// <summary>Amplitude of the rain ripples. The actual amplitude is further multiplied by whether it is raining this frame (0/1), so on a clear day they never appear at all.</summary>
    public float RippleStrength { get; set; } = 0.9f;

    /// <summary>Fresnel reflectance at normal incidence. Water has an IOR of about 1.333 at 20°C, giving about 0.02 at normal incidence.</summary>
    public float FresnelF0 { get; set; } = 0.02f;

    /// <summary>Roughness of the reflection: 0 = mirror-like sky, and larger values approach a blurred bright spot.</summary>
    public float Roughness { get; set; } = 0.06f;

    // ---- P4: Gerstner displacement and tiled instancing ----

    /// <summary>
    /// Wind speed (m/s). 0 disables Gerstner vertex displacement and falls back to P3's "pixel-side normal perturbation only" -- the surface stays flat,
    /// just without any geometric relief. This value determines both the wavelength spectrum and the phase velocity: the dominant wave uses the wavelength
    /// λ_peak = 2πU²/g of a fully developed sea, and the four components are laid out as a geometric series; the phase velocity follows automatically from deep-water dispersion c = sqrt(g/k).
    /// </summary>
    public float WindSpeed { get; set; }

    /// <summary>Wind direction (radians, in the world XZ plane, 0 = +X, π/2 = +Z). The four wave components fan out ±30° around it.</summary>
    public float WindDirection { get; set; }

    /// <summary>
    /// Total crest-to-trough height (meters). The four waves distribute the amplitude by weight, summing to half of it (since the peak of sin is 1 while the crest-to-trough span is 2A).
    /// This value affects vertex displacement only; the high-frequency detail waves on the pixel side are unaffected by it.
    /// </summary>
    public float SwellHeight { get; set; } = 1f;

    /// <summary>
    /// Horizontal displacement factor. 1 is standard Gerstner (sharper crests, flatter troughs); 0 degenerates into a purely vertical sine (rounded waves);
    /// values above 1 self-intersect and are only used for an exaggerated "blade-like" swell.
    /// </summary>
    public float Choppiness { get; set; } = 1f;

    /// <summary>
    /// Phase velocity multiplier. 1 = physical phase velocity sqrt(g/k); larger values make the waves travel faster, smaller values make them heavier.
    /// The spatial frequency of the waves is unaffected, so it is a purely "temporal" parameter that does not change the wavelength structure of the image.
    /// </summary>
    public float WaveSpeed { get; set; } = 1f;

    /// <summary>
    /// Maximum edge length of one tile in the tiled instancing (meters). 0 means no tiling: the whole rectangle uses only 6 vertices (the P3 behavior),
    /// which suits small surfaces such as ponds. Ocean surfaces must be tiled, otherwise a 1400m rectangle has vertices only at its 4 corners and there is nowhere to evaluate Gerstner.
    /// </summary>
    public float TileSize { get; set; }

    /// <summary>
    /// Number of subdivision segments per tile (each tile is subdiv×subdiv quads). 0 is equivalent to <see cref="TileSize"/> being 0.
    /// The vertex count is tilesX×tilesZ×subdiv²×6, so together with TileSize it determines the vertex budget.
    /// </summary>
    public int TileSubdiv { get; set; }

    /// <summary>Optional normal texture name. Empty means the procedural dual sine wave is used, depending on no resources.</summary>
    public string? NormalTexture { get; set; }

    /// <summary>Same as <see cref="DecalVolume.SortOrder"/>: smaller values are drawn first, and a stable sort keeps registration order for equal values.</summary>
    public float SortOrder { get; set; }

    /// <summary>Model-to-world matrix: scales the [-0.5,0.5]² unit plane up to <see cref="Size"/> and translates it to <see cref="Position"/>.</summary>
    public Matrix4x4 WorldMatrix => Matrix4x4.CreateScale(Size.X, 1f, Size.Y)
        * Matrix4x4.CreateTranslation(Position);

    /// <summary>Shader-side world-space UV origin, equal to the minimum corner of the rectangle; used so the two normal layers do not stretch with the rectangle size.</summary>
    public Vector2 UvOrigin => new(Position.X - Size.X * 0.5f, Position.Z - Size.Y * 0.5f);
}

/// <summary>
/// P2 scene water-surface registry, isomorphic to <see cref="SceneDecals"/>: the authoring layer only adds and removes, and the rendering layer collects on demand each frame.
///
/// The limit is counted as one 6-vertex small draw per water surface, not a video-memory limit. The real draw cost of water surfaces comes mainly from refraction sampling,
/// independent of the number of surfaces (each surface resamples the scene color once per screen), so this number should be conservative.
/// </summary>
public sealed class SceneWaters
{
    public const int MaxActiveWaters = 32;

    readonly List<WaterPlane> _waters = new();
    readonly ReadOnlyCollection<WaterPlane> _view;

    public SceneWaters() => _view = _waters.AsReadOnly();

    public IReadOnlyList<WaterPlane> Waters => _view;

    public int ActiveCount
    {
        get
        {
            int count = 0;
            for (int i = 0; i < _waters.Count; i++) if (_waters[i].Enabled) count++;
            return count;
        }
    }

    internal void EnsureActiveCapacity()
    {
        if (ActiveCount >= MaxActiveWaters)
            throw new InvalidOperationException($"A scene supports at most {MaxActiveWaters} active water surfaces.");
    }

    public void Add(WaterPlane water)
    {
        ArgumentNullException.ThrowIfNull(water);
        if (ReferenceEquals(water.Owner, this)) return;
        if (water.Owner != null)
            throw new InvalidOperationException("A water surface cannot be registered to multiple scenes at once.");
        if (water.Enabled) EnsureActiveCapacity();
        _waters.Add(water);
        water.Owner = this;
    }

    public bool Remove(WaterPlane water)
    {
        if (!_waters.Remove(water)) return false;
        water.Owner = null;
        return true;
    }

    public void Clear()
    {
        for (int i = 0; i < _waters.Count; i++) _waters[i].Owner = null;
        _waters.Clear();
    }
}
