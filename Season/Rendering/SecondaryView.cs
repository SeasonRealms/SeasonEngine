// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

/// <summary>
/// One additional camera rendered into its own offscreen attachments and published as a texture, which is everything
/// a minimap, a rear-view mirror or a security-camera monitor needs. The class owns the whole lifecycle: it allocates
/// a view slot, creates the fixed-size targets, registers the view with <see cref="FrameSchedule"/>, publishes the
/// presentation image through <see cref="IGraphics.RegisterViewSurface"/>, and releases all of it again on
/// <see cref="Dispose"/>.
///
/// <para>Usage is one call plus one sprite:</para>
/// <code>
/// _mirror = SecondaryView.TryCreate(g, "view://rear-mirror", camera, 512, 288, mirrorFeatures);
/// new Sprite2D { Name = "view://rear-mirror", PosX = ..., Width = ..., Height = ... };
/// </code>
///
/// <para>Design notes:</para>
/// - <b>Fixed size</b>: the targets do not follow the backbuffer. A resize therefore never rebuilds the resource
///   behind the published texture, so the wrappers a control holds stay valid for the view's whole lifetime.
/// - <b>Same MRT shape as the main view</b>: D3D12 bakes one pipeline state per process with a fixed number of render
///   targets, so a view that dropped the velocity/normal slots would bind fewer attachments than the PSO declares.
///   The mirrors copy whichever slots <see cref="FrameSchedule.PrimaryTargets"/> has.
/// - <b>No DPI compensation</b>: the view renders 1:1 into its own target and the control scales it on screen, so
///   <see cref="RenderView.Prepare(float, float)"/> is called through the backend's identity path.
/// - <b>Declines instead of degrading</b>: when the backend cannot render a second view (MSAA on DX, a backend without
///   per-view constants), or the slot/descriptor budget is exhausted, <see cref="TryCreate"/> returns null and the app
///   keeps its single-view look instead of showing garbage.
/// </summary>
public sealed class SecondaryView : IDisposable
{
    /// <summary>Framebuffer of the view before presentation: the Scene pass output, sampled by per-view effects.</summary>
    public RenderTarget? SceneColor => Targets.SceneColor;

    /// <summary>The LDR image the view presented into, i.e. what the published texture samples.</summary>
    public RenderTarget? UiColor => Targets.UiColor;

    /// <summary>View slot this instance owns; 0 is reserved for the main view.</summary>
    public int Index => View.Index;

    /// <summary>Name the presentation image is published under, for <c>Sprite2D.Name</c>.</summary>
    public string TextureName { get; }

    /// <summary>Camera-facing half of the view. Pose changes need no bookkeeping; the next frame picks them up.</summary>
    public RenderView View { get; }

    /// <summary>Attachment set the view renders into, already registered with <see cref="FrameSchedule"/>.</summary>
    public ViewTargets Targets { get; }

    readonly IGraphics _graphics;
    readonly List<RenderTarget> _owned = new();
    bool _disposed;

    SecondaryView(IGraphics graphics, RenderView view, ViewTargets targets, string textureName)
    {
        _graphics = graphics;
        View = view;
        Targets = targets;
        TextureName = textureName;
    }

