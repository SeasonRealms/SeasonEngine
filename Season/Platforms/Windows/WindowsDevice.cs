// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using Windows.Devices.Enumeration;
using Windows.Media.Audio;
using Windows.Media.Capture;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Playback;
using Windows.Media.Render;
using Windows.Services.Store;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI.ViewManagement;

namespace Season.Platforms.Windows;

internal class WindowsDeviceCore : IDeviceCore
{
    public Basic.Platform Platform => Basic.Platform.Windows;

    public Channel Channel { get; set; } = Channel.Microsoft;

    public Orientation Orientation
    {
        get
        {
            try
            {
                DEVMODE dm = new DEVMODE();
                dm.dmSize = (ushort)Marshal.SizeOf(typeof(DEVMODE));

                if (WindowsNative.EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref dm) != 0)
                {
                    return dm.dmDisplayOrientation switch
                    {
                        0 => Orientation.LandscapeLeft,   // DMDO_DEFAULT
                        1 => Orientation.Portrait,        // DMDO_90
                        2 => Orientation.LandscapeRight,  // DMDO_180
                        3 => Orientation.PortraitUpsideDown, // DMDO_270
                        _ => Orientation.LandscapeLeft
                    };
                }
            }
            catch (Exception ex)
            {
                // Ignore
            }
            return Orientation.LandscapeLeft;
        }
        set
        {
            try
            {
                int orientation = value switch
                {
                    Orientation.LandscapeLeft => 0,   // DMDO_DEFAULT
                    Orientation.Portrait => 1,        // DMDO_90
                    Orientation.LandscapeRight => 2,  // DMDO_180
                    Orientation.PortraitUpsideDown => 3, // DMDO_270
                    _ => -1
                };

                if (orientation != -1)
                {
                    DEVMODE dm = new DEVMODE();
                    dm.dmSize = (ushort)Marshal.SizeOf(typeof(DEVMODE));

                    if (WindowsNative.EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref dm) != 0)
                    {
                        bool isCurrentPortrait = dm.dmDisplayOrientation == 1 || dm.dmDisplayOrientation == 3;
                        bool isTargetPortrait = orientation == 1 || orientation == 3;

                        if (isCurrentPortrait != isTargetPortrait)
                        {
                            uint temp = dm.dmPelsWidth;
                            dm.dmPelsWidth = dm.dmPelsHeight;
                            dm.dmPelsHeight = temp;
                        }

                        dm.dmDisplayOrientation = (uint)orientation;
                        dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYORIENTATION;

                        WindowsNative.ChangeDisplaySettings(ref dm, 0);
                    }
                }
            }
            catch (Exception ex)
            {
                DeviceServices.BaseApp.AddLog(LogType.Error, $"{DateTime.UtcNow} WindowsDevice  Failed to set orientation {ex}");
            }
        }
    }

    const int ENUM_CURRENT_SETTINGS = -1;
    const uint DM_DISPLAYORIENTATION = 0x00000080;
    const uint DM_PELSWIDTH = 0x00080000;
    const uint DM_PELSHEIGHT = 0x00100000;

    public string GetLocalIP()
    {
        var ipAddress = "";

        var ips = new List<string>();

        var ipEntry = Dns.GetHostEntry(Dns.GetHostName());

        var addrs = ipEntry.AddressList.NullToEmptyArray();

        foreach (var addr in addrs)
        {
            if (addr.AddressFamily == AddressFamily.InterNetworkV6 && !addr.IsIPv6LinkLocal)
            {
                var ip = addr.ToString();

                ips.Add(ip);
            }
        }

        var ipv4 = addrs.FirstOrDefault(ad => ad.AddressFamily == AddressFamily.InterNetwork).ToString();

        if (ips.Count > 0)
        {
            ipAddress = ips.MaxBy(ip => ip.Length);
        }

        return ipAddress;
    }

    public string LoadFilePath(string res)
    {
        var location = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

        var file = Path.Combine(location, res);

        return file;
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
        var uiSettings = new UISettings();
        var color = uiSettings.GetColorValue(UIColorType.Background);

        // Determine color brightness (dark mode backgrounds are usually darker)
        // Calculate relative brightness formula (ITU-R BT.709)

        double luminance = (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255.0;
        return luminance < 0.5;
    }

    public async Task<bool> RequestPermissionAsync(string[] permissions)
    {
        // Windows capture permissions are gated by the package manifest capability plus
        // the per-app privacy toggle; there is no runtime consent prompt to drive. Probe
        // the real status instead of pretending every request is granted: a microphone
        // request maps DeniedByUser/DeniedBySystem to false so callers can route the
        // user to Settings > Privacy > Microphone. Everything else stays manifest-gated.
        var needsMicrophone = permissions is not null && permissions.Any(p =>
            p is not null && (p.Contains("RECORD_AUDIO", StringComparison.OrdinalIgnoreCase)
                || p.Contains("MICROPHONE", StringComparison.OrdinalIgnoreCase)));

        if (!needsMicrophone)
        {
            return true;
        }

        var status = DeviceAccessInformation.CreateFromDeviceClass(DeviceClass.AudioCapture).CurrentStatus;
        // Unspecified also covers unpackaged/dev runs where no per-app privacy entry
        // exists yet; the actual gate still surfaces AccessDenied at capture time.
        var granted = status is DeviceAccessStatus.Allowed or DeviceAccessStatus.Unspecified;

        DeviceServices.BaseApp?.AddLog(LogType.None, $"{DateTime.UtcNow} [Permission] microphone probe status={status} granted={granted}");
        return granted;
    }
}

