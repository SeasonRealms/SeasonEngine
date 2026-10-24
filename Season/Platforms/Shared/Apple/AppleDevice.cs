// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Foundation;
using UIKit;
using Metal;
using AVFoundation;
using CoreMedia;
using CoreGraphics;
using System.Net;
using PhotosUI;

namespace Season.Platforms.Shared.Apple;

internal class AppleDeviceCore : IDeviceCore
{
    public virtual Basic.Platform Platform => Basic.Platform.None;

    public Channel Channel { get; set; } = Channel.Apple;

    /// <summary>
    /// Returns the window scene currently hosting the UI. On iPadOS windowed apps and
    /// iPhone Duo, the active scene is the only reliable source for screen traits;
    /// UIScreen.MainScreen can describe a different display after a fold/unfold.
    /// </summary>
    internal static UIWindowScene GetActiveWindowScene()
    {
        var scenes = UIApplication.SharedApplication.ConnectedScenes.OfType<UIWindowScene>();

        return scenes.FirstOrDefault(scene => scene.ActivationState == UISceneActivationState.ForegroundActive)
            ?? scenes.FirstOrDefault();
    }

    /// <summary>
    /// Current interface orientation of the scene. iOS 26 obsoleted
    /// UIWindowScene.InterfaceOrientation in favour of EffectiveGeometry; the old
    /// member remains the fallback so the 17.0 deployment target keeps working,
    /// and because a scene created before UIKit reports effective geometry has none.
    /// </summary>
    internal static UIInterfaceOrientation GetInterfaceOrientation(UIWindowScene scene)
    {
#if IOS
        if (OperatingSystem.IsIOSVersionAtLeast(26))
#else
        if (OperatingSystem.IsMacCatalystVersionAtLeast(26))
#endif
        {
            return scene.EffectiveGeometry.InterfaceOrientation;
        }

        // Fallback for pre-26 runtimes, where EffectiveGeometry does not exist.
#pragma warning disable CA1422
        return scene.InterfaceOrientation;
#pragma warning restore CA1422
    }

    public Orientation Orientation
    {
        get
        {
            var version = UIDevice.CurrentDevice.SystemVersion;

            var orientation = Orientation.Unknown;

            if (version.ToFloat() < 13.0)
            {
                orientation = UIDevice.CurrentDevice.Orientation switch
                {
                    UIDeviceOrientation.Portrait => Orientation.Portrait,
                    UIDeviceOrientation.LandscapeLeft => Orientation.LandscapeLeft,
                    UIDeviceOrientation.LandscapeRight => Orientation.LandscapeRight,
                    _ => Orientation.Unknown
                };

                if (orientation is Orientation.Unknown)
                {
                    if (UIScreen.MainScreen.Bounds.Width < UIScreen.MainScreen.Bounds.Height)
                    {
                        orientation = Orientation.Portrait;
                    }
                    else
                    {
                        orientation = Orientation.LandscapeLeft;
                    }
                }
            }
            else
            {
                var scene = GetActiveWindowScene();

                if (scene is null)
                {
                    return Orientation.Unknown;
                }

                if (GetInterfaceOrientation(scene).IsPortrait())
                {
                    orientation = Orientation.Portrait;
                }
                else if (GetInterfaceOrientation(scene).IsLandscape())
                {
                    orientation = Orientation.LandscapeLeft;
                }
            }

            return orientation;
        }
        set
        {
            var scene = GetActiveWindowScene();

            if (scene is null)
            {
                return;
            }

            UIWindowSceneGeometryPreferencesIOS geometry = null;

            if (value is Orientation.LandscapeLeft)
            {
                geometry = new UIWindowSceneGeometryPreferencesIOS(UIInterfaceOrientationMask.LandscapeLeft);
            }
            else if (value is Orientation.LandscapeRight)
            {
                geometry = new UIWindowSceneGeometryPreferencesIOS(UIInterfaceOrientationMask.LandscapeLeft);
            }
            else if (value is Orientation.Portrait)
            {
                geometry = new UIWindowSceneGeometryPreferencesIOS(UIInterfaceOrientationMask.Portrait);
            }

            if (geometry is null)
            {
                return;
            }

            scene.RequestGeometryUpdate(geometry, null);
        }
    }

    /// <summary>
    /// Density factor in physical pixels per point of the active window. Reading the
    /// window's trait collection keeps input and layout correct when the window moves
    /// between displays (Mac Catalyst) or the device folds/unfolds (iPhone Duo), where
    /// UIScreen.MainScreen can describe a different screen.
    /// </summary>
    public float DensityScale
    {
        get
        {
            var window = GetActiveWindowScene()?.Windows.FirstOrDefault();

            var scale = (float)(window?.TraitCollection.DisplayScale ?? UIScreen.MainScreen.NativeScale);

            if (!float.IsFinite(scale) || scale <= 0f)
            {
                return 1f;
            }

            return scale;
        }
    }

    /// <summary>
    /// Safe-area insets (left, top, right, bottom) in points from the active window, keeping
    /// content clear of the notch, the dynamic island and the home indicator. Points already
    /// match the logical-unit convention while density following is on.
    /// </summary>
    public Vector4 SafeInsets
    {
        get
        {
            try
            {
                var scene = GetActiveWindowScene();

                var insets = scene?.Windows.FirstOrDefault()?.SafeAreaInsets ?? default;

                return new Vector4((float)insets.Left, (float)insets.Top, (float)insets.Right, (float)insets.Bottom);
            }
            catch (Exception)
            {
                return Vector4.Zero;
            }
        }
    }

    /// <summary>
    /// Semantic hardware traits for the foldable and <see cref="DeviceProfile.None"/> everywhere
    /// else, including Mac Catalyst: the Platform check keeps a narrow desktop window or an iPad
    /// Split View window from being mistaken for the cover display, since all three can be about
    /// the same width. The two displays of the foldable share one model identifier and differ only
    /// in the scene's native pixel size, so the screen kind comes from there and never from the
    /// window bounds. The side column figures follow the window's live safe area and stay empty
    /// until UIKit has laid it out: read the profile for the layout pass that consumes it instead
    /// of caching the value read while the scene connects.
    /// </summary>
    public DeviceProfile Profile
    {
        get
        {
            try
            {
                if (Platform != Basic.Platform.iOS || !AppleHardware.IsIPhoneDuo)
                {
                    return DeviceProfile.None;
                }

                var scene = GetActiveWindowScene();

                var screen = scene?.Screen;

                if (screen is null)
                {
                    // The model is known but no scene has connected yet, so the device is a
                    // foldable whose screen kind is still unknown rather than a plain phone.
                    return new DeviceProfile(true, false, false, false, 0f);
                }

                var native = screen.NativeBounds;

                var shortSide = (double)Math.Min(native.Width, native.Height);

                var longSide = (double)Math.Max(native.Width, native.Height);

                var isOuter = MatchesDisplay(OuterDisplay, shortSide, longSide);

                var isInner = !isOuter && MatchesDisplay(InnerDisplay, shortSide, longSide);

                var insets = SafeInsets;

                return new DeviceProfile(true, isOuter, isInner, insets.Z > 0f, insets.Z);
            }
            catch (Exception)
            {
                return DeviceProfile.None;
            }
        }
    }

    /// <summary>
    /// Native pixel size of the cover display, short side first. NativeBounds is expressed in a
    /// fixed portrait space, so the sides are compared sorted rather than as width and height.
    /// </summary>
    static readonly (double ShortSide, double LongSide) OuterDisplay = (1398d, 2034d);

