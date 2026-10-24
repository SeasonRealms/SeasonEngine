// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Android;
using Android.OS;
using Android.App;
using Android.Text;
using Android.Runtime;
using Android.Content;
using Android.Content.PM;
using Android.Content.Res;
using Android.Database;
using Android.Media;
using Android.Provider;
using Android.Webkit;
using Android.Widget;

using Activity = Android.App.Activity;
using Application = Android.App.Application;
using AudioSource = Android.Media.AudioSource;
using Encoding = Android.Media.Encoding;
using Environment = Android.OS.Environment;
using Path = System.IO.Path;
using Uri = Android.Net.Uri;
using Orientation0 = Android.Content.Res.Orientation;
using Build = Android.OS.Build;
using WindowInsets = Android.Views.WindowInsets;
using WindowManagerFlags = Android.Views.WindowManagerFlags;

using Season.Platforms.Shared.LinuxAndroid;

namespace Season.Platforms.Android;

public enum ActivityResult
{
    SaveFile = 10001,
    FilePicker = 10002,
    TakePhoto = 10003
}

internal class AndroidDeviceCore : IDeviceCore
{
    /// <summary>
    /// Request code carried by the runtime-permission prompts issued in
    /// <see cref="RequestPermissionAsync"/>. BaseActivity forwards the dialog result back through
    /// <see cref="OnRequestPermissionsResult"/>, matched against this code.
    /// </summary>
    internal const int PermissionsRequestCode = 1;

    internal TaskCompletionSource<bool> tcsPermissions = null;

    public Season.Basic.Platform Platform { get; set; } = Season.Basic.Platform.Android;

    public Basic.Channel Channel { get; set; } = Basic.Channel.Google;

    public Season.Basic.Orientation Orientation
    {
        get
        {
            var orientation = Season.Basic.Orientation.Unknown;

            var ori = AndroidApp.MainActivity.ApplicationContext.Resources.Configuration.Orientation;

            if (ori is Orientation0.Portrait)
            {
                orientation = Season.Basic.Orientation.Portrait;
            }
            else
            {
                orientation = Season.Basic.Orientation.LandscapeLeft;
            }

            return orientation;
        }
        set
        {
            if (value == Basic.Orientation.Portrait)
            {
                AndroidApp.MainActivity.RequestedOrientation = ScreenOrientation.Portrait;
            }
            else
            {
                AndroidApp.MainActivity.RequestedOrientation = ScreenOrientation.Landscape;
            }
        }
    }

    /// <summary>
    /// Density factor in physical pixels per dp. Read from the current configuration so a
    /// system display-size (density) change is picked up by the next resolution re-apply.
    /// </summary>
    public float DensityScale
    {
        get
        {
            var density = AndroidApp.MainActivity?.ApplicationContext?.Resources?.DisplayMetrics?.Density ?? 1f;

            if (!float.IsFinite(density) || density <= 0f)
            {
                return 1f;
            }

            return density;
        }
    }

    /// <summary>
    /// Safe-area insets (left, top, right, bottom) in dp, read from the decor view so content
    /// stays clear of cutouts and system bars. The raw insets are physical pixels and are
    /// divided by the density factor to match the logical-unit convention. Insets are zero
    /// until the decor view has been attached to a window.
    /// </summary>
    public Vector4 SafeInsets
    {
        get
        {
            try
            {
                var view = AndroidApp.MainActivity?.Window?.DecorView;

                var windowInsets = view?.RootWindowInsets;

                if (windowInsets is null)
                {
                    return Vector4.Zero;
                }

                var density = DensityScale;

                if (OperatingSystem.IsAndroidVersionAtLeast(30))
                {
                    var insets = windowInsets.GetInsets(WindowInsets.Type.SystemBars());

                    return new Vector4(insets.Left / density, insets.Top / density, insets.Right / density, insets.Bottom / density);
                }
                else
                {
                    return new Vector4(
                        windowInsets.SystemWindowInsetLeft / density,
                        windowInsets.SystemWindowInsetTop / density,
                        windowInsets.SystemWindowInsetRight / density,
                        windowInsets.SystemWindowInsetBottom / density);
                }
            }
            catch (Exception)
            {
                return Vector4.Zero;
            }
        }
    }

    /// <summary>
    /// Keeps the display awake while a model load or generation is running, or while a game
    /// pins the screen, by setting FLAG_KEEP_SCREEN_ON on the activity window. The flag needs
    /// no WAKE_LOCK permission and is scoped to the visible window: when the app leaves the
    /// foreground Android ignores it, so the process can still be suspended.
    /// </summary>
    public void SetKeepAwake(bool enabled)
    {
        var activity = AndroidApp.MainActivity;

        if (activity is null)
        {
            return;
        }

        void Apply()
        {
            try
            {
                var window = activity.Window;

                if (window is null)
                {
                    return;
                }

                if (enabled)
                {
                    window.AddFlags(WindowManagerFlags.KeepScreenOn);
                }
                else
                {
                    window.ClearFlags(WindowManagerFlags.KeepScreenOn);
                }
            }
            catch (Exception)
            {
                // Losing the keep-awake hint must never break the operation that asked for it.
            }
        }

        activity.RunOnUiThread(Apply);
    }

    public string GetLocalIP()
    {
        var ipAddress = "";

        var ipv4 = "";

        var ips = new List<string>();

        var networkInterfaces = Java.Net.NetworkInterface.NetworkInterfaces;

        while (networkInterfaces.HasMoreElements)
        {
            var networkInterface = networkInterfaces.NextElement() as Java.Net.NetworkInterface;

            var inetAddresses = networkInterface.InetAddresses;

            while (inetAddresses.HasMoreElements)
            {
                var inetAddress = inetAddresses.NextElement() as Java.Net.InetAddress;

                if (!inetAddress.IsLoopbackAddress && inetAddress is Java.Net.Inet6Address && !inetAddress.IsLinkLocalAddress)
                {
                    var ip = inetAddress.HostAddress.ToString();

                    ips.Add(ip);
                }

                if (!inetAddress.IsLoopbackAddress && inetAddress is Java.Net.Inet4Address && !inetAddress.IsLinkLocalAddress)
                {
                    ipv4 = inetAddress.HostAddress.ToString();
                }
            }
        }

        if (ips.Count > 0)
        {
            ipAddress = ips[0];
        }

        return ipAddress;

        //var wifiManager = (WifiManager)Activity.GetSystemService(Context.WifiService);

        //var wifiInfo = wifiManager.ConnectionInfo;

        //var ipAddress = wifiInfo.IpAddress;  //

        //ip = String.Format("{0}.{1}.{2}.{3}",
        //        (ipAddress & 0xff),
        //        (ipAddress >> 8 & 0xff),
        //        (ipAddress >> 16 & 0xff),
        //        (ipAddress >> 24 & 0xff));
        //return ip;
    }

    public string LoadFilePath(string res)
    {
        var path = res.Replace('\\', '/');

        // Real files win: user media picked through the document picker and files already
        // copied into the data directory resolve to themselves, matching the desktop
        // contract of returning the absolute path of an existing file.
        if (System.IO.File.Exists(path))
        {
            return path;
        }

        // Everything else is expected in the APK assets tree (MauiAsset packs Resources\Raw\**
        // keeping the relative path as the logical name, so "Musics/Warmfresh.wav" and
        // "Assets/Qwen3.5-0.8B-Q4_K_M.gguf" map to assets 1:1). llama.cpp, ONNX Runtime and
        // MediaPlayer cannot read AssetManager streams - they need a real file - so the asset
        // is materialized into CacheDir, the same degradation SetMediaSource falls back to for
        // compressed assets. An existing copy is reused, so a model is paid for only once.
        var asset = ResolveAssetPath(res);

        var cachePath = Path.Combine(AndroidApp.MainActivity.CacheDir.AbsolutePath, asset);

        if (System.IO.File.Exists(cachePath))
        {
            return cachePath;
        }

        try
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(cachePath));

