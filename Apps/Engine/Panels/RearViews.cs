// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Engine.Panels;

/// <summary>
/// P3 acceptance widget: the island scene seen through two extra cameras, laid out like cockpit instruments.
///
/// <para>Layout: the rear-view mirror is pinned to the top centre with a <see cref="Padding"/> gap below the screen
/// edge, and the minimap sits immediately to its right. The pair keeps that arrangement at any resolution because
/// the whole block is centred from <see cref="Season.Basic.BaseApp.ExtendResolution"/> every frame.</para>
///
/// <para>Both instruments are <see cref="SecondaryView"/>s: a camera plus its own fixed-size attachments, published as
/// a texture that an ordinary <see cref="Sprite2D"/> samples. That is the whole bridge - no new control type, no
/// readback, no platform code in this file.</para>
///
/// <para>They also demonstrate the per-view policy that this feature added:</para>
/// - <c>Features</c>: the mirror keeps opaque and transparent geometry but drops shadows, fog, weather, AO, bloom,
///   anti-aliasing, particles and the Post slot; the minimap keeps opaque geometry only.
/// - <c>ShadowPolicy.Off</c>: both read the white fallback instead of the main view's cascaded atlas, whose texel
///   density is meaningless at this size.
/// - <c>UpdateInterval</c>: the minimap re-renders every few frames and keeps its previous image in between.
/// </summary>
internal class RearViews : Panel
{
    // -- Layout, in design units (Windows: one unit is one pixel) --

    /// <summary>Gap between the top screen edge and the instruments.</summary>
    public const float Padding = 24f;

    public const float MirrorWidth = 480f;
    public const float MirrorHeight = 270f;
    public const float MinimapSize = 200f;

    /// <summary>Horizontal gap between the mirror and the minimap.</summary>
    public const float Gap = 16f;

    // -- Render resolution, independent of the on-screen size --

    // A mirror does not need display resolution: rendering it smaller cuts the second pass chain proportionally,
    // and the blit upsamples it linearly into the display-sized UiColor target.
    const int MirrorRenderWidth = 384;
    const int MirrorRenderHeight = 216;
    const int MinimapRenderSize = 192;

    /// <summary>Re-render the map once every N frames; the mirror stays at one frame for responsiveness.</summary>
    const int MinimapUpdateInterval = 3;

    /// <summary>World extent the orthographic map covers vertically (and, being square, horizontally).</summary>
    const float MinimapWorldHeight = 140f;

    /// <summary>Height of the map camera above the ground; it has to stay inside the orthographic clip range.</summary>
    const float MinimapCameraHeight = 200f;

    /// <summary>Transparent geometry and opaque geometry, nothing else: the cheapest useful rear view.</summary>
    const ViewFeature MirrorFeatures = ViewFeature.Opaque | ViewFeature.Transparent;

    /// <summary>Opaque only: a plan view has no use for sorted transparency, and it halves the mirror's cost.</summary>
    const ViewFeature MinimapFeatures = ViewFeature.Opaque;

    public const string MirrorTextureName = "view://rear-mirror";
    public const string MinimapTextureName = "view://minimap";

    readonly Camera3D _mirrorCamera = new()
    {
        FovY = MathF.PI * 13 / 36f,
        Near = 0.1f,
        Far = 1300f,
    };

    readonly Camera3D _minimapCamera = new()
    {
        Mode = CameraProjectionMode.Orthographic,
        OrthographicHeight = MinimapWorldHeight,
        Near = 1f,
        Far = 400f,
        // Straight down, so the up vector cannot be +Y. +Z keeps world X to the right on screen and world Z up,
        // which is the direction the sample scene starts facing.
        Up = Vector3.UnitZ,
    };

    // Both pairs are optional: a backend that declines a secondary view leaves them null and the widget simply
    // does not exist, which is why they are declared nullable rather than forced with null-forgiving operators.
    Sprite2D? _mirrorSprite;
    Sprite2D? _minimapSprite;
    SecondaryView? _mirror;
    SecondaryView? _minimap;