internal class WindowsMediaPlayer : IMediaPlayer
{
    public bool IsPlaying 
    {
        get
        {
            if (MusicPlayer?.CurrentState is MediaPlayerState.Playing || SoundPlayer?.CurrentState is MediaPlayerState.Playing)
            {
                return true;
            }

            return false;
        }
    }

    MediaPlayer MusicPlayer = null;

    MediaPlayer SoundPlayer = null;

    public void PlayMedia(string type, string id, string vol)
    {
        //Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(() =>
        WindowsApp.Window.DispatcherQueue.TryEnqueue(() =>
        {
            if (MusicPlayer == null || SoundPlayer == null)
            {
                MusicPlayer = new MediaPlayer();
                
                SoundPlayer = new MediaPlayer();
                
                //SoundPlayer.MediaEnded += (s, e) =>
                //{
                //    SoundPlayer.Pause();
                //};
            }

            var mediaPlayer = type is "Music" ? MusicPlayer : SoundPlayer;

            mediaPlayer.Source = MediaSource.CreateFromUri(new Uri(id));

            mediaPlayer.AutoPlay = false;

            mediaPlayer.IsLoopingEnabled = false;

            if (vol?.Length > 0)
            {
                mediaPlayer.Volume = float.Parse(vol) / 100;
            }

            //mediaPlayer.CurrentStateChanged += (s, e) =>
            //{
            //    if (mediaPlayer.CurrentState == MediaPlayerState.Paused)
            //    {
            //        mediaPlayer.Pause();
            //    }
            //};

            mediaPlayer.Play();
        });
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

internal class WindowsDialogService : IDialogService
{
    public async Task<string> ShowMessage(string title, string desc, string[] buttons, string text)
    {
        var tcs = new TaskCompletionSource<string>();

        WindowsApp.Window.DispatcherQueue.TryEnqueue(async () =>
        {
            var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog()
            {
                Content = new Microsoft.UI.Xaml.Controls.TextBlock()
            };
            dialog.Title = title;
            dialog.XamlRoot = WindowsApp.Window.Content.XamlRoot;
            dialog.PrimaryButtonText = buttons[0];
            //dialog.CloseButtonText = buttons[1];
            dialog.Style = Microsoft.UI.Xaml.Application.Current.Resources["DefaultContentDialogStyle"] as Microsoft.UI.Xaml.Style;

            var edit = dialog.Content as Microsoft.UI.Xaml.Controls.TextBlock;
            edit.Text = text;

            var result = await dialog.ShowAsync();

            tcs.TrySetResult(null);
        });

        return await tcs.Task;
    }

    static readonly SemaphoreSlim _dialogLock = new SemaphoreSlim(1, 1);

    public async Task<string> ShowKeyboard(string title, string desc, string[] buttons, string text)
    {
        await _dialogLock.WaitAsync();

        if (buttons is null || buttons.Length <= 1)
        {
            buttons = new string[] { "OK", "Cancel" };
        }

        var tcs = new TaskCompletionSource<string>();

        WindowsApp.Window.DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog()
                {
                    Content = new Microsoft.UI.Xaml.Controls.RichEditBox()
                };
                dialog.Title = title + " " + desc;
                dialog.XamlRoot = WindowsApp.Window.Content.XamlRoot;
                dialog.PrimaryButtonText = buttons[0];
                dialog.CloseButtonText = buttons[1];
                dialog.Style = Microsoft.UI.Xaml.Application.Current.Resources["DefaultContentDialogStyle"] as Microsoft.UI.Xaml.Style;

                var edit = dialog.Content as Microsoft.UI.Xaml.Controls.RichEditBox;
                edit.Document.SetText(Microsoft.UI.Text.TextSetOptions.None, text);

                var result = await dialog.ShowAsync();

                if (result is Microsoft.UI.Xaml.Controls.ContentDialogResult.None)
                {
                    tcs.TrySetResult(null);
                }
                else
                {
                    edit.Document.GetText(Microsoft.UI.Text.TextGetOptions.None, out var output);

                    if (output.EndsWith("\r"))
                    {
                        output = output.Substring(0, output.Length - 1);
                    }

                    tcs.TrySetResult(output);
                }
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
            finally
            {
                _dialogLock.Release();
            }
        });

        return await tcs.Task;
    }
}