    /// <summary>Native pixel size of the main display, short side first.</summary>
    static readonly (double ShortSide, double LongSide) InnerDisplay = (2007d, 2853d);

    /// <summary>
    /// Compares a native screen size with a known display. The tolerance absorbs the rounding
    /// UIKit applies to native bounds; the model check that gates this comparison keeps a future
    /// display of similar size from being mistaken for one of these.
    /// </summary>
    static bool MatchesDisplay((double ShortSide, double LongSide) display, double shortSide, double longSide)
    {
        const double tolerance = 8d;

        return Math.Abs(shortSide - display.ShortSide) <= tolerance
            && Math.Abs(longSide - display.LongSide) <= tolerance;
    }

    /// <summary>
    /// Keeps the display awake while a model load or generation is running, or while a game
    /// pins the screen. iOS is the only Apple target with an idle timer; Mac Catalyst follows
    /// the Mac's own display sleep settings, so the method stays a no-op there.
    /// </summary>
    public void SetKeepAwake(bool enabled)
    {
#if IOS
        void Apply()
        {
            try
            {
                UIApplication.SharedApplication.IdleTimerDisabled = enabled;
            }
            catch (Exception)
            {
                // Losing the idle-timer hint must never break the operation that asked for it.
            }
        }

        if (NSThread.IsMain)
        {
            Apply();
        }
        else
        {
            UIApplication.SharedApplication.BeginInvokeOnMainThread(Apply);
        }
#endif
    }

    public string GetLocalIP()
    {
        var ipAddress = "";

        var ips = new List<string>();

        var ipEntry = Dns.GetHostEntry(Dns.GetHostName());

        var addrs = ipEntry.AddressList.NullToEmptyArray();

        foreach (var addr in addrs)
        {
            if (addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && !addr.IsIPv6LinkLocal)
            {
                var ip = addr.ToString();

                ips.Add(ip);
            }
        }

        var ipv4 = addrs.FirstOrDefault(ad => ad.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).ToString();

        if (ips.Count > 0)
        {
            ipAddress = ips.MaxBy(ip => ip.Length);
        }

        return ipAddress;
    }

    public virtual string LoadFilePath(string res)
    {
        return null;
    }

    public bool LoadFileExists(string res)
    {
        var file = LoadFilePath(res);

        return File.Exists(file);
    }

    public Stream LoadFile(string res)
    {
        var file = LoadFilePath(res);

        return File.OpenRead(file);
    }

    public bool IsDarkMode()
    {
        var style = GetActiveWindowScene()?.Windows.FirstOrDefault()?.TraitCollection.UserInterfaceStyle
            ?? UIScreen.MainScreen.TraitCollection.UserInterfaceStyle;

        return style == UIUserInterfaceStyle.Dark;
    }

    public async Task<bool> RequestPermissionAsync(string[] permissions)
    {
        // Microphone requests go through the TCC-backed AVCaptureDevice consent (one system
        // prompt per app install). Everything else is treated as granted, matching the other
        // platforms: the former Photos fallback was removed for App Store compliance, so apps
        // that need the photo library implement it in their own downstream code.
        var needsMicrophone = permissions is not null && permissions.Any(p =>
            p is not null && (p.Contains("RECORD_AUDIO", StringComparison.OrdinalIgnoreCase)
                || p.Contains("MICROPHONE", StringComparison.OrdinalIgnoreCase)));

        if (!needsMicrophone)
        {
            return true;
        }

        return await RequestMicrophonePermissionAsync();
    }

    /// <summary>
    /// Total memory resources for AI model grading: physical capacity only, never the
    /// free/available amounts, so the verdict does not drift as other apps open or close.
    /// iOS overrides the form factor; macOS reports a desktop.
    /// </summary>
    public virtual DeviceMemoryInfo GetMemoryInfo()
    {
        var info = new DeviceMemoryInfo
        {
            FormFactor = DeviceFormFactor.Desktop
        };

        try
        {
            // Foundation's PhysicalMemory is the total unified/physical RAM, the same
            // total (not free) figure the other platforms report.
            var total = NSProcessInfo.ProcessInfo.PhysicalMemory;

            if (total > 0)
            {
                info.TotalSystemMemoryMb = (int)(total / (1024 * 1024));
            }
        }
        catch
        {
            // Leave the system-memory figure at 0 when Foundation is unavailable.
        }

        try
        {
            var mtlDevice = MTLDevice.SystemDefault;

            if (mtlDevice is not null)
            {
                // Apple Silicon and iOS/iPadOS share one pool; an Intel Mac with a
                // discrete GPU reports a separate one and a usable VRAM budget.
                info.IsUnifiedMemory = mtlDevice.HasUnifiedMemory;

                if (!mtlDevice.HasUnifiedMemory)
                {
                    var workingSet = (ulong)mtlDevice.RecommendedMaxWorkingSetSize;

                    if (workingSet > 0)
                    {
                        info.TotalVideoMemoryMb = (int)(workingSet / (1024 * 1024));
                        info.IsDedicatedGpu = true;
                    }
                }
            }
        }
        catch
        {
            // Metal unavailable (for example a simulator with no device); leave GPU empty.
        }

        return info;
    }

    /// <summary>
    /// Reads, and on first use requests, microphone consent through AVCaptureDevice, which is the
    /// TCC-backed authority on both iOS and Mac Catalyst (Apple documents requestAccess for audio
    /// as equivalent to AVAudioSession.requestRecordPermission). AVAudioSession.RecordPermission is
    /// deliberately not used: on Mac Catalyst it can report Granted before macOS has ever shown a
    /// prompt, which then surfaces much later as AVAudioRecorder.PrepareToRecord returning false
    /// with no error to explain it.
    /// </summary>
    static async Task<bool> RequestMicrophonePermissionAsync()
    {
        var status = AVCaptureDevice.GetAuthorizationStatus(AVAuthorizationMediaType.Audio);

        if (status is AVAuthorizationStatus.Authorized)
        {
            return true;
        }

        if (status is AVAuthorizationStatus.NotDetermined)
        {
            // This is the call that surfaces the system microphone prompt. It needs
            // NSMicrophoneUsageDescription in Info.plist and, under the Mac Catalyst App Sandbox,
            // the com.apple.security.device.audio-input entitlement.
            var granted = await AVCaptureDevice.RequestAccessForMediaTypeAsync(AVAuthorizationMediaType.Audio);

            DeviceServices.BaseApp?.AddLog(LogType.None, $"{DateTime.UtcNow} [Permission] microphone requested granted={granted}");

            return granted;
        }

        // Denied/Restricted cannot be re-requested in-process: the user has to enable the app in
        // System Settings > Privacy & Security > Microphone (macOS) or Settings > Privacy &
        // Security > Microphone (iOS). On macOS an app launched from a terminal or IDE inherits
        // that parent's microphone status, so the parent may need the grant too.
        DeviceServices.BaseApp?.AddLog(LogType.Error, $"{DateTime.UtcNow} [Permission] microphone {status}, enable it in the system privacy settings");

        return false;
    }
}

/// <summary>
/// Process-wide AVAudioSession state shared by recording and playback.
/// The recorder needs an input category (Record), and that category has no output at
/// all: it silences playback. Deactivating a session does not change its category, so
/// after the first recording the session stayed at Record + inactive. The next
/// AVPlayer.Play() then ran without an output route, failed at once (Rate 0) and the
/// play button - which reads Rate to choose its icon - fell straight back to "stopped".
/// Killing the app reset the category to the system default, which is why the same file
/// played fine after a restart. Recording now restores a playback category when it
/// ends, and every play restates that the session must be active.
/// </summary>
internal static class AppleAudioSession
{
    // The binding exposes no "None" member for the options flag enum.
    const AVAudioSessionCategoryOptions NoCategoryOptions = (AVAudioSessionCategoryOptions)0;

