// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Basic;

/// <summary>
/// Movement mode: World = top-down view (arrow keys stay aligned to the cardinal directions,
/// and after movement the camera resets to a fixed position above and behind the character);
/// Character = over-the-shoulder view (arrow keys are interpreted relative to the camera's
/// horizontal facing, the character faces forward relative to the camera, moving forward/backward
/// or strafing left/right, while the camera keeps its dragged pose and follows the actual displacement).
/// Switchable at runtime from the Setting panel.
/// </summary>
public enum Movement
{
    World,
    Character
}

public abstract class BaseApp : Panel
{
    public string Title { get; set; }

    public Vector2 DesignResolution { get; set; } = new Vector2(1280, 720);

    public Vector2 BasicResolution { get; set; } = new Vector2(1280, 720);

    public Vector2 ExtendResolution { get; set; } = new Vector2(1280, 720);

    public Vector2 DeviceResolution { get; internal set; }

    public Vector2 CompositionScale { get; internal set; }

    public List<string> Logs = new();

    public float Scale { get; internal set; } = 1f;

    public float FontSize = 32f;

    public Vector4 BackgroundColor { get; set; } = new Vector4(1f, 1f, 1f, 1f);

    public bool IsActive { get; internal set; }

    public bool FontsCreated = false;

    // Create an unbounded channel (a bounded channel could also be used to limit backlog):
    // queue items are any entities implementing ILoadable (leaf controls / panels / background tasks).
    // Loading is a cross-cutting capability and is unrelated to rendering; "Control" in method names
    // is preserved for historical reasons (TryDequeueControl/ProcessControlQueueFrame).
    private readonly System.Threading.Channels.Channel<ILoadable> _channel = System.Threading.Channels.Channel.CreateUnbounded<ILoadable>();

    // Deduplication: track queued items that have not finished processing yet
    // to prevent the same loadable from being queued multiple times.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ILoadable, byte> _pendingControls = new();

    /// <summary>
    /// Mutual-exclusion semaphore between HandleResize and control.Load().
    /// HandleResize uses Wait(timeout) to avoid blocking the render thread for too long;
    /// the loading side uses WaitAsync() so it waits asynchronously without blocking a thread.
    /// This ensures background threads do not perform GPU resource operations during resize
    /// (texture creation/upload/state transitions), avoiding SEHException when the D3D12 Debug Layer
    /// detects in-flight commands.
    /// </summary>
    public static readonly SemaphoreSlim ResizeSemaphore = new(1, 1);

    public float Time = 0f;

    public DateTime LastActiveTime { get; internal set; }

    public DateTime LastInActiveTime { get; internal set; }

    public static string Language
    {
        get
        {
            return CultureInfo.CurrentCulture.ToString();
        }
    }

    public static bool Debug
    {
        get
        {
            bool debug = false;
#if DEBUG
            debug = true;
#endif
            return debug;
        }
    }

    public static bool LogLoad = true;

    public static LogType LogTypes = LogType.None | LogType.Load | LogType.Error | LogType.Backend;

    public string Status { get; set; }

    /// <summary>
    /// 1-3: Main scene camera shared across all four platforms, serving as the single source of truth
    /// for FOV/near/far/frustum data. CameraPos/CameraTarget are compatibility forwarding properties,
    /// so existing call sites need no changes; once the platform frame loops are wired up, each backend
    /// drives matrix and frustum rebuilds every frame through Camera.UpdateIfChanged(aspect) with Changed gating.
    /// </summary>
    public Camera3D Camera { get; } = new Camera3D();

    /// <summary>Camera position (forwarded to <see cref="Camera"/>.Position for legacy field compatibility).</summary>
    public Vector3 CameraPos
    {
        get => Camera.Position;
        set => Camera.Position = value;
    }

    /// <summary>Camera target (forwarded to <see cref="Camera"/>.Target for legacy field compatibility).</summary>
    public Vector3 CameraTarget
    {
        get => Camera.Target;
        set => Camera.Target = value;
    }

