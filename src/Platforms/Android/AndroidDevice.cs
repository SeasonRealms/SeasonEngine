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

using Season.Platforms.Shared.LinuxAndroid;

namespace Season.Platforms.Android;

public enum ActivityResult
{
    SaveFile = 10001,
    FilePicker = 10002
}

internal class AndroidDeviceCore : IDeviceCore
{
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
        return null;
    }

    public bool LoadFileExists(string res)
    {
        var list = AndroidApp.MainActivity.BaseContext.Assets.List("");

        return list.Contains(res);
    }

    public System.IO.Stream LoadFile(string res)
    {
        return AndroidApp.MainActivity.BaseContext.Assets.Open(res);

        //Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        //Environment.ProcessPath;
        //Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    }

    public bool IsDarkMode()
    {
        var uiModeFlags = Application.Context.Resources.Configuration.UiMode & UiMode.NightMask;

        return uiModeFlags == UiMode.NightYes;
    }

    public async Task<bool> RequestPermissionAsync(string[] permissions)
    {
        tcsPermissions = new TaskCompletionSource<bool>();

        // Verify that all required Media permissions have been granted.

        if (permissions.Any(permission => AndroidX.Core.Content.ContextCompat.CheckSelfPermission(AndroidApp.MainActivity, permission) != Permission.Granted))
        {
            AndroidX.Core.App.ActivityCompat.RequestPermissions(AndroidApp.MainActivity, permissions, 1);
        }
        else
        {
            // Permissions have been granted. 
            tcsPermissions.TrySetResult(true);
        }

        return await tcsPermissions.Task;
    }
}

internal class AndroidMediaPlayer : IMediaPlayer
{
    MediaPlayer MusicPlayer = null;

    MediaPlayer SoundPlayer = null;

    public void PlayMedia(string type, string id, string vol)
    {
        if (MusicPlayer == null || SoundPlayer == null)
        {
            MusicPlayer = new MediaPlayer();

            SoundPlayer = new MediaPlayer();
        }

        var mediaPlayer = type is "Music" ? MusicPlayer : SoundPlayer;

        mediaPlayer.Reset();

        mediaPlayer.SetDataSource(id);

        if (vol?.Length > 0)
        {
            var volume = float.Parse(vol) / 100;

            mediaPlayer.SetVolume(volume, volume);
        }

        mediaPlayer.Prepare();

        mediaPlayer.Start();
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

    public void Pause()
    {
        MusicPlayer?.Pause();

        SoundPlayer?.Pause();
    }

    public void Resume()
    {
        MusicPlayer?.Start();

        SoundPlayer?.Start();
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
        intent.SetType(FileMimeTypes.All);
        intent.PutExtra(Intent.ExtraAllowMultiple, multiple);

        var pickerIntent = Intent.CreateChooser(intent, "Select file");

        try
        {
            tcsPickFiles = new TaskCompletionSource<Uri[]>();

            AndroidApp.MainActivity.StartActivityForResult(pickerIntent, (int)ActivityResult.FilePicker);

            var uris = await tcsPickFiles.Task;

            foreach (var uri in uris)
            {
                var cursor = AndroidCommon.DownloadQueryCursor(uri, null, null);

                cursor.MoveToNext();

                var index = cursor.GetColumnIndex(MediaStore.Files.IFileColumns.MimeType);

                var mimeType = cursor.GetString(index);

                var ext = "." + mimeType.Replace("image/", "");

                //image/png
                //image/jpeg

                System.IO.Stream stream = null;

                if (open)
                {
                    stream = AndroidApp.MainActivity.ContentResolver.OpenInputStream(uri);
                }
                else
                {

                }

                var taskFile = new TaskFile()
                {
                    Name = uri?.ToString(),
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
            return null;
        }

        return taskFiles;
    }

    public TaskCompletionSource<Uri> tcsSaveFile = null;

    public async Task<string> SaveFile(string fileName, System.IO.Stream stream, CancellationToken cancellationToken)
    {
        fileName = Path.GetFileName(fileName);

        var result = "";

        if (!OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            var status = await Permissions.RequestAsync<Permissions.StorageWrite>().WaitAsync(cancellationToken).ConfigureAwait(false);

            if (status is not PermissionStatus.Granted)
            {
                throw new PermissionException("Storage permission is not granted.");
            }
        }

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

        tcsSaveFile = new TaskCompletionSource<Uri>();

        AndroidApp.MainActivity.StartActivityForResult(intent, (int)ActivityResult.SaveFile);

        var uri = await tcsSaveFile.Task;

        var parcelFileDescriptor = Application.Context.ContentResolver?.OpenFileDescriptor(uri, "wt");

        var fileOutputStream = new Java.IO.FileOutputStream(parcelFileDescriptor?.FileDescriptor);

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
        DeviceServices.Download.DownloadNew(null, name);

        DeviceServices.Download.DownloadSave(null, name, bytes);

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

    public async Task<bool> StartRecord()
    {
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

    public void DownloadSave(string directory, string name, byte[] bytes)
    {
        if (downloadUri == null)
        {
            DownloadNew(directory, name);
        }

        using (var output = AndroidApp.MainActivity.ContentResolver.OpenOutputStream(downloadUri, "wa"))
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