            using (var source = AndroidApp.MainActivity.BaseContext.Assets.Open(asset))
            using (var target = System.IO.File.Create(cachePath))
            {
                source.CopyTo(target);
            }
        }
        catch (Java.IO.FileNotFoundException)
        {
            // Neither on disk nor in the bundle. Callers that can read packaged assets
            // themselves (LoadFile / stream consumers) treat null as "use the stream path".
            return null;
        }

        return cachePath;
    }

    public bool LoadFileExists(string res)
    {
        var list = AndroidApp.MainActivity.BaseContext.Assets.List("");

        return list.Contains(res);
    }

    public System.IO.Stream LoadFile(string res)
    {
        return AndroidApp.MainActivity.BaseContext.Assets.Open(ResolveAssetPath(res));

        //Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        //Environment.ProcessPath;
        //Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    }

    /// <summary>
    /// APK assets only accept relative paths; the caller on Windows/Linux typically constructs them from the assembly directory
    /// ```"...\Content\..." absolute path (e.g., font, texture, and audio declarations), with the backslash "\\" delimiter retained.```.
    /// ```Here, normalization is uniformly applied: backslashes are converted to '/', and the relative path of assets starting with "Content/" is extracted from the absolute path.```.
    /// </summary>
    internal static string ResolveAssetPath(string res)
    {
        var path = res.Replace('\\', '/');

        int index = path.IndexOf("/Content/", StringComparison.Ordinal);

        if (index >= 0)
        {
            return path.Substring(index + 1);
        }

        return path;
    }

    public bool IsDarkMode()
    {
        var uiModeFlags = Application.Context.Resources.Configuration.UiMode & UiMode.NightMask;

        return uiModeFlags == UiMode.NightYes;
    }

    public async Task<bool> RequestPermissionAsync(string[] permissions)
    {
        // Verify that all required permissions have been granted.
        if (permissions.All(permission => AndroidX.Core.Content.ContextCompat.CheckSelfPermission(AndroidApp.MainActivity, permission) == Permission.Granted))
        {
            return true;
        }

        // RunContinuationsAsynchronously keeps the awaiting caller (often the render thread)
        // off the UI thread's result callback, the same contract the file/camera bridges use.
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        tcsPermissions = tcs;

        // The permission dialog carries Activity affinity; RequestPermissionAsync is invoked
        // from the render thread, so the request is posted to the UI thread. Without this the
        // dialog never appears and the await below would never be resolved.
        AndroidApp.MainActivity.RunOnUiThread(() =>
        {
            AndroidX.Core.App.ActivityCompat.RequestPermissions(AndroidApp.MainActivity, permissions, PermissionsRequestCode);
        });

        return await tcs.Task;
    }

    /// <summary>
    /// Completion entry point for <see cref="RequestPermissionAsync"/>, called by
    /// BaseActivity.OnRequestPermissionsResult. Resolves the pending prompt with whether every
    /// requested permission was granted, or with false when the dialog is dismissed, so the
    /// awaiting caller does not hang.
    /// </summary>
    internal void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        if (requestCode != PermissionsRequestCode)
        {
            return;
        }

        var tcs = tcsPermissions;

        tcsPermissions = null;

        if (tcs is null)
        {
            return;
        }

        var granted = grantResults is { Length: > 0 }
            && grantResults.All(result => result == Permission.Granted);

        tcs.TrySetResult(granted);
    }

    public DeviceMemoryInfo GetMemoryInfo()
    {
        var info = new DeviceMemoryInfo
        {
            // Android SoCs share one physical pool between the CPU and GPU.
            IsUnifiedMemory = true
        };

        try
        {
            var configuration = Application.Context.Resources?.Configuration;

            if (configuration is not null)
            {
                // 600dp is Android's own tablet threshold (the sw600dp resource qualifier).
                info.FormFactor = configuration.SmallestScreenWidthDp >= 600
                    ? DeviceFormFactor.Tablet
                    : DeviceFormFactor.Phone;
            }
        }
        catch
        {
            // Leave the form factor Unknown when the configuration is unavailable.
        }

        try
        {
            if (AndroidApp.MainActivity?.GetSystemService(Context.ActivityService) is ActivityManager activityManager)
            {
                var memoryInfo = new ActivityManager.MemoryInfo();
                activityManager.GetMemoryInfo(memoryInfo);

                // TotalMem is physical RAM; AvailMem is deliberately ignored so the grade
                // does not drift with whatever else the device currently has running.
                if (memoryInfo.TotalMem > 0)
                {
                    info.TotalSystemMemoryMb = (int)(memoryInfo.TotalMem / (1024 * 1024));
                }
            }
        }
        catch
        {
            // Leave the system-memory figure at 0 when ActivityManager is unavailable.
        }

        return info;
    }
}

internal class AndroidMediaPlayer : IMediaPlayer
{
    public bool IsPlaying
    {
        get
        {
            if (MusicPlayer != null && MusicPlayer.IsPlaying || SoundPlayer != null && SoundPlayer.IsPlaying)
            {
                return true;
            }

            return false;
        }
    }

    MediaPlayer MusicPlayer = null;

    MediaPlayer SoundPlayer = null;

    // Raw source path last handed to each channel by PlayMedia, kept so IsPlayingFile
    // can tell which file a player is currently running.
    string CurrentMusicFile = null;

    string CurrentSoundFile = null;

    public bool IsPlayingFile(string fileName)
    {
        if (MusicPlayer != null && MusicPlayer.IsPlaying && MediaPlayerFiles.IsSame(CurrentMusicFile, fileName)
            || SoundPlayer != null && SoundPlayer.IsPlaying && MediaPlayerFiles.IsSame(CurrentSoundFile, fileName))
        {
            return true;
        }

        return false;
    }