    public RearViews()
    {
        RenderDomain = Season.Controls.RenderDomain.Overlay;

        var graphics = Season.Basic.Graphics.Instance;
        _mirror = SecondaryView.TryCreate(graphics, MirrorTextureName, _mirrorCamera,
            MirrorRenderWidth, MirrorRenderHeight, MirrorFeatures);
        _minimap = SecondaryView.TryCreate(graphics, MinimapTextureName, _minimapCamera,
            MinimapRenderSize, MinimapRenderSize, MinimapFeatures,
            ViewShadowPolicy.Off, MinimapUpdateInterval);

        // The publications exist before the sprites name them, so LoadSprite2D finds the wrapper instead of trying
        // to decode a file. A view the backend declined leaves no control behind at all.
        if (_mirror != null)
        {
            _mirrorSprite = new Sprite2D
            {
                Name = MirrorTextureName,
                Color = Season.Basic.Colors.White,
                // A mirror shows the world reversed; the view itself looks backwards, this restores left/right.
                FlipX = true,
                Width = (int)MirrorWidth,
                Height = (int)MirrorHeight,
                Alpha = 1f,
            };
            AddControl(_mirrorSprite);
        }

        if (_minimap != null)
        {
            _minimapSprite = new Sprite2D
            {
                Name = MinimapTextureName,
                Color = Season.Basic.Colors.White,
                Width = (int)MinimapSize,
                Height = (int)MinimapSize,
                Alpha = 1f,
            };
            AddControl(_minimapSprite);
        }
    }

    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? posZ = null, float? width = null, float? height = null, float? depth = null)
    {
        var result = base.Update(time, alpha: alpha, posX: posX, posY: posY, posZ: posZ, width: width, height: height, depth: depth);

        var app = App.Instance;
        if (app != null)
        {
            Layout(app);
            SyncCameras(app);
            PickPointer(app);
        }

        _mirrorSprite?.Update(time, alpha: Alpha);

        _minimapSprite?.Update(time, alpha: Alpha);

        return result;
    }

    /// <summary>
    /// Centres the pair on the screen: mirror first, then the map to its right. When a view was declined the
    /// remaining one still centres alone, so an unsupported tier shows one instrument instead of a hole.
    /// </summary>
    void Layout(Season.Basic.BaseApp app)
    {
        // Snapshotted once: a sprite and its view only ever exist together, so pairing the locals keeps the layout
        // allocation-free and lets the compiler see the null checks.
        var mirrorSprite = _mirrorSprite;
        var mapSprite = _minimapSprite;
        var mirrorView = _mirror?.View;
        var mapView = _minimap?.View;
        bool hasMirror = mirrorSprite != null && mirrorView != null;
        bool hasMinimap = mapSprite != null && mapView != null;
        float total = (hasMirror ? MirrorWidth : 0f) + (hasMirror && hasMinimap ? Gap : 0f) + (hasMinimap ? MinimapSize : 0f);
        float left = MathF.Max(0f, (app.ExtendResolution.X - total) * 0.5f);

        if (hasMirror)
        {
            mirrorSprite!.PosX = left;
            mirrorSprite.PosY = Padding;
            mirrorView!.ScreenRect = new Rect2D(left, Padding, MirrorWidth, MirrorHeight);
            left += MirrorWidth + Gap;
        }

        if (hasMinimap)
        {
            mapSprite!.PosX = left;
            mapSprite.PosY = Padding;
            mapView!.ScreenRect = new Rect2D(left, Padding, MinimapSize, MinimapSize);
        }
    }

    /// <summary>
    /// Drives both cameras from the main one. The rear view keeps the eye position and looks the opposite way, which
    /// is what a mirror on the view axis shows; the map follows the point the main camera looks at, so it stays
    /// centred on the player while they move.
    /// </summary>
    void SyncCameras(Season.Basic.BaseApp app)
    {
        var camera = app.Camera;

        if (_mirror != null)
        {
            var direction = camera.Target - camera.Position;
            _mirrorCamera.Position = camera.Position;
            _mirrorCamera.Target = camera.Position - direction;
            _mirrorCamera.FovY = camera.FovY;
            _mirrorCamera.Near = camera.Near;
            _mirrorCamera.Far = camera.Far;
        }

        if (_minimap != null)
        {
            var focus = app.CameraTarget;
            _minimapCamera.Position = new Vector3(focus.X, MinimapCameraHeight, focus.Z);
            _minimapCamera.Target = new Vector3(focus.X, 0f, focus.Z);
        }
    }

    /// <summary>
    /// Per-view picking acceptance: a click inside an instrument is cast through that instrument's camera, not the
    /// main one. The hit is logged rather than acted on, but the coordinates are checkable against the map itself.
    /// </summary>
    void PickPointer(Season.Basic.BaseApp app)
    {
        if (!TouchService.IsReleased || TouchService.LastDownPos is not Vector2 pointer) return;

        // Both the press and the layout below live in logical pixels, the space of control PosX/PosY, so the pointer
        // needs no further division: whatever the platform's device scale is, one design unit is one unit here.

        if (TryPickMinimap(pointer, out var origin, out var direction))
        {
            // The map looks straight down, so the ground plane y = 0 gives the world point under the pointer.
            if (MathF.Abs(direction.Y) > 1e-4f)
            {
                float t = -origin.Y / direction.Y;
                if (t > 0f)
                {
                    var hit = origin + direction * t;
                    app.AddLog(LogType.None, $"{DateTime.UtcNow} [P3][Pick] minimap -> world ({hit.X:F1}, {hit.Y:F1}, {hit.Z:F1})");
                    return;
                }
            }
        }

        if (TryPickMirror(pointer, out _, out var forward))
        {
            app.AddLog(LogType.None, $"{DateTime.UtcNow} [P3][Pick] rear-mirror -> dir ({forward.X:F2}, {forward.Y:F2}, {forward.Z:F2})");
        }
    }

    /// <summary>Pointer in design units to a world ray through the rear-view mirror.</summary>
    public bool TryPickMirror(Vector2 pointer, out Vector3 origin, out Vector3 direction)
    {
        origin = default;
        direction = Vector3.UnitZ;
        // The sprite displays the view mirrored, so the horizontal fraction has to be mirrored back before the ray.
        return _mirror != null && _mirror.TryPick(pointer, out origin, out direction, flipX: _mirrorSprite?.FlipX ?? false);
    }

    /// <summary>Pointer in design units to a world ray through the minimap.</summary>
    public bool TryPickMinimap(Vector2 pointer, out Vector3 origin, out Vector3 direction)
    {
        origin = default;
        direction = Vector3.UnitZ;
        return _minimap != null && _minimap.TryPick(pointer, out origin, out direction);
    }

    public override void Dispose()
    {
        _mirror?.Dispose();
        _minimap?.Dispose();
        _mirror = null;
        _minimap = null;

        base.Dispose();
    }
}