internal class WindowsFileService : IFileService
{
    internal static string DownloadDir
    {
        get
        {
            var myVideo = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);

            return myVideo.Replace("Videos", "Downloads");
        }
    }

    public async Task<string> PickFolder()
    {
        string path = null;

        var folderPicker = new FolderPicker()
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary
        };
        WinRT.Interop.InitializeWithWindow.Initialize(folderPicker, Process.GetCurrentProcess().MainWindowHandle);

        var storageFolder = await folderPicker.PickSingleFolderAsync();

        if (storageFolder is null)
        {

        }
        else
        {
            path = storageFolder.Path;
        }

        return path;
    }

    public async Task<List<TaskFile>> PickFiles(FileType fileType, string[] exts, bool multiple, bool open)
    {
        List<TaskFile> taskFiles = null;

        using var process = Process.GetCurrentProcess();

        var hwnd = process.MainWindowHandle;

        var picker = new FileOpenPicker();

        var initializeWithWindow = WinRT.CastExtensions.As<IInitializeWithWindow>(picker);

        initializeWithWindow.Initialize(hwnd);

        if (exts is null || exts.Length == 0)
        {
            //picker.FileTypeFilter.Add(".lnk");
            picker.FileTypeFilter.Add("*");
        }
        else
        {
            foreach (var ext in exts)
            {
                picker.FileTypeFilter.Add(ext);
            }
        }

        if (fileType is FileType.Image)
        {
            picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
        }
        else if (fileType is FileType.Video)
        {
            picker.SuggestedStartLocation = PickerLocationId.VideosLibrary;
        }
        else if (fileType is FileType.Audio)
        {
            picker.SuggestedStartLocation = PickerLocationId.MusicLibrary;
        }
        else if (fileType is FileType.File)
        {
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        }
        else if (fileType is FileType.Link)
        {
            picker.SuggestedStartLocation = PickerLocationId.Desktop;

            picker.FileTypeFilter.Add(".url");
            picker.FileTypeFilter.Add(".lnk");
            picker.FileTypeFilter.Add(".exe");
        }
        else if (fileType is FileType.Font)
        {
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;

            picker.FileTypeFilter.Add(".ttf");
            picker.FileTypeFilter.Add(".otf");
        }
        else
        {
            picker.SuggestedStartLocation = PickerLocationId.Desktop;

            picker.FileTypeFilter.Add("*");
            //picker.FileTypeFilter.Add(".");
        }

        if (multiple)
        {
            var files0 = await picker.PickMultipleFilesAsync();

            if (files0 == null)
            {

            }
            else
            {
                taskFiles = new List<TaskFile>();

                for (var i = 0; i < files0.Count; i++)
                {
                    var file = files0[i];

                    Stream stream = null;

                    if (open)
                    {
                        stream = await file.OpenStreamForReadAsync();
                    }
                    else
                    {

                    }

                    taskFiles.Add(new TaskFile()
                    {
                        Name = file.Path,
                        Ext = System.IO.Path.GetExtension(file.Name).ToLower(),
                        Stream = stream
                        //Bytes = bytes stream.Result.StreamToBytes();
                    });
                }
            }
        }
        else
        {
            var file0 = await picker.PickSingleFileAsync();

            if (file0 == null)
            {

            }
            else
            {
                Stream stream = null;

                if (open)
                {
                    stream = await file0.OpenStreamForReadAsync();
                }
                else
                {

                }

                taskFiles = new List<TaskFile>();

                taskFiles.Add(new TaskFile()
                {
                    Name = file0.Path,
                    Ext = System.IO.Path.GetExtension(file0.Name).ToLower(),
                    Stream = stream
                    //Bytes = bytes stream.Result.StreamToBytes();
                });
            }
        }

        return taskFiles;
    }

    public async Task<string> SaveFile(string fileName, Stream stream, CancellationToken cancellationToken)
    {
        var savePicker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary
        };
        WinRT.Interop.InitializeWithWindow.Initialize(savePicker, Process.GetCurrentProcess().MainWindowHandle);

        var extension = Path.GetExtension(fileName);

        if (!string.IsNullOrEmpty(extension))
        {
            savePicker.FileTypeChoices.Add(extension, new List<string> { extension });
        }

        savePicker.FileTypeChoices.Add("All files", new string[] { "." });
        savePicker.SuggestedFileName = Path.GetFileNameWithoutExtension(fileName);
        var filePickerOperation = savePicker.PickSaveFileAsync();

        await using var _ = cancellationToken.Register(() => { filePickerOperation.Cancel(); });
        var file = await filePickerOperation;
        if (string.IsNullOrEmpty(file?.Path))
        {
            throw new Exception("Operation cancelled or Path doesn't exist.");
        }

        var filePath = file.Path;

        await using var fileStream = new FileStream(filePath, FileMode.OpenOrCreate);
        fileStream.SetLength(0);
        if (stream.CanSeek)
        {
            stream.Seek(0, SeekOrigin.Begin);
        }

        await stream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);

        return filePath;
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
        }

        return result;
    }

    async Task<string> SaveStorageFile(string name)
    {
        var directory =  DownloadDir;

        var newPath = Path.Combine(directory, Path.GetFileName(name));

        if (Storage.StorageService.TryGetStream(Storage.StorageService.DirectoryBase, name, out Stream stream, out string errMsg))
        {
            using (stream)
            {
                using (var streamTarget = new FileStream(newPath, FileMode.OpenOrCreate))
                {
                    using (var writer = new StreamWriter(streamTarget))
                    {
                        var length = (int)(stream.Length < 4096 ? stream.Length : 4096);

                        var array = new byte[length];

                        int bytesRead = 0;

                        while ((bytesRead = stream.Read(array, 0, length)) > 0)
                        {
                            streamTarget.Write(array, 0, bytesRead);
                        }
                    }
                }
            }
        }

        return newPath;
    }

    async Task<string> SaveBytesFile(string name, byte[] bytes)
    {
        var directory = DownloadDir;

        var path = Path.Combine(directory, Path.GetFileName(name));

        File.WriteAllBytes(path, bytes);

        return path;
    }

    async Task<bool> OpenMediaGalleryFile(string name)
    {
        //var stream = File.OpenRead(name);

        //var cancellation = new CancellationTokenSource();

        //name = await SaveFile(name, stream, cancellation.Token);

        return await Launcher.OpenAsync(name);
    }

    public async Task<bool> OpenLink(string name)
    {
        return await Launcher.OpenAsync(name);
    }

    public async void OpenFolder(string name)
    {
        await Launcher.OpenAsync(name);
    }
}

