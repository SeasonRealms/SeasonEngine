// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Controls;

/// <summary>
/// An invisible, selectable, placeable <b>box volume</b> that drives a rect-spawn particle emitter
/// (waterfall spray, smoke columns, dust). It is the same recipe as <see cref="Waterfall"/>:
///
/// <list type="bullet">
/// <item>the control draws nothing itself: the puffs are still submitted by the particle system, which only reads
/// the box's center, yaw, and size each frame;</item>
/// <item>but it <b>is</b> a plain <see cref="Mesh3D"/> box, so it inherits the property panel
/// (PosX/PosY/PosZ, Width/Height/Depth, yaw), triangle-level picking, and the Bounds/Wireframe/Outline highlight
/// styles, exactly like a house or a rock; the default style is set to Wireframe below, see the comment there;</item>
/// <item>the emitter reads <see cref="Center"/>, <see cref="SpawnAxis"/>, <see cref="HalfLength"/>,
/// <see cref="HalfDepth"/> and <see cref="WorldHeight"/>; the box therefore <b>is</b> the parameterization —
/// moving/scaling/rotating it in the panel edits the effect live, no code constants involved.</item>
/// </list>
///
/// Why the proxy is invisible: the material uses <see cref="SurfaceBlendMode.Opaque"/> while <c>Alpha</c> is a tiny
/// value, so the backend sorts it into the Fade bucket; setting <see cref="Mesh3D.ExcludeFromAo"/> to true then makes
/// that bucket skip depth writes, otherwise the box would depth-test the translucent puffs behind it out of existence.
/// Color and alpha are both zero, so the blend result equals the background itself, leaving only the highlight edges.
///
/// Using Mask + AlphaCutoff=1 to hide it is <b>wrong</b>: the Outline mask shader also clips when alphaMode==1
/// (see <c>Pipeline.PSOutlineMask</c>), so the outline would disappear entirely.
/// </summary>
public sealed class MistVolume : Mesh3D
{
    // Placement input waiting to be applied: Place may arrive before Load, when there is no bounding box yet.
    bool _placed;
    Vector3 _placeCenter;
    float _placeYawRadians;
    Vector3? _placeSize;

    /// <summary>
    /// Creates a 1 m unit-cube proxy at the origin. The anchor of a centered box is its own center, so
    /// PosX/PosY/PosZ in the property panel are literally the box center and Width/Height/Depth are its world extents.
    /// </summary>
    public MistVolume(string name = "MistVolume")
    {
        Name = name;

        // Not 0: DrawMesh3D returns immediately when Alpha == 0, in which case not even the bounding box and the
        // highlight would be submitted and picking would never light up.
        Alpha = 0.001f;

        // Keep the proxy out of SceneDepth so the particles behind it are not culled by their own emitter box.
        ExcludeFromAo = true;
        CastShadows = false;

        // Highlight style: Wireframe instead of the engine default Bounds, because Bounds is the *world AABB*
        // (Mesh3DBase.GetWorldBoundsRaw, see DXMesh3D's WriteHighlightBox). Rotating this box by yaw therefore
        // inflates the axis-aligned box instead of turning it -- a 8.8 x 2.4 m mist band at 45 degrees would show
        // as an ~8 x 8 m box, which reads as "rotation scales it" and hides the fact that the band did turn.
        // The Wireframe shell is baked from this proxy's own cube and drawn with the same world matrix, so the
        // highlight is an <b>oriented</b> box: it turns with the panel's rotation and every knob stays legible.
        Highlight.Style = HighlightStyle.Wireframe;

        // 2 x EdgeWidth = strip width relative to the box's largest side. 0.004 gives ~7 cm strips on the default
        // 8.8 m long band; the 0.002 default is nearly invisible on a proxy this large.
        Highlight.EdgeWidth = 0.004f;

        BuildBox();
    }

    /// <summary>True once <see cref="Place"/> was called; an instance that was never placed keeps the unit box at the origin.</summary>
    public bool Placed => _placed;

    /// <summary>
    /// Places the volume at the world center point <paramref name="center"/>, rotated <paramref name="yawDegrees"/>
    /// degrees around the Y axis, with per-axis target size <paramref name="size"/> in meters.
    ///
    /// <paramref name="center"/> and the panel's PosX/PosY/PosZ are <b>the same quantity</b> (the box center), and
    /// <paramref name="size"/> matches Width/Height/Depth, so the visual tuning loop is closed: drag or type a number,
    /// then copy the panel values back here to reproduce the layout at startup. A null or non-positive axis keeps the
    /// template's local size.
    ///
    /// Callable before <see cref="Load"/>; the input is applied immediately (Pos is the box center and has nothing to do
    /// with the bounding box), so an emitter reading the box already works on the very first frame even if this proxie's
    /// GPU mesh is still queued. Load re-applies the same numbers once the anchor exists, so the two paths cannot diverge.
    /// </summary>
    public MistVolume Place(Vector3 center, float yawDegrees = 0f, Vector3? size = null)
    {
        _placed = true;
        _placeCenter = center;
        _placeYawRadians = yawDegrees * MathF.PI / 180f;
        _placeSize = size;
        ApplyPlacement();
        return this;
    }

    /// <summary>Loads the proxy and then applies the stored placement: only now is the anchor meaningful.</summary>
    public override async Task<bool> Load()
    {
        bool result = await base.Load();
        ApplyPlacement();
        return result;
    }

