// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Basic;

public static class DeviceServices
{
    public static IDeviceCore Core { get; private set; }

    public static IMediaPlayer Media { get; private set; }

    public static IDialogService Dialog { get; private set; }

    public static IFileService File { get; private set; }

    public static IGalleryService Gallery { get; private set; }

    public static IRecordService Record { get; private set; }

    public static IDownloadService Download { get; private set; }

    public static IStoreService Store { get; private set; }

    public static IWindowsFeatures WindowsFeatures { get; private set; }

    public static void Initialize(IDeviceCore core, IMediaPlayer media, IDialogService dialog, IFileService file, IGalleryService gallery, IRecordService record, IDownloadService download, IStoreService store, IWindowsFeatures windowsFeatures)
    {
        Core = core;

        Media = media;

        Dialog = dialog;

        File = file;

        Gallery = gallery;

        Record = record;

        Download = download;

        Store = store;

        WindowsFeatures = windowsFeatures;
    }
}

public abstract class BaseApp
{
    public string Title { get; set; }

    public bool FullScreen { get; set; }

    public bool HideSystemBars { get; set; }

    public virtual async void Create()
    {

    }
}

public enum Platform
{
    Windows,
    Linux,
    MacCatalyst,
    Android,
    iOS
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

public interface IDeviceCore
{
    Platform Platform { get; }

    Channel Channel { get; }

    string GetLocalIP();

    Stream LoadFile(string res);

    bool LoadFileExists(string res);

    bool IsDarkMode();
}

public interface IMediaPlayer
{
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

public class SeasonTask
{
    public string ID { get; set; }

    public List<TaskFile> Files = null;

    public Object Object = null;

    public Action<string[]> Task = null;

    public AsyncCallback Finish = null;

    public DownloadColumns DownloadColumns;

    public string[] Messages = null;

    public string Status = null;

    public CancellationTokenSource CancellationTokenSource = null;

    public SeasonTask()
    {

    }

    public void Start(string[] mes)
    {
        if (Task != null)
        {
            Task.Invoke(mes);
        }
    }

    public void Complete()
    {
        if (Finish != null)
        {
            Finish.Invoke(null);
        }
    }

    public void StartAsync()
    {
        if (Task != null)
        {
            Task.BeginInvoke(null, null, null);
        }

        if (Finish != null)
        {
            Finish.BeginInvoke(null, null, null);
        }
    }

    public void Cancel()
    {
        if (Finish != null)
        {
            Finish.Invoke(null);
        }
    }
}

public class TaskFile
{
    public string Name = null;

    public string Ext = null;

    public string Text = null;

    public byte[] Bytes = null;

    public Stream Stream = null;
}

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

    public long Progress { get; set; }

    public long Speed { get; set; }

    public string Time { get; set; }

    public string Status { get; set; }

    public int Current { get; set; }

    public int Total { get; set; }
}


public interface IRecordService
{
    Task<bool> StartRecord();

    Task<byte[]> StopRecord();

    Task<TaskFile> TakePhoto();
}

public abstract class RecordService
{
    public async Task<TaskFile> TakePhoto()
    {
        var file = await MediaPicker.CapturePhotoAsync();

        var stream = await file.OpenReadAsync();

        var bytes = stream.ReadAllBytes();

        var taskFile = new TaskFile()
        {
            Name = file.FileName,
            Text = "",
            Bytes = bytes
        };

        GC.Collect();

        return taskFile;
    }
}

public interface IDownloadService
{
    void DownloadNew(string category, string name);

    void DownloadSave(string category, string name, byte[] bytes);

    void DownloadUpdate(string category, string name, string namenew);

    void DownloadDel(string category, string name);

    string Download(string url);

    DownloadColumns DownloadQuery(string requestId, float time);

    void DownloadCancel(string requestId);
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
    Task<Product> Query(string storeId);

    Task<string> Purchase(string storeId, Action<string> onResult);

    Task<string> Review(string product);

    Task<(int version, string desc)> CheckForUpdates();
}

public class IconInfo
{
    public string ID { get; set; }

    public string Name { get; set; }

    public string Desc { get; set; }

    public string Icon { get; set; }

    public string Image { get; set; }

    public string Version { get; set; }

    public string Publisher { get; set; }

    public string Path { get; set; }
}

public interface IWindowsFeatures
{
    bool IsActive(out DateTime? lastTime);

    Task<bool> IsAutoStartEnabled(string taskId);

    Task<(bool?, string)> SetAutoStart(string taskId, bool enable);

    void OpenTaskbarSettings();

    void SetBlockingKeys(bool block);

    string ExtractIcon(string file);

    List<System.Diagnostics.Process> GetChildProcesses(System.Diagnostics.Process parentProcess);

    System.Diagnostics.Process GetForegroundProcess();

    Shortcut? ReadLnkFile(string file);

    string ConvertIcon(string icon);

    int GetVolume();

    bool SetVolume(int volume);

    bool SetForeground(string title);

    List<IconInfo> ListApps(List<string> ids, bool copyLogo);

    void Shutdown();

    void Reboot();

    (bool, int, int) GetLockScreens(out string errMsg);
}

public struct Shortcut
{
    public string Name { get; set; } // 名称

    public string TargetPath { get; set; } // 目标路径

    public string Arguments { get; set; } // 启动参数

    public string WorkingDirectory { get; set; } // 工作目录

    public string IconLocation { get; set; } // 图标位置
}