    public void PlayMedia(string type, string id, string vol)
    {
        if (MusicPlayer == null || SoundPlayer == null)
        {
            MusicPlayer = new MediaPlayer();

            SoundPlayer = new MediaPlayer();
        }

        if (type is "Music")
        {
            CurrentMusicFile = id;
        }
        else
        {
            CurrentSoundFile = id;
        }

        var mediaPlayer = type is "Music" ? MusicPlayer : SoundPlayer;

        mediaPlayer.Reset();

        try
        {
            SetMediaSource(mediaPlayer, id);

            if (vol?.Length > 0)
            {
                var volume = float.Parse(vol) / 100;

                mediaPlayer.SetVolume(volume, volume);
            }

            mediaPlayer.Prepare();

            mediaPlayer.Start();
        }
        catch (Exception ex)
        {
            // SeasonBase.PlayEffect/PlaySong silently swallows audio exceptions, which was precisely the issue on the Android side previously
            // The reason for "silent failures and silent collapses"; logs are left here to distinguish missing assets and decoding failures.
            mediaPlayer.Reset();

            System.Diagnostics.Debug.WriteLine($"[Android] PlayMedia failed: type={type}, id={id}: {ex.GetType().Name}: {ex.Message}");

            DeviceServices.BaseApp?.AddLog(LogType.Error, $"{DateTime.UtcNow} [Android] PlayMedia failed: type={type}, id={id}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>

    ///Resolve the ID provided by the game layer. Use the desktop contract to spell it into an absolute path under AppLext.BaseDirectory
    ///(Like '.../Content/Sound \ Move. wav', where the back slash is a literal file name character), but for Android Content
    ///The tree is located within APK assets and not in the file system, and MediaPlayer. SetDataSource (string) only recognizes real files.
    ///Search order: First, open the normalized real file, then open it as a file descriptor using APK asset;
    ///Finally, for compressed assets that cannot be opened according to file descriptors, they will be degraded to copying to CacheDir before playback.
    /// </summary>
    static void SetMediaSource(MediaPlayer mediaPlayer, string id)
    {
        var path = id.Replace('\\', '/');

        if (System.IO.File.Exists(path))
        {
            mediaPlayer.SetDataSource(path);

            return;
        }

        var asset = AndroidDeviceCore.ResolveAssetPath(id);

        try
        {
            using (var fd = AndroidApp.MainActivity.BaseContext.Assets.OpenFd(asset))
            {
                mediaPlayer.SetDataSource(fd.FileDescriptor, fd.StartOffset, fd.Length);
            }

            return;
        }
        catch (Java.IO.IOException)
        {
        }

        var cachePath = Path.Combine(AndroidApp.MainActivity.CacheDir.AbsolutePath, asset);

        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(cachePath));

        using (var source = AndroidApp.MainActivity.BaseContext.Assets.Open(asset))
        using (var target = System.IO.File.Create(cachePath))
        {
            source.CopyTo(target);
        }

        mediaPlayer.SetDataSource(cachePath);
    }

    public void SetVolume(int music, int sound)
    {
        if (MusicPlayer == null || SoundPlayer == null)
        {

        }
        else
        {
            MusicPlayer.SetVolume((float)music / 100, (float)music / 100);

            SoundPlayer.SetVolume((float)sound / 100, (float)sound / 100);
        }
    }

    // True while Pause actually suspended a playing channel. Resume restarts exactly these, so a
    // channel that already completed cannot replay its last track or effect from the beginning
    // when the app returns to the foreground, and no Start runs in Idle state.
    bool _musicHeld;

    bool _soundHeld;

    public void Pause()
    {
        _musicHeld = PauseChannel(MusicPlayer, _musicHeld, "Music");

        _soundHeld = PauseChannel(SoundPlayer, _soundHeld, "Sound");
    }

    public void Resume()
    {
        _musicHeld = ResumeChannel(MusicPlayer, _musicHeld, "Music");

        _soundHeld = ResumeChannel(SoundPlayer, _soundHeld, "Sound");
    }

    /// <summary>
    /// Suspend one channel, but only while it is really playing: MediaPlayer throws
    /// IllegalStateException when Pause or Start runs in Idle or Error state, and restarting a
    /// completed player would replay it from the beginning. Returns the new held state.
    /// </summary>
    static bool PauseChannel(MediaPlayer mediaPlayer, bool held, string channel)
    {
        if (mediaPlayer == null) return held;

        try
        {
            if (mediaPlayer.IsPlaying)
            {
                mediaPlayer.Pause();

                return true;
            }
        }
        catch (Exception ex)
        {
            LogMediaFailure($"Pause({channel})", ex);
        }

        return held;
    }

    /// <summary>Continue a channel that was suspended earlier; see <see cref="PauseChannel"/>. Returns the new held state.</summary>
    static bool ResumeChannel(MediaPlayer mediaPlayer, bool held, string channel)
    {
        if (mediaPlayer == null || !held) return false;

        try
        {
            mediaPlayer.Start();
        }
        catch (Exception ex)
        {
            LogMediaFailure($"Resume({channel})", ex);
        }

        return false;
    }

    static void LogMediaFailure(string operation, Exception ex)
    {
        System.Diagnostics.Debug.WriteLine($"[Android] Media {operation} failed: {ex.GetType().Name}: {ex.Message}");

        DeviceServices.BaseApp?.AddLog(LogType.Error, $"{DateTime.UtcNow} [Android] Media {operation} failed: {ex.GetType().Name}: {ex.Message}");
    }
}

internal class AndroidDialogService : IDialogService
{
    public async Task<string> ShowMessage(string title, string desc, string[] buttons, string text)
    {
        string result = null;

        var tcs = new TaskCompletionSource<string>();

        AndroidApp.MainActivity.RunOnUiThread(() =>
        {
            var builder = new AlertDialog.Builder(AndroidApp.MainActivity);

            builder.SetTitle(title);

            var input = new TextView(AndroidApp.MainActivity) { Text = text };

            input.SetPadding(30, 30, 30, 30);

            builder.SetPositiveButton(buttons[0], (dialog, whichButton) =>
            {
                result = input.Text;

                tcs.TrySetResult(result);
            });

            builder.NothingSelected += (s, e) =>
            {
                result = null;

                tcs.TrySetResult(result);
            };

            var dialog = builder.Create();

            dialog.SetView(input);

            dialog.Show();
        });

        return await tcs.Task;
    }

    public async Task<string> ShowKeyboard(string title, string desc, string[] buttons, string text)
    {
        string result = null;

        bool usePasswordMode = false;

        if (buttons is null || buttons.Length <= 1)
        {
            buttons = new string[] { "OK", "Cancel" };
        }

        var tcs = new TaskCompletionSource<string>();

        AndroidApp.MainActivity.RunOnUiThread(() =>
        {
            var builder = new AlertDialog.Builder(AndroidApp.MainActivity);

            builder.SetTitle(title);

            //builder.SetMessage(desc);

            //var scrollView = new ScrollView(builder.Context);

            var input = new EditText(AndroidApp.MainActivity) { Text = text };

            if (text != null)
            {
                input.SetSelection(text.Length);
            }
            if (usePasswordMode)
            {
                input.InputType = InputTypes.ClassText | InputTypes.TextVariationPassword;
            }

            //input.LayoutParameters = new ViewGroup.LayoutParams(300, 500);

            //input.LayoutParameters.Height = 500;

            //var scrollView = new ScrollView(MainActivity.BaseContext);

            //scrollView.AddView(input);

            //var layoutParams = scrollView.LayoutParameters;

            //alert.SetView(input);

            builder.SetPositiveButton(buttons[0], (dialog, whichButton) =>
            {
                result = input.Text;

                tcs.TrySetResult(result);
            });

            builder.SetNegativeButton(buttons[1], (dialog, whichButton) =>
            {
                result = null;

                tcs.TrySetResult(result);
            });

            builder.NothingSelected += (s, e) =>
            {
                result = null;

                tcs.TrySetResult(result);
            };

            //alert.SetCancelable(true);

            //builder.SetView(scrollView);

            //builder.Show();

            var dialog = builder.Create();

            dialog.SetView(input);

            dialog.Show();

            //var dialog = alert.Show();

            //dialog.Window.Attributes.Height = 400;
        });

        return await tcs.Task;
    }
}

internal class AndroidFileService : IFileService
{
    static class FileMimeTypes
    {
        internal const string All = "*/*";
        internal const string ImageAll = "image/*";
        internal const string ImagePng = "image/png";
        internal const string ImageJpg = "image/jpeg";
        internal const string VideoAll = "video/*";
        internal const string EmailMessage = "message/rfc822";
        internal const string Pdf = "application/pdf";
        internal const string TextPlain = "text/plain";
        internal const string OctetStream = "application/octet-stream";
    }

    public TaskCompletionSource<Uri[]> tcsPickFiles = null;

    public async Task<string> PickFolder()
    {
        return null;
    }

    public async Task<List<TaskFile>> PickFiles(FileType fileType, string[] exts, bool multiple, bool open)
    {
        var taskFiles = new List<TaskFile>();

        var action = Intent.ActionOpenDocument;

        var intent = new Intent(action);

        //Limit MIMEs by file type: Image → image/*, while keeping them universally compatible across all classes; Exts filtering is performed by the caller
        //Perform self verification based on the returned Ext/Name (consistent contract for PickFiles across platforms).
        intent.SetType(fileType switch
        {
            FileType.Image => FileMimeTypes.ImageAll,
            FileType.Video => FileMimeTypes.VideoAll,
            _ => FileMimeTypes.All
        });

        intent.PutExtra(Intent.ExtraAllowMultiple, multiple);

        var pickerIntent = Intent.CreateChooser(intent, "Select file");

        try
        {
            var tcs = new TaskCompletionSource<Uri[]>(TaskCreationOptions.RunContinuationsAsynchronously);

            tcsPickFiles = tcs;

            //StartActiveForResult carries Activity affinity: It must be delivered when called by the rendering thread (backend)
            //To the UI thread; Startup failed (device without file selector), end session with empty array to prevent exceptions from escaping into UI
            //Thread message loop.
            var activity = AndroidApp.MainActivity;

            activity.RunOnUiThread(() =>
            {
                try
                {
                    activity.StartActivityForResult(pickerIntent, (int)ActivityResult.FilePicker);
                }
                catch (Exception ex)
                {
                    DeviceServices.BaseApp?.AddLog(LogType.Error, $"{DateTime.UtcNow} [File] PickFiles launch failed: {ex}");

                    tcsPickFiles = null;

                    tcs.TrySetResult(Array.Empty<Uri>());
                }
            });

            var uris = await tcs.Task;

            if (uris is null || uris.Length == 0)
            {
                return taskFiles;
            }

            foreach (var uri in uris)
            {
                if (uri is null)
                {
                    continue;
                }

                //The column set of SAF/DocumentProvider does not guarantee to contain the mime_type of MediaStore; Missing columns
                //Return -1 for VNet ColumnIndex or ContentResolver.GetType when the query fails.
                //D_splay_name is the SAF standard column, retrieved in one query with mime_type.
                string mimeType = null;
                string displayName = null;

                using (var cursor = AndroidCommon.DownloadQueryCursor(uri, null, null))
                {
                    if (cursor is not null && cursor.MoveToNext())
                    {
                        var index = cursor.GetColumnIndex(MediaStore.Files.IFileColumns.MimeType);

                        if (index >= 0)
                        {
                            mimeType = cursor.GetString(index);
                        }

                        var nameIndex = cursor.GetColumnIndex(IOpenableColumns.DisplayName);

                        if (nameIndex >= 0)
                        {
                            displayName = cursor.GetString(nameIndex);
                        }
                    }
                }

                if (string.IsNullOrEmpty(mimeType))
                {
                    mimeType = AndroidApp.MainActivity.ContentResolver?.GetType(uri);
                }

                if (displayName.IsNullOrWhiteSpace())
                {
                    displayName = uri.LastPathSegment;
                }

                if (displayName.IsNullOrWhiteSpace())
                {
                    displayName = "picked_file";
                }

                var fileName = Path.GetFileName(displayName);

                if (fileName.IsNullOrWhiteSpace())
                {
                    fileName = "picked_file";
                }

                //SAF content://uri is not a file system path: use it as a path (File.Cpy)/
                //File. OpenRead) will be normalized to "/content:/..." by Path. GetFullPath and reported
                //Could not find a part of the path. Align Apple UIDocumentPicker
                //The semantics of Mode. Import - content first falls into the application cache and then returns to the local path - PickFiles on each end
                //Unified 'Name=local readable path' (Windows/Linux native path, Apple sandbox import path
                //Android cache import path), downstream CopyToLocal/File.Cpy/extension verification is not required
                //Perception platform. Cache is reclaimed by the system during low storage.
                var directory = Path.Combine(AndroidApp.MainActivity.CacheDir.AbsolutePath, "PickFiles");

                System.IO.Directory.CreateDirectory(directory);

                var localPath = Path.Combine(directory, fileName);

                try
                {
                    //Copy and pay off program pool: PickFiles are awaiting by the rendering thread, with hundreds of MB of models/videos
                    //Frame loops should not be blocked in the synchronization segment.
                    await Task.Run(() =>
                    {
                        using var source = AndroidApp.MainActivity.ContentResolver.OpenInputStream(uri);
                        using var destination = System.IO.File.Create(localPath);

                        source.CopyTo(destination);
                    });
                }
                catch (Exception ex)
                {
                    //If a single file fails to read, only skip that item (if multiple selections are made, the rest will remain the same), and prevent exceptions from escaping
                    //Rendering loop.
                    DeviceServices.BaseApp?.AddLog(LogType.Error, $"{DateTime.UtcNow} [File] PickFiles copy failed for {uri}: {ex}");

                    continue;
                }

                var ext = Path.GetExtension(fileName).ToLower();

                if (string.IsNullOrEmpty(ext) && !string.IsNullOrEmpty(mimeType))
                {
                    // Minimum guarantee for display name without extension: take mime subtype (image/jpeg → ". jpeg",
                    // application/octet-stream → ".octet-stream"）。
                    var slash = mimeType.LastIndexOf('/');

                    ext = "." + (slash >= 0 ? mimeType[(slash + 1)..] : mimeType);
                }

                System.IO.Stream stream = null;

                if (open)
                {
                    stream = System.IO.File.OpenRead(localPath);
                }
                else
                {

                }

                var taskFile = new TaskFile()
                {
                    Name = localPath,
                    Ext = ext,
                    Text = "",
                    Stream = stream
                    //Bytes = bytes
                };
                taskFiles.Add(taskFile);
            }
        }
        catch (Exception ex)
        {
            DeviceServices.BaseApp?.AddLog(LogType.Error, $"{DateTime.UtcNow} [File] PickFiles failed: {ex}");

            return null;
        }

        return taskFiles;
    }

    public Task<List<TaskFile>> PickPhotos(bool open)
    {
        // No native photo picker on this platform: report an empty selection, which callers
        // handle exactly like a cancelled picker.
        return Task.FromResult(new List<TaskFile>());
    }

    /// <summary>
    /// Completion entry point for <see cref="PickFiles"/>, called by MainActivity.OnActivityResult
    /// for <see cref="ActivityResult.FilePicker"/>. Resolves the picked uris (the single result or
    /// the ClipData items of a multi-select), or an empty array when the user cancels, so the
    /// awaiting PickFiles returns an empty list instead of hanging.
    /// </summary>
    internal void OnFilePickerResult(global::Android.App.Result resultCode, Intent? data)
    {
        var tcs = tcsPickFiles;

        tcsPickFiles = null;

        if (tcs is null)
        {
            return;
        }

        var uris = Array.Empty<Uri>();

        if (resultCode is global::Android.App.Result.Ok && data is not null)
        {
            var clipData = data.ClipData;

            if (clipData is not null && clipData.ItemCount > 0)
            {
                var items = new List<Uri>(clipData.ItemCount);

                for (var i = 0; i < clipData.ItemCount; i++)
                {
                    var itemUri = clipData.GetItemAt(i)?.Uri;

                    if (itemUri is not null)
                    {
                        items.Add(itemUri);
                    }
                }

                uris = items.ToArray();
            }
            else if (data.Data is not null)
            {
                uris = new Uri[] { data.Data };
            }
        }

        tcs.TrySetResult(uris);
    }

    public TaskCompletionSource<Uri> tcsSaveFile = null;

    public async Task<string> SaveFile(string fileName, System.IO.Stream stream, CancellationToken cancellationToken)
    {
        fileName = Path.GetFileName(fileName);

        var result = "";

        // No storage permission is needed here: the user's pick through the SAF create-document
        // panel grants a per-uri write authorization for the returned document, independent of
        // WRITE_EXTERNAL_STORAGE (which requests on modern targets are auto-denied anyway).

        //if (Android.OS.Environment.ExternalStorageDirectory is not null)
        //{
        //    initialPath = initialPath.Replace(Android.OS.Environment.ExternalStorageDirectory.AbsolutePath, string.Empty, StringComparison.InvariantCulture);
        //}

        var initialFolderUri = MediaStore.Downloads.ExternalContentUri; // AndroidUri.Parse(AndroidStorageConstants.ExternalStorageBaseUrl + HttpUtility.UrlEncode(initialPath));

        var intent = new Intent(Intent.ActionCreateDocument);

        intent.AddCategory(Intent.CategoryOpenable);

        intent.SetType(MimeTypeMap.Singleton?.GetMimeTypeFromExtension(MimeTypeMap.GetFileExtensionFromUrl(fileName)) ?? "*/*");

        intent.PutExtra(Intent.ExtraTitle, fileName);

        intent.PutExtra(DocumentsContract.ExtraInitialUri, initialFolderUri);

        var tcs = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);

        tcsSaveFile = tcs;

        var activity = AndroidApp.MainActivity;

        activity.RunOnUiThread(() =>
        {
            try
            {
                activity.StartActivityForResult(intent, (int)ActivityResult.SaveFile);
            }
            catch (Exception ex)
            {
                DeviceServices.BaseApp?.AddLog(LogType.Error, $"{DateTime.UtcNow} [File] SaveFile launch failed: {ex}");

                tcsSaveFile = null;

                tcs.TrySetResult(null);
            }
        });

        var uri = await tcs.Task;

        if (uri is null)
        {
            return "";
        }

        // Both wrappers own an OS file descriptor and must be released with the method - relying
        // on finalizers would keep the descriptors alive until a GC cycle, and repeated saves
        // would drain the per-process fd table.
        using var parcelFileDescriptor = Application.Context.ContentResolver?.OpenFileDescriptor(uri, "wt");

        using var fileOutputStream = new Java.IO.FileOutputStream(parcelFileDescriptor?.FileDescriptor);

        var length = (int)(stream.Length < 4096 ? stream.Length : 4096);

        var array = new byte[length];

        int bytesRead = 0;

        while ((bytesRead = stream.Read(array, 0, length)) > 0)
        {
            fileOutputStream.Write(array, 0, bytesRead);
        }

        result = uri.ToString() ?? throw new Exception($"Unable to resolve the file path'{uri}'.");

        return result;
    }

    /// <summary>
    /// Completion entry point for <see cref="SaveFile"/>, called by MainActivity.OnActivityResult
    /// for <see cref="ActivityResult.SaveFile"/>. Resolves the destination uri, or null when the
    /// user cancels.
    /// </summary>
    internal void OnSaveFileResult(global::Android.App.Result resultCode, Intent? data)
    {
        var tcs = tcsSaveFile;

        tcsSaveFile = null;

        if (tcs is null)
        {
            return;
        }

        tcs.TrySetResult(resultCode is global::Android.App.Result.Ok ? data?.Data : null);
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

            OpenMediaGalleryFile(result);

            //var stream = Activity.ContentResolver.OpenOutputStream(downloadUri, "wa");
        }

        return result;
    }