    /// <summary>
    /// True while a recorder or engine capture owns the session. Playback must not
    /// renegotiate the category then, or it would tear the input route away mid-record.
    /// </summary>
    internal static bool Recording { get; set; }

    /// <summary>
    /// Makes sure the shared session is active before an AVPlayer starts. Idempotent and
    /// cheap, and it also recovers a session the system deactivated on an interruption.
    /// The category is left alone: apps that never record keep the system default, and
    /// after a recording <see cref="ReleaseCapture"/> has already restored Playback.
    /// </summary>
    internal static void EnsurePlayback()
    {
        if (Recording)
        {
            return;
        }

        try
        {
            var session = AVAudioSession.SharedInstance();

            if (!session.SetActive(true, out NSError error))
            {
                Log(LogType.Error, $"SetActive(true) failed: {Describe(error)}");
            }
        }
        catch (Exception ex)
        {
            Log(LogType.Error, $"activation failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Applies the input category a capture attempt needs and marks the session as owned
    /// by the capture, so <see cref="EnsurePlayback"/> leaves the route alone.
    /// </summary>
    internal static void ApplyCapture(AVAudioSession session, AVAudioSessionCategory category, AVAudioSessionCategoryOptions options, bool activate)
    {
        Recording = true;

        if (!session.SetCategory(category, options, out NSError categoryError))
        {
            Log(LogType.Error, $"SetCategory({category}, {options}) failed: {Describe(categoryError)}");
        }

        if (!activate)
        {
            return;
        }

        if (!session.SetActive(true, out NSError activeError))
        {
            Log(LogType.Error, $"SetActive(true) failed: {Describe(activeError)}");
        }
    }

    /// <summary>
    /// Gives the session back to playback after a capture ends or aborts. Restoring the
    /// category is the part that matters: deactivating alone leaves it on Record, which
    /// has no output route and silently breaks every later play until the app restarts.
    /// </summary>
    internal static void ReleaseCapture()
    {
        Recording = false;

        try
        {
            var session = AVAudioSession.SharedInstance();

            if (!session.SetCategory(AVAudioSessionCategory.Playback, NoCategoryOptions, out NSError categoryError))
            {
                Log(LogType.Error, $"SetCategory(Playback) failed: {Describe(categoryError)}");
            }

            try { session.SetActive(false, out _); }
            catch { }
        }
        catch (Exception ex)
        {
            Log(LogType.Error, $"release failed: {ex.Message}");
        }
    }

    static string Describe(NSError error)
    {
        return error == null ? "none" : $"{error.Domain} {error.Code} {error.LocalizedDescription}";
    }

    static void Log(LogType logType, string message)
    {
        DeviceServices.BaseApp?.AddLog(logType, $"{DateTime.UtcNow} [AudioSession] {message}");
    }
}

internal class AppleMediaPlayer : IMediaPlayer
{
    public bool IsPlaying
    {
        get
        {
            // Rate, not Status: a freshly created player (and one whose item failed to
            // load) reports Unknown, and "not ReadyToPlay" was counting both as playing.
            var music = MusicPlayer != null && MusicPlayer.Rate > 0f;

            var sound = SoundPlayer != null && SoundPlayer.Rate > 0f;

            return music || sound;
        }
    }

    protected AVPlayer MusicPlayer = null;

    protected AVPlayer SoundPlayer = null;

    // Raw (un-escaped) source path last handed to each channel by PlayMedia, kept so
    // IsPlayingFile can tell which file a player is currently running. The percent
    // escaping and "file://" prefix applied below make the player's own URL awkward to
    // compare against a caller-supplied name, so the original path is kept verbatim.
    protected string CurrentMusicFile = null;

    protected string CurrentSoundFile = null;

    /// <summary>
    /// Reports whether <paramref name="fileName"/> is the track currently playing on the
    /// music or sound channel. The file is matched either by full path or by file-name
    /// component (Apple volumes are case-insensitive), and playback is confirmed through
    /// the player's non-zero rate, so a paused or finished track reports false.
    /// </summary>
    public bool IsPlayingFile(string fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return false;
        }

        return IsChannelPlayingFile(MusicPlayer, CurrentMusicFile, fileName)
            || IsChannelPlayingFile(SoundPlayer, CurrentSoundFile, fileName);
    }

    static bool IsChannelPlayingFile(AVPlayer player, string currentFile, string fileName)
    {
        // Rate is 0 when paused/stopped and after an item plays to its end; any non-zero
        // rate means the player is actively running the item loaded from currentFile.
        if (player == null || player.Rate == 0f)
        {
            return false;
        }

        return MediaPlayerFiles.IsSame(currentFile, fileName);
    }

    public void PlayMedia(string type, string id, string vol)
    {
        // The session may still be deactivated (or, after a recording that never
        // restored it, input-only). AVPlayer needs it active before the item starts.
        AppleAudioSession.EnsurePlayback();

        if (MusicPlayer == null || SoundPlayer == null)
        {
            MusicPlayer = new AVPlayer();
            SoundPlayer = new AVPlayer();
        }

        // Remember the caller-supplied path for this channel before it is escaped below.
        if (type is "Music")
        {
            CurrentMusicFile = id;
        }
        else
        {
            CurrentSoundFile = id;
        }

        // Callers follow the Windows convention: resource names may embed backslashes
        // (e.g. "Sound\Move.wav", a literal file-name character on Apple systems) and paths
        // are assembled from AppContext.BaseDirectory, which on Mac Catalyst is the
        // MonoBundle directory while assets live in the sibling Resources directory.
        // Normalize separators and resolve through the platform core — the same reroot
        // LoadFile/LoadFileExists use — so AVPlayer never points at a nonexistent file and
        // stays silent. Android's SetMediaSource performs the equivalent normalization for
        // APK assets; the Web player resolves through WebApp.ResolveAssetPath.
        id = DeviceServices.Core.LoadFilePath(id.Replace('\\', '/')) ?? id;

        id = new NSString(id).CreateStringByAddingPercentEscapes(NSStringEncoding.UTF8);

        var url = AVAsset.FromUrl(new NSUrl("file://" + id));

        var item = new AVPlayerItem(url);

        var mediaPlayer = type is "Music" ? MusicPlayer : SoundPlayer;

        mediaPlayer.ReplaceCurrentItemWithPlayerItem(item);

        mediaPlayer.Volume = float.Parse(vol) / 100;

        mediaPlayer.Seek(CMTime.Zero);

        mediaPlayer.Play();
    }

    public void SetVolume(int music, int sound)
    {
        if (MusicPlayer == null || SoundPlayer == null)
        {

        }
        else
        {
            MusicPlayer.Volume = (float)music / 100;

            SoundPlayer.Volume = (float)sound / 100;
        }
    }

    public void Pause()
    {
        MusicPlayer?.Pause();

        SoundPlayer?.Pause();
    }

    public void Resume()
    {
        MusicPlayer?.Play();

        SoundPlayer?.Play();
    }
}

internal class AppleDialogService : IDialogService
{
    /// <summary>
    /// UIKit shows one modal at a time. A second PresentViewController issued while an
    /// alert is already on screen is silently dropped by the system, and the dropped
    /// caller would await a result that never arrives. Serializing keeps every call
    /// paired with a controller that actually made it on screen.
    /// </summary>
    static readonly SemaphoreSlim DialogLock = new SemaphoreSlim(1, 1);

    /// <summary>
    /// Shows a native alert. The platform dialog contract (Android/Linux/Windows/Web
    /// agree) is that the first button returns the caller's text and every other
    /// dismissal returns null, so callers can read a non-null answer as "confirmed".
    /// </summary>
    public async Task<string> ShowMessage(string title, string desc, string[] buttons, string text)
    {
        if (buttons is null || buttons.Length == 0)
        {
            buttons = new string[] { "OK" };
        }

        await DialogLock.WaitAsync();

        try
        {
            var tcs = new TaskCompletionSource<string>();

            UIApplication.SharedApplication.InvokeOnMainThread(delegate
            {
                try
                {
                    var body = string.IsNullOrEmpty(desc)
                        ? text
                        : string.IsNullOrEmpty(text)
                            ? desc
                            : desc + "\n\n" + text;

                    // UIKit raises if both the title and the message are nil.
                    var alert = UIAlertController.Create(title ?? string.Empty, body ?? string.Empty, UIAlertControllerStyle.Alert);

                    for (var i = 0; i < buttons.Length; i++)
                    {
                        var isPrimary = i == 0;

                        // The trailing button of a multi-button alert reads as a
                        // dismissal ("Cancel") and gets the cancel style, which sits
                        // it below the rest.
                        var style = !isPrimary && i == buttons.Length - 1
                            ? UIAlertActionStyle.Cancel
                            : UIAlertActionStyle.Default;

                        alert.AddAction(UIAlertAction.Create(buttons[i] ?? string.Empty, style, _ =>
                        {
                            tcs.TrySetResult(isPrimary ? text : null);
                        }));
                    }

                    var parentController = TopViewController();

                    if (parentController is null)
                    {
                        tcs.TrySetResult(null);

                        return;
                    }

                    parentController.PresentViewController(alert, true, delegate
                    {
                        // Fires when the presentation animation ends, not on dismissal.
                        // Do NOT resolve tcs here in the normal case: doing so would return
                        // before the user taps. It only releases the caller when UIKit
                        // reports the alert is off screen, where no action can follow.
                        if (alert.PresentingViewController is null)
                        {
                            tcs.TrySetResult(null);
                        }
                    });
                }
                catch (System.Exception ex)
                {
                    DeviceServices.BaseApp?.AddLog(LogType.Error, $"{DateTime.UtcNow} [ShowMessage] {ex}");

                    tcs.TrySetResult(null);
                }
            });

            return await tcs.Task;
        }
        finally
        {
            DialogLock.Release();
        }
    }

    /// <summary>
    /// The controller a modal can be attached to: the current one, or the last one it is
    /// already presenting. Presenting on a controller that is itself presenting gets the
    /// alert rejected by UIKit (pickers and the keyboard dialog leave one up).
    /// </summary>
    static UIViewController TopViewController()
    {
        var controller = Microsoft.Maui.ApplicationModel.Platform.GetCurrentUIViewController();

        while (controller?.PresentedViewController is not null)
        {
            controller = controller.PresentedViewController;
        }

        return controller;
    }

    public async Task<string> ShowKeyboard(string title, string desc, string[] buttons, string text)
    {
        var tcs = new TaskCompletionSource<string>();

        UIApplication.SharedApplication.InvokeOnMainThread(delegate
        {
            try
            {
                var alertController = new UIViewController();

                // The frames are assigned by LayoutKeyboardDialog once the dialog knows the surface
                // it actually got (full screen on iPhone, a page sheet on iPad/Mac Catalyst). The
                // old fixed 580x500 field at (20,20) overflowed a 414pt-wide iPhone.
                var textView = new UITextView(new CGRect(0, 0, 0, 0));

                textView.Font = UIFont.SystemFontOfSize(18);
                textView.Text = text;
                textView.Editable = true;

                textView.BecomeFirstResponder();

                alertController.View.AddSubview(textView);

                var btnOK = new UIButton(UIButtonType.Custom);

                btnOK.BackgroundColor = UIColor.Gray;

                btnOK.SetTitle("OK", UIControlState.Normal);

                btnOK.TouchDown += (s, e) =>
                {
                    // Read the CURRENT text from the UITextView at click time so user edits are captured.
                    tcs.TrySetResult(textView.Text);

                    alertController.DismissViewController(true, () => { });
                };

                alertController.View.AddSubview(btnOK);

                var btnCancel = new UIButton(UIButtonType.Custom);

                btnCancel.BackgroundColor = UIColor.Gray;

                btnCancel.SetTitle("Cancel", UIControlState.Normal);

                btnCancel.TouchDown += (s, e) =>
                {
                    tcs.TrySetResult(null);

                    alertController.DismissViewController(true, () => { });
                };

                alertController.View.AddSubview(btnCancel);

                // A swipe-down would close a page sheet without any button running, leaving the
                // caller awaiting a result that never arrives. Only the two buttons close it.
                alertController.ModalInPresentation = true;

                var parentController = Microsoft.Maui.ApplicationModel.Platform.GetCurrentUIViewController();

                if (parentController is not null)
                {
                    // NOTE: the completionHandler of PresentViewController fires when the PRESENTATION
                    // animation finishes (i.e., right after the sheet appears), NOT when it is dismissed.
                    // Do NOT resolve tcs here in the normal case — otherwise the caller returns with the
                    // initial text before the user gets a chance to edit, and later OK/Cancel TrySetResult
                    // calls become no-ops. It is the first point where the presented view has its final
                    // size, which is what LayoutKeyboardDialog needs.
                    parentController.PresentViewController(alertController, true, delegate
                    {
                        // A null presenter means UIKit dropped the modal (another one is already up),
                        // so no button can ever run and the caller has to be released here.
                        if (alertController.PresentingViewController is null)
                        {
                            tcs.TrySetResult(null);

                            return;
                        }

                        LayoutKeyboardDialog(alertController.View, textView, btnOK, btnCancel);
                    });
                }
                else
                {
                    tcs.TrySetResult(null);
                }
            }
            catch (System.Exception)
            {
                tcs.TrySetResult(null);
            }
        });

        return await tcs.Task;
    }

    /// <summary>
    /// Frames the editor and its two buttons inside the surface the dialog actually got. The
    /// editor is confined to the upper half and the buttons start at the lower half: the
    /// on-screen keyboard owns the bottom of the screen and cannot always be dismissed, so
    /// anything placed there would be hidden while the user is typing.
    /// </summary>
    static void LayoutKeyboardDialog(UIView view, UITextView textView, UIButton btnOK, UIButton btnCancel)
    {
        var bounds = view.Bounds;

        var insets = view.SafeAreaInsets;

        const float margin = 20f;

        const float buttonWidth = 120f;

        const float buttonHeight = 50f;

        const float buttonGap = 40f;

        var viewWidth = (float)bounds.Width;

        var viewHeight = (float)bounds.Height;

        var safeTop = (float)insets.Top;

        var safeBottom = (float)insets.Bottom;

        // Capped so the field does not stretch across a wide iPad/Catalyst window.
        var width = Math.Min(Math.Max(viewWidth - margin * 2f, 80f), 620f);

        var middle = viewHeight * 0.5f;

        // The buttons open the lower half, which on phones still sits above the keyboard; the
        // clamp keeps them above the home indicator when the surface is too short for that.
        var buttonY = Math.Min(middle + margin, viewHeight - safeBottom - margin); // - buttonHeight);

        var contentTop = safeTop + margin;

        var contentBottom = Math.Min(middle - margin, buttonY - margin);

        // Never taller than the upper half, so the keyboard cannot cover the text being edited.
        var contentHeight = Math.Max(60f, contentBottom - contentTop);

        textView.Frame = new CGRect((viewWidth - width) / 2f, contentTop, width, contentHeight);

        var buttonsWidth = buttonWidth * 2f + buttonGap;

        var buttonLeft = (viewWidth - buttonsWidth) / 2f;

        btnOK.Frame = new CGRect(buttonLeft, buttonY, buttonWidth, buttonHeight);

        btnCancel.Frame = new CGRect(buttonLeft + buttonWidth + buttonGap, buttonY, buttonWidth, buttonHeight);
    }
}

internal class AppleFileService : IFileService
{
    UIDocumentPickerViewController? documentPickerViewController;

    TaskCompletionSource<string>? taskCompetedSource;

    // Strong references to the picker delegates. UIKit holds these delegates weakly, so without a
    // field keeping them alive they get GC'd right after present, which both breaks the
    // DidPickDocument/DidDismiss callbacks and (via the finalizer) can complete the tcs prematurely.
    PickerDelegate? pickerDelegate;

    UIPresentationControllerDelegate? presentationControllerDelegate;

    public async Task<string> PickFolder()
    {
        return null;
    }

    public async Task<List<TaskFile>> PickFiles(FileType fileType, string[] exts, bool multiple, bool open)
    {
#if MACCATALYST
        // MacCatalyst hands the picker over to the out-of-process AppKit open panel service. Under App
        // Sandbox that service refuses to display the panel when the app is missing the User Selected File
        // entitlement: AppKit only writes "Unable to display open panel" to the system log, UIKit keeps an
        // invisible modal that swallows the window's input, and none of the picker callbacks ever arrives.
        // Fail fast with a log entry instead of presenting a picker that can never show.
        if (!HasUserSelectedFileEntitlement())
        {
            DeviceServices.BaseApp?.AddLog(LogType.Error,
                $"{DateTime.UtcNow} [PickFiles] aborted: the app sandbox is missing " +
                "'com.apple.security.files.user-selected.read-write', the AppKit open panel cannot be displayed.");

            return new List<TaskFile>();
        }
#endif

        var tcs = new TaskCompletionSource<IEnumerable<NSUrl>>();

        UIDocumentPickerViewController? picker = null;

        // Set by the presentation completion handler: false means the modal never made it on screen.
        var presented = false;

        UIApplication.SharedApplication.InvokeOnMainThread(delegate
        {
            try
            {
                var allowedUtis = new string[]
                {
                    MobileCoreServices.UTType.Content,
                    MobileCoreServices.UTType.Item,
                    "public.data"
                };

                var documentPicker = new UIDocumentPickerViewController(allowedUtis, UIDocumentPickerMode.Import)
                {
                    AllowsMultipleSelection = multiple
                };

                picker = documentPicker;

                pickerDelegate = new PickerDelegate
                {
                    PickHandler = urls => tcs.TrySetResult(urls)
                };
                documentPicker.Delegate = pickerDelegate;

                var parentController = Microsoft.Maui.ApplicationModel.Platform.GetCurrentUIViewController();

                if (parentController is not null)
                {
                    // The completion handler fires when the PRESENTATION animation finishes, not on dismissal.
                    // It is also the first moment PresentationController exists (UIKit creates it as the
                    // transition starts), so the adaptive-dismissal delegate can only be attached from in here:
                    // assigning it before present is a no-op.
                    parentController.PresentViewController(documentPicker, true, delegate
                    {
                        presented = true;

                        presentationControllerDelegate =
                            new UIPresentationControllerDelegate(() => tcs.TrySetResult(null));
                        documentPicker.PresentationController?.Delegate = presentationControllerDelegate;
                    });
                }
                else
                {
                    tcs.TrySetResult(null);
                }
            }
            catch (System.Exception ex)
            {
                DeviceServices.BaseApp?.AddLog(LogType.Error, $"{DateTime.UtcNow} [PickFiles] {ex}");

                tcs.TrySetResult(null);
            }
        });

        var files = (await WaitPickerSession(tcs, () => picker, () => presented)).NullToEmptyArray();

        var taskFiles = new List<TaskFile>();

        foreach (var file in files)
        {
            var ext = System.IO.Path.GetExtension(file.Path).ToLower();

            FileStream stream = null;

            if (open)
            {
                try
                {
                    stream = System.IO.File.OpenRead(file.Path);
                }
                catch (System.Exception ex)
                {
                    // A failed read must not travel up through the render loop, which would take the whole
                    // app down: report it and let the caller fall back to the path alone.
                    DeviceServices.BaseApp?.AddLog(LogType.Error, $"{DateTime.UtcNow} [PickFiles] open {file.Path} failed: {ex.Message}");
                }
            }
            else
            {

            }

            var taskFile = new TaskFile()
            {
                Name = file.Path,
                Ext = ext,
                Text = "",
                Stream = stream
                //Bytes = bytes
            };

            taskFiles.Add(taskFile);
        }

        return taskFiles;
    }
    
    /// <summary>
    /// Presents the system photo picker and returns the selected photo as a task file, or an
    /// empty list when the user cancels or the asset cannot be read. PHPickerViewController is
    /// used instead of UIImagePickerController on purpose: it runs out of process, so the app
    /// never asks for - or receives - photo-library access, and Info.plist needs no
    /// NSPhotoLibraryUsageDescription.
    /// </summary>
    public async Task<List<TaskFile>> PickPhotos(bool open)
    {
        var tcs = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);

        // The picker and the presenting controller both touch UIKit, which asserts it is
        // on the main thread (EnsureUIThread), while the click handler runs on the render
        // thread.
        UIApplication.SharedApplication.InvokeOnMainThread(() =>
        {
            try
            {
                var configuration = new PHPickerConfiguration
                {
                    Filter = PHPickerFilter.ImagesFilter,
                    SelectionLimit = 1
                };

                var controller = new PHPickerViewController(configuration)
                {
                    // An interactive swipe-down would dismiss the sheet without calling the
                    // delegate, leaving the awaited task pending forever; Cancel/Done always report.
                    ModalInPresentation = true
                };
                var pickerDelegate = new PHPickerDelegate(tcs);

                controller.Delegate = pickerDelegate;

                activeController = controller;
                activeDelegate = pickerDelegate;

                var parent = Microsoft.Maui.ApplicationModel.Platform.GetCurrentUIViewController();

                if (parent is null)
                {
                    Complete(null, null, tcs);
                    return;
                }

                parent.PresentViewController(controller, true, null);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PickPhotos] failed to present the picker: {ex.Message}");

                Complete(null, null, tcs);
            }
        });

        var bytes = await tcs.Task;

        var taskFiles = new List<TaskFile>();

        if (bytes is null || bytes.Length == 0)
        {
            return taskFiles;
        }

        // PHPicker hands back pixels instead of a file, so the PickFiles contract - a
        // readable local path plus an optional stream - is re-established with a JPEG in
        // the container's tmp directory. That is the same transient location the document
        // picker imports into, and callers copy what they need before it is reclaimed.
        var ext = ".jpg";

        var name = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            $"Photo-{DateTime.Now:yyyyMMddHHmmss}-{Guid.NewGuid():N}{ext}");

