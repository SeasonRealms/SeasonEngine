// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Gtk;

using Season.Platforms.Shared.LinuxAndroid;

namespace Season.Platforms.Linux;

internal class LinuxDeviceCore : IDeviceCore
{
    public Season.Basic.Platform Platform { get; set; } = Season.Basic.Platform.Linux;

    public Basic.Channel Channel { get; set; } = Basic.Channel.None;

    public Basic.Orientation Orientation { get; set; } = Basic.Orientation.LandscapeLeft;

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

    public string LoadFilePath(string res)
    {
        var location = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

        var file = Path.Combine(location, "Resources", "Raw", res);

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
        return false;
    }

    public async Task<bool> RequestPermissionAsync(string[] permissions)
    {
        return await Task.FromResult(true);
    }
}

internal class LinuxMediaPlayer : IMediaPlayer
{
    System.Diagnostics.Process musicPlayer = null;

    public void PlayMedia(string type, string id, string vol)
    {
        new Task(() =>
        {
            if (type is "Music")
            {
                if (musicPlayer != null)
                {
                    musicPlayer.Kill();
                }
                var startInfo = new System.Diagnostics.ProcessStartInfo("mplayer", new string[] { "-volume", vol, id });
                startInfo.UseShellExecute = false;
                startInfo.CreateNoWindow = true;
                startInfo.RedirectStandardOutput = true;
                startInfo.RedirectStandardError = true;

                musicPlayer = System.Diagnostics.Process.Start(startInfo);

                musicPlayer.WaitForExit();
            }
            else
            {
                var startInfo = new System.Diagnostics.ProcessStartInfo("mplayer", new string[] { "-volume", vol, id });
                startInfo.UseShellExecute = false;
                startInfo.CreateNoWindow = true;
                startInfo.RedirectStandardOutput = true;
                startInfo.RedirectStandardError = true;

                var soundPlayer = System.Diagnostics.Process.Start(startInfo);

                soundPlayer.WaitForExit();
            }
        }).Start();
    }

    public void SetVolume(int music, int sound)
    {

    }

    public void Pause()
    {

    }

    public void Resume()
    {

    }
}

internal class LinuxDialogService : IDialogService
{
    public async Task<string> ShowMessage(string title, string desc, string[] buttons, string text)
    {
        var tcs = new TaskCompletionSource<string>();

        Gtk.Application.Init();

        var window = new Gtk.Window(title);

        var vbox = new Gtk.Box(Gtk.Orientation.Vertical, 5);
        window.Add(vbox);

        var hbar = new Gtk.HeaderBar();

        hbar.Title = title;

        hbar.Subtitle = desc;

        var select = new Gtk.Button(); select.Label = buttons?.Length > 0 ? buttons?[0] : "OK";

        hbar.PackEnd(select);

        window.Titlebar = hbar;

        var grid = new Gtk.Grid();
        grid.SetSizeRequest(600, 400);

        grid.RowHomogeneous = true;
        grid.ColumnHomogeneous = true;

        grid.ColumnSpacing = 2;
        grid.RowSpacing = 2;

        var scrolledWindow = new Gtk.ScrolledWindow();

        scrolledWindow.SetSizeRequest(580, 350);

        var textView = new Gtk.Label();

        scrolledWindow.Add(textView);

        scrolledWindow.Halign = Gtk.Align.Center;

        scrolledWindow.Valign = Gtk.Align.Center;

        grid.Attach(scrolledWindow, 1, 1, 1, 1);

        textView.SetSizeRequest(580, 350);

        textView.Text = text;

        window.Add(grid);

        vbox.PackStart(grid, true, true, 0);

        select.Clicked += (s, e) =>
        {
            window.Destroy();

            Gtk.Application.Quit();

            GC.SuppressFinalize(window);

            GC.Collect();

            tcs?.SetResult(text);
        };

        window.ShowAll();

        Gtk.Application.Run();

        return await tcs.Task;
    }