[ComImport]
[System.Runtime.InteropServices.Guid("3E68D4BD-7135-4D10-8018-9FB6D9F33FA1")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IInitializeWithWindow
{
    void Initialize(IntPtr hwnd);
}

internal class WindowsGalleryService : IGalleryService
{
    public async Task<Stream> MediaAsset(MediaAsset mediaAsset)
    {
        return File.OpenRead(mediaAsset.Path);
    }

    public async Task<List<MediaAsset>> MediaGallery()
    {
        var mediaAssets = new List<MediaAsset>();

        var myPictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

        var myMusic = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);

        var myVideo = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);

        var myDownloads = WindowsFileService.DownloadDir;

        var directories = new string[] { myPictures, myMusic, myVideo, myDownloads };

        foreach (var directory in directories)
        {
            List<string> files = null;

            if (directory == myDownloads)
            {
                files = Directory.GetFiles(directory, "*.*", SearchOption.TopDirectoryOnly).NullToEmptyList();
            }
            else
            {
                files = Directory.GetFiles(directory, "*.*", SearchOption.AllDirectories).NullToEmptyList();
            }

            foreach (var file in files)
            {
                var fileInfo = new FileInfo(file);

                var mediaAsset = new MediaAsset()
                {
                    Id = "",
                    Name = Path.GetFileNameWithoutExtension(file),
                    Ext = Path.GetExtension(file),
                    Category = fileInfo.CreationTime.ToMonthDate(),
                    Path = file,
                    PreviewPath = "",
                    Type = MediaAssetType.Unknown,
                    Size = fileInfo.Length,
                    Time = fileInfo.CreationTime.ToDateTimeSeconds(),
                    Object = null
                };

                mediaAssets.Add(mediaAsset);
            }
        }

        return mediaAssets;
    }

    public async Task<List<MediaAsset>> MediaGalleryDownloads()
    {
        var files = Directory.GetFiles(WindowsFileService.DownloadDir).NullToEmptyArray();

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
        if (mediaAssets == null || mediaAssets.Length == 0)
        {

        }
        else
        {
            foreach (var mediaAsset in mediaAssets)
            {
                File.Delete(mediaAsset.Path);

                if (delEmptyDirectory)
                {
                    var directory = Path.GetDirectoryName(mediaAsset.Path);

                    var files = Directory.GetFiles(directory, "*.*", SearchOption.AllDirectories);

                    if (files == null || files.Length == 0)
                    {
                        try
                        {
                            Directory.Delete(directory, true);
                        }
                        catch (Exception ex)
                        {
                            return false;
                        }
                    }
                }
            }
        }

        //May need to delete empty directory

        return true;
    }
}