        System.IO.FileStream stream = null;

        try
        {
            System.IO.File.WriteAllBytes(name, bytes);

            if (open)
            {
                stream = System.IO.File.OpenRead(name);
            }
        }
        catch (Exception ex)
        {
            // A failed write must not travel up through the render loop; report it and
            // return no file so the caller leaves the current image untouched.
            DeviceServices.BaseApp?.AddLog(LogType.Error, $"{DateTime.UtcNow} [PickPhotos] writing {name} failed: {ex.Message}");

            return taskFiles;
        }

        taskFiles.Add(new TaskFile()
        {
            Name = name,
            Ext = ext,
            Text = "",
            Stream = stream
        });

        return taskFiles;
    }

    // UIKit keeps the delegate weak and nothing else references the controller once
    // PresentViewController returns, so both are rooted here for the lifetime of the sheet.
    static PHPickerViewController? activeController;
    static PHPickerDelegate? activeDelegate;

    static void Complete(PHPickerViewController? controller, byte[]? bytes, TaskCompletionSource<byte[]?> tcs)
    {
        controller?.DismissViewController(true, null);

        activeController = null;
        activeDelegate = null;

        tcs.TrySetResult(bytes);
    }

    sealed class PHPickerDelegate : PHPickerViewControllerDelegate
    {
        readonly TaskCompletionSource<byte[]?> tcs;

        internal PHPickerDelegate(TaskCompletionSource<byte[]?> tcs)
        {
            this.tcs = tcs;
        }

        public override void DidFinishPicking(PHPickerViewController picker, PHPickerResult[] results)
        {
            // An empty result set means the user cancelled; there is nothing to load.
            if (results is not { Length: > 0 })
            {
                Complete(picker, null, tcs);
                return;
            }

            _ = ReadAsync(picker, results[0]);
        }

        async Task ReadAsync(PHPickerViewController picker, PHPickerResult result)
        {
            byte[]? bytes = null;

            try
            {
                // PHPicker hands back an item provider instead of a file URL: the asset may
                // be HEIC, PNG or RAW, so it is decoded to UIImage and re-encoded as JPEG.
                // PickPhotos then exposes that single .jpg as a regular task file.
                var image = await result.ItemProvider.LoadObjectAsync<UIImage>();

                if (image is not null)
                {
                    using var data = image.AsJPEG(0.9f);

                    bytes = data?.ToArray();

                    image.Dispose();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PickPhotos] failed to read the picked photo: {ex.Message}");
            }

            // The item-provider load finishes on a worker queue; dismissing the sheet and
            // completing the caller belong on the main thread.
            UIApplication.SharedApplication.BeginInvokeOnMainThread(() => Complete(picker, bytes, tcs));
        }
    }

    /// <summary>Seconds waited after present before probing whether the picker actually made it on screen.</summary>
    const int PickerProbeDelaySeconds = 3;

    /// <summary>Seconds a picker session may stay open without any callback before it is force-dismissed.</summary>
    const int PickerSessionTimeoutSeconds = 300;

    /// <summary>
    /// Waits for the picker callbacks while guarding against a presentation that fails silently. The short
    /// probe catches a session that never became visible; the long stop guarantees the caller never awaits
    /// forever. Both recovery paths dismiss the picker first — a half-presented modal keeps swallowing the
    /// window's input — and then resolve the tcs with null, so PickFiles returns an empty list instead of
    /// leaving the app stuck until it is force quit.
    /// </summary>
    static async Task<IEnumerable<NSUrl>> WaitPickerSession(TaskCompletionSource<IEnumerable<NSUrl>> tcs,
        Func<UIDocumentPickerViewController?> picker, Func<bool> presented)
    {
        if (await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(PickerProbeDelaySeconds))) != tcs.Task)
        {
            if (!await PickerIsAlive(picker, presented))
            {
                DismissPickerSession(tcs, picker, "the presentation aborted and no picker became visible");

                return await tcs.Task;
            }
        }

        if (await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(PickerSessionTimeoutSeconds))) != tcs.Task)
        {
            DismissPickerSession(tcs, picker, $"no callback arrived within {PickerSessionTimeoutSeconds}s");
        }

        return await tcs.Task;
    }

    /// <summary>
    /// True while the session is plausibly alive: the presentation completed, or the picker carries either a
    /// presenting controller or a window. Anything else means UIKit never got the modal on screen.
    /// </summary>
    static async Task<bool> PickerIsAlive(Func<UIDocumentPickerViewController?> picker, Func<bool> presented)
    {
        // UIKit state must be read on the main thread; the picker callbacks that set those values run there
        // as well, so reading them from the same thread is what makes the probe sound.
        return await Microsoft.Maui.ApplicationModel.MainThread.InvokeOnMainThreadAsync(delegate
        {
            var controller = picker();

            return presented()
                || controller?.PresentingViewController is not null
                || controller?.View?.Window is not null;
        });
    }

    /// <summary>Dismisses a stuck picker session on the main thread and resolves the tcs with null.</summary>
    static void DismissPickerSession(TaskCompletionSource<IEnumerable<NSUrl>> tcs,
        Func<UIDocumentPickerViewController?> picker, string reason)
    {
        DeviceServices.BaseApp?.AddLog(LogType.Error,
            $"{DateTime.UtcNow} [PickFiles] picker session recovered: {reason}; the picker was dismissed and no file was returned.");

        UIApplication.SharedApplication.InvokeOnMainThread(delegate
        {
            // The delegate may have resolved the tcs while this block was queued: then the picker is already
            // dismissing itself and the user's selection must win over the recovery.
            if (!tcs.Task.IsCompleted)
                picker()?.DismissViewController(true, null);

            tcs.TrySetResult(null);
        });
    }

    /// <summary>
    /// Save-side counterpart of <see cref="WaitPickerSession"/>: the ExportToService panel gets the same
    /// silent-presentation guard as the import picker. The short probe catches a session that never became
    /// visible; the long stop guarantees the caller never awaits forever. Both recovery paths resolve the
    /// tcs with an empty string, so SaveFile reports "not saved" instead of leaving the app stuck.
    /// </summary>
    static async Task<string> WaitSaveSession(TaskCompletionSource<string> tcs,
        Func<UIDocumentPickerViewController?> picker, Func<bool> presented)
    {
        if (await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(PickerProbeDelaySeconds))) != tcs.Task)
        {
            if (!await PickerIsAlive(picker, presented))
            {
                DismissSaveSession(tcs, picker, "the presentation aborted and no picker became visible");

                return await tcs.Task;
            }
        }

        if (await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(PickerSessionTimeoutSeconds))) != tcs.Task)
        {
            DismissSaveSession(tcs, picker, $"no callback arrived within {PickerSessionTimeoutSeconds}s");
        }

        return await tcs.Task;
    }

    /// <summary>Dismisses a stuck save session on the main thread and resolves the tcs with an empty string.</summary>
    static void DismissSaveSession(TaskCompletionSource<string> tcs,
        Func<UIDocumentPickerViewController?> picker, string reason)
    {
        DeviceServices.BaseApp?.AddLog(LogType.Error,
            $"{DateTime.UtcNow} [SaveFile] picker session recovered: {reason}; the picker was dismissed and no file was saved.");

        UIApplication.SharedApplication.InvokeOnMainThread(delegate
        {
            // A half-presented modal keeps swallowing the window's input, so it is dismissed before the
            // caller is released; the completed-tcs check keeps a selection or cancel that raced with this
            // recovery winning over it.
            if (!tcs.Task.IsCompleted)
                picker()?.DismissViewController(true, null);

            tcs.TrySetResult("");
        });
    }