    async Task<string> SaveStorageFile(string name)
    {
        string result = null;

        DeviceServices.Download.DownloadNew(null, name);

        if (StorageService.TryGetStream(StorageService.DirectoryBase, name, out System.IO.Stream stream, out string errMsg))
        {
            using (stream)
            {
                using (var streamTarget = AndroidApp.MainActivity.ContentResolver.OpenOutputStream(AndroidDownloadService.downloadUri, "wa"))
                {
                    //using (var writer = new StreamWriter(streamTarget))
                    //{
                    var length = (int)(stream.Length < 4096 ? stream.Length : 4096);

                    var array = new byte[length];

                    int bytesRead = 0;

                    while ((bytesRead = stream.Read(array, 0, length)) > 0)
                    {
                        streamTarget.Write(array, 0, bytesRead);
                    }
                    //}

                    result = AndroidDownloadService.downloadUri.ToString();
                }
            }
        }

        return result;
    }

    async Task<string> SaveBytesFile(string name, byte[] bytes)
    {
        DeviceServices.Download.DownloadSave(null, name, bytes, false);

        //var downloads = Android.OS.Environment.GetExternalStoragePublicDirectory(Android.OS.Environment.DirectoryDownloads);
        //var file = Path.Combine(downloads.AbsolutePath, seasonTask.ID);
        //uri = FileProvider.GetUriForFile(Activity.ApplicationContext, Activity.ApplicationContext.PackageName, downloads);

        return AndroidDownloadService.downloadUri.ToString();
    }