    public async Task<string> ShowKeyboard(string title, string desc, string[] buttons, string text)
    {
        var tcs = new TaskCompletionSource<string>();

        //Gtk.Application.Init();

        var window = new Gtk.Window(title);

        var vbox = new Gtk.Box(Gtk.Orientation.Vertical, 5);
        window.Add(vbox);

        var hbar = new Gtk.HeaderBar();

        hbar.Title = title;

        hbar.Subtitle = desc;

        var select = new Gtk.Button(); select.Label = buttons?.Length > 0 ? buttons?[0] : "OK";
        var cancel = new Gtk.Button(); cancel.Label = buttons?.Length > 1 ? buttons?[1] : "Cancel";

        hbar.PackStart(cancel);
        hbar.PackEnd(select);

        window.Titlebar = hbar;

        var grid = new Gtk.Grid();
        grid.SetSizeRequest(600, 400);

        grid.RowHomogeneous = true;
        grid.ColumnHomogeneous = true;

        grid.ColumnSpacing = 2;
        grid.RowSpacing = 2;

        var scrolledWindow = new Gtk.ScrolledWindow();

        scrolledWindow.SetSizeRequest(580, 350);

        var textView = new Gtk.TextView();

        scrolledWindow.Add(textView);

        scrolledWindow.Halign = Gtk.Align.Center;

        scrolledWindow.Valign = Gtk.Align.Center;

        grid.Attach(scrolledWindow, 1, 1, 1, 1);

        var buffer = textView.Buffer;

        buffer.Text = text;

        textView.SetSizeRequest(580, 350);

        textView.WrapMode = Gtk.WrapMode.Word;

        window.Add(grid);

        vbox.PackStart(grid, true, true, 0);

        // Setup buttons callbacks
        cancel.Clicked += (s, e) =>
        {
            window.Destroy();

            Gtk.Application.Quit();

            GC.SuppressFinalize(window);

            GC.Collect();

            tcs.SetResult(null);
            //tcs?.SetCanceled();
        };

        select.Clicked += (s, e) =>
        {
            var text = textView.Buffer.Text.NullToStringTrim();

            window.Destroy();

            Gtk.Application.Quit();

            GC.SuppressFinalize(window);

            GC.Collect();

            tcs?.SetResult(text);
        };

        window.ShowAll();

        Gtk.Application.Run();

        return await tcs.Task;
    }

}

internal class LinuxFileService : IFileService
{
    public async Task<string> PickFolder()
    {
        return null;
    }

    public async Task<List<TaskFile>> PickFiles(FileType fileType, string[] exts, bool multiple, bool open)
    {
        var tcs = new TaskCompletionSource<List<TaskFile>>();

        List<TaskFile> taskFiles = null;

        //Gtk.Application.Init();

        var window = new Gtk.Window("Pick Files");

        var dialog = new FileChooserDialog("FileChooser", window, FileChooserAction.Open, Gtk.Stock.Cancel, Gtk.ResponseType.Cancel, Gtk.Stock.Open, Gtk.ResponseType.Accept);
        //dialog.SelectMultiple = true;

        var filter = new Gtk.FileFilter();
        dialog.Filter = new FileFilter();
        dialog.Filter.AddPattern("*.*"); // .AddMimeType("image/jpeg");

        var preview = new Gtk.Image();
        dialog.PreviewWidget = preview;
        dialog.UpdatePreview += (s, e) =>
        {
            var uri = dialog.PreviewUri;

            if (uri != null && uri.StartsWith("file://"))
            {
                try
                {
                    uri = uri.Replace("file://", "");

                    var pixbuf = new Gdk.Pixbuf(uri, 300, 300); //  .GetFileInfoFinish(uri);

                    preview.Pixbuf = pixbuf;

                    preview.Show();
                }
                catch (Exception ex)
                {
                    preview.Hide();
                }
            }
        };

        var result = dialog.Run();

        if (result is (int)Gtk.ResponseType.Accept)
        {
            taskFiles = new List<TaskFile>();

            FileStream stream = null;

            if (open)
            {
                stream = File.Open(dialog.Filename, FileMode.Open);
            }
            else
            {

            }

            taskFiles.Add(new TaskFile()
            {
                Name = dialog.Filename,
                Ext = System.IO.Path.GetExtension(dialog.Filename).ToLower(),
                Stream = stream
                //Bytes = bytes stream.Result.StreamToBytes();
            });
        }

        dialog.Destroy();

        GC.SuppressFinalize(dialog);

        window.ShowAll();

        window.Close();

        window.Destroy();

        GC.SuppressFinalize(window);

        GC.Collect();

        return taskFiles;
    }