#if MACCATALYST
    /// <summary>
    /// True when the picker may be handed over to the AppKit open panel service: the app is either not
    /// sandboxed at all, or its sandbox grants user-selected file access. Fails open (true) when the
    /// entitlements cannot be read, so a broken probe never blocks an otherwise working picker.
    /// </summary>
    static bool HasUserSelectedFileEntitlement()
    {
        try
        {
            var task = SecTaskCreateFromSelf(IntPtr.Zero);

            if (task == IntPtr.Zero)
                return true;

            try
            {
                if (!EntitlementBool(task, "com.apple.security.app-sandbox"))
                    return true;

                return EntitlementBool(task, "com.apple.security.files.user-selected.read-write")
                    || EntitlementBool(task, "com.apple.security.files.user-selected.read-only");
            }
            finally
            {
                CFRelease(task);
            }
        }
        catch (System.Exception)
        {
            return true;
        }
    }

    /// <summary>Reads one boolean entitlement of the current process out of its code signature.</summary>
    static bool EntitlementBool(IntPtr task, string entitlement)
    {
        using var key = new NSString(entitlement);

        var value = SecTaskCopyValueForEntitlement(task, key.Handle, out var error);

        if (error != IntPtr.Zero)
            CFRelease(error);

        if (value == IntPtr.Zero)
            return false;

        try
        {
            return CFGetTypeID(value) == CFBooleanGetTypeID() && CFBooleanGetValue(value) != 0;
        }
        finally
        {
            CFRelease(value);
        }
    }

    [DllImport("/System/Library/Frameworks/Security.framework/Security", EntryPoint = "SecTaskCreateFromSelf")]
    static extern IntPtr SecTaskCreateFromSelf(IntPtr allocator);

    [DllImport("/System/Library/Frameworks/Security.framework/Security", EntryPoint = "SecTaskCopyValueForEntitlement")]
    static extern IntPtr SecTaskCopyValueForEntitlement(IntPtr task, IntPtr entitlement, out IntPtr error);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation", EntryPoint = "CFRelease")]
    static extern void CFRelease(IntPtr cf);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation", EntryPoint = "CFGetTypeID")]
    static extern IntPtr CFGetTypeID(IntPtr cf);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation", EntryPoint = "CFBooleanGetTypeID")]
    static extern IntPtr CFBooleanGetTypeID();

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation", EntryPoint = "CFBooleanGetValue")]
    static extern byte CFBooleanGetValue(IntPtr boolean);
