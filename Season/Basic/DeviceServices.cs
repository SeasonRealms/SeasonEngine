// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Basic;

public static class DeviceServices
{
    public static BaseApp BaseApp { get; set; }

    public static IDeviceCore Core { get; private set; }

    public static IMediaPlayer Media { get; private set; }

    public static IDialogService Dialog { get; private set; }

    public static IFileService File { get; private set; }

    public static IImageService Image { get; private set; }

    public static IVideoPlayerService Video { get; private set; }

    public static IGalleryService Gallery { get; private set; }

    public static IRecordService Record { get; private set; }

    public static IDownloadService Download { get; private set; }

    public static IStoreService Store { get; private set; }

    public static IAds Ads { get; set; }

    public static IWindowsFeatures WindowsFeatures { get; private set; }

    public static void Initialize(BaseApp baseApp, IDeviceCore core, IMediaPlayer media, IVideoPlayerService video, IDialogService dialog, IFileService file, IImageService image, IGalleryService gallery, IRecordService record, IDownloadService download, IStoreService store, IAds ads, IWindowsFeatures windowsFeatures)
    {
        BaseApp = baseApp;

        Core = core;

        Media = media;

        Dialog = dialog;

        File = file;

        Image = image;

        Video = video;

        Gallery = gallery;

        Record = record;

        Download = download;

        Store = store;

        Ads = ads;

        WindowsFeatures = windowsFeatures;

        BaseApp.Init();
    }
}

public interface IDeviceCore
{
    Platform Platform { get; }

    Channel Channel { get; }

    Orientation Orientation { get; set; }

    string GetLocalIP();

    string LoadFilePath(string res);

    bool LoadFileExists(string res);

    Stream LoadFile(string res);

    /// <summary>
    /// Load a resource file asynchronously. The default implementation simply wraps synchronous <see cref="LoadFile"/>.
    /// Web overrides it with dynamic HTTP download because Blazor Wasm has no synchronous I/O
    /// and the returned task must not be waited on synchronously.
    /// </summary>
    Task<Stream> LoadFileAsync(string res) => Task.FromResult(LoadFile(res));

    bool IsDarkMode();

    Task<bool> RequestPermissionAsync(string[] permissions);
}

public interface IMediaPlayer
{
    bool IsPlaying { get; }

    void PlayMedia(string type, string id, string vol);

    void SetVolume(int music, int sound);

    void Pause();

    void Resume();
}

public interface IDialogService
{
    Task<string> ShowMessage(string title, string desc, string[] buttons, string text);

    Task<string> ShowKeyboard(string title, string desc, string[] buttons, string text);
}

public interface IFileService
{
    Task<string> PickFolder();

    Task<List<TaskFile>> PickFiles(FileType fileType, string[] exts, bool multiple, bool open);

    Task<string> SaveFile(string fileName, Stream stream, CancellationToken cancellationToken);

    void OpenFolder(string name);

    Task<string> OpenFile(string name, string category, byte[] bytes);

    Task<bool> OpenLink(string name);
}

public interface IImageService
{
    INativeImageDecoder GetImageFromStream(Stream stream, string ext);

    Task<INativeImageDecoder> GetImageFromStreamAsync(Stream stream, string ext);

    byte[] SaveImage(INativeImageDecoder image, ImageFormat imageFormat, int quality = 90);

    Task<byte[]> SaveImageAsync(INativeImageDecoder image, ImageFormat imageFormat, int quality = 90);

    Task<byte[]> SaveVideo(INativeImageDecoder[] images, VideoSaveOptions options = null);

    Task<INativeImageDecoder[]> LoadVideo(Stream stream, VideoLoadOptions options = null);

    MediaStream LoadVideoStream(Stream stream, VideoLoadOptions? options = null, CancellationToken cancellationToken = default);
}

public enum ImageFormat
{
    Jpeg, Png, Bmp, Gif, Tiff
}

public sealed class VideoSaveOptions
{
    public int FramesPerSecond { get; set; } = 16;

    public int Quality { get; set; } = 90;
}

public sealed class VideoLoadOptions
{
    public int? MaxFrames { get; set; }
    public int? MaxWidth { get; set; }
    public int? MaxHeight { get; set; }
    public int? FramesPerSecond { get; set; }  // Downsampled frame rate
    public TimeSpan? StartTime { get; set; }
}

// Audio/video stream parsing result types.

/// <summary>
/// Video stream parsing context. It contains the video frame stream and, if an audio track exists,
/// the audio sample stream. Both are driven by a single demuxer, so audio and video stay naturally synchronized.
/// Call <see cref="DisposeAsync"/> to release resources.
/// </summary>
public sealed class MediaStream : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts;
    private readonly Task _demuxTask;

    public System.Threading.Channels.ChannelReader<VideoFramePacket> VideoFrames { get; }
    public System.Threading.Channels.ChannelReader<AudioPcmPacket>? AudioSamples { get; }
    public VideoTrackInfo VideoInfo { get; }
    public AudioTrackInfo? AudioInfo { get; }

    internal MediaStream(
        System.Threading.Channels.ChannelReader<VideoFramePacket> video,
        System.Threading.Channels.ChannelReader<AudioPcmPacket>? audio,
        VideoTrackInfo videoInfo,
        AudioTrackInfo? audioInfo,
        CancellationTokenSource cts,
        Task demuxTask)
    {
        VideoFrames = video;
        AudioSamples = audio;
        VideoInfo = videoInfo;
        AudioInfo = audioInfo;
        _cts = cts;
        _demuxTask = demuxTask;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _demuxTask; } catch (OperationCanceledException) { }
        _cts.Dispose();
    }
}

public readonly record struct VideoFramePacket(
    INativeImageDecoder Frame,
    TimeSpan PresentationTimestamp
);