    public Movement Movement { get; set; } = Movement.Character;

    /// <summary>1-2 lighting system: GPU-side scene lighting structure (directional/point/spot lights share
    /// the Lights array, capped at <see cref="SceneLightParams.MaxLights"/>; see RenderQuality section 1-2
    /// for the contract). Prefer writing into this field in place through <see cref="Lighting"/>.Bake;
    /// the default Ambient (0.5,0.5,0.5) x 1.0 ensures examples without explicit lights still receive ambient light.</summary>
    public SceneLightParams SceneLights = new SceneLightParams
    {
        Ambient = new Vector4(0.5f, 0.5f, 0.5f, 1f),
    };

    /// <summary>Persistent lighting authoring layer: an unbounded list of <see cref="Season.Rendering.LightSource"/>.
    /// During Bake, lights are trimmed by priority into the <see cref="SceneLightParams.MaxLights"/> GPU slots.
    /// Add lights when scene objects appear and Remove them when they leave; no per-frame rebuild is needed.</summary>
    public readonly Season.Rendering.SceneLighting Lighting = new();

    /// <summary>The effective scene lighting consumed by each platform frame loop (single convergence point;
    /// hdrExposure is still injected by each backend via SetLighting).</summary>
    public SceneLightParams EffectiveSceneLights => SceneLights;

    /// <summary>1-7 environment map (radiance cube + SH9 irradiance). null means no environment map:
    /// diffuse lighting falls back to the constant ambient term in <see cref="SceneLights"/>.Ambient and
    /// there is no specular reflection, matching the per-pixel behavior before 1-7.
    /// On the app side, assign through <c>EnvironmentMap.LoadFromFacesAsync</c>; each backend calls
    /// <c>Apply</c> from SetLighting as the single integration point to inject data into the tail of the lighting UBO
    /// (contract section 4). Direct writes by the app to those trailing UBO fields are ineffective.</summary>
    public Season.Rendering.EnvironmentMap? SceneEnvironment;

    /// <summary>
    /// TaskCompletionSource injected by the CaptureApp() caller.
    /// Once GPU readback finishes on the render thread, TrySetResult is called and the caller's await resumes immediately.
    /// </summary>
    public static TaskCompletionSource<INativeImageDecoder?>? CaptureAppTcs;

    /// <summary>
    /// Continuous backbuffer readback request installed by <see cref="DeviceServices.Recorder"/>.
    /// Null means no recording session is running, and the render path must then
    /// behave exactly as it did before recording existed: the per-frame cost of
    /// this field is one null check.
    /// Unlike <see cref="CaptureAppTcs"/>, which is a one-shot shutter that the
    /// backend clears after delivering a single image, this request stays
    /// installed for the whole session and paces itself against wall-clock time.
    /// </summary>
    public static FrameCaptureRequest? ActiveFrameCapture;

    public Words Words { get; set; }

    public Settings Settings = null;

    const string settingsFile = "Settings.json";

    readonly object _saveSettingsLock = new();

    int _saveSettingsRequestVersion;

    bool _saveSettingsRequestDisposed;

    string modePre;

    DateTime? modePreTime = null;

    string defaultMode = null;

    public string Mode
    {
        get
        {
            var mode = "";

            if (Settings.Mode is null or "" or "Auto")
            {
                if (defaultMode is null)
                {
                    var isDark = DeviceServices.Core.IsDarkMode();

                    defaultMode = isDark ? "Dark" : "Light";
                }

                mode = defaultMode;
            }
            else
            {
                mode = Settings.Mode;
            }

            if (modePre is null)
            {
                modePre = mode;
            }
            else if (modePre != mode)
            {
                modePre = mode;
                modePreTime = DateTime.Now;
            }

            return mode;
        }
    }

