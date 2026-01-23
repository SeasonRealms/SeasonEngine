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

namespace Season.Platforms.Shared.Apple;

internal class AppleDeviceCore : IDeviceCore
{
    public Basic.Platform Platform => Basic.Platform.None;

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
        var status = PHPhotoLibrary.AuthorizationStatus;

        bool authotization = status == PHAuthorizationStatus.Authorized;

        if (!authotization)
        {
            authotization = await PHPhotoLibrary.RequestAuthorizationAsync() == PHAuthorizationStatus.Authorized;
        }
        return authotization;
    }
}

internal class AppleMediaPlayer : IMediaPlayer
{
    protected AVPlayer MusicPlayer = null;

    protected AVPlayer SoundPlayer = null;

    public void PlayMedia(string type, string id, string vol)
    {
        if (MusicPlayer == null || SoundPlayer == null)
        {
            MusicPlayer = new AVPlayer();
            SoundPlayer = new AVPlayer();
        }

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
            var alertController = new UIViewController();

            var width = DeviceServices.BaseApp.DeviceResolution.X - 50;

            var textView = new UITextView(new CGRect(new CGPoint(20, 20), new CGSize(width, 350)));

            textView.Text = text;

            textView.Editable = true;

            //textView.BecomeFirstResponder();

            alertController.View.AddSubview(textView);

            var btnOK = new UIButton(new CGRect(200, 400, 80, 40));

            btnOK.BackgroundColor = UIColor.Gray;

            btnOK.SetTitle("OK", UIControlState.Normal);

            btnOK.TouchDown += (s, e) =>
            {
                tcs.TrySetResult(textView.Text);

                alertController.DismissViewController(true, () => { });
            };

            alertController.View.AddSubview(btnOK);

            var btnCancel = new UIButton(new CGRect(100, 400, 80, 40));

            btnCancel.BackgroundColor = UIColor.Gray;

            btnCancel.SetTitle("Cancel", UIControlState.Normal);

            btnCancel.TouchDown += (s, e) =>
            {
                tcs.TrySetResult(null);

                alertController.DismissViewController(true, () => { });
            };

            alertController.View.AddSubview(btnCancel);

            var parentController = Microsoft.Maui.ApplicationModel.Platform.GetCurrentUIViewController();

            parentController.PresentViewController(alertController, true, () =>
            {
                tcs.TrySetResult(textView.Text);
            });
        });

        return await tcs.Task;
    }
}

internal class AppleFileService : IFileService
{
    UIDocumentPickerViewController? documentPickerViewController;

    TaskCompletionSource<string>? taskCompetedSource;

    public async Task<string> PickFolder()
    {
        return null;
    }