    async Task<bool> OpenMediaGalleryFile(string name)
    {
        //var file = new Java.IO.File(name);
        //if (file == null || !file.Exists())
        //{
        //    //return false;
        //}

        var uri = Uri.Parse(name);

        //var uri = Uri.FromFile(file);

        var intent = new Intent(Intent.ActionView, uri); //ActionGetContent

        //intent.AddCategory(Intent.CategoryDefault);

        intent.AddFlags(ActivityFlags.GrantReadUriPermission);

        intent.AddFlags(ActivityFlags.GrantWriteUriPermission);

        //intent.SetDataAndType(uri, "file/*");  //"image/*"   "application/pdf"   "text/plain"

        var targetIntent = Intent.CreateChooser(intent, "Open File...");

        targetIntent.SetFlags(ActivityFlags.NewTask);

        AndroidApp.MainActivity.StartActivity(targetIntent);

        return true;

        //        Intent intent = new Intent(Intent.ACTION_VIEW);
        //        Uri uri = Uri.fromFile(file);
        //        intent.addCategory(Intent.CATEGORY_DEFAULT);

        //intent.addFlags(Intent.FLAG_ACTIVITY_CLEAR_TOP);
        //        intent.putExtra("oneshot", 0);
        //        intent.putExtra("configchange", 0);
        //        intent.setDataAndType(uri, "audio/*");

        //intent.addFlags(Intent.FLAG_ACTIVITY_CLEAR_TOP);
        //        intent.putExtra("oneshot", 0);
        //        intent.putExtra("configchange", 0);
        //        intent.setDataAndType(uri, "video/*");

        //intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
        //        intent.setDataAndType(uri, "application/x-chm");

        //intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
        //        intent.setDataAndType(uri, "application/vnd.android.package-archive");

        //intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
        //        intent.setDataAndType(uri, "application/vnd.ms-powerpoint");

        //intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
        //        intent.setDataAndType(uri, "application/vnd.ms-excel");

        //intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
        //        intent.setDataAndType(uri, "application/msword");
    }

    public async Task<bool> OpenLink(string name)
    {
        var uri = Uri.Parse(name);

        var intent = new Intent(Intent.ActionView, uri);

        AndroidApp.MainActivity.StartActivity(intent);

        return true;
    }

    public void OpenFolder(string name)
    {

    }
}