    public float? ModeTime
    {
        get
        {
            if (modePreTime is null)
            {
                return null;
            }
            else
            {
                var elapsed = (float)(DateTime.Now - (DateTime)modePreTime).TotalSeconds;

                if (elapsed >= 1f)
                {
                    modePreTime = null;

                    elapsed = 1f;
                }

                return elapsed;
            }
        }
    }

    public virtual void Init()
    {
        ReadSettings();

        if (Settings == null)
        {
            Settings = new Settings()
            {
                Guid = Guid.NewGuid().ToString(),
                Language = "",
                Music = 70,
                Sound = 70,
                WindowState = new WindowState()
                {
                    Maximized = true
                },
                RenderQuality = new RenderQuality(),
                Products = new List<string>
                {
                    //"create", "play"
                }
            };

            var language = Thread.CurrentThread.CurrentCulture.Name.ToLower();
            if (language.Contains("zh"))
            {
                Settings.Language = "Chinese";
            }

            SaveSettings();
        }

        // Step 6: Older Settings.json files saved before this feature do not contain the RenderQuality field,
        // so it becomes null after deserialization. Backfill defaults once here (capturing any Default* overrides
        // applied during app construction) and persist them; after that, all runtime changes are saved through
        // Settings.RenderQuality + RequestSaveSettings.
        if (Settings.RenderQuality == null)
        {
            Settings.RenderQuality = new RenderQuality();
            SaveSettings();
        }
    }

    public void AddLog(LogType logType, string log)
    {
        if (Debug && LogTypes.HasFlag(logType))
        {
            lock (Logs)
            {
                Logs.Add(log);
            }
        }
    }