public readonly record struct AudioPcmPacket(
    byte[] PcmData,         // s16le, interleaved
    int SampleRate,
    int Channels,
    TimeSpan PresentationTimestamp
);

public readonly record struct VideoTrackInfo(
    int Width, int Height,
    double FramesPerSecond,
    TimeSpan Duration
);

public readonly record struct AudioTrackInfo(
    int SampleRate, int Channels,
    TimeSpan Duration
);

public interface IGalleryService
{
    Task<List<MediaAsset>> MediaGallery();

    Task<List<MediaAsset>> MediaGalleryDownloads();

    Task<Stream> MediaAsset(MediaAsset mediaAsset);

    Task<bool> MediaRemove(MediaAsset[] mediaAssets, bool delEmptyDirectory);
}

public class MediaAsset
{
    public string Id { get; set; }

    public string Name { get; set; }

    public string Ext { get; set; }

    public string Category { get; set; }

    public MediaAssetType Type { get; set; }

    public string PreviewPath { get; set; }

    public string Path { get; set; }

    public string Url { get; set; }

    public string DownId { get; set; }

    public long Size { get; set; }

    public string Time { get; set; }

    public Object Object { get; set; }

    public DownloadColumns DownloadColumns { get; set; }
}

public enum MediaAssetType
{
    Image, Video, File, Unknown
}

public interface IRecordService
{
    Task<bool> StartRecord();

    Task<byte[]> StopRecord();

    Task<TaskFile> TakePhoto();

    // Screen capture support.

    /// <summary>
    /// Capture the entire screen that contains the current application.
    /// Supported only on Windows / Linux desktop platforms; mobile and Web return null.
    /// </summary>
    Task<INativeImageDecoder?> CaptureScreen();

    /// <summary>
    /// Capture the current application's rendered content itself
    /// by reading back the GPU backbuffer as RGBA8.
    /// Supported on all platforms through their respective graphics APIs.
    /// </summary>
    Task<INativeImageDecoder?> CaptureApp();
}

/// <summary>
/// Cross-platform video playback service.
/// It encapsulates video decoding, rendering, and audio output, and pushes video frames through events.
/// On Windows it uses MediaPlayer hardware acceleration and relies on the system for audio output.
/// Other platforms can provide compatible implementations later.
/// </summary>
public interface IVideoPlayerService : IDisposable
{
    /// <summary>A new video frame is ready (RGBA8 pixels). The callback thread depends on the platform.</summary>
    event Action<INativeImageDecoder>? VideoFrameAvailable;

    /// <summary>Playback ended, either naturally or because <see cref="Stop"/> was called.</summary>
    event Action? PlaybackEnded;

    /// <summary>Start playing the specified file. Playback begins pushing frames after the asynchronous setup completes.</summary>
    void Play(string filePath);

    /// <summary>Stop playback and release resources.</summary>
    void Stop();

    /// <summary>Video width in pixels. Valid after <see cref="Play"/> has completed setup.</summary>
    int VideoWidth { get; }

    /// <summary>Video height in pixels. Valid after <see cref="Play"/> has completed setup.</summary>
    int VideoHeight { get; }

    /// <summary>Whether playback is currently active.</summary>
    bool IsPlaying { get; }
}

public abstract class RecordService
{
    public async Task<TaskFile> TakePhoto()
    {
        return null;
        //var file = await MediaPicker.CapturePhotoAsync();

        //var stream = await file.OpenReadAsync();

        //var bytes = stream.ReadAllBytes();

        //var taskFile = new TaskFile()
        //{
        //    Name = file.FileName,
        //    Text = "",
        //    Bytes = bytes
        //};

        //GC.Collect();

        //return taskFile;
    }
}

public interface IDownloadService
{
    void DownloadNew(string category, string name);

    void DownloadSave(string category, string name, byte[] bytes, bool openFolder);

    void DownloadUpdate(string category, string name, string namenew);

    void DownloadDel(string category, string name);

    string Download(string url);

    DownloadColumns DownloadQuery(string requestId, float time);

    void DownloadCancel(string requestId);
}

public class DownloadColumns
{
    public string Id { get; set; }

    public string Title { get; set; }

    public string Desc { get; set; }

    public string Type { get; set; }

    public string MediaType { get; set; }

    public string LocalUri { get; set; }

    public DateTime PreTime { get; set; }

    public long PreAlready { get; set; }

    public long Already { get; set; }

    public long TotalSize { get; set; }

    public int Progress { get; set; }

    public int Speed { get; set; }

    public string Time { get; set; }

    public string Status { get; set; }

    public int Current { get; set; }

    public int Total { get; set; }
}

public class Product
{
    public string StoreId { get; set; }

    public string Title { get; set; }

    public string Type { get; set; }

    public string Price { get; set; }

    public bool InCollection { get; set; }

    public string Message { get; set; }
}

public interface IStoreService
{
    Task<(List<Product>, string)> Query();

    Task<Product> Query(string storeId);

    Task<string> Purchase(string storeId, Action<string> onResult);

    Task<string> Review(string product);

    Task<(int version, string desc)> CheckForUpdates();
}

public interface IAds
{
    string AdUnit { get; set; }

    string InitAd();

    Task<string> LoadAd();

    Task<string> ShowAd();
}

public enum Platform
{
    None,
    Windows,
    Linux,
    MacCatalyst,
    Android,
    iOS,
    Web
}

public enum Channel
{
    None,
    Microsoft,
    Google,
    Apple
}

public enum FileType
{
    None,
    Image,
    Video,
    Audio,
    File,
    Link,
    Font
}

public enum Orientation
{
    Unknown,
    Portrait,
    PortraitUpsideDown,
    LandscapeLeft,
    LandscapeRight
}