internal class AndroidGalleryService : IGalleryService
{
    async Task<List<MediaAsset>> MediaGalleryBase(Uri[] uris)
    {
        var assets = new List<MediaAsset>();

        string[] permissions = null;

        if ((int)Build.VERSION.SdkInt < 33)
        {
            permissions = new string[]
            {
                Manifest.Permission.ReadExternalStorage,
                Manifest.Permission.WriteExternalStorage
                //Manifest.Permission.ManageExternalStorage
            };
        }
        else
        {
            permissions = new string[]
            {
                Manifest.Permission.ReadMediaImages,
                Manifest.Permission.ReadMediaAudio,
                Manifest.Permission.ReadMediaVideo
            };
        }

        var hasPermission = await DeviceServices.Core.RequestPermissionAsync(permissions);

        if (hasPermission)
        {
            var ctx = Application.Context;

            //var uri = MediaStore.Images.Media.GetContentUri(MediaStore.VolumeExternal);
            //MediaStore.Files.GetContentUri("external");

            foreach (var uri in uris)
            {
                var cursor = ctx.ContentResolver.Query(uri, null, null, null);   //ctx.ApplicationContext

                while (cursor.MoveToNext())
                {
                    var id = cursor.GetInt(cursor.GetColumnIndex(MediaStore.Files.FileColumns.Id));

                    //var mediaType = cursor.GetInt(cursor.GetColumnIndex(MediaStore.Files.FileColumns.MediaType));

                    var displayName = cursor.GetString(cursor.GetColumnIndex(MediaStore.Files.FileColumns.DisplayName));

                    var size = cursor.GetLong(cursor.GetColumnIndex(MediaStore.Files.FileColumns.Size));

                    var path = cursor.GetString(cursor.GetColumnIndex(MediaStore.Files.FileColumns.Data));

                    var time = cursor.GetLong(cursor.GetColumnIndex(MediaStore.Files.FileColumns.DateAdded)) * 1000;

                    var date0 = new Java.Util.Date(time);

                    DateTime date = DateTime.Now;

                    date = new DateTime(date0.Year + 1900, date0.Month + 1, date0.Day + 1, date0.Hours, date0.Minutes, date0.Seconds);

                    var pathUri = ContentUris.WithAppendedId(uri, id);

                    var asset = new MediaAsset()
                    {
                        Id = $"{id}",
                        Name = Path.GetFileNameWithoutExtension(displayName),
                        Ext = Path.GetExtension(displayName),
                        Category = date.ToMonthDate(),
                        Path = pathUri.ToString(),
                        PreviewPath = path,
                        Url = uri.ToString(),
                        Type = MediaAssetType.Unknown,
                        Size = size,
                        Time = date.ToDateTimeSeconds(),
                        Object = null
                    };

                    assets.Add(asset);
                }
            }
        }

        return assets;
    }

    public async Task<List<MediaAsset>> MediaGallery()
    {
        var uris = new Uri[] { MediaStore.Images.Media.ExternalContentUri, MediaStore.Audio.Media.ExternalContentUri, MediaStore.Video.Media.ExternalContentUri, MediaStore.Downloads.ExternalContentUri };

        return await MediaGalleryBase(uris);
    }

    public async Task<List<MediaAsset>> MediaGalleryDownloads()
    {
        var uris = new Uri[] { MediaStore.Downloads.ExternalContentUri };

        var mediaAssets = await MediaGalleryBase(uris);

        return mediaAssets;
    }

    public async Task<System.IO.Stream> MediaAsset(MediaAsset mediaAsset)
    {
        byte[] bytes = null;

        var uri = Uri.Parse(mediaAsset.Path);

        return AndroidApp.MainActivity.ContentResolver.OpenInputStream(uri);

        //using (var stream = Activity.ContentResolver.OpenInputStream(uri))
        //{
        //    bytes = stream.StreamToBytes();
        //}
    }

    public async Task<bool> MediaRemove(MediaAsset[] mediaAssets, bool delEmptyDirectory)
    {
        if (mediaAssets == null || mediaAssets.Length == 0)
        {

        }
        else
        {
            var fileExts = new string[] { ".apk", ".lrc" };

            var uris = new List<Uri>();

            foreach (var mediaAsset in mediaAssets)
            {
                var file0 = new Java.IO.File(mediaAsset.PreviewPath);

                if (file0.Exists())
                {
                    var result = file0.Delete();
                }
                else
                {

                }

                if (fileExts.Any(ext => mediaAsset.PreviewPath.EndsWith(ext)) || 0 < mediaAsset.Size && mediaAsset.Size < 5000)
                {

                }
                else
                {
                    uris.Add(Uri.Parse(mediaAsset.Path));
                }
            }

            if (uris.Count > 0)
            {
                var request = MediaStore.CreateDeleteRequest(AndroidApp.MainActivity.ContentResolver, uris);

                AndroidApp.MainActivity.StartIntentSenderForResult(request.IntentSender, 0, null, 0, 0, 0);
            }
        }

        return true;
    }
}

//Record (Record Permission)
internal class AndroidRecordService : RecordService, IRecordService
{
    int SAMPLE_RATE = 16000; //44100;

    bool isRecording = false;

    AudioRecord audioRecord = null;

    MemoryStream memoryStream = null;

    // TakePhoto session state: the MediaStore row the camera app writes into, and the
    // completion source MainActivity.OnActivityResult resolves through OnTakePhotoResult.
    Uri? takePhotoUri = null;

    TaskCompletionSource<Uri?>? tcsTakePhoto = null;

    public async Task<bool> StartRecord()
    {
        LastFailure = RecordFailure.None;

        bool success = false;

        var permissions = new string[]
        {
            Manifest.Permission.RecordAudio
        };

        var hasPermission = await DeviceServices.Core.RequestPermissionAsync(permissions);

        if (hasPermission)
        {
            isRecording = true;

            new Thread(() =>
            {
                var CHANNEL_CONFIG = ChannelIn.Mono; //AudioFormat.CHANNEL_IN_MONO;

                var AUDIO_FORMAT = Encoding.Pcm16bit; //AudioFormat.ENCODING_PCM_8BIT;

                var BUFFER_SIZE_RECORDING = AudioRecord.GetMinBufferSize(SAMPLE_RATE, CHANNEL_CONFIG, AUDIO_FORMAT);

                audioRecord = new AudioRecord(AudioSource.Mic, SAMPLE_RATE, ChannelIn.Mono, Encoding.Pcm16bit, BUFFER_SIZE_RECORDING);

                audioRecord.StartRecording();

                memoryStream = new MemoryStream();

                byte[] data = new byte[BUFFER_SIZE_RECORDING / 2];

                while (isRecording)
                {
                    int read = audioRecord.Read(data, 0, data.Length);

                    memoryStream.Write(data, 0, read);
                }
            }).Start();

            success = true;
        }
        else
        {
            LastFailure = RecordFailure.Permission;
        }

        return success;
    }

    public async Task<byte[]> StopRecord()
    {
        isRecording = false;

        audioRecord.Stop();

        memoryStream.Seek(0, SeekOrigin.Begin);

        var bitsPerSample = (audioRecord.AudioFormat == Encoding.Pcm16bit) ? 16 : 8;

        WriteWavHeader(memoryStream, audioRecord.ChannelCount, SAMPLE_RATE, bitsPerSample, (int)memoryStream.Length);  //byteCount

        var bytes = memoryStream.ToArray();

        memoryStream.Dispose();

        memoryStream = null;

        audioRecord.Release();

        audioRecord.Dispose();

        audioRecord = null;

        return bytes;
    }