    public async Task<List<TaskFile>> PickFiles(FileType fileType, string[] exts, bool multiple, bool open)
    {
        var allowedUtis = new string[]
        {
            MobileCoreServices.UTType.Content,
            MobileCoreServices.UTType.Item,
            "public.data"
        };

        var tcs = new TaskCompletionSource<IEnumerable<NSUrl>>();

        using var documentPicker = new UIDocumentPickerViewController(allowedUtis, UIDocumentPickerMode.Import)
        {
            //DirectoryUrl = NSUrl.FromString("/")
        };

        documentPicker.AllowsMultipleSelection = true;

        documentPicker.Delegate = new PickerDelegate
        {
            PickHandler = urls =>
            {
                tcs.TrySetResult(urls);
                //GetFileResults(urls, tcs)
            }
        };

        if (documentPicker.PresentationController != null)
        {
            documentPicker.PresentationController.Delegate =
                new UIPresentationControllerDelegate(() =>
                {
                    tcs.TrySetResult(null);
                })
                {

                };
            //() => GetFileResults(null, tcs)
        }

        var parentController = Microsoft.Maui.ApplicationModel.Platform.GetCurrentUIViewController();

        parentController.PresentViewController(documentPicker, true, null);

        var files = (await tcs.Task).NullToEmptyArray();

        var taskFiles = new List<TaskFile>();

        foreach (var file in files)
        {
            var ext = System.IO.Path.GetExtension(file.Path).ToLower();

            FileStream stream = null;

            if (open)
            {
                stream = System.IO.File.OpenRead(file.Path);
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

        //var option = new PickOptions()
        //{
        //};
        //var result = await FilePicker.PickMultipleAsync(option);
    }

    public async Task<string> SaveFile(string fileName, Stream stream, CancellationToken cancellationToken)
    {
        var result = "";

        var fileManager = NSFileManager.DefaultManager;

        fileName = Path.GetFileName(fileName);

        var fileUrl = Path.Combine(Path.GetTempPath(), fileName);

        var streamTarget = System.IO.File.OpenWrite(fileUrl);

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

        var currentViewController = Microsoft.Maui.ApplicationModel.Platform.GetCurrentUIViewController();

        if (currentViewController is null)
        {
            //something error
        }
        else
        {
            currentViewController.PresentViewController(documentPickerViewController, true, null);
        }

        var usrl = await taskCompetedSource.Task;

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
        //(ok, err) => Console.WriteLine("photo updated successfully: {0}", ok));

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

    public void OpenFolder(string name)
    {

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
        dismissHandler?.Invoke();
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

    //AVAudioRecorder recorder;

    //TaskCompletionSource<byte[]> tcsRecord = null;

    public async Task<bool> StartRecord()
    {
        bool success = false;

        //var tcs = new TaskCompletionSource<bool>();

        //var session = AVAudioSession.SharedInstance();

        //session.RequestRecordPermission((granted) =>
        //{
        //    if (granted)
        //    {
        //        if (session.SetCategory(AVAudioSession.CategoryRecord, out NSError error))
        //        {
        //            if (session.SetActive(true, out error))
        //            {
        //                var fileName = $"Record-{DateTime.Now.ToSeasonDateTimeTicks()}.wav";

        //                var audioFile = Path.Combine(Path.GetTempPath(), fileName);

        //                var audioFilePath = NSUrl.FromFilename(audioFile);

        //                var audioSettings = new AudioSettings
        //                {
        //                    SampleRate = 16000, //44100,
        //                    NumberChannels = 1,
        //                    AudioQuality = AVAudioQuality.High,
        //                    Format = AudioToolbox.AudioFormatType.LinearPCM  //.MPEG4AAC,
        //                };

        //                recorder = AVAudioRecorder.Create(audioFilePath, audioSettings, out error);

        //                if (error == null)
        //                {
        //                    if (recorder.PrepareToRecord())
        //                    {
        //                        recorder.FinishedRecording += (s, e) =>
        //                        {
        //                            var bytes = System.IO.File.ReadAllBytes(audioFile);

        //                            tcsRecord.TrySetResult(bytes);
        //                        };

        //                        if (recorder.Record())
        //                        {
        //                            tcs.TrySetResult(true);
        //                        }
        //                        else
        //                        {

        //                        }
        //                    }
        //                    else
        //                    {

        //                    }
        //                }
        //                else
        //                {
        //                    //error.LocalizedDescription
        //                }

        //                recorder.Dispose();

        //                recorder = null;
        //            }
        //            else
        //            {

        //            }
        //        }
        //        else
        //        {
        //            //error.LocalizedDescription
        //        }
        //    }
        //    else
        //    {
        //        //Need permission
        //    }

        //    tcs.TrySetResult(false);
        //});

        //return await tcs.Task;

        return success;
    }

    public async Task<byte[]> StopRecord()
    {
        //tcsRecord = new TaskCompletionSource<byte[]>();

        //recorder.Stop();

        //recorder.Dispose();

        //recorder = null;

        //var bytes = await tcsRecord.Task;

        //tcsRecord = null;

        //return bytes;

        return null;
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
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Downloads");
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

    public void DownloadSave(string directory, string name, byte[] bytes)
    {
        if (directory.IsNullOrWhiteSpace())
        {
            directory = DownloadDir;
        }
        else
        {
            directory = Path.Combine(DownloadDir, directory);
        }

        if (Directory.Exists(directory))
        {

        }
        else
        {
            Directory.CreateDirectory(directory);
        }

        var file = Path.Combine(directory, name);

        using (var fs = System.IO.File.Open(file, FileMode.Append))
        {
            //if (offset == null)
            //{

            //}
            //else
            //{
            //    fs.Seek((int)offset, SeekOrigin.Begin);
            //}

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