    /// <summary>Writes the stored placement into the unified positioning model's fields (Pos/Rotation/Size).</summary>
    void ApplyPlacement()
    {
        if (!_placed)
            return;

        // Rotation first, then size, then position: the anchor is the box center, so none of the three disturbs another.
        // Before Load the local size is still zero, so an unspecified axis stays 0 here and OnBoundsEstablished fills it
        // with the template size afterwards (it only touches null/0), which is exactly the intended fallback.
        Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, _placeYawRadians);

        var local = LocalSize;
        var size = _placeSize ?? Vector3.Zero;
        Width = Axis(size.X, local.X);
        Height = Axis(size.Y, local.Y);
        Depth = Axis(size.Z, local.Z);

        PosX = _placeCenter.X;
        PosY = _placeCenter.Y;
        PosZ = _placeCenter.Z;
    }

    /// <summary>Per-axis target size when it is a usable positive number, otherwise the current/template fallback.</summary>
    static float Axis(float target, float fallback) => target > 0f ? target : fallback;

    // -- Emitter-facing geometry: the single source the particle driver reads. --

    /// <summary>World center of the box, identical to the panel's PosX/PosY/PosZ.</summary>
    public Vector3 Center => new(PosX, PosY, PosZ);

    /// <summary>
    /// World direction of the box's local +X axis, flattened to the ground plane. This is the long axis of the rect
    /// spawn, which is why the emitter can be turned by rotating the box in the panel.
    /// </summary>
    public Vector3 SpawnAxis
    {
        get
        {
            var axis = Vector3.TransformNormal(Vector3.UnitX, GetRotationMatrix());
            axis.Y = 0f;
            float length = axis.Length();
            return length > 1e-6f ? axis / length : Vector3.UnitX;
        }
    }

    /// <summary>Half of Width: the rect spawn's half length along <see cref="SpawnAxis"/>.</summary>
    public float HalfLength => (Width ?? 0f) * 0.5f;

    /// <summary>Half of Depth: the rect spawn's half depth across <see cref="SpawnAxis"/>.</summary>
    public float HalfDepth => (Depth ?? 0f) * 0.5f;

    /// <summary>
    /// World height of the box. Particles are born on the bottom face and rise, so the driver maps this to their
    /// upward speed: the emitted cloud fills the box instead of floating at a fixed thickness.
    /// </summary>
    public float WorldHeight => Height ?? 0f;

    /// <summary>World Y of the box's bottom face, where the rect spawn sits.</summary>
    public float BottomY => PosY - WorldHeight * 0.5f;

    /// <summary>
    /// A unit cube in [-0.5, 0.5]^3 with outward faces, built with the same vertex order and winding as the stone
    /// boxes in Pond/WaterfallPool: quad vertices are (origin, +u, +v, +u+v) and indices {2,3,1,2,1,0}.
    /// </summary>
    void BuildBox()
    {
        Surfaces.Clear();

        var vertices = new Vertex[24];
        var indices = new ushort[36];
        int vertexIndex = 0;
        int indexIndex = 0;

        void Quad(Vector3 origin, Vector3 u, Vector3 v, Vector3 normal)
        {
            vertices[vertexIndex + 0] = MakeVertex(origin, normal);
            vertices[vertexIndex + 1] = MakeVertex(origin + u, normal);
            vertices[vertexIndex + 2] = MakeVertex(origin + v, normal);
            vertices[vertexIndex + 3] = MakeVertex(origin + u + v, normal);
            vertexIndex += 4;

            indices[indexIndex++] = (ushort)(vertexIndex - 2);
            indices[indexIndex++] = (ushort)(vertexIndex - 1);
            indices[indexIndex++] = (ushort)(vertexIndex - 3);
            indices[indexIndex++] = (ushort)(vertexIndex - 2);
            indices[indexIndex++] = (ushort)(vertexIndex - 3);
            indices[indexIndex++] = (ushort)(vertexIndex - 4);
        }

        var sx = new Vector3(1f, 0f, 0f);
        var sy = new Vector3(0f, 1f, 0f);
        var sz = new Vector3(0f, 0f, 1f);
        const float h = 0.5f;

        Quad(new Vector3(-h, -h, -h), sx, sy, new Vector3(0f, 0f, -1f));
        Quad(new Vector3(h, -h, h), -sx, sy, new Vector3(0f, 0f, 1f));
        Quad(new Vector3(-h, -h, h), -sz, sy, new Vector3(-1f, 0f, 0f));
        Quad(new Vector3(h, -h, -h), sz, sy, new Vector3(1f, 0f, 0f));
        Quad(new Vector3(-h, h, -h), sx, sz, new Vector3(0f, 1f, 0f));
        Quad(new Vector3(-h, -h, h), sx, -sz, new Vector3(0f, -1f, 0f));

        Surfaces.Add(new Surface
        {
            Vertices = vertices,
            Indices = indices,
            // All-zero color and alpha: the Fade bucket's blend result equals the background itself, so nothing is
            // drawn. Highlight (bounds / wireframe / outline) is submitted independently and does not read this color.
            BaseColor = Vector4.Zero,
            Mode = SurfaceBlendMode.Opaque,
            // Double-sided for the same reason as the Waterfall proxy: the camera can end up inside the box
            // (the mist box straddles the pond, which the player can walk into), and then the outline/wireframe
            // highlight would otherwise be back-face culled and vanish.
            DoubleSided = true,
            Unlit = true,
        });
    }

    static Vertex MakeVertex(Vector3 position, Vector3 normal)
    {
        return new Vertex
        {
            Position = position,
            Normal = normal,
            TexCoord = Vector2.Zero,
        };
    }
}