//Need Record Permission
internal class WindowsRecordService : RecordService, IRecordService
{
    [DllImport("user32.dll")]
    static extern int GetSystemMetrics(int nIndex);

    const int SM_CXSCREEN = 0;
    const int SM_CYSCREEN = 1;

    // ── AudioGraph session (Windows.Media.Audio) ──
    // Replaces the legacy MCI waveaudio path (mciSendString): MCI is a pre-package
    // desktop API with no microphone-capability/privacy integration, so it fails
    // silently under an MSIX/Store identity. AudioGraph is the WinRT capture
    // pipeline: node creation reports explicit statuses (AccessDenied,
    // DeviceNotAvailable, ...), the graph resamples to the pinned encoding, and
    // the container is finalized by the graph itself (correct RIFF header). The
    // temp WAV lives in the StorageService LocalAppData tree (package-redirected
    // and always writable under MSIX) — never ApplicationData.Current, which has
    // UI-thread affinity in WinUI 3 — and is deleted after being read back.
    // Output contract stays: 16 kHz / mono / 16-bit PCM WAV as byte[] at StopRecord,
    // identical to AndroidRecordService so STT consumers see no difference.
    const uint SampleRate = 16000;
    const uint Channels = 1;
    const uint BitsPerSample = 16;

    AudioGraph? _graph;
    AudioDeviceInputNode? _inputNode;
    AudioFileOutputNode? _fileOutputNode;
    StorageFile? _recordFile;