    void WriteWavHeader(System.IO.Stream stream, int channelCount, int sampleRate, int bitsPerSample, int audioLength = -1)
    {
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8))
        {
            var blockAlign = (short)(channelCount * (bitsPerSample / 8));
            var averageBytesPerSecond = sampleRate * blockAlign;

            if (writer.BaseStream.CanSeek)
            {
                writer.Seek(0, SeekOrigin.Begin);
            }

            //chunk ID
            writer.Write(System.Text.Encoding.UTF8.GetBytes("RIFF"));

            if (audioLength > -1)
            {
                writer.Write((Int32)(audioLength - 8)); // Size of the overall file - 8 bytes, in bytes (32-bit integer). 
            }
            else
            {
                writer.Write(audioLength); // -1 (Unkown size)
            }

            //format
            writer.Write(System.Text.Encoding.UTF8.GetBytes("WAVE"));

            //subchunk 1 ID
            writer.Write(System.Text.Encoding.UTF8.GetBytes("fmt "));

            writer.Write(16); //subchunk 1 (fmt) size
            writer.Write((short)1); //PCM audio format

            writer.Write((short)channelCount);
            writer.Write(sampleRate);
            writer.Write(averageBytesPerSecond);
            writer.Write(blockAlign);
            writer.Write((short)bitsPerSample);

            //subchunk 2 ID
            writer.Write(System.Text.Encoding.UTF8.GetBytes("data"));

            //subchunk 2 (data) size
            writer.Write(audioLength - 44);
        }
    }

    public Task<INativeImageDecoder?> CaptureScreen()
    {
        // Capturing the external screen on Android requires the MediaProjection API
        // and a user authorization dialog every time.
        // That is not suitable for automatic screenshot scenarios, so return null.
        return Task.FromResult<INativeImageDecoder?>(null);
    }

    public Task<INativeImageDecoder?> CaptureApp()
    {
        // Inject the TaskCompletionSource, set the NeedCaptureApp flag,
        // and let the render thread detect it during AfterRender, read back from the GPU,
        // and complete it through TrySetResult.
        var tcs = new TaskCompletionSource<INativeImageDecoder?>();
        BaseApp.CaptureAppTcs = tcs;
        return tcs.Task;
    }

    public byte[] DecodeToWavPcm16(string path)
    {
        // MediaExtractor + MediaCodec would be the Android implementation; not wired up yet.
        throw new NotImplementedException($"DecodeToWavPcm16 is not implemented on Android: {path}");
    }

    /// <summary>
    /// Launches the system camera app and hands the captured photo back as bytes, or null when
    /// the user cancels, no camera app is installed, or the capture cannot be read back. The
    /// output goes through a pre-inserted MediaStore row: a file:// extra would trip StrictMode's
    /// FileUriExposedException on modern Android, while the row gives the camera app a writable
    /// uri (plus the granted write permission) without any FileProvider.
    /// </summary>
    public override async Task<TaskFile> TakePhoto()
    {
        LastFailure = RecordFailure.None;

        try
        {
            var activity = AndroidApp.MainActivity;

            var resolver = activity.ContentResolver;

            var contentUri = MediaStore.Images.Media.ExternalContentUri;

            if (resolver is null || contentUri is null)
            {
                return null;
            }

            var values = new ContentValues();

            values.Put(MediaStore.Images.Media.InterfaceConsts.DisplayName, $"TakePhoto-{DateTime.Now:yyyyMMddHHmmss}.jpg");

            values.Put(MediaStore.Images.Media.InterfaceConsts.MimeType, "image/jpeg");

            var outputUri = resolver.Insert(contentUri, values);

            if (outputUri is null)
            {
                return null;
            }

            takePhotoUri = outputUri;

            var intent = new Intent(MediaStore.ActionImageCapture);

            intent.PutExtra(MediaStore.ExtraOutput, outputUri);

            intent.AddFlags(ActivityFlags.GrantWriteUriPermission);

            // RunContinuationsAsynchronously 与 Windows/Apple 端的 UI 桥接 TCS 一致：
            // 结果经 UI 线程回填时续体改由线程池执行，渲染线程同步等待（GetResult）不会
            // 与 UI 线程互相阻塞。
            var tcs = new TaskCompletionSource<Uri?>(TaskCreationOptions.RunContinuationsAsynchronously);

            tcsTakePhoto = tcs;

            // StartActivityForResult carries Activity affinity; the render loop calls this
            // from a background thread, so the launch is posted to the UI thread. A launcher
            // failure (no camera app installed) resolves the session with null instead of
            // escaping into the UI thread's message loop and taking the app down.
            activity.RunOnUiThread(() =>
            {
                try
                {
                    activity.StartActivityForResult(intent, (int)ActivityResult.TakePhoto);
                }
                catch (Exception ex)
                {
                    DeviceServices.BaseApp?.AddLog(LogType.Error, $"{DateTime.UtcNow} [Record] TakePhoto launch failed: {ex}");

                    tcsTakePhoto = null;

                    tcs.TrySetResult(null);
                }
            });

            var uri = await tcs.Task;

            if (uri is null)
            {
                return null;
            }

            byte[] bytes;

            using (var stream = resolver.OpenInputStream(uri))
            using (var memory = new MemoryStream())
            {
                if (stream is null)
                {
                    return null;
                }

                stream.CopyTo(memory);
                bytes = memory.ToArray();
            }

            if (bytes.Length == 0)
            {
                return null;
            }

            var taskFile = new TaskFile()
            {
                Name = uri.ToString() ?? "",
                Ext = ".jpg",
                Text = "",
                Bytes = bytes
            };

            DeviceServices.BaseApp?.AddLog(LogType.None, $"{DateTime.UtcNow} [Record] TakePhoto captured {bytes.Length} bytes from '{uri}'");

            return taskFile;
        }
        catch (Exception ex)
        {
            DeviceServices.BaseApp?.AddLog(LogType.Error, $"{DateTime.UtcNow} [Record] TakePhoto failed: {ex}");

            return null;
        }
    }

    /// <summary>
    /// Completion entry point for <see cref="TakePhoto"/>, called by MainActivity.OnActivityResult
    /// for <see cref="ActivityResult.TakePhoto"/>. Resolves the session with the output uri on OK
    /// or with null on cancellation — deleting the placeholder row again, so a cancelled capture
    /// leaves no zero-byte image behind in the gallery.
    /// </summary>
    internal void OnTakePhotoResult(global::Android.App.Result resultCode, Intent? data)
    {
        var uri = data?.Data ?? takePhotoUri;

        takePhotoUri = null;

        var tcs = tcsTakePhoto;

        tcsTakePhoto = null;

        if (tcs is null)
        {
            return;
        }

        if (resultCode is not global::Android.App.Result.Ok)
        {
            if (uri is not null)
            {
                try { AndroidApp.MainActivity.ContentResolver.Delete(uri, null); }
                catch (Exception ex) { DeviceServices.BaseApp?.AddLog(LogType.None, $"{DateTime.UtcNow} [Record] TakePhoto placeholder cleanup failed: {ex.Message}"); }
            }

            tcs.TrySetResult(null);

            return;
        }

        tcs.TrySetResult(uri);
    }
}

internal class AndroidDownloadService : IDownloadService
{
    DownloadManager downloadManager = null;

    DownloadManager.Query downloadQuery = null;

    DownloadColumns downloadColumns = null;

    float downloadTime = 0f;

    internal static Uri downloadUri = null;

    public string Download(string url)
    {
        downloadManager = null;

        downloadQuery = null;

        downloadColumns = null;

        var fileName = Path.GetFileName(url);

        downloadManager = (DownloadManager)AndroidApp.MainActivity.GetSystemService(Context.DownloadService);

        var request = new DownloadManager.Request(Uri.Parse(url));

        request.SetAllowedNetworkTypes(DownloadNetwork.Wifi);

        request.SetTitle(fileName);

        request.SetVisibleInDownloadsUi(true);

        request.SetAllowedOverRoaming(false);

        request.SetNotificationVisibility(DownloadVisibility.VisibleNotifyCompleted);

        request.SetDestinationInExternalPublicDir(Environment.DirectoryDownloads, fileName);

        //request.SetDestinationUri(Android.Net.Uri.Parse($"file://{dir}/{fileName}"));

        request.SetNotificationVisibility(DownloadVisibility.VisibleNotifyCompleted);

        var requestId = downloadManager.Enqueue(request);

        return requestId.ToString();
    }