    public virtual async void Create()
    {
        try
        {
            Graphics.Instance.Init();

            // On Web, consume the channel from the main-thread render loop; on native platforms, start a background consumer.
            if (DeviceServices.Core.Platform is not Platform.Web)
            {
                var cancellationToken = new CancellationToken();
                StartConsumerLoop(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            // Unhandled exceptions in async void methods terminate the process directly.
            // Log them for diagnosis instead of failing silently.
            AddLog(LogType.Error, $"{DateTime.UtcNow} [BaseApp] Create exception: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }
    }

    // Consumer (background loop):
    private async Task StartConsumerLoop(CancellationToken cancellationToken)
    {
        await Task.Run(async () =>
        {
            try
            {
                await foreach (var control in _channel.Reader.ReadAllAsync(cancellationToken))
                {
                    // Use ResizeSemaphore to limit concurrent loading.
                    await ResizeSemaphore.WaitAsync(cancellationToken);
                    try
                    {
                        await LoadControlAsync(control);
                    }
                    catch (Exception ex)
                    {
                        throw ex;
                    }
                    finally
                    {
                        ResizeSemaphore.Release();
                        _pendingControls.TryRemove(control, out _);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected cancellation; do not log as an error.
            }
            catch (Exception ex)
            {
                // Unhandled exceptions on background threads terminate the process (.NET default behavior).
                // Log them for diagnosis instead of failing silently.
                AddLog(LogType.Error, $"{DateTime.UtcNow} [BaseApp] ConsumerLoop exception: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Shared flow for loading a single queued item (ILoadable): Dispose race protection,
    /// Load result evaluation, Ready marking, and logging. Used by both the native background
    /// consumer loop and the Web per-frame main-thread consumer.
    /// </summary>
    private async Task LoadControlAsync(ILoadable control)
    {
        try
        {
            // The control was disposed after being queued: skip Load and create no GPU resources.
            if (control.IsDisposed)
            {
                AddLog(LogType.Load, $"{DateTime.UtcNow} {control.ID} {control.ToString()} control.IsDisposed continue");

                return;
            }

            AddLog(LogType.Load, $"{DateTime.UtcNow} {control.ID} {control.ToString()} Load");

            control.LoadStart = DateTime.UtcNow;
            if (await control.Load())
            {
                // It may have been disposed during Load: do not mark it Ready;
                // GPU resources are cleaned up by the Dispose path.
                if (control.IsDisposed)
                {
                    AddLog(LogType.Load, $"{DateTime.UtcNow} {control.ID} {control.ToString()} Loaded IsDisposed");
                }
                else
                {
                    AddLog(LogType.Load, $"{DateTime.UtcNow} {control.ID} {control.ToString()} Loaded");

                    control.Ready = true;
                    control.LoadComplete = DateTime.UtcNow;
                }
            }
            else
            {
                AddLog(LogType.Load, $"{DateTime.UtcNow} {control.ID} {control.ToString()} Load false");
            }
        }
        catch (Exception ex)
        {
            AddLog(LogType.Error, $"{DateTime.UtcNow} {control.ID} {control.ToString()} LoadFailed...{ex}");
        }
    }

    // Upper bound of controls consumed per frame; actual throughput is still limited by the time budget.
    // Once atlases are warm, a single Load only takes milliseconds, so a looser cap avoids batch rebuilds
    // appearing one item at a time.
    const int _maxControlsLoadedPerFrame = 8;

    /// <summary>
    /// Process control Load requests by consuming BaseApp's channel without blocking.
    /// Native platforms use a background thread; Web runs asynchronously on the main thread.
    /// </summary>
    internal static async Task ProcessControlQueueFrame(TimeSpan budget)
    {
        var app = DeviceServices.BaseApp;
        if (app is null)
            return;

        var stopWatch = System.Diagnostics.Stopwatch.StartNew();
        int processed = 0;
        while (processed < _maxControlsLoadedPerFrame && stopWatch.Elapsed < budget)
        {
            if (!app.TryDequeueControl(out ILoadable control))
                break;

            await app.LoadControlAsync(control);
            processed++;

            // Yield once on the Wasm main thread so multiple control loads do not monopolize a frame.
            await Task.Yield();
        }
    }

    /// <summary>
    /// Used by Web to consume queued control-load requests from the main-thread render loop without blocking.
    /// Native platforms use the background StartConsumerLoop instead and do not need to call this method.
    /// </summary>
    public bool TryDequeueControl(out ILoadable control)
    {
        if (_channel.Reader.TryRead(out control))
        {
            _pendingControls.TryRemove(control, out _);
            return true;
        }
        return false;
    }

    public void ReadSettings()
    {
        lock (settingsFile)
        {
            if (StorageService.FileExist(StorageService.DirectoryBase, settingsFile))
            {
                if (StorageService.TryGetText(StorageService.DirectoryBase, settingsFile, out string str, out string errMsg))
                {
                    Settings = Season.Utils.JsonUtils.Deserialize<Settings>(str);
                }
            }
        }
    }

    public void SaveSettings()
    {
        lock (settingsFile)
        {
            var json = Season.Utils.JsonUtils.Serialize<Settings>(Settings);
            var file = StorageService.SubPath(StorageService.DirectoryBase, settingsFile);
            var state = Settings?.WindowState;
            var rq = Settings?.RenderQuality;

            AddLog(LogType.None, $"{DateTime.UtcNow} [Settings][Save] file={file} state=({state?.X},{state?.Y},{state?.Width},{state?.Height}) max={state?.Maximized} full={state?.FullScreen} rq=gi:{rq?.GlobalIllumination} sdf:{rq?.GiSdfResolution} grid:{rq?.GiProbeGridX}x{rq?.GiProbeGridY}x{rq?.GiProbeGridZ} rays:{rq?.GiRaysPerProbe} div:{rq?.GiProbeUpdateDivisor}");

            StorageService.SaveText(StorageService.DirectoryBase, settingsFile, json);

            if (StorageService.TryGetText(StorageService.DirectoryBase, settingsFile, out string verifyJson, out string errMsg))
            {
                var verifySettings = Season.Utils.JsonUtils.Deserialize<Settings>(verifyJson);
                var verifyState = verifySettings?.WindowState;
                var verifyRq = verifySettings?.RenderQuality;

                AddLog(LogType.None, $"{DateTime.UtcNow} [Settings][SaveVerify] file={file} state=({verifyState?.X},{verifyState?.Y},{verifyState?.Width},{verifyState?.Height}) max={verifyState?.Maximized} full={verifyState?.FullScreen} rq=gi:{verifyRq?.GlobalIllumination} sdf:{verifyRq?.GiSdfResolution} grid:{verifyRq?.GiProbeGridX}x{verifyRq?.GiProbeGridY}x{verifyRq?.GiProbeGridZ} rays:{verifyRq?.GiRaysPerProbe} div:{verifyRq?.GiProbeUpdateDivisor}");
            }
            else
            {
                AddLog(LogType.None, $"{DateTime.UtcNow} [Settings][SaveVerify] file={file} failed err={errMsg}");
            }
        }
    }

    public void RequestSaveSettings(int delayMs = 300)
    {
        int requestVersion;

        lock (_saveSettingsLock)
        {
            _saveSettingsRequestDisposed = false;
            requestVersion = ++_saveSettingsRequestVersion;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(delayMs);

            lock (_saveSettingsLock)
            {
                if (_saveSettingsRequestDisposed || requestVersion != _saveSettingsRequestVersion)
                {
                    return;
                }
            }

            SaveSettings();
        });
    }

    public void DisposeSaveSettingsRequest()
    {
        lock (_saveSettingsLock)
        {
            _saveSettingsRequestDisposed = true;
            _saveSettingsRequestVersion++;
        }
    }

    public virtual bool RequestLoad(ILoadable control)
    {
        if (control is null)
        {
            return false;
        }

        // Deduplicate: prevent the same loadable from being queued more than once.
        if (!_pendingControls.TryAdd(control, 0))
        {
            return false;
        }

        if (!_channel.Writer.TryWrite(control))
        {
            _pendingControls.TryRemove(control, out _);
            return false;
        }

        return true;
    }

    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? width = null, float? height = null, float? posZ = null, float? depth = null)
    {
        Time += time;

        TouchService.Update(time, Scale);

        return base.Update(time, alpha: alpha, posX: posX, posY: posY, width: width, height: height);
    }

    internal void ApplyResolution(int width, int height, float compositionScaleX, float compositionScaleY)
    {
        DeviceServices.BaseApp.DeviceResolution = new Vector2(width, height);

        DeviceServices.BaseApp.CompositionScale = new Vector2(compositionScaleX, compositionScaleY);

        if (DeviceServices.Core.Platform is Platform.Windows or Platform.Linux or Platform.MacCatalyst or Platform.Web)
        {
            DeviceServices.BaseApp.ExtendResolution = DeviceServices.BaseApp.BasicResolution = DeviceServices.BaseApp.DeviceResolution;
            DeviceServices.BaseApp.Scale = 1f;
        }
        else
        {
            // Keep BasicResolution unchanged during Resize (orientation changes are handled by a separate event source).
            // Recompute only Scale and ExtendResolution from the current DeviceResolution and the stable BasicResolution.
            var basic = DeviceServices.BaseApp.DesignResolution;
            if (basic.X <= 0 || basic.Y <= 0) return;

            var scaleX = (float)width / basic.X;
            var scaleY = (float)height / basic.Y;

            if (scaleX < 1 || scaleY < 1)
            {
                DeviceServices.BaseApp.ExtendResolution = DeviceServices.BaseApp.BasicResolution = DeviceServices.BaseApp.DeviceResolution;
                DeviceServices.BaseApp.Scale = 1f;
                //if (scaleX < 1 && scaleY >= 1)
                //{

                //}
                //else if (scaleX >= 1 && scaleY < 1)
                //{

                //}
                //else
                //{

                //}
            }
            // Use the smaller scale of the two axes so the entire BasicResolution stays visible on screen;
            // the longer axis is extended through ExtendResolution.
            else
            {
                DeviceServices.BaseApp.BasicResolution = DeviceServices.BaseApp.DesignResolution;
                if (scaleX > scaleY)
                {
                    DeviceServices.BaseApp.Scale = scaleY;
                    DeviceServices.BaseApp.ExtendResolution = new Vector2(width / scaleY, basic.Y);
                }
                else
                {
                    DeviceServices.BaseApp.Scale = scaleX;
                    DeviceServices.BaseApp.ExtendResolution = new Vector2(basic.X, height / scaleX);
                }
            }
        }
    }

    public virtual void Resize()
    {
        // Notify registered compute effects to rebuild size-dependent storage textures in place for the new DeviceResolution.
        // Callers on each platform have already executed HandleResize beforehand (GPU idle: VK DeviceWaitIdle / DX WaitForGpu /
        // Metal retained-reference / Web JS GC), so old native resources can be safely destroyed and rebuilt in place.
        if (Graphics.Instance != null)
            FrameSchedule.ResizeCompute(Graphics.Instance);
        InvalidatePanelLayout(this);
    }

    static void InvalidatePanelLayout(Panel panel)
    {
        if (panel == null)
            return;

        foreach (var control in panel.Controls)
        {
            if (control == null)
                continue;

            control.Changed = true;

            if (control is Texts texts)
                texts.InvalidateLayout();
        }

        foreach (var childPanel in panel.Panels)
        {
            if (childPanel == null)
                continue;

            childPanel.Changed = true;
            InvalidatePanelLayout(childPanel);
        }
    }

    public virtual void ResizeContent()
    {
        foreach (var control in Controls)
        {
            if (control is not null)
            {
                control.ContentDirty = true;
            }
        }
    }

    public override void Draw()
    {
        DrawScene();
    }

    public void DrawScene()
    {
        base.Draw(Season.Controls.RenderDomain.Scene);
    }

    public void DrawOverlay()
    {
        base.Draw(Season.Controls.RenderDomain.Overlay);
    }
}

public struct Log
{
    public DateTime DateTime { get; set; }

    public string Message { get; set; }
}

public class Settings
{
    public string Guid { get; set; }

    public string Language { get; set; }

    public string Mode { get; set; }

    public int Music { get; set; }

    public int Sound { get; set; }

    public WindowState WindowState { get; set; }

    /// <summary>2-4 Step 6: render-quality profile persisted in Settings.json and editable at runtime.
    /// Property defaults are snapshotted at construction time from the static Default* sources in
    /// <see cref="RenderQuality"/>; any Default* overrides applied during app construction are captured
    /// together when BaseApp.Init() performs backfilling. Runtime render consumers (DDGI and others)
    /// always read this field through DdgiEffect.GiSettings, while null falls back to the static Default* sources.</summary>
    public RenderQuality RenderQuality { get; set; }

    public string User { get; set; }

    public string Password { get; set; }

    public string Avatar { get; set; }

    public string Wallpaper { get; set; }

    public string Name { get; set; }

    public string Desc { get; set; }

    public Fonts Fonts { get; set; }

    public List<KeyValue> KeyValues { get; set; }

    public List<string> Products { get; set; }

    public int Credits { get; set; }

    public bool Rated { get; set; }
}

public class WindowState
{
    public bool FullScreen { get; set; }

    public bool Maximized { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public int X { get; set; }

    public int Y { get; set; }

    public bool HideSystemBars { get; set; }
}

public class KeyValue
{
    public string Key { get; set; }

    public string Value { get; set; }
}

public class Fonts
{
    public List<Font> List { get; set; }
}

public class Font
{
    public string File { get; set; }

    public string Name { get; set; }

    public string Language { get; set; }

    public float Size { get; set; }

    public bool ReadOnly { get; set; }

    public string Time { get; set; }
}

[Flags]
public enum LogType
{
    None = 0,
    Error = 1,
    Load = 2,
    Texts = 4,
    GI = 8,
    Backend = 16
}
