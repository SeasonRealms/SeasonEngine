// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Services.Store;
using Windows.Storage.Pickers;
using Windows.UI.ViewManagement;

namespace Season.Platforms.Windows;

internal class WindowsDeviceCore : IDeviceCore
{
    public Basic.Platform Platform => Basic.Platform.Windows;

    public Channel Channel { get; set; } = Channel.Microsoft;

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

    string LoadFilePath(string res)
    {
        var location = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

        var file = Path.Combine(location, res);

        return file;
    }

    public Stream LoadFile(string res)
    {
        var file = LoadFilePath(res);

        return File.OpenRead(file);
    }

    public bool LoadFileExists(string res)
    {
        var file = LoadFilePath(res);

        return File.Exists(file);
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
}

internal class WindowsMediaPlayer : IMediaPlayer
{
    MediaPlayer MusicPlayer = null;

    MediaPlayer SoundPlayer = null;

    public void PlayMedia(string type, string id, string vol)
    {
        Dispatcher.GetForCurrentThread()?.Dispatch(() =>
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

        Dispatcher.GetForCurrentThread()?.Dispatch(async () =>
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

        var tcs = new TaskCompletionSource<string>();

        Dispatcher.GetForCurrentThread()?.Dispatch(async () =>
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
            //picker.FileTypeFilter.Add("*");
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
    public async Task<bool> StartRecord()
    {
        bool success = false;

        WindowsNative.mciSendString("stop WaveDump" + "", "", 0, IntPtr.Zero);

        //int lu_cch = 0;
        //string lsb_ret = "";

        try
        {
            var ls_mciRetV = "";

            WindowsNative.mciSendString("open new type waveaudio alias WaveDump", ls_mciRetV, 0, IntPtr.Zero);

            //mciSendString("set WaveDump time format ms bitspersample 16 channels 1 samplespersec 44100 bytespersec 88200 alignment 2", ls_mciRetV, 0, IntPtr.Zero);

            WindowsNative.mciSendString("set WaveDump time format ms bitspersample 16 channels 1 samplespersec 16000 bytespersec 88200 alignment 2", ls_mciRetV, 0, IntPtr.Zero);

            var lu_errcode = WindowsNative.mciSendString("record WaveDump", ls_mciRetV, 0, IntPtr.Zero);

            if (lu_errcode == 0)
            {
                success = true;
            }
            else
            {
                //lsb_ret
            }
        }
        catch (Exception ex)
        {

        }

        return success;
    }

    public async Task<byte[]> StopRecord()
    {
        int lu_errcode;

        int lu_cch = 0;

        string lsb_ret = "";

        try
        {
            lu_errcode = WindowsNative.mciSendString("pause WaveDump", "", 0, IntPtr.Zero);
        }
        catch (Exception ex)
        {

        }

        if (StorageService.DirectoryExist(StorageService.DirectoryBase, "Temp"))
        {

        }
        else
        {
            StorageService.DirectoryCreate(StorageService.DirectoryBase, "Temp");
        }

        var file = Path.Combine("Temp", $"{DateTime.Now.ToDateTimeTicks()}.wav");

        var ps_SoundLocation = StorageService.SubPath(StorageService.DirectoryBase, file);

        WindowsNative.mciSendString("save WaveDump " + ps_SoundLocation, "", 0, IntPtr.Zero);

        WindowsNative.mciSendString("close WaveDump", "", 0, IntPtr.Zero);

        if (StorageService.TryGetBytes(StorageService.DirectoryBase, file, out byte[] bytes, out string errMsg))
        {

        }

        return bytes;
    }
}

internal class WindowsDownloadService : IDownloadService
{
    DownloadColumns downloadColumns = null;

    SeasonTask downloadTask = null;

    public string Download(string url)
    {
        var id = DateTime.Now.ToDateTimeSeconds();

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

                downloadColumns.Speed = (downloadColumns.Already - preAlready) * 10;

                preAlready = downloadColumns.Already;
            }
            else
            {

            }

            downloadColumns.Progress = (long)percent;

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

    public void DownloadSave(string directory, string name, byte[] bytes)
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

internal class WindowsStoreService : IStoreService
{
    public async Task<Product> Query(string storeId)
    {
        var product = new Product();

        string[] filterList = new string[] { "Consumable", "Durable", "UnmanagedConsumable" };

        var addOns = await WindowsApp.StoreContext.GetAssociatedStoreProductsAsync(filterList);

        if (addOns.ExtendedError != null)
        {
            if (addOns.ExtendedError.HResult == IAP_E_UNEXPECTED)
            {
                product.Message = "This sample has not been properly configured.";
            }
            else
            {
                product.Message = $"ExtendedError: {addOns.ExtendedError.Message}";
            }
        }
        else if (addOns.Products.Count == 0)
        {
            var description = "Add-Ons";

            product.Message = $"No configured {description} found for this Store Product.";
        }
        else
        {
            var addOn = addOns.Products.Values.FirstOrDefault(pro => pro.StoreId == storeId);

            if (addOn is null)
            {
                product.Message = $"Can't find {storeId}.";
            }
            else
            {
                product.StoreId = storeId;
                product.Title = addOn.Title;
                product.Type = addOn.ProductKind;
                product.Price = addOn.Price.FormattedPrice;
                product.InCollection = addOn.IsInUserCollection;
            }
        }

        return product;
    }

    const int IAP_E_UNEXPECTED = unchecked((int)0x803f6107);

    public async Task<string> Purchase(string storeId, Action<string> onResult)
    {
        var result = await WindowsApp.StoreContext.RequestPurchaseAsync(storeId);

        var message = "";

        if (result.ExtendedError is null)
        {
            if (result.Status is StorePurchaseStatus.AlreadyPurchased)
            {
                message = $"You already bought this AddOn.";
            }
            else if (result.Status is StorePurchaseStatus.Succeeded)
            {
                message = storeId;
            }
            else if (result.Status is StorePurchaseStatus.NotPurchased)
            {
                message = $"Product was not purchased, it may have been canceled.";
            }
            else if (result.Status is StorePurchaseStatus.NetworkError)
            {
                message = $"Product was not purchased due to a network error.";
            }
            else if (result.Status is StorePurchaseStatus.ServerError)
            {
                message = $"Product was not purchased due to a server error.";
            }
            else
            {
                message = $"Product was not purchased due to an unknown error.";
            }
        }
        else
        {
            if (result.ExtendedError.HResult is IAP_E_UNEXPECTED)
            {
                message = "This sample has not been properly configured.";
            }
            else
            {
                message = $"ExtendedError: {result.ExtendedError.Message}";
            }
        }

        return message;
    }

    public async Task<string> Review(string product)
    {
        var result = "";

        try
        {
            await Launcher.OpenAsync(new Uri($"ms-windows-store://review/?ProductId={product}"));

            //var review = await StoreContext.RequestRateAndReviewAppAsync();
            //result = review.Status switch
            //{
            //    StoreRateAndReviewStatus.Succeeded => "Success",
            //    StoreRateAndReviewStatus.CanceledByUser => "Cancel",
            //    StoreRateAndReviewStatus.NetworkError => "Error",
            //    StoreRateAndReviewStatus.Error => "Error"
            //};
        }
        catch (Exception ex)
        {

        }

        return result;
    }

    public async Task<(int version, string desc)> CheckForUpdates()
    {
        var version = 0;
        var desc = "";

        var context = StoreContext.GetDefault();
        var updates = await context.GetAppAndOptionalStorePackageUpdatesAsync();

        if (updates?.Count > 0)
        {
            var package = updates[0].Package;
            version = package.Id.Version.Build;
            desc = package.Description;
        }

        return (version, desc);
    }
}