    public void DownloadCancel(string requestId)
    {
        downloadManager.Remove(new long[] { (int)requestId.ToInt() });

        downloadManager = null;

        downloadQuery = null;

        downloadColumns = null;

        downloadTime = 0f;
    }

    public void DownloadDel(string directory, string name)
    {
        var uri = MediaStore.Files.GetContentUri("external");

        var cursor = AndroidCommon.DownloadQueryCursor(uri, directory, name);

        while (cursor.MoveToNext())
        {
            var id = cursor.GetInt(cursor.GetColumnIndex(MediaStore.Files.FileColumns.Id));

            var pathUri = ContentUris.WithAppendedId(uri, id);

            try
            {
                AndroidApp.MainActivity.ContentResolver.Delete(pathUri, null);
            }
            catch (Exception ex)
            {

            }
        }
    }

    public void DownloadNew(string directory, string name)
    {
        var values = new ContentValues();

        values.Put(MediaStore.Downloads.InterfaceConsts.DisplayName, name);

        //values.put(MediaStore.Downloads.DESCRIPTION, fileName);
        //values.put(MediaStore.Downloads.MIME_TYPE, "application/vnd.android.package-archive");
        //values.put(MediaStore.Downloads.RELATIVE_PATH, "Download" + File.separator + "apk");

        var relativePath = Environment.DirectoryDownloads;

        if (directory.IsNullOrWhiteSpace())
        {

        }
        else
        {
            relativePath = Path.Combine(relativePath, directory);
        }

        values.Put(MediaStore.Downloads.InterfaceConsts.RelativePath, relativePath);

        var uri = MediaStore.Files.GetContentUri("external");
        //Uri external = MediaStore.Downloads.EXTERNAL_CONTENT_URI;

        downloadUri = AndroidApp.MainActivity.ContentResolver.Insert(uri, values);
    }

    public DownloadColumns DownloadQuery(string requestId, float time)
    {
        downloadTime += time;

        if (downloadTime >= 1f)
        {
            downloadTime -= 1f;

            if (downloadQuery == null)
            {
                downloadQuery = new DownloadManager.Query();
            }

            var cursor = downloadManager.InvokeQuery(downloadQuery.SetFilterById((int)requestId.ToInt()));

            if (cursor != null && cursor.MoveToFirst())
            {
                if (downloadColumns == null)
                {
                    downloadColumns = new DownloadColumns();
                }

                var preAlready = downloadColumns.Already;

                downloadColumns.Id = cursor.GetString(cursor.GetColumnIndex(DownloadManager.ColumnId));

                downloadColumns.Title = cursor.GetString(cursor.GetColumnIndex(DownloadManager.ColumnTitle));

                downloadColumns.Desc = cursor.GetString(cursor.GetColumnIndex(DownloadManager.ColumnDescription));

                downloadColumns.MediaType = cursor.GetString(cursor.GetColumnIndex(DownloadManager.ColumnMediaType));

                downloadColumns.LocalUri = cursor.GetString(cursor.GetColumnIndex(DownloadManager.ColumnLocalUri));

                downloadColumns.Already = cursor.GetLong(cursor.GetColumnIndex(DownloadManager.ColumnBytesDownloadedSoFar));

                downloadColumns.TotalSize = cursor.GetLong(cursor.GetColumnIndex(DownloadManager.ColumnTotalSizeBytes));

                var status = cursor.GetInt(cursor.GetColumnIndex(DownloadManager.ColumnStatus));

                downloadColumns.Status = status switch
                {
                    (int)DownloadStatus.Paused => "Paused",
                    (int)DownloadStatus.Pending => "Pending",
                    (int)DownloadStatus.Running => "Running",
                    (int)DownloadStatus.Successful => "Successful",
                    (int)DownloadStatus.Failed => "Failed",
                    _ => ""
                };

                downloadColumns.Progress = (int)(downloadColumns.Already * 100 / downloadColumns.TotalSize);

                var speed = downloadColumns.Already - preAlready;

                if (speed > 1000)
                {
                    downloadColumns.Speed = (int)speed;
                }
            }
        }

        return downloadColumns;
    }

    public void DownloadSave(string directory, string name, byte[] bytes, bool openFolder)
    {
        // Every save needs the entry created for this request: the static downloadUri
        // only ever names the previous insert, and that entry may already be gone
        // (a same-name DownloadDel before an export, an external removal, ...) -
        // writing through it then throws FileNotFoundException "No item at
        // content://media/external/file/<id>". Even while it survives, "wa" would
        // append the new content to the previous file instead of saving this one.
        DownloadNew(directory, name);

        using (var output = AndroidApp.MainActivity.ContentResolver.OpenOutputStream(downloadUri, "w"))
        {
            output.Write(bytes);
        }
    }

    public void DownloadUpdate(string directory, string name, string namenew)
    {
        var uri = MediaStore.Files.GetContentUri("external");

        var cursor = AndroidCommon.DownloadQueryCursor(uri, directory, name);

        if (cursor.Count > 1)
        {
            cursor.MoveToNext();

            var id = cursor.GetInt(cursor.GetColumnIndex(MediaStore.Files.FileColumns.Id));

            var pathUri = ContentUris.WithAppendedId(uri, id);

            ContentValues values0 = new ContentValues();

            values0.Put(MediaStore.Downloads.InterfaceConsts.DisplayName, namenew);

            values0.Put(MediaStore.Downloads.InterfaceConsts.RelativePath, Environment.DirectoryDownloads);

            AndroidApp.MainActivity.ContentResolver.Update(uri, values0, null);
        }
    }
}

internal static class AndroidCommon
{
    internal static ICursor DownloadQueryCursor(Uri uri, string category, string name)
    {
        //ContentValues values = new ContentValues();
        //values.Put(MediaStore.Downloads.InterfaceConsts.DisplayName, name);
        //values.Put(MediaStore.Downloads.InterfaceConsts.RelativePath, Android.OS.Environment.DirectoryDownloads);

        string selection = null;

        if (category.IsNullOrWhiteSpace() || name.IsNullOrWhiteSpace())
        {

        }
        else if (!category.IsNullOrWhiteSpace() && name.IsNullOrWhiteSpace())
        {
            var relativePath = Path.Combine(Environment.DirectoryDownloads, category);

            selection = $"{MediaStore.Files.FileColumns.RelativePath}='{relativePath}'";
        }
        else if (category.IsNullOrWhiteSpace() && !name.IsNullOrWhiteSpace())
        {
            selection = $"{MediaStore.Files.FileColumns.DisplayName}='{name}'";
        }
        else if (!category.IsNullOrWhiteSpace() && !name.IsNullOrWhiteSpace())
        {
            var relativePath = Path.Combine(Environment.DirectoryDownloads, category);

            selection = $"{MediaStore.Files.FileColumns.RelativePath}='{relativePath}' OR {MediaStore.Files.FileColumns.DisplayName}='{name}'";
        }

        var cursor = AndroidApp.MainActivity.ContentResolver.Query(uri, null, selection, null, null);

        //Cursor cursor = Activity.ContentResolver.Query(Phones.CONTENT_URI, null, Phones.NUMBER + " LIKE" + " '%" + text + "%'" + " OR " + Phones.NAME + " LIKE" + " '%" + text + "%'", null, null);

        return cursor;
    }
}

public struct ANativeWindow { }

internal static class AndroidRuntime
{
    const string LibName = "android.so";

    [DllImport(LibName)]
    public static extern IntPtr ANativeWindow_fromSurface(IntPtr jniEnv, IntPtr surface);

    [DllImport(LibName)]
    public static extern int ANativeWindow_setBuffersGeometry(IntPtr aNativeWindow, int width, int height, int format);

    [DllImport(LibName)]
    public static extern void ANativeWindow_release(IntPtr aNativeWindow);
}