    public Task<string> SaveFile(string fileName, Stream stream, CancellationToken cancellationToken)
    {
        var window = new Gtk.Window("Save Files");

        var dialog = new FileChooserDialog("FileChooser", window, FileChooserAction.Save, Gtk.Stock.Cancel, Gtk.ResponseType.Cancel, Gtk.Stock.Save, Gtk.ResponseType.Accept, "test2.png");
        dialog.CurrentName = "test888.png";

        var name = dialog.Filename;

        dialog.CurrentFolderChanged += (s, e) =>
        {

        };

        dialog.SelectionChanged += (s, e) =>
        {
            var fileName = dialog.Filename;

            if (fileName == name)
            {

            }
            else
            {
                if (File.Exists(dialog.Filename))
                {
                    var message = new Gtk.MessageDialog(window, DialogFlags.DestroyWithParent, MessageType.Question, ButtonsType.OkCancel, "Already exists, overwrite?", Gtk.Stock.Cancel, Gtk.ResponseType.Cancel, Gtk.Stock.Save, Gtk.ResponseType.Accept);

                    var mes = message.Run();

                    if (mes is (int)Gtk.ResponseType.Accept)
                    {

                    }
                    else if (mes is (int)Gtk.ResponseType.Cancel)
                    {
                        dialog.CurrentName = "";

                        dialog.UnselectAll();
                    }

                    message.Destroy();
                }
            }
        };

        var result = dialog.Run();

        if (result is (int)Gtk.ResponseType.Accept)
        {

        }

        dialog.Destroy();

        GC.SuppressFinalize(dialog);

        window.ShowAll();

        window.Close();

        window.Destroy();

        GC.SuppressFinalize(window);

        GC.Collect();

        return null;
    }

    public async Task<string> OpenFile(string name, string category, byte[] bytes)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo(name);

        var player = System.Diagnostics.Process.Start(startInfo);

        player.WaitForExit();

        return "";
    }

    public async Task<bool> OpenLink(string name)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo("xdg-open", new string[] { name });  //google-chrome

        var player = System.Diagnostics.Process.Start(startInfo);

        player.WaitForExit();

        return true;
    }

    public void OpenFolder(string name)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo(name);

        var player = System.Diagnostics.Process.Start(startInfo);

        player.WaitForExit();
    }
}

internal class LinuxGalleryService : IGalleryService
{
    public Task<List<MediaAsset>> MediaGallery()
    {
        throw new NotImplementedException();
    }

    public Task<List<MediaAsset>> MediaGalleryDownloads()
    {
        throw new NotImplementedException();
    }

    public Task<Stream> MediaAsset(MediaAsset mediaAsset)
    {
        throw new NotImplementedException();
    }

    public Task<bool> MediaRemove(MediaAsset[] mediaAssets, bool delEmptyDirectory)
    {
        throw new NotImplementedException();
    }
}

internal class LinuxRecordService : RecordService, IRecordService
{
    public Task<bool> StartRecord()
    {
        throw new NotImplementedException();
    }

    public Task<byte[]> StopRecord()
    {
        throw new NotImplementedException();
    }
}

internal class LinuxDownloadService : IDownloadService
{
    public string Download(string url)
    {
        throw new NotImplementedException();
    }

    public void DownloadCancel(string requestId)
    {
        throw new NotImplementedException();
    }

    public void DownloadDel(string category, string name)
    {
        throw new NotImplementedException();
    }

    public void DownloadNew(string category, string name)
    {
        throw new NotImplementedException();
    }

    public void DownloadSave(string category, string name, byte[] bytes)
    {
        throw new NotImplementedException();
    }

    public void DownloadUpdate(string category, string name, string namenew)
    {
        throw new NotImplementedException();
    }

    public DownloadColumns DownloadQuery(string requestId, float time)
    {
        throw new NotImplementedException();
    }

}

internal class LinuxStoreService : IStoreService
{
    public async Task<Product> Query(string storeId)
    {
        var product = new Product();

        product.StoreId = storeId;
        product.Title = "";
        product.Type = "";
        product.Price = "";
        product.InCollection = true;

        return product;
    }

    public Task<string> Purchase(string product, Action<string> onResult)
    {
        throw new NotImplementedException();
    }

    public async Task<string> Review(string product)
    {
        await DeviceServices.File.OpenLink("http://seasont.com");

        return "";
    }

    public async Task<(int version, string desc)> CheckForUpdates()
    {
        var version = 0;
        var desc = "";

        return (version, desc);
    }
}