    /// <summary>
    /// Creates and registers a secondary view. Returns null (and logs) when this backend or this process cannot render
    /// an additional view, so callers can treat "no view" and "unsupported backend" the same way.
    /// </summary>
    /// <param name="graphics">Backend that owns the resources.</param>
    /// <param name="textureName">Name the finished image is published under; later sampled by name, like any texture.</param>
    /// <param name="camera">Camera the view looks through. Its pose and lens stay owned by the caller.</param>
    /// <param name="width">Render width in pixels (fixed; independent of the window size).</param>
    /// <param name="height">Render height in pixels (fixed).</param>
    /// <param name="features">Feature switches for this view. A mirror usually wants everything except shadows,
    /// anti-aliasing and post; see <see cref="ViewFeature"/>.</param>
    /// <param name="shadowPolicy">Whether to sample the frame-wide shadow atlas. Default <see cref="ViewShadowPolicy.Off"/>:
    /// the atlas texel density of the main view is meaningless at minimap scale, and turning it off skips the lookup.</param>
    /// <param name="updateInterval">Render once every N frames (1 = every frame). Intermediate frames keep the previous
    /// image, which is what a slowly changing minimap wants.</param>
    public static SecondaryView? TryCreate(IGraphics graphics, string textureName, Camera3D camera,
        int width, int height, ViewFeature features,
        ViewShadowPolicy shadowPolicy = ViewShadowPolicy.Off, int updateInterval = 1)
    {
        if (graphics == null || camera == null || textureName.IsNullOrWhiteSpace())
            return null;
        if (width <= 0 || height <= 0)
        {
            Log($"'{textureName}' has an empty render size ({width}x{height}).");
            return null;
        }
        if (!graphics.SecondaryViewSupported)
        {
            // Not an error: the tier simply cannot show a second camera, and the app's single-view look stays intact.
            Log($"'{textureName}' skipped: this backend/tier does not support secondary views.");
            return null;
        }

        // The slot indexes both the per-view constant buffers and the per-view texture scope suffix, so it has to be
        // free for the whole lifetime of this view.
        int index = FrameSchedule.AllocateViewIndex();
        if (index < 0)
        {
            Log($"'{textureName}' skipped: all {FrameSchedule.MaxViews} view slots are taken.");
            return null;
        }

        var primary = FrameSchedule.PrimaryTargets;
        // The Scene color format follows the main view exactly: the PSO's RTV0 format is baked once per process from
        // the HDR switch, so a mismatching format would be rejected at draw time.
        var colorFormat = primary.SceneColor?.Desc.ColorFormat ?? RtFormat.BackbufferCompatible;

        var targets = new ViewTargets(index, textureName) { Present = false };
        var view = new RenderView(camera, index)
        {
            Features = features,
            ShadowPolicy = shadowPolicy,
            UpdateInterval = updateInterval,
        };
        var created = new SecondaryView(graphics, view, targets, textureName);

        try
        {
            targets.SceneColor = created.CreateTarget("SceneColor", new RenderTargetDesc
            {
                ColorFormat = colorFormat,
                MatchBackbufferSize = false,
                Width = (uint)width,
                Height = (uint)height,
                SampleCount = 1,
            });

            // Always private: without it the Scene pass would bind the full-screen global depth buffer, whose size
            // and raster position have nothing to do with this view's target.
            targets.SceneDepth = created.CreateTarget("SceneDepth", new RenderTargetDesc
            {
                DepthFormat = RtFormat.D32Float,
                MatchBackbufferSize = false,
                Width = (uint)width,
                Height = (uint)height,
                SampleCount = 1,
            });

            // Mirror the main view's MRT shape; see the class remarks. The zero clear matches the optimized clear
            // value Device bakes for these slots, so no CLEARRENDERTARGETVIEW_MISMATCHINGCLEARVALUE warnings appear.
            if (primary.SceneVelocity != null)
            {
                targets.SceneVelocity = created.CreateTarget("SceneVelocity", new RenderTargetDesc
                {
                    ColorFormat = RtFormat.Rg16Float,
                    MatchBackbufferSize = false,
                    Width = (uint)width,
                    Height = (uint)height,
                    SampleCount = 1,
                    NoImplicitDepth = true,
                    ClearColor = Vector4.Zero,
                });
            }
            if (primary.SceneNormal != null && targets.SceneVelocity != null)
            {
                targets.SceneNormal = created.CreateTarget("SceneNormal", new RenderTargetDesc
                {
                    ColorFormat = RtFormat.Rgba16Float,
                    MatchBackbufferSize = false,
                    Width = (uint)width,
                    Height = (uint)height,
                    SampleCount = 1,
                    NoImplicitDepth = true,
                    ClearColor = Vector4.Zero,
                });
            }

            // The presentation image: same format as the backbuffer, because the FinalBlit that fills it is the very
            // same tonemap/RGBA variant that would have written the backbuffer.
            targets.UiColor = created.CreateTarget("UiColor", new RenderTargetDesc
            {
                ColorFormat = RtFormat.BackbufferCompatible,
                MatchBackbufferSize = false,
                Width = (uint)width,
                Height = (uint)height,
                SampleCount = 1,
                NoImplicitDepth = true,
            });

            if (!graphics.RegisterViewSurface(textureName, targets.UiColor))
            {
                Log($"'{textureName}' skipped: the backend refused to publish the view surface (name already in use?).");
                created.Dispose();
                return null;
            }

            FrameSchedule.AddView(view, targets);
            return created;
        }
        catch (Exception ex)
        {
            // Descriptor slots, formats and sizes are validated by the backend; a refusal leaves nothing behind.
            Log($"'{textureName}' failed to create its targets: {ex.Message}");
            created.Dispose();
            return null;
        }
    }

    RenderTarget CreateTarget(string label, in RenderTargetDesc desc)
    {
        var target = _graphics.CreateRenderTarget(desc);
        _owned.Add(target);
        return target;
    }

    /// <summary>
    /// Pointer (in layout pixels, the space of control PosX/PosY) to world ray through this view.
    /// <see cref="ScreenRect"/> decides whether the pointer is over the view at all; the fraction it produces is
    /// independent of the render resolution, so the view can be displayed at any on-screen size.
    /// <paramref name="flipX"/> mirrors the horizontal fraction for widgets that display the view through a
    /// <c>Sprite2D</c> with <c>FlipX</c>: such a sprite shows the texture back to front, so a click on its left
    /// half belongs to the view's right half.
    /// </summary>
    public bool TryPick(Vector2 pointer, out Vector3 origin, out Vector3 direction, bool flipX = false)
    {
        direction = Vector3.UnitZ;
        origin = View.Position;
        if (_disposed || View.ScreenRect.IsEmpty || !View.ScreenRect.Contains(pointer)) return false;
        var rect = View.ScreenRect;
        float u = (pointer.X - rect.X) / rect.Width;
        if (flipX) u = 1f - u;
        return View.PickRay(u, (pointer.Y - rect.Y) / rect.Height, out origin, out direction);
    }

    /// <summary>
    /// Detaches the view and releases its targets. Dispose the controls that displayed it first: their sprite still
    /// holds the published wrapper, and only the target resources are released here.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        FrameSchedule.RemoveView(View);
        if (_graphics != null) _graphics.UnregisterViewSurface(TextureName);

        for (int i = 0; i < _owned.Count; i++) _owned[i].Dispose();
        _owned.Clear();
        Targets.SceneColor = null;
        Targets.SceneDepth = null;
        Targets.SceneVelocity = null;
        Targets.SceneNormal = null;
        Targets.UiColor = null;
    }

    static void Log(string message) => DeviceServices.BaseApp?.AddLog(LogType.None, $"{DateTime.UtcNow} [SecondaryView] {message}");

    public override string ToString() => $"SecondaryView[{Index}:{TextureName}]";
}