    public async Task<bool> StartRecord()
    {
        if (_graph != null)
        {
            //_graph.Stop();
            //_graph.Dispose();
            return false;
        }

        try
        {
            DeviceServices.BaseApp?.AddLog(LogType.None, $"{DateTime.UtcNow} [Record] StartRecord begin thread={Environment.CurrentManagedThreadId} ui={WindowsApp.Window?.DispatcherQueue.HasThreadAccess}");

            var settings = new AudioGraphSettings(AudioRenderCategory.Speech)
            {
                EncodingProperties = AudioEncodingProperties.CreatePcm(SampleRate, Channels, BitsPerSample)
            };

            var createResult = await AudioGraph.CreateAsync(settings);
            if (createResult.Status != AudioGraphCreationStatus.Success)
            {
                DeviceServices.BaseApp?.AddLog(LogType.Error, $"{DateTime.UtcNow} [Record] AudioGraph create failed: {createResult.Status}");
                return false;
            }
            _graph = createResult.Graph;

            var inputResult = await _graph.CreateDeviceInputNodeAsync(MediaCategory.Speech);
            if (inputResult.Status != AudioDeviceNodeCreationStatus.Success)
            {
                DeviceServices.BaseApp?.AddLog(LogType.Error, $"{DateTime.UtcNow} [Record] microphone open failed: {inputResult.Status}"
                    + (inputResult.Status == AudioDeviceNodeCreationStatus.AccessDenied
                        ? " - check Windows Settings > Privacy > Microphone and the package microphone capability"
                        : ""));
                await CleanupSessionAsync();
                return false;
            }
            _inputNode = inputResult.DeviceInputNode;

            // ApplicationData.Current carries UI-thread affinity in WinUI 3 and throws
            // InvalidOperationException when called off the UI thread — and recording may
            // be driven from a background STT worker. Use the same LocalAppData base as
            // StorageService (package-redirected, always writable under MSIX) and obtain
            // the StorageFile through the path-based API, which is thread-agile.
            var tempDirectory = System.IO.Path.Combine(
                StorageService.Path(StorageService.DirectoryBase ?? "SeasonEngine"), "Temp");
            Directory.CreateDirectory(tempDirectory);

            var recordFolder = await StorageFolder.GetFolderFromPathAsync(tempDirectory);
            _recordFile = await recordFolder.CreateFileAsync(
                $"Record-{DateTime.Now:yyyyMMddHHmmss}.wav", CreationCollisionOption.GenerateUniqueName);

            var profile = MediaEncodingProfile.CreateWav(AudioEncodingQuality.Low);
            profile.Audio = AudioEncodingProperties.CreatePcm(SampleRate, Channels, BitsPerSample);

            var fileResult = await _graph.CreateFileOutputNodeAsync(_recordFile, profile);
            if (fileResult.Status != AudioFileNodeCreationStatus.Success)
            {
                DeviceServices.BaseApp?.AddLog(LogType.Error, $"{DateTime.UtcNow} [Record] WAV output node failed: {fileResult.Status}");
                await CleanupSessionAsync();
                return false;
            }
            _fileOutputNode = fileResult.FileOutputNode;

            _inputNode.AddOutgoingConnection(_fileOutputNode);

            _graph.Start();

            DeviceServices.BaseApp?.AddLog(LogType.None, $"{DateTime.UtcNow} [Record] started {SampleRate}Hz/{Channels}ch/{BitsPerSample}bit");
            return true;
        }
        catch (Exception ex)
        {
            DeviceServices.BaseApp?.AddLog(LogType.Error, $"{DateTime.UtcNow} [Record] StartRecord failed: {ex}");
            await CleanupSessionAsync();
            return false;
        }
    }

    public async Task<byte[]> StopRecord()
    {
        var graph = _graph;
        if (graph == null) return null;

        var inputNode = _inputNode;
        var outputNode = _fileOutputNode;
        var recordFile = _recordFile;

        // Detach first so a concurrent Start/Stop cannot observe a half-stopped session.
        _graph = null;
        _inputNode = null;
        _fileOutputNode = null;
        _recordFile = null;

        try
        {
            graph.Stop();

            if (outputNode != null)
            {
                // FinalizeAsync drains every queued sample and closes the container:
                // the RIFF length fields are patched only when this completes.
                await outputNode.FinalizeAsync();
            }

            if (recordFile == null) return null;

            using (var stream = await recordFile.OpenStreamForReadAsync())
            using (var memory = new MemoryStream())
            {
                await stream.CopyToAsync(memory);
                if (memory.Length == 0) return null;
                return memory.ToArray();
            }
        }
        catch (Exception ex)
        {
            DeviceServices.BaseApp?.AddLog(LogType.Error, $"{DateTime.UtcNow} [Record] StopRecord failed: {ex}");
            return null;
        }
        finally
        {
            inputNode?.Dispose();
            //outputNode?.Dispose();
            graph.Dispose();

            if (recordFile != null)
            {
                try { await recordFile.DeleteAsync(); }
                catch (Exception ex) { DeviceServices.BaseApp?.AddLog(LogType.None, $"{DateTime.UtcNow} [Record] temp cleanup failed: {ex.Message}"); }
            }
        }
    }