#endif

    public async Task<string> SaveFile(string fileName, Stream stream, CancellationToken cancellationToken)
    {
        fileName = Path.GetFileName(fileName);

        var result = "";

#if MACCATALYST
        // Same fail-fast guard as PickFiles: the ExportToService panel is handed over to the out-of-process
        // AppKit open/save panel service, which refuses to display it under App Sandbox when the app is
        // missing the User Selected File entitlement. No picker callback ever arrives in that case, so fail
        // fast with a log entry instead of presenting a panel that can never show.
        if (!HasUserSelectedFileEntitlement())
        {
            DeviceServices.BaseApp?.AddLog(LogType.Error,
                $"{DateTime.UtcNow} [SaveFile] aborted: the app sandbox is missing " +
                "'com.apple.security.files.user-selected.read-write', the AppKit save panel cannot be displayed.");

            return result;
        }
#endif

        var fileUrl = Path.Combine(Path.GetTempPath(), fileName);

        // The staged file must be fully on disk before the panel is presented: the out-of-process
        // panel service reads this file as soon as the user confirms, so the write scope closes
        // right here. A method-scoped 'using var' would keep the FileStream buffer (4 KB) alive
        // until SaveFile returns, which is only after the panel closed: small files would then
        // land empty on the first save and only appear from the second save on (old bytes flushed
        // late). File.Create also truncates, so a reused temp file name never keeps stale bytes.
        using (var streamTarget = System.IO.File.Create(fileUrl))
        {
            var length = (int)(stream.Length < 4096 ? stream.Length : 4096);

            var array = new byte[length];

            int bytesRead = 0;

            while ((bytesRead = stream.Read(array, 0, length)) > 0)
            {
                streamTarget.Write(array, 0, bytesRead);
            }
        }

        taskCompetedSource = new(cancellationToken);

        // Set by the presentation completion handler: false means the modal never made it on screen.
        var presented = false;

        UIApplication.SharedApplication.InvokeOnMainThread(delegate
        {
            try
            {
                var currentViewController = Microsoft.Maui.ApplicationModel.Platform.GetCurrentUIViewController();

                if (currentViewController is not null)
                {
                    // UIKit view controllers must be created on the main thread: constructing the picker
                    // on the caller's thread trips the UIKit Consistency check whenever SaveFile is invoked
                    // from a background thread (e.g. the Task.Run wrapper of the download button).
                    var fileNsUrl = NSUrl.FromFilename(fileUrl);

                    documentPickerViewController = new UIDocumentPickerViewController(fileNsUrl, UIDocumentPickerMode.ExportToService)
                    {
                        //DirectoryUrl = NSUrl.FromString("/")
                    };

                    documentPickerViewController.DidPickDocumentAtUrls += (s, e) =>
                    {
                        try
                        {
                            taskCompetedSource?.TrySetResult(e.Urls[0].Path ?? throw new Exception("Unable to get the file"));
                        }
                        finally
                        {
                            InternalDispose();
                        }
                    };

                    documentPickerViewController.WasCancelled += (s, e) =>
                    {
                        taskCompetedSource?.TrySetException(new Exception("Cancelled"));

                        InternalDispose();
                    };

                    // The completion handler fires when the PRESENTATION animation finishes, not on dismissal;
                    // it is the only moment the presentation guard can know the modal made it on screen.
                    currentViewController.PresentViewController(documentPickerViewController, true, delegate
                    {
                        presented = true;
                    });
                }
                else
                {
                    taskCompetedSource?.TrySetException(new Exception("No view controller to present the document picker."));
                }
            }
            catch (Exception ex)
            {
                // The tcs must always be resolved: an exception escaping this block would leave
                // WaitSaveSession guarding a session that never armed and could never complete it.
                taskCompetedSource?.TrySetException(ex);
            }
        });

        var usrl = await WaitSaveSession(taskCompetedSource, () => documentPickerViewController, () => presented);

        result = usrl;

        return result;
    }

    void InternalDispose()
    {
        if (documentPickerViewController is not null)
        {
            documentPickerViewController.Dispose();
        }
    }

    public async Task<string> OpenFile(string name, string category, byte[] bytes)
    {
        string result = "";

        if (name.IsNullOrWhiteSpace())
        {

        }
        else if (name.StartsWith("http"))
        {
            OpenLink(name);
        }
        else
        {
            if (!category.IsNullOrWhiteSpace())
            {
                result = name;
            }
            else if (bytes == null)
            {
                result = await SaveStorageFile(name);
            }
            else
            {
                result = await SaveBytesFile(name, bytes);
            }

            if (StorageService.TryGetStream(StorageService.DirectoryBase, name, out Stream streamOrigin, out string errMsg))
            {

            }

            var cancellation = new CancellationTokenSource();

            result = await SaveFile(name, streamOrigin, cancellation.Token);
        }

        return result;

        //PHPhotoLibrary.shared().performChanges({
        //                PHAssetChangeRequest.creationRequestForAssetFromVideo(atFileURL: mediaUrl as URL)
        //}, completionHandler:
        //            {
        //                (success, error) in 
        //    // completion callback 
        //}) 

        //    // save the filtered image data to a PHContentEditingOutput instance
        //    var editingOutput = new PHContentEditingOutput(input);
        //    var adjustmentData = new PHAdjustmentData();
        //    var data = uiImage.AsJPEG();
        //    NSError error;
        //    data.Save(editingOutput.RenderedContentUrl, false, out error);
        //    editingOutput.AdjustmentData = adjustmentData;

        //    PHPhotoLibrary.SharedPhotoLibrary.PerformChangesAndWait(() =>
        //    {
        //        PHAssetChangeRequest request = PHAssetChangeRequest.ChangeRequest(Asset);
        //        request.ContentEditingOutput = editingOutput;
        //    },
        //(ok, err) => Debug.WriteLine("photo updated successfully: {0}", ok));

        //var li = new ALAssetsLibrary();
        //var nsUrl = new NSUrl(id);            
        //nsUrl = await li.WriteVideoToSavedPhotosAlbumAsync(nsUrl);
    }

    //public async Task<string> OpenFile(string name, string category, byte[] bytes)
    //{
    //    var result = "";

    //    if (name.IsNullOrWhiteSpace())
    //    {

    //    }
    //    else if (name.StartsWith("http"))
    //    {
    //        UIApplication.SharedApplication.OpenUrl(new NSUrl(name));
    //    }
    //    else
    //    {
    //        var images = new string[] { ".jpg", ".jpeg", ".png" };

    //        var videos = new string[] { ".mp4", ".mov" };

    //        var ext = Path.GetExtension(name).ToLower();

    //        string file = "";

    //        if (images.Contains(ext) || videos.Contains(ext))
    //        {
    //            if (bytes == null)
    //            {
    //                if (name.Contains("/Application/"))
    //                {

    //                }

    //                File.Copy(name, file);
    //            }
    //            else
    //            {
    //                file = Path.Combine(FileSystem.CacheDirectory, name);

    //                File.WriteAllBytes(file, bytes);
    //            }

    //            var nsUrl = new NSUrl(file);

    //            NSError error = null;

    //            PHPhotoLibrary.SharedPhotoLibrary.PerformChangesAndWait(() =>
    //            {
    //                if (images.Contains(ext))
    //                {
    //                    var request = PHAssetChangeRequest.FromImage(nsUrl);
    //                }
    //                else if (videos.Contains(ext))
    //                {
    //                    var request = PHAssetChangeRequest.FromVideo(nsUrl);
    //                }
    //                //request.ContentEditingOutput
    //            }, out error);
    //        }
    //        else
    //        {
    //            file = Path.Combine(FileSystem.CacheDirectory, name);
    //            File.Copy(name, file);
    //            var nsUrl = new NSUrl(file);
    //            await Launcher.OpenAsync(new OpenFileRequest
    //            {
    //                File = new ReadOnlyFile(nsUrl.RelativePath)
    //            });
    //        }
    //        result = file;
    //    }
    //    return result;
    //}

    async Task<string> SaveStorageFile(string name)
    {
        // The former Photos branch (adding the file to the photo library and reading back the
        // private asset URL) was removed for App Store compliance. The file is only staged in
        // the cache directory; user-visible saving happens through the save panel in OpenFile.
        var fileName = Path.GetFileName(name);

        var file = Path.Combine(FileSystem.CacheDirectory, name);

        File.Copy(fileName, file);

        return file;
    }

    async Task<string> SaveBytesFile(string name, byte[] bytes)
    {
        var file = Path.Combine(FileSystem.CacheDirectory, name);

        File.WriteAllBytes(file, bytes);

        return file;
    }

    public Task<bool> OpenLink(string name)
    {
        // UIApplication.OpenUrlAsync starts with the runtime's UI-thread consistency check:
        // called from a background thread it throws UIKitThreadAccessException before the
        // URL ever reaches the system. Callers run on every context (the frame loop, the
        // Task.Run wrappers of buttons like Create or Download, and continuation threads
        // after an await), and they invoke this service fire-and-forget, so such an
        // exception would be swallowed and the link would silently never open.
        // Marshal first, exactly like SaveFile / PickFiles / the gallery below.
        var tcs = new TaskCompletionSource<bool>();

        UIApplication.SharedApplication.InvokeOnMainThread(async delegate
        {
            try
            {
                var options = new UIApplicationOpenUrlOptions()
                {

                };

                var result = await UIApplication.SharedApplication.OpenUrlAsync(new NSUrl(name), options);

                tcs.TrySetResult(result);
            }
            catch (Exception ex)
            {
                DeviceServices.BaseApp?.AddLog(LogType.Error, $"{DateTime.UtcNow} [OpenLink] open failed name={name} err={ex}");

                tcs.TrySetException(ex);
            }
        });

        return tcs.Task;
    }

    //public async Task<bool> OpenLink(string name)
    //{
    //    var url = new NSUrl(name);
    //    var result = UIApplication.SharedApplication.OpenUrl(url);
    //    return true;
    //}

