// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Foundation;
using UIKit;
using AVFoundation;
using CoreMedia;
using CoreGraphics;
using Photos;
using StoreKit;
using System.Net;

namespace Season.Platforms.Shared.Apple;

internal class AppleDeviceCore : IDeviceCore
{
    public virtual Basic.Platform Platform => Basic.Platform.None;

    public Channel Channel { get; set; } = Channel.Apple;

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
                var scene = UIApplication.SharedApplication.ConnectedScenes.ToArray()[0] as UIWindowScene;

                if (scene.InterfaceOrientation.IsPortrait())
                {
                    orientation = Orientation.Portrait;
                }
                else if (scene.InterfaceOrientation.IsLandscape())
                {
                    orientation = Orientation.LandscapeLeft;
                }
            }

            return orientation;
        }
        set
        {
            var scene = UIApplication.SharedApplication.ConnectedScenes.ToArray()[0] as UIWindowScene;

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

            scene.RequestGeometryUpdate(geometry, null);
        }
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
        return UIScreen.MainScreen.TraitCollection.UserInterfaceStyle == UIUserInterfaceStyle.Dark;
    }

    public async Task<bool> RequestPermissionAsync(string[] permissions)
    {
        // Microphone requests go through the TCC-backed AVCaptureDevice consent (one system
        // prompt per app install); photo requests keep the existing Photos flow. Everything
        // else is treated as granted, matching the other platforms.
        var needsMicrophone = permissions is not null && permissions.Any(p =>
            p is not null && (p.Contains("RECORD_AUDIO", StringComparison.OrdinalIgnoreCase)
                || p.Contains("MICROPHONE", StringComparison.OrdinalIgnoreCase)));

        if (!needsMicrophone)
        {
            var status = PHPhotoLibrary.AuthorizationStatus;

            bool authotization = status == PHAuthorizationStatus.Authorized;

            if (!authotization)
            {
                authotization = await PHPhotoLibrary.RequestAuthorizationAsync() == PHAuthorizationStatus.Authorized;
            }
            return authotization;
        }

        return await RequestMicrophonePermissionAsync();
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

internal class AppleMediaPlayer : IMediaPlayer
{
    public bool IsPlaying
    {
        get
        {
            if (MusicPlayer != null && MusicPlayer.Status != AVPlayerStatus.ReadyToPlay || SoundPlayer != null && SoundPlayer.Status != AVPlayerStatus.ReadyToPlay)
            {
                return true;
            }

            return false;
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
    public async Task<string> ShowMessage(string title, string desc, string[] buttons, string text)
    {
        return null;
    }

    public async Task<string> ShowKeyboard(string title, string desc, string[] buttons, string text)
    {
        var tcs = new TaskCompletionSource<string>();

        UIApplication.SharedApplication.InvokeOnMainThread(delegate
        {
            try
            {
                var alertController = new UIViewController();

                //var width = DeviceServices.BaseApp.DeviceResolution.X - 50;

                var textView = new UITextView(new CGRect(new CGPoint(20, 20), new CGSize(580, 550)));

                textView.Font = UIFont.SystemFontOfSize(18);
                textView.Text = text;
                textView.Editable = true;

                //textView.BecomeFirstResponder();

                alertController.View.AddSubview(textView);

                var btnOK = new UIButton(new CGRect(150, 600, 100, 50));

                btnOK.BackgroundColor = UIColor.Gray;

                btnOK.SetTitle("OK", UIControlState.Normal);

                btnOK.TouchDown += (s, e) =>
                {
                    // Read the CURRENT text from the UITextView at click time so user edits are captured.
                    tcs.TrySetResult(textView.Text);

                    alertController.DismissViewController(true, () => { });
                };

                alertController.View.AddSubview(btnOK);

                var btnCancel = new UIButton(new CGRect(350, 600, 100, 50));

                btnCancel.BackgroundColor = UIColor.Gray;

                btnCancel.SetTitle("Cancel", UIControlState.Normal);

                btnCancel.TouchDown += (s, e) =>
                {
                    tcs.TrySetResult(null);

                    alertController.DismissViewController(true, () => { });
                };

                alertController.View.AddSubview(btnCancel);

                var parentController = Microsoft.Maui.ApplicationModel.Platform.GetCurrentUIViewController();

                if (parentController is not null)
                {
                    // NOTE: the completionHandler of PresentViewController fires when the PRESENTATION
                    // animation finishes (i.e., right after the sheet appears), NOT when it is dismissed.
                    // Do NOT resolve tcs here — otherwise the caller returns with the initial text before
                    // the user gets a chance to edit, and later OK/Cancel TrySetResult calls become no-ops.
                    parentController.PresentViewController(alertController, true, null);
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

        using var streamTarget = System.IO.File.OpenWrite(fileUrl);

        var length = (int)(stream.Length < 4096 ? stream.Length : 4096);

        var array = new byte[length];

        int bytesRead = 0;

        while ((bytesRead = stream.Read(array, 0, length)) > 0)
        {
            streamTarget.Write(array, 0, bytesRead);
        }

        taskCompetedSource = new(cancellationToken);

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

        // Set by the presentation completion handler: false means the modal never made it on screen.
        var presented = false;

        UIApplication.SharedApplication.InvokeOnMainThread(delegate
        {
            var currentViewController = Microsoft.Maui.ApplicationModel.Platform.GetCurrentUIViewController();

            if (currentViewController is not null)
            {
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

            //OpenMediaGalleryFile(result);
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

    //Photo (Photo Permission)

    async Task<string> SaveStorageFile(string name)
    {
        var file = "";

        var images = new string[] { ".jpg", ".jpeg", ".png" };

        var videos = new string[] { ".mp4", ".mov" };

        var fileName = Path.GetFileName(name);

        var ext = Path.GetExtension(name).ToLower();

        var origin = Path.Combine(StorageService.Path(StorageService.DirectoryBase), name);

        if (images.Contains(ext) || videos.Contains(ext))
        {
            var nsUrl = new NSUrl(origin);

            NSError error = null;

            string identifier = null;

            var result = PHPhotoLibrary.SharedPhotoLibrary.PerformChangesAndWait(() =>
            {
                PHAssetChangeRequest request = null;

                if (images.Contains(ext))
                {
                    request = PHAssetChangeRequest.FromImage(nsUrl);
                }
                else if (videos.Contains(ext))
                {
                    request = PHAssetChangeRequest.FromVideo(nsUrl);
                }

                identifier = request.PlaceholderForCreatedAsset.LocalIdentifier;
            }, out error);

            var assets = PHAsset.FetchAssetsUsingLocalIdentifiers(new string[] { identifier }, null);

            var phAsset = assets.FirstObject as PHAsset;

            var assetResource = PHAssetResource.GetAssetResources(phAsset)?.FirstOrDefault();

            file = assetResource.ValueForKey(new NSString("privateFileURL")).ToString();
        }
        else
        {
            file = Path.Combine(FileSystem.CacheDirectory, name);

            File.Copy(fileName, file);
        }

        return file;
    }

    async Task<string> SaveBytesFile(string name, byte[] bytes)
    {
        var file = Path.Combine(FileSystem.CacheDirectory, name);

        File.WriteAllBytes(file, bytes);

        return file;
    }

    async Task<bool> OpenMediaGalleryFile(string name)
    {
        var nsUrl = new NSUrl(name);

        return await Launcher.OpenAsync(new OpenFileRequest
        {
            File = new ReadOnlyFile(nsUrl.RelativePath)
        });
    }

    public async Task<bool> OpenLink(string name)
    {
        var options = new UIApplicationOpenUrlOptions()
        {

        };

        return await UIApplication.SharedApplication.OpenUrlAsync(new NSUrl(name), options);
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

internal class AppleGalleryService : IGalleryService
{
    public async Task<Stream> MediaAsset(MediaAsset mediaAsset)
    {
        //var phAsset = mediaAsset.Object as PHAsset;

        if (mediaAsset.Path.IsNullOrWhiteSpace())
        {

        }
        else if (System.IO.File.Exists(mediaAsset.Path))
        {
            var stream = File.OpenRead(mediaAsset.Path);

            return stream;
        }

        var imageManager = new PHCachingImageManager();

        var thumbnailRequestOptions = new PHImageRequestOptions();
        thumbnailRequestOptions.ResizeMode = PHImageRequestOptionsResizeMode.Fast;
        thumbnailRequestOptions.DeliveryMode = PHImageRequestOptionsDeliveryMode.FastFormat;
        thumbnailRequestOptions.NetworkAccessAllowed = true;
        thumbnailRequestOptions.Synchronous = true;

        var requestOptions = new PHImageRequestOptions();
        requestOptions.ResizeMode = PHImageRequestOptionsResizeMode.Exact;
        requestOptions.DeliveryMode = PHImageRequestOptionsDeliveryMode.HighQualityFormat;
        requestOptions.NetworkAccessAllowed = true;
        requestOptions.Synchronous = true;

        var tcs = new TaskCompletionSource<Stream>();

        var fetchOptions = new PHFetchOptions();
        fetchOptions.SortDescriptors = new NSSortDescriptor[] { new NSSortDescriptor("creationDate", false) };
        fetchOptions.Predicate = NSPredicate.FromFormat($"mediaType == {(int)PHAssetMediaType.Image} || mediaType == {(int)PHAssetMediaType.Video}");

        var nsUrls = new NSUrl[] { new NSUrl(mediaAsset.Path) };

        PHFetchResult fetchResults = null; // PHAsset.FetchAssets(nsUrls, null);

        if (fetchResults == null || fetchResults.Count == 0)
        {
            tcs.TrySetResult(null);
        }
        else
        {
            var phAsset = fetchResults[0] as PHAsset;

            var tmpPath = Path.GetTempPath();

            var allAssets = fetchResults.Select(p => p as PHAsset).ToArray();

            var thumbnailSize = new CGSize(300.0f, 300.0f);

            var name = PHAssetResource.GetAssetResources(phAsset)?.FirstOrDefault()?.OriginalFilename;

            if (phAsset.MediaType is PHAssetMediaType.Image)
            {
                imageManager.RequestImageData(phAsset, null, (data, dataUti, orientation, info) =>
                {
                    var bytes = data.ToArray();

                    var stream = new MemoryStream(bytes);

                    bytes = null;

                    tcs.TrySetResult(stream);
                });
            }
            else
            {
                PHVideoRequestOptions pHVideoRequestOptions = null;

                imageManager.RequestAVAsset(phAsset, pHVideoRequestOptions, (asset, audioMix, info) =>
                {
                    var avAsset = asset as AVUrlAsset;

                    if (avAsset == null)
                    {

                    }
                    else
                    {
                        var url = avAsset.Url.RelativePath;

                        var stream = System.IO.File.OpenRead(url);

                        //var avData = NSData.FromUrl(avAsset.Url);
                        //bytes = avData.ToArray();
                        //stream.Write(bytes, 0, bytes.Length);

                        tcs.TrySetResult(stream);
                    }
                });

                //var memoryStream = new MemoryStream();
                //var assetResource = PHAssetResource.GetAssetResources(phAsset)?.FirstOrDefault();
                //PHAssetResourceRequestOptions option = null;
                //PHAssetResourceManager.DefaultManager.RequestData(assetResource, option, nsData =>
                //{
                //    var bytes0 = nsData.ToArray();
                //    memoryStream.Write(bytes0, 0, bytes0.Length);
                //},
                //err =>
                //{
                //    if (err == null)
                //    {
                //        tcs.TrySetResult(memoryStream);
                //    }
                //    else
                //    {
                //        tcs.TrySetResult(null);
                //    }
                //});
            }
        }

        return await tcs.Task;
    }

    public async Task<List<MediaAsset>> MediaGallery()
    {
        var assets = new List<MediaAsset>();

        var imageManager = new PHCachingImageManager();

        var hasPermission = await DeviceServices.Core.RequestPermissionAsync(null);

        if (hasPermission)
        {
            var thumbnailRequestOptions = new PHImageRequestOptions();
            thumbnailRequestOptions.ResizeMode = PHImageRequestOptionsResizeMode.Fast;
            thumbnailRequestOptions.DeliveryMode = PHImageRequestOptionsDeliveryMode.FastFormat;
            thumbnailRequestOptions.NetworkAccessAllowed = true;
            thumbnailRequestOptions.Synchronous = true;

            var requestOptions = new PHImageRequestOptions();
            requestOptions.ResizeMode = PHImageRequestOptionsResizeMode.Exact;
            requestOptions.DeliveryMode = PHImageRequestOptionsDeliveryMode.HighQualityFormat;
            requestOptions.NetworkAccessAllowed = true;
            requestOptions.Synchronous = true;

            var fetchOptions = new PHFetchOptions();
            fetchOptions.SortDescriptors = new NSSortDescriptor[] { new NSSortDescriptor("creationDate", false) };
            fetchOptions.Predicate = NSPredicate.FromFormat($"mediaType == {(int)PHAssetMediaType.Image} || mediaType == {(int)PHAssetMediaType.Video}");

            var fetchResults = PHAsset.FetchAssets(fetchOptions);
            var tmpPath = Path.GetTempPath();
            var allAssets = fetchResults.Select(p => p as PHAsset).ToArray();
            var thumbnailSize = new CoreGraphics.CGSize(300.0f, 300.0f);

            imageManager.StartCaching(allAssets, thumbnailSize, PHImageContentMode.AspectFit, thumbnailRequestOptions);
            imageManager.StartCaching(allAssets, PHImageManager.MaximumSize, PHImageContentMode.AspectFit, requestOptions);

            foreach (var result in fetchResults)
            {
                var phAsset = (result as PHAsset);

                var assetResource = PHAssetResource.GetAssetResources(phAsset)?.FirstOrDefault();

                var size = assetResource.ValueForKey(new NSString("fileSize"));

                var file = assetResource.ValueForKey(new NSString("privateFileURL"));

                var name = assetResource?.OriginalFilename;

                var date = (DateTime)phAsset.CreationDate;

                var asset = new MediaAsset()
                {
                    Id = phAsset.LocalIdentifier,
                    Name = Path.GetFileNameWithoutExtension(name),
                    Ext = Path.GetExtension(name),
                    Category = date.ToMonthDate(),
                    Path = file.ToString(),
                    PreviewPath = "",
                    Type = phAsset.MediaType == PHAssetMediaType.Image ? MediaAssetType.Image : MediaAssetType.Video,
                    Size = long.Parse(size.ToString()),
                    Time = date.ToDateTimeSeconds(),
                    Object = phAsset
                };

                assets.Add(asset);
            }

            return assets;
        }

        return null;
    }

    public async Task<List<MediaAsset>> MediaGalleryDownloads()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Downloads");

        if (Directory.Exists(directory))
        {

        }
        else
        {
            Directory.CreateDirectory(directory);
        }

        var files = Directory.GetFiles(directory).NullToEmptyArray();

        var mediaAssets = new List<MediaAsset>();

        foreach (var file in files)
        {
            var fileInfo = new FileInfo(file);

            if (fileInfo.Attributes.HasFlag(System.IO.FileAttributes.Hidden))
            {

            }
            else
            {
                var mediaAsset = new MediaAsset()
                {
                    Name = Path.GetFileNameWithoutExtension(file),
                    Ext = Path.GetExtension(file),
                    Path = file,
                    Size = fileInfo.Length,
                    Type = MediaAssetType.Unknown,
                    Time = fileInfo.CreationTime.ToDateTimeSeconds()
                };

                mediaAssets.Add(mediaAsset);
            }
        }

        return mediaAssets;
    }

    public async Task<bool> MediaRemove(MediaAsset[] mediaAssets, bool delEmptyDirectory)
    {
        var tcs = new TaskCompletionSource<bool>();

        if (mediaAssets == null || mediaAssets.Length == 0)
        {
            tcs.TrySetResult(false);
        }
        else
        {
            var mediaDowns = mediaAssets.Where(me => me.Object == null).NullToEmptyArray();

            foreach (var mediaDown in mediaDowns)
            {
                System.IO.File.Delete(mediaDown.Path);
            }

            var phAssets = mediaAssets.Where(me => me.Object != null).NullToEmptyArray().Select(me => me.Object as PHAsset).NullToEmptyArray();

            if (phAssets.Length > 0)
            {
                PHPhotoLibrary.SharedPhotoLibrary.PerformChanges(() =>
                {
                    PHAssetChangeRequest.DeleteAssets(phAssets);

                },
                (result, err) =>
                {
                    if (result)
                    {
                        tcs.TrySetResult(true);
                    }
                    else
                    {
                        tcs.TrySetResult(false);
                    }
                });
            }
            else
            {
                tcs.TrySetResult(true);
            }
        }

        return await tcs.Task;
    }
}

//Need Record Permission
internal class AppleRecordService : RecordService, IRecordService
{
    //Record (Record Permission)

    // Output contract shared with the Android/Windows/Web recorders: 16 kHz, mono, 16-bit
    // little-endian PCM in a .wav container, handed back as byte[] by StopRecord.
    const double RecordSampleRate = 16000;

    const int RecordChannels = 1;

    const int RecordBitsPerSample = 16;

    // Fallback capture rate: the Mac hardware sample rate, used to tell whether a refused Record()
    // is about the 16 kHz request rather than about the input itself.
    const double RecordHardwareSampleRate = 48000;

    // The binding exposes no "None" member for the options flag enum.
    const AVAudioSessionCategoryOptions NoCategoryOptions = (AVAudioSessionCategoryOptions)0;

    /// <summary>One AVAudioRecorder configuration to try; see <see cref="Attempts"/>.</summary>
    readonly record struct RecordAttempt(string Name, AVAudioSessionCategory Category, AVAudioSessionCategoryOptions Options, double SampleRate, bool ConfigureSession, bool Prepare);

    // AVAudioRecorder configurations, plainest first: AllowBluetooth/DefaultToSpeaker are iOS-only
    // options that Mac Catalyst accepts and then ignores, and its AVAudioSession is just a shim over
    // CoreAudio, so every step drops one more iOS assumption. The first attempt whose Record()
    // actually starts wins, and each attempt logs the session state it ended up with.
    static readonly RecordAttempt[] Attempts =
    {
        new("record-16k", AVAudioSessionCategory.Record, NoCategoryOptions, RecordSampleRate, true, true),
        new("record-16k-noprepare", AVAudioSessionCategory.Record, NoCategoryOptions, RecordSampleRate, true, false),
        new("playandrecord-16k", AVAudioSessionCategory.PlayAndRecord, NoCategoryOptions, RecordSampleRate, true, true),
        new("record-16k-nosession", AVAudioSessionCategory.Record, NoCategoryOptions, RecordSampleRate, false, true),
        new("record-48k", AVAudioSessionCategory.Record, NoCategoryOptions, RecordHardwareSampleRate, true, true),
        new("playandrecord-16k-iosoptions", AVAudioSessionCategory.PlayAndRecord, AVAudioSessionCategoryOptions.DefaultToSpeaker | AVAudioSessionCategoryOptions.AllowBluetooth, RecordSampleRate, true, true)
    };

    AVAudioRecorder recorder = null;

    TaskCompletionSource<byte[]> tcsRecord = null;

    // Absolute path of the temp .wav the live recorder writes to, kept so StopRecord can delete
    // it once the bytes have been read back.
    string recordFile = null;

#if MACCATALYST
    // AVAudioEngine capture state: the Mac Catalyst path, which reaches CoreAudio without going
    // through the AVAudioSession shim at all.
    AVAudioEngine captureEngine = null;

    // Strong reference to the installed tap block: it is invoked on the audio render thread and has
    // to stay alive for as long as the engine runs.
    AVAudioNodeTapBlock captureTap = null;

    object captureGate = null;

    List<float> captureSamples = null;

    double captureSampleRate = 0;
#endif

    public async Task<bool> StartRecord()
    {
        LastFailure = RecordFailure.None;

        var permissions = new string[]
        {
            "RECORD_AUDIO"
        };

        var hasPermission = await DeviceServices.Core.RequestPermissionAsync(permissions);

        if (!hasPermission)
        {
            Log(LogType.Error, "StartRecord aborted, microphone permission not granted");

            LastFailure = RecordFailure.Permission;

            return false;
        }

        // AVAudioSession and AVAudioRecorder have to be driven from the main thread: the permission
        // await above resumes on an AVFoundation callback queue, and configuring the session off
        // the main run loop is on its own enough to make PrepareToRecord return false. A recorder
        // left over from a session that was never stopped is released there too.
        var tcs = new TaskCompletionSource<bool>();

        UIApplication.SharedApplication.InvokeOnMainThread(delegate
        {
            tcs.TrySetResult(StartRecorder());
        });

        var success = await tcs.Task;

        if (!success)
        {
            try { AVAudioSession.SharedInstance().SetActive(false, out _); }
            catch { }
        }

        return success;
    }

    bool StartRecorder()
    {
        try
        {
            ReleaseStaleRecorder();

            var session = AVAudioSession.SharedInstance();

            LogSessionState(session);

#if MACCATALYST
            // The session starts out in SoloAmbient, a category with no input route at all, and on Mac
            // Catalyst that leaves AVAudioEngine.InputNode without a hardware format (CommonFormat
            // Other, 0 Hz, 0 channels) even though the microphone itself works, so the session has to
            // be brought into an input-capable category and activated before the engine can see any
            // hardware. PlayAndRecord rather than Record because the engine also instantiates its
            // output node, and because the app keeps playing sound while a recording is armed.
            if (!session.SetCategory(AVAudioSessionCategory.PlayAndRecord, NoCategoryOptions, out NSError sessionError))
            {
                Log(LogType.Error, $"SetCategory(PlayAndRecord) failed: {Describe(sessionError)}");
            }

            if (!session.SetActive(true, out NSError activeError))
            {
                Log(LogType.Error, $"SetActive(true) failed: {Describe(activeError)}");
            }

            LogRouteState(session, "session");

            // Fail fast when the machine has no microphone: neither AVAudioRecorder nor AVAudioEngine
            // reports a usable error in that case, PrepareToRecord still succeeds and writes a 4 KB
            // WAV skeleton, Record() just returns false, and every rung of the ladder below then
            // spends seconds re-activating a session that can never get a route. Mac mini and Mac
            // Studio ship without a built-in microphone.
            if (!ProbeInputDevice(out string inputDevice))
            {
                Log(LogType.Error, $"no input device ({inputDevice}): CoreAudio exposes no microphone to this Mac, connect a USB, Bluetooth or 3.5mm microphone and select it under System Settings > Sound > Input");

                LastFailure = RecordFailure.Device;

                return false;
            }

            Log(LogType.None, $"input device present: {inputDevice}");

            // AVAudioRecorder reads its samples through the AVAudioSession route, while the engine's
            // input node is wired to the CoreAudio HAL, so the engine is the more direct path here.
            if (TryStartEngineCapture())
            {
                return true;
            }
#endif

            foreach (var attempt in Attempts)
            {
                if (TryStartRecording(session, attempt))
                {
                    return true;
                }
            }

            Log(LogType.Error, "StartRecord failed for every capture path, check System Settings > Privacy & Security > Microphone (an app launched from a terminal or IDE inherits that parent's microphone status) and System Settings > Sound > Input for a selected device");

            LastFailure = RecordFailure.Unknown;

            return false;
        }
        catch (Exception ex)
        {
            Log(LogType.Error, $"StartRecord failed: {ex}");

            LastFailure = RecordFailure.Unknown;

            return false;
        }
    }

    bool TryStartRecording(AVAudioSession session, RecordAttempt attempt)
    {
        // On Mac Catalyst the audio session is only a partial shim of the iOS one, so a rejected
        // category or activation is logged and recording is attempted anyway: the recorder is the
        // authority on whether an input is really reachable.
        if (attempt.ConfigureSession)
        {
            if (!session.SetCategory(attempt.Category, attempt.Options, out NSError categoryError))
            {
                Log(LogType.Error, $"{attempt.Name}: SetCategory({attempt.Category}, {attempt.Options}) failed: {Describe(categoryError)}");
            }

            if (!session.SetActive(true, out NSError activeError))
            {
                Log(LogType.Error, $"{attempt.Name}: SetActive(true) failed: {Describe(activeError)}");
            }
        }
        else
        {
            // Leave the session untouched (and deactivated) to tell a recorder-settings problem apart
            // from a session-route problem.
            try { session.SetActive(false, out _); }
            catch { }
        }

        LogRouteState(session, attempt.Name);

        var audioFile = Path.Combine(Path.GetTempPath(), $"Record-{DateTime.Now:yyyyMMddHHmmssfff}-{attempt.Name}.wav");

        Directory.CreateDirectory(Path.GetDirectoryName(audioFile));

        var audioSettings = new AudioSettings
        {
            SampleRate = attempt.SampleRate,
            NumberChannels = RecordChannels,
            AudioQuality = AVAudioQuality.High,
            Format = AudioToolbox.AudioFormatType.LinearPCM,
            LinearPcmBitDepth = RecordBitsPerSample,
            // WAV is little-endian integer PCM; leaving these unset lets the encoder fall back to
            // its own defaults and produces a file the STT backends cannot decode.
            LinearPcmFloat = false,
            LinearPcmBigEndian = false
        };

        recorder = AVAudioRecorder.Create(NSUrl.FromFilename(audioFile), audioSettings, out NSError createError);

        if (recorder == null)
        {
            Log(LogType.Error, $"{attempt.Name}: AVAudioRecorder.Create({audioFile}) failed: {Describe(createError)}");

            DeleteTempFile(audioFile);

            return false;
        }

        // The completion source is captured locally: StopRecord nulls the field before this callback
        // is delivered, and reading the field here would throw and leave the caller awaiting a task
        // that never completes.
        var tcs = new TaskCompletionSource<byte[]>();

        tcsRecord = tcs;

        recordFile = audioFile;

        // Set once this attempt gives up: Stop still queues FinishedRecording, and the callback would
        // otherwise read a temp file that the failure path has just deleted.
        var abandoned = false;

        recorder.FinishedRecording += (s, e) =>
        {
            byte[] bytes = null;

            try
            {
                if (!abandoned && e.Status && System.IO.File.Exists(audioFile))
                {
                    bytes = System.IO.File.ReadAllBytes(audioFile);
                }
            }
            catch (Exception ex)
            {
                Log(LogType.Error, $"FinishedRecording read failed: {ex.Message}");
            }

            tcs.TrySetResult(bytes);
        };

        var prepared = !attempt.Prepare || recorder.PrepareToRecord();

        // Deliberately no RecordAt(DeviceCurrentTime + delay) retry here: it blocks the calling thread
        // until the scheduled device time and, when the reason for the refusal is a missing input
        // rather than timing, it adds tens of seconds of frozen UI for a guaranteed false.
        var started = prepared && recorder.Record();

        // bytes is the decisive fact: PrepareToRecord alone writes a 4 KB WAV skeleton (RIFF/JUNK/fmt
        // /FLLR, no data chunk), so a non-zero size still says nothing about audio having been read.
        Log(started ? LogType.None : LogType.Error, $"{attempt.Name}: prepareToRecord={prepared} record={started} recording={recorder.Recording} bytes={FileLength(audioFile)} createError={Describe(createError)} file={audioFile}");

        if (!started)
        {
            abandoned = true;

            try { recorder.Stop(); }
            catch { }

            recorder.Dispose();

            recorder = null;

            tcsRecord = null;

            recordFile = null;

            // Release the session so the next category can be activated cleanly.
            try { session.SetActive(false, out _); }
            catch { }

            DeleteTempFile(audioFile);

            return false;
        }

        return true;
    }

    /// <summary>
    /// Logs the route the session actually ended up with, which is the fact that decides whether
    /// AVAudioRecorder can ever start. inputAvailable=false with a non-empty availableInputs means the
    /// session shim never built an input route; both empty means macOS gave the process no input
    /// device at all. Purely diagnostic, so a failure here must never abort the attempt.
    /// </summary>
    static void LogRouteState(AVAudioSession session, string tag)
    {
        try
        {
            var inputs = session.CurrentRoute?.Inputs.NullToEmptyArray().Select(input => $"{input.PortType} {input.PortName}").ToArray();

            var available = session.AvailableInputs.NullToEmptyArray().Select(input => $"{input.PortType} {input.PortName}").ToArray();

            Log(LogType.None, $"{tag}: applied category={session.Category} options={session.CategoryOptions} sampleRate={session.SampleRate} inputAvailable={session.InputAvailable} inputs=[{string.Join(", ", inputs.NullToEmptyArray())}] availableInputs=[{string.Join(", ", available.NullToEmptyArray())}]");
        }
        catch (Exception ex)
        {
            Log(LogType.None, $"{tag}: route state unavailable: {ex.Message}");
        }
    }

    static long FileLength(string file)
    {
        try
        {
            return file != null && System.IO.File.Exists(file) ? new FileInfo(file).Length : -1;
        }
        catch
        {
            return -1;
        }
    }

#if MACCATALYST
    /// <summary>
    /// Reports whether a microphone exists at all, through AVCaptureDevice discovery. That is the
    /// right authority because it is independent of AVAudioSession: AVAudioEngine.InputNode has no
    /// hardware format until the session sits in an input-capable category and is active, so probing
    /// through the engine reads CommonFormat Other / 0 Hz / 0 channels on a machine whose microphone
    /// works perfectly and would short-circuit every capture path behind it. GetDefaultDevice is the
    /// AVFoundation counterpart of CoreAudio's default input device.
    /// </summary>
    static bool ProbeInputDevice(out string detail)
    {
        detail = "none";

        try
        {
            var device = AVCaptureDevice.GetDefaultDevice(AVMediaTypes.Audio);

            if (device == null)
            {
                return false;
            }

            detail = $"{device.LocalizedName} connected={device.Connected} uniqueId={device.UniqueID}";

            return device.Connected;
        }
        catch (Exception ex)
        {
            detail = ex.Message;

            return false;
        }
    }

    /// <summary>
    /// Captures through a tap on an AVAudioEngine input node instead of AVAudioRecorder. The recorder
    /// reads its samples through the AVAudioSession route, and the Mac Catalyst session shim reports
    /// inputAvailable=false with no route inputs even after the category was accepted, the session was
    /// activated and TCC granted the microphone, which makes Record() answer false while
    /// PrepareToRecord already wrote the WAV skeleton. The engine's input node is wired straight to the
    /// CoreAudio HAL and does not depend on that route; the float samples it delivers are converted
    /// here to the same 16 kHz mono PCM16 WAV the other platforms hand back.
    /// </summary>
    bool TryStartEngineCapture()
    {
        AVAudioEngine engine = null;

        try
        {
            engine = new AVAudioEngine();

            var inputNode = engine.InputNode;

            if (inputNode == null)
            {
                Log(LogType.Error, "engine: no input node");

                return false;
            }

            var format = inputNode.GetBusOutputFormat((nuint)0);

            // Only float32 exposes a FloatChannelData pointer; any other common format would need a
            // conversion node in between, and the recorder ladder below is the cheaper fallback.
            if (format == null || format.CommonFormat != AVAudioCommonFormat.PCMFloat32 || format.SampleRate <= 0 || format.ChannelCount == 0)
            {
                Log(LogType.Error, $"engine: unusable input format commonFormat={format?.CommonFormat} sampleRate={format?.SampleRate} channels={format?.ChannelCount}");

                return false;
            }

            var gate = new object();

            var samples = new List<float>();

            var channels = (int)format.ChannelCount;

            // Deinterleaved float32 (what an input node normally reports) hands over one pointer per
            // channel; interleaved keeps them all in the first buffer with a stride.
            var interleaved = format.Interleaved;

            // Runs on the audio render thread, so it must not throw, must not touch the audio session
            // and must keep its per-callback allocation down to the mono scratch buffer.
            var tap = new AVAudioNodeTapBlock((buffer, when) =>
            {
                try
                {
                    var frames = (int)buffer.FrameLength;

                    if (frames <= 0)
                    {
                        return;
                    }

                    var channelPointers = buffer.FloatChannelData;

                    if (channelPointers == IntPtr.Zero)
                    {
                        return;
                    }

                    var mono = new float[frames];

                    var data = interleaved ? new float[frames * channels] : new float[frames];

                    if (interleaved)
                    {
                        var pointer = System.Runtime.InteropServices.Marshal.ReadIntPtr(channelPointers, 0);

                        if (pointer != IntPtr.Zero)
                        {
                            System.Runtime.InteropServices.Marshal.Copy(pointer, data, 0, data.Length);

                            for (var i = 0; i < frames; i++)
                            {
                                var sum = 0f;

                                for (var channel = 0; channel < channels; channel++)
                                {
                                    sum += data[i * channels + channel];
                                }

                                mono[i] = sum / channels;
                            }
                        }
                    }
                    else
                    {
                        for (var channel = 0; channel < channels; channel++)
                        {
                            var pointer = System.Runtime.InteropServices.Marshal.ReadIntPtr(channelPointers, channel * IntPtr.Size);

                            if (pointer == IntPtr.Zero)
                            {
                                continue;
                            }

                            System.Runtime.InteropServices.Marshal.Copy(pointer, data, 0, frames);

                            for (var i = 0; i < frames; i++)
                            {
                                mono[i] += data[i];
                            }
                        }

                        if (channels > 1)
                        {
                            for (var i = 0; i < frames; i++)
                            {
                                mono[i] /= channels;
                            }
                        }
                    }

                    lock (gate)
                    {
                        samples.AddRange(mono);
                    }
                }
                catch
                {
                    // Never propagate into the render thread.
                }
            });

            inputNode.InstallTapOnBus((nuint)0, (uint)4096, format, tap);

            engine.Prepare();

            if (!engine.StartAndReturnError(out NSError startError))
            {
                Log(LogType.Error, $"engine: StartAndReturnError failed: {Describe(startError)}");

                return false;
            }

            // captureTap keeps the block alive: without a strong reference it can be collected while
            // the engine is still running, which crashes the render thread.
            captureEngine = engine;

            captureTap = tap;

            captureGate = gate;

            captureSamples = samples;

            captureSampleRate = format.SampleRate;

            Log(LogType.None, $"engine: capturing sampleRate={captureSampleRate} channels={channels} commonFormat={format.CommonFormat} interleaved={interleaved} running={engine.Running}");

            return true;
        }
        catch (Exception ex)
        {
            Log(LogType.Error, $"engine: capture start failed: {ex.Message}");

            return false;
        }
        finally
        {
            // Torn down here only when the engine never became the live capture; ownership moved to
            // captureEngine otherwise.
            if (engine != null && !ReferenceEquals(engine, captureEngine))
            {
                try { engine.InputNode.RemoveTapOnBus((nuint)0); }
                catch { }

                try { engine.Dispose(); }
                catch { }
            }
        }
    }

    /// <summary>
    /// Stops the tap, resamples what it collected and packs it into the shared WAV contract.
    /// </summary>
    byte[] FinishEngineCapture()
    {
        var engine = captureEngine;

        var gate = captureGate;

        var samples = captureSamples;

        var rate = captureSampleRate;

        captureEngine = null;

        captureTap = null;

        captureGate = null;

        captureSamples = null;

        captureSampleRate = 0;

        if (engine == null || samples == null || gate == null)
        {
            return null;
        }

        try
        {
            engine.InputNode.RemoveTapOnBus((nuint)0);
        }
        catch (Exception ex)
        {
            Log(LogType.None, $"engine: RemoveTapOnBus failed: {ex.Message}");
        }

        try
        {
            engine.Stop();

            engine.Dispose();
        }
        catch (Exception ex)
        {
            Log(LogType.None, $"engine: shutdown failed: {ex.Message}");
        }

        float[] captured;

        lock (gate)
        {
            captured = samples.ToArray();
        }

        var audio = Resample(captured, rate, RecordSampleRate);

        Log(LogType.None, $"engine: captured {captured.Length} frames at {rate} Hz, delivering {audio.Length} frames at {RecordSampleRate} Hz");

        return WriteWavPcm16Mono(audio, (int)RecordSampleRate);
    }

    /// <summary>
    /// Linear resample, which is all speech at 48 kHz down to 16 kHz needs; the STT backends resample
    /// again internally.
    /// </summary>
    static float[] Resample(float[] source, double sourceRate, double targetRate)
    {
        if (source == null || source.Length == 0)
        {
            return Array.Empty<float>();
        }

        if (sourceRate <= 0 || targetRate <= 0 || Math.Abs(sourceRate - targetRate) < 0.5)
        {
            return source;
        }

        var ratio = sourceRate / targetRate;

        var count = (int)(source.Length / ratio);

        var result = new float[count];

        for (var i = 0; i < count; i++)
        {
            var position = i * ratio;

            var index = (int)position;

            if (index >= source.Length)
            {
                break;
            }

            var fraction = (float)(position - index);

            var left = source[index];

            var right = index + 1 < source.Length ? source[index + 1] : left;

            result[i] = left + (right - left) * fraction;
        }

        return result;
    }

    /// <summary>
    /// Writes the canonical 44-byte WAV header plus little-endian PCM16 samples, the layout
    /// Whisper.ReadWavPcm16 walks chunk by chunk.
    /// </summary>
    static byte[] WriteWavPcm16Mono(float[] samples, int sampleRate)
    {
        samples ??= Array.Empty<float>();

        var dataBytes = samples.Length * 2;

        var bytes = new byte[44 + dataBytes];

        using var stream = new MemoryStream(bytes);
        using var writer = new BinaryWriter(stream);

        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray());
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)RecordChannels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * RecordChannels * RecordBitsPerSample / 8);
        writer.Write((short)(RecordChannels * RecordBitsPerSample / 8));
        writer.Write((short)RecordBitsPerSample);
        writer.Write("data"u8.ToArray());
        writer.Write(dataBytes);

        foreach (var sample in samples)
        {
            writer.Write((short)(Math.Clamp(sample, -1f, 1f) * 32767f));
        }

        writer.Flush();

        return bytes;
    }
#endif

    /// <summary>
    /// Drops a recorder whose StopRecord never ran (the caller was closed or threw in between), so
    /// the field does not stay occupied and silently block every later attempt. A caller still
    /// awaiting that session is completed with null instead of hanging.
    /// </summary>
    void ReleaseStaleRecorder()
    {
#if MACCATALYST
        if (captureEngine != null)
        {
            Log(LogType.None, "releasing the leftover engine capture");

            FinishEngineCapture();
        }
#endif

        var rec = recorder;

        var tcs = tcsRecord;

        var file = recordFile;

        recorder = null;

        tcsRecord = null;

        recordFile = null;

        if (rec == null)
        {
            return;
        }

        Log(LogType.None, "releasing the leftover recorder");

        try { rec.Stop(); }
        catch { }

        try { rec.Dispose(); }
        catch { }

        tcs?.TrySetResult(null);

        DeleteTempFile(file);
    }

    static void DeleteTempFile(string file)
    {
        // The temp .wav is only an intermediate; callers keep the bytes, same as the Windows
        // recorder which deletes its temp file after reading it back.
        try
        {
            if (file != null && System.IO.File.Exists(file))
            {
                System.IO.File.Delete(file);
            }
        }
        catch (Exception ex)
        {
            Log(LogType.None, $"temp cleanup failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Logs the permission, route and mute facts that decide whether PrepareToRecord can ever
    /// succeed. Purely diagnostic, so a failure here must never abort the recording attempt.
    /// </summary>
    static void LogSessionState(AVAudioSession session)
    {
        try
        {
            var inputs = session.CurrentRoute?.Inputs.NullToEmptyArray().Select(input => $"{input.PortType} {input.PortName}").ToArray();

            Log(LogType.None, $"tcc={AVCaptureDevice.GetAuthorizationStatus(AVAuthorizationMediaType.Audio)} {AudioApplicationState()} category={session.Category} inputAvailable={session.InputAvailable} sampleRate={session.SampleRate} inputs=[{string.Join(", ", inputs.NullToEmptyArray())}] temp={Path.GetTempPath()}");
        }
        catch (Exception ex)
        {
            Log(LogType.None, $"session state unavailable: {ex.Message}");
        }
    }

    /// <summary>
    /// AVAudioApplication, which also reports the system-level microphone mute, only exists on
    /// iOS/Mac Catalyst 17+ while the engine still supports 15+, so the query is version gated.
    /// </summary>
    static string AudioApplicationState()
    {
        if (OperatingSystem.IsIOSVersionAtLeast(17) || OperatingSystem.IsMacCatalystVersionAtLeast(17))
        {
            var application = AVAudioApplication.SharedInstance;

            return $"recordPermission={application.RecordPermission} inputMuted={application.InputMuted}";
        }

        return "recordPermission=unavailable";
    }

    static string Describe(NSError error)
    {
        return error == null ? "none" : $"{error.Domain} {error.Code} {error.LocalizedDescription}";
    }

    static void Log(LogType logType, string message)
    {
        DeviceServices.BaseApp?.AddLog(logType, $"{DateTime.UtcNow} [Record] {message}");
    }

    public async Task<byte[]> StopRecord()
    {
#if MACCATALYST
        // The engine path never touches the recorder, the temp file or FinishedRecording: it hands
        // back the WAV bytes it assembled from the tap itself.
        if (captureEngine != null)
        {
            var captured = FinishEngineCapture();

            try { AVAudioSession.SharedInstance().SetActive(false, out _); }
            catch { }

            return captured;
        }
#endif

        var rec = recorder;

        var tcs = tcsRecord;

        var file = recordFile;

        recorder = null;

        tcsRecord = null;

        recordFile = null;

        if (rec == null || tcs == null)
        {
            return null;
        }

        try
        {
            rec.Stop();
        }
        catch (Exception ex)
        {
            Log(LogType.Error, $"StopRecord stop failed: {ex.Message}");
        }

        // Stop only queues audioRecorderDidFinishRecording on the main run loop, and disposing the
        // recorder before that callback lands drops it, so wait for the file to be finalized first.
        // The timeout keeps a lost callback from hanging the caller forever.
        var completed = await Task.WhenAny(tcs.Task, Task.Delay(5000)) == tcs.Task;

        if (!completed)
        {
            Log(LogType.Error, "StopRecord timed out waiting for FinishedRecording");
        }

        try
        {
            rec.Dispose();
        }
        catch (Exception ex)
        {
            Log(LogType.Error, $"StopRecord dispose failed: {ex.Message}");
        }

        try { AVAudioSession.SharedInstance().SetActive(false, out _); }
        catch { }

        var bytes = completed ? await tcs.Task : null;

        DeleteTempFile(file);

        return bytes;
    }

    public Task<INativeImageDecoder?> CaptureScreen()
    {
        // iOS and MacCatalyst sandbox mode does not allow capturing external screen content.
        return Task.FromResult<INativeImageDecoder?>(null);
    }

    public Task<INativeImageDecoder?> CaptureApp()
    {
        // Inject the TaskCompletionSource, set the NeedCaptureApp flag,
        // then let the render thread detect it in AfterRender, read back from GPU, and call TrySetResult.
        var tcs = new TaskCompletionSource<INativeImageDecoder?>();
        BaseApp.CaptureAppTcs = tcs;
        return tcs.Task;
    }

    /// <summary>
    /// Decodes the audio of any container AVFoundation understands (.m4a/.m4v/.mp4/.mov/.mp3 ...)
    /// into a complete WAV: 16 kHz mono 16-bit PCM behind the canonical 44-byte RIFF header. That is
    /// the very output contract StopRecord has, and the only layout the STT front ends parse.
    /// AVAssetReaderTrackOutput does the decoding, the channel mixing and the resampling in one pass,
    /// and only the audio track is requested, so audio-only files and movies both work.
    /// Blocking, so callers run it on a background thread.
    /// </summary>
    public byte[] DecodeToWavPcm16(string path)
    {
        if (!System.IO.File.Exists(path))
        {
            throw new InvalidDataException($"The audio file does not exist: {path}");
        }

        using var asset = AVAsset.FromUrl(NSUrl.FromFilename(path))
            ?? throw new InvalidDataException($"Failed to open the audio file: {path}");

        var audioTrack = asset.Tracks.FirstOrDefault(track => track.MediaType == AVMediaTypes.Audio.GetConstant()!)
            ?? throw new InvalidDataException($"The file does not contain an audio track: {path}");

        using var reader = new AVAssetReader(asset, out NSError error)
            ?? throw new InvalidDataException(error?.LocalizedDescription ?? "Failed to create AVAssetReader.");

        var audioSettings = new AudioSettings
        {
            Format = AudioToolbox.AudioFormatType.LinearPCM,
            LinearPcmBigEndian = false,
            LinearPcmFloat = false,
            LinearPcmBitDepth = RecordBitsPerSample,
            NumberChannels = RecordChannels,
            SampleRate = RecordSampleRate
        };

        using var audioOutput = new AVAssetReaderTrackOutput(audioTrack, audioSettings);

        reader.AddOutput(audioOutput);

        if (!reader.StartReading())
        {
            throw new InvalidDataException(reader.Error?.LocalizedDescription ?? "Failed to start reading the audio track.");
        }

        using var pcm = new MemoryStream();

        while (reader.Status == AVAssetReaderStatus.Reading)
        {
            using var sample = audioOutput.CopyNextSampleBuffer();

            if (sample == null)
            {
                break;
            }

            using var blockBuffer = sample.GetDataBuffer();

            if (blockBuffer == null)
            {
                continue;
            }

            nuint dataLength = blockBuffer.DataLength;

            if (dataLength == 0)
            {
                continue;
            }

            var chunk = new byte[(int)dataLength];
            var handle = GCHandle.Alloc(chunk, GCHandleType.Pinned);

            try
            {
                nuint offset = 0;
                var status = blockBuffer.CopyDataBytes(offset, dataLength, handle.AddrOfPinnedObject());

                if (status == 0) // kCMBlockBufferNoErr
                {
                    pcm.Write(chunk, 0, chunk.Length);
                }
            }
            finally
            {
                handle.Free();
            }
        }

        if (reader.Status != AVAssetReaderStatus.Completed)
        {
            throw new InvalidDataException(reader.Error?.LocalizedDescription ?? $"Failed to decode the audio track ({reader.Status}).");
        }

        var pcmBytes = pcm.ToArray();

        if (pcmBytes.Length == 0)
        {
            throw new InvalidDataException($"The audio track did not contain any samples: {path}");
        }

        Log(LogType.None, $"decode: '{Path.GetFileName(path)}' -> {pcmBytes.Length / (RecordChannels * RecordBitsPerSample / 8)} frames at {RecordSampleRate} Hz");

        return WriteWavPcm16(pcmBytes, (int)RecordSampleRate);
    }

    /// <summary>
    /// Writes the canonical 44-byte WAV header in front of little-endian PCM16 bytes that already are
    /// in the recorder's output format, so no conversion is left to do.
    /// </summary>
    static byte[] WriteWavPcm16(byte[] pcm, int sampleRate)
    {
        var bytes = new byte[44 + pcm.Length];

        using var stream = new MemoryStream(bytes);
        using var writer = new BinaryWriter(stream);

        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + pcm.Length);
        writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray());
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)RecordChannels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * RecordChannels * RecordBitsPerSample / 8);
        writer.Write((short)(RecordChannels * RecordBitsPerSample / 8));
        writer.Write((short)RecordBitsPerSample);
        writer.Write("data"u8.ToArray());
        writer.Write(pcm.Length);
        writer.Write(pcm);

        writer.Flush();

        return bytes;
    }
}

internal class AppleDownloadService : IDownloadService
{
    DownloadColumns downloadColumns = null;

    SeasonTask downloadTask = null;

    static string DownloadDir
    {
        get
        {
#if MACCATALYST
            // Real user Downloads folder. Under App Sandbox, UserProfile resolves to the container's
            // Data dir whose "Downloads" entry is a symlink to the real ~/Downloads; writing through
            // it requires the com.apple.security.files.downloads.read-write entitlement.
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
#else
            // iOS has no user-visible Downloads folder; keep downloads inside the app container.
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Downloads");
#endif
        }
    }

    public string Download(string url)
    {
        var id = DateTime.Now.ToDateTimeMilliseconds();

        var fileName = Path.GetFileName(url);

        var file = Path.Combine(DownloadDir, fileName);

        downloadTask = new SeasonTask();

        downloadColumns = new DownloadColumns();

        var preTime = DateTime.Now;

        var time = 0d;

        long preAlready = 0;

        downloadTask.Task = r =>
        {
            time += (DateTime.Now - preTime).TotalSeconds;

            preTime = DateTime.Now;

            var percent = downloadTask.Messages[0].ToInt();

            var already = long.Parse(downloadTask.Messages[1]);

            var total = long.Parse(downloadTask.Messages[2]);

            downloadColumns.Id = id;
            downloadColumns.Title = "";
            downloadColumns.Desc = "";
            downloadColumns.MediaType = "";
            downloadColumns.LocalUri = file;
            downloadColumns.TotalSize = (long)total;
            downloadColumns.Already = already;

            if (time >= 0.1f)
            {
                time -= 0.1f;

                downloadColumns.Speed = (int)(downloadColumns.Already - preAlready) * 10;

                preAlready = downloadColumns.Already;
            }
            else
            {

            }

            downloadColumns.Progress = (int)percent;

            downloadColumns.Status = percent == 100f ? "Successful" : "Running";   //Paused   Pending  Running  Successful  Failed
        };

        Task.Run(async () =>
        {
            var bytes = await Season.Net.WebClient.HttpDownload("", url, downloadTask);

            File.WriteAllBytes(file, bytes);

            downloadTask = null;

            downloadColumns = null;
        });

        return id;
    }

    public void DownloadCancel(string requestId)
    {
        downloadTask.CancellationTokenSource.Cancel();

        downloadColumns = null;

        downloadTask = null;
    }

    public void DownloadDel(string directory, string name)
    {
        string file = "";

        if (directory.IsNullOrWhiteSpace())
        {
            file = Path.Combine(DownloadDir, name);
        }
        else
        {
            file = Path.Combine(DownloadDir, directory, name);
        }

        if (System.IO.File.Exists(file))
        {
            System.IO.File.Delete(file);
        }
    }

    public void DownloadNew(string directory, string name)
    {
        DownloadDel(directory, name);

        if (directory.IsNullOrWhiteSpace())
        {

        }
        else
        {
            directory = Path.Combine(DownloadDir, directory);

            if (Directory.Exists(directory))
            {

            }
            else
            {
                Directory.CreateDirectory(directory);
            }
        }
    }

    public DownloadColumns DownloadQuery(string requestId, float time)
    {
        if (downloadColumns == null || downloadColumns.Id == requestId)
        {
            return downloadColumns;
        }
        else
        {
            return null;
        }
    }

    public void DownloadSave(string directory, string name, byte[] bytes, bool openFolder)
    {
        if (directory.IsNullOrWhiteSpace())
        {
            directory = DownloadDir;
        }
        else
        {
            directory = Path.Combine(DownloadDir, directory);
        }

        var file = Path.Combine(directory, name);

        try
        {
            if (Directory.Exists(directory))
            {

            }
            else
            {
                Directory.CreateDirectory(directory);
            }

            using (var fs = System.IO.File.Open(file, FileMode.Append))
            {
                fs.Write(bytes);

                fs.Close();
            }
        }
        catch (Exception ex)
        {
            // Never let an IO failure here tear down the app: downloads run inside the
            // render/update loop. Under MacCatalyst App Sandbox the usual cause is the real
            // ~/Downloads folder being blocked; com.apple.security.files.downloads.read-write
            // must be declared in Platforms/MacCatalyst/Entitlements.plist.
            DeviceServices.BaseApp?.AddLog(LogType.Error, $"{DateTime.UtcNow} [DownloadSave] file={file} failed err={ex}");

            return;
        }

        if (openFolder)
        {
            // Reveal the download folder in Finder (matches the Windows implementation).
            DeviceServices.File.OpenFolder(directory);
        }
    }

    public void DownloadUpdate(string directory, string name, string namenew)
    {
        string fileName = "";

        string fileNameNew = "";

        if (directory.IsNullOrWhiteSpace())
        {
            fileName = Path.Combine(DownloadDir, name);

            fileNameNew = Path.Combine(DownloadDir, namenew);
        }
        else
        {
            fileName = Path.Combine(DownloadDir, directory, name);

            fileNameNew = Path.Combine(DownloadDir, directory, namenew);
        }

        File.Move(fileName, fileNameNew, true);
    }
}