    /// <summary>
    /// Error-path teardown for StartRecord: releases the graph (which releases the
    /// microphone) and deletes the half-written temp file, if any.
    /// </summary>
    async Task CleanupSessionAsync()
    {
        var file = _recordFile;
        _recordFile = null;

        _inputNode?.Dispose();
        _inputNode = null;
        _fileOutputNode?.Dispose();
        _fileOutputNode = null;
        _graph?.Dispose();
        _graph = null;

        if (file != null)
        {
            try { await file.DeleteAsync(); }
            catch (Exception ex) { }
        }
    }

    public Task<INativeImageDecoder?> CaptureScreen()
    {
        INativeImageDecoder? CaptureCore()
        {
            try
            {
                int screenW = GetSystemMetrics(SM_CXSCREEN);
                int screenH = GetSystemMetrics(SM_CYSCREEN);

                using var bmp = new Bitmap(screenW, screenH);
                using var g = global::System.Drawing.Graphics.FromImage(bmp);
                g.CopyFromScreen(0, 0, 0, 0, new global::System.Drawing.Size(screenW, screenH));

                var rect = new Rectangle(0, 0, screenW, screenH);
                var data = bmp.LockBits(
                    rect,
                    ImageLockMode.ReadOnly,
                    PixelFormat.Format32bppArgb);

                byte[] pixels = new byte[screenW * screenH * 4];
                Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
                bmp.UnlockBits(data);

                return new NativeImageData(screenW, screenH, pixels);
            }
            catch (Exception ex)
            {
                DeviceServices.BaseApp.AddLog(LogType.Error, $"{DateTime.UtcNow} CaptureScreen {ex}");
                return null;
            }
        }

        var dispatcherQueue = WindowsApp.Window?.DispatcherQueue;
        if (dispatcherQueue == null || dispatcherQueue.HasThreadAccess)
            return Task.FromResult(CaptureCore());

        var tcs = new TaskCompletionSource<INativeImageDecoder?>();
        if (!dispatcherQueue.TryEnqueue(() => tcs.TrySetResult(CaptureCore())))
            return Task.FromResult(CaptureCore());

        return tcs.Task;
    }

    public Task<INativeImageDecoder?> CaptureApp()
    {
        // Inject the TCS -> set the NeedCaptureApp flag -> let the render thread detect it in
        // AfterRender, read back from the GPU, and complete with TrySetResult.
        var tcs = new TaskCompletionSource<INativeImageDecoder?>();
        BaseApp.CaptureAppTcs = tcs;
        return tcs.Task;
    }
}

internal class WindowsDownloadService : IDownloadService
{
    DownloadColumns downloadColumns = null;

    SeasonTask downloadTask = null;

    public string Download(string url)
    {
        var id = DateTime.Now.ToDateTimeMilliseconds();

        var fileName = Path.GetFileName(url);

        var file = Path.Combine(WindowsFileService.DownloadDir, fileName);

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
            file = Path.Combine(WindowsFileService.DownloadDir, name);
        }
        else
        {
            file = Path.Combine(WindowsFileService.DownloadDir, directory, name);
        }

        if (File.Exists(file))
        {
            File.Delete(file);
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
            directory = Path.Combine(WindowsFileService.DownloadDir, directory);

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
        string file = "";

        if (directory.IsNullOrWhiteSpace())
        {
            file = Path.Combine(WindowsFileService.DownloadDir, name);
        }
        else
        {
            file = Path.Combine(WindowsFileService.DownloadDir, directory, name);
        }

        using (var fs = File.Open(file, FileMode.Append))
        {
            fs.Write(bytes);

            fs.Close();
        }

        DeviceServices.File.OpenFolder(WindowsFileService.DownloadDir);
    }

    public void DownloadUpdate(string directory, string name, string namenew)
    {
        string fileName = "";

        string fileNameNew = "";

        if (directory.IsNullOrWhiteSpace())
        {
            fileName = Path.Combine(WindowsFileService.DownloadDir, name);

            fileNameNew = Path.Combine(WindowsFileService.DownloadDir, namenew);
        }
        else
        {
            fileName = Path.Combine(WindowsFileService.DownloadDir, directory, name);

            fileNameNew = Path.Combine(WindowsFileService.DownloadDir, directory, namenew);
        }

        File.Move(fileName, fileNameNew, true);
    }
}