#if MACCATALYST
    // ObjCRuntime.Messaging is internal in .NET 10, so P/Invoke libobjc directly to reach NSWorkspace,
    // which the MacCatalyst AppKit *bindings* omit but the runtime (real macOS AppKit) still provides.
    [System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_getClass")]
    static extern IntPtr ObjcGetClass(string className);

    [System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "sel_registerName")]
    static extern IntPtr SelRegisterName(string selectorName);

    [System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    static extern IntPtr ObjcMsgSendIntPtr(IntPtr receiver, IntPtr selector);

    [System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    static extern bool ObjcMsgSendBoolIntPtr(IntPtr receiver, IntPtr selector, IntPtr arg1);
#endif

    public void OpenFolder(string name)
    {
#if MACCATALYST
        // Reveal the folder in Finder via +[NSWorkspace sharedWorkspace] / -[NSWorkspace openURL:].
        // Guarded so that if NSWorkspace is genuinely absent at runtime it degrades to a no-op.
        if (!string.IsNullOrEmpty(name))
        {
            var nsWorkspaceClass = ObjcGetClass("NSWorkspace");

            if (nsWorkspaceClass != IntPtr.Zero)
            {
                var sharedWorkspace = ObjcMsgSendIntPtr(nsWorkspaceClass, SelRegisterName("sharedWorkspace"));

                if (sharedWorkspace != IntPtr.Zero)
                {
                    using var url = NSUrl.FromFilename(name);

                    ObjcMsgSendBoolIntPtr(sharedWorkspace, SelRegisterName("openURL:"), url.Handle);
                }
            }
        }
#endif
    }
}

class PickerDelegate : UIDocumentPickerDelegate
{
    public Action<NSUrl[]> PickHandler { get; set; }

    public override void WasCancelled(UIDocumentPickerViewController controller)
        => PickHandler?.Invoke(null);

    public override void DidPickDocument(UIDocumentPickerViewController controller, NSUrl[] urls)
        => PickHandler?.Invoke(urls);

    public override void DidPickDocument(UIDocumentPickerViewController controller, NSUrl url)
        => PickHandler?.Invoke(new NSUrl[] { url });
}

class UIPresentationControllerDelegate : UIAdaptivePresentationControllerDelegate
{
    Action dismissHandler;

    internal UIPresentationControllerDelegate(Action dismissHandler)
    {
        this.dismissHandler = dismissHandler;
    }

    public override void DidDismiss(UIPresentationController presentationController)
    {
        dismissHandler?.Invoke();
        dismissHandler = null;
    }

    protected override void Dispose(bool disposing)
    {
        // Do NOT invoke dismissHandler here: Dispose runs from the GC finalizer when UIKit drops the
        // weak delegate reference, which would wrongly complete the picker tcs with null. Dismissal
        // is handled by DidDismiss above.
        base.Dispose(disposing);
    }
}
