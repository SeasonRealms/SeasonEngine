// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
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

        var file = Path.Combine(location, res); //Path.Combine(location, "Resources", "Raw", res);

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
    // Guards the legacy mplayer fallback state only; LinuxAudioPlayer keeps its own
    // synchronization and the two locks never nest.
    readonly object _legacySync = new();

    System.Diagnostics.Process musicPlayer = null;

    public bool IsPlaying
    {
        get
        {
            if (LinuxAudioPlayer.IsPlaying)
            {
                return true;
            }

            lock (_legacySync)
            {
                return LegacyMusicAlive();
            }
        }
    }

    // Raw source path last handed to the music channel by PlayMedia. The sound channel
    // runs as an untracked short-lived process, so IsPlayingFile resolves music only.
    string CurrentMusicFile = null;

    public bool IsPlayingFile(string fileName)
    {
        fileName = NormalizePath(fileName);

        if (LinuxAudioPlayer.IsPlayingFile(fileName))
        {
            return true;
        }

        lock (_legacySync)
        {
            // mplayer runs as a child process; HasExited tells whether it is still playing.
            return LegacyMusicAlive() && MediaPlayerFiles.IsSame(CurrentMusicFile, fileName);
        }
    }

    public void PlayMedia(string type, string id, string vol)
    {
        int volume = ParseVolume(vol);

        id = NormalizePath(id);

        new Task(() =>
        {
            // Preferred path: in-process decode + SDL3 output, so no external player
            // has to be installed and volume/pause control actually works. Returns
            // false when the SDL audio stack or the decoder cannot serve this file.
            if (LinuxAudioPlayer.TryPlay(type, id, volume))
            {
                if (type is "Music")
                {
                    // Tear down a leftover mplayer from an earlier fallback so the two
                    // backends never play at once.
                    KillLegacyMusic();
                }

                return;
            }

            if (type is "Music")
            {
                // The child process cannot adjust the managed stream; stop managed
                // music so the fallback is not layered on top of it.
                LinuxAudioPlayer.StopMusic();
            }

            PlayLegacy(type, id, vol);
        }).Start();
    }

    static int ParseVolume(string vol)
    {
        return int.TryParse(vol, NumberStyles.Integer, CultureInfo.InvariantCulture, out int volume)
            ? Math.Clamp(volume, 0, 100)
            : 100;
    }

    /// <summary>
    /// Game code written for XNA on Windows hands over Windows-style separators
    /// (e.g. "Sound\Move.wav"); on Linux a backslash is a regular file-name
    /// character, so paths must be normalized before lookup and matching.
    /// </summary>
    static string NormalizePath(string path) => path?.Replace('\\', '/')!;

    /// <summary>
    /// Legacy mplayer child-process playback, kept as the fallback for systems where
    /// the SDL audio stack or the managed decoders are unavailable.
    /// </summary>
    void PlayLegacy(string type, string id, string vol)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo("mplayer", new string[] { "-volume", vol, id });
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;

        try
        {
            if (type is "Music")
            {
                lock (_legacySync)
                {
                    KillLegacyMusicLocked();

                    CurrentMusicFile = id;

                    musicPlayer = System.Diagnostics.Process.Start(startInfo);
                }
            }
            else
            {
                System.Diagnostics.Process.Start(startInfo);
            }
        }
        catch (Exception ex)
        {
            // Thrown when mplayer is not installed; log it instead of surfacing an
            // unobserved task exception.
            DeviceServices.BaseApp?.AddLog(LogType.None, $"{DateTime.UtcNow} [LinuxMediaPlayer] mplayer fallback failed: {ex.Message}");
        }
    }

    bool LegacyMusicAlive()
    {
        try
        {
            return musicPlayer != null && !musicPlayer.HasExited;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    void KillLegacyMusic()
    {
        lock (_legacySync)
        {
            KillLegacyMusicLocked();
        }
    }

    void KillLegacyMusicLocked()
    {
        try
        {
            if (musicPlayer != null && !musicPlayer.HasExited)
            {
                musicPlayer.Kill();
            }
        }
        catch (Exception)
        {
            // The process may have exited between the check and the kill.
        }
    }

    public void SetVolume(int music, int sound)
    {
        LinuxAudioPlayer.SetVolume(music, sound);
    }

    public void Pause()
    {
        LinuxAudioPlayer.Pause();
    }

    public void Resume()
    {
        LinuxAudioPlayer.Resume();
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

    public Task<List<TaskFile>> PickFiles(FileType fileType, string[] exts, bool multiple, bool open)
    {
        List<TaskFile> taskFiles = null;

        //Gtk.Application.Init();

        // Pass a null parent: the dialog does not need one, and a parent window that
        // is created but never shown only resurfaces during teardown (the old code
        // literally showed it) as a stuck empty window on screen.
        var dialog = new FileChooserDialog("FileChooser", null, FileChooserAction.Open, Gtk.Stock.Cancel, Gtk.ResponseType.Cancel, Gtk.Stock.Open, Gtk.ResponseType.Accept);
        //dialog.SelectMultiple = true;

        var filter = new Gtk.FileFilter();
        filter.AddPattern("*.*"); // .AddMimeType("image/jpeg");
        dialog.Filter = filter;

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

        // gtk_dialog_run owns a nested main loop and hides the dialog on return.
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

        // The engine's main loop is SDL/Vulkan and never pumps GTK, so now that the
        // nested loop inside Run() has exited, every window op queued from here on
        // (including the destruction above) would wait forever: the dialog would
        // stay on screen and ignore clicks, including the title-bar close button.
        // Drain the pending events by hand. EventsPending() is checked first
        // because RunIteration() would block on an empty queue.
        while (Gtk.Application.EventsPending())
        {
            Gtk.Application.RunIteration();
        }

        GC.Collect();

        return Task.FromResult(taskFiles!);
    }

    public Task<string> SaveFile(string fileName, Stream stream, CancellationToken cancellationToken)
    {
        // Same model as PickFiles: a null parent instead of a stray window, and the
        // GTK event queue drained by hand after Run() because the engine's SDL main
        // loop never pumps GTK. Overwrite confirmation is GTK's built-in one
        // (DoOverwriteConfirmation); the old hand-rolled SelectionChanged message box
        // was unfinished and would have stranded a second stuck window anyway.
        var dialog = new FileChooserDialog("FileChooser", null, FileChooserAction.Save, Gtk.Stock.Cancel, Gtk.ResponseType.Cancel, Gtk.Stock.Save, Gtk.ResponseType.Accept);

        if (!string.IsNullOrWhiteSpace(fileName))
        {
            dialog.CurrentName = System.IO.Path.GetFileName(fileName);
        }

        dialog.DoOverwriteConfirmation = true;

        var filter = new Gtk.FileFilter();
        filter.AddPattern("*.*");
        dialog.Filter = filter;

        // gtk_dialog_run owns a nested main loop and hides the dialog on return.
        var result = dialog.Run();

        var path = result is (int)Gtk.ResponseType.Accept ? dialog.Filename : null;

        dialog.Destroy();

        GC.SuppressFinalize(dialog);

        // Drain the pending GTK events, same as PickFiles: without this the teardown
        // above never reaches the display server and a stuck window stays on screen.
        while (Gtk.Application.EventsPending())
        {
            Gtk.Application.RunIteration();
        }

        GC.Collect();

        // Cancelled: report "not saved" with an empty string (Android semantics)
        // rather than an exception, so callers can test with IsNullOrWhiteSpace.
        if (string.IsNullOrWhiteSpace(path) || stream == null)
        {
            return Task.FromResult("");
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Write only after the dialog is fully torn down, so a failed write cannot
        // strand a GTK window. File handling mirrors WindowsDevice.SaveFile.
        if (stream.CanSeek)
        {
            stream.Seek(0, SeekOrigin.Begin);
        }

        using (var target = new FileStream(path, FileMode.OpenOrCreate))
        {
            target.SetLength(0);

            stream.CopyTo(target);
        }

        return Task.FromResult(path);
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
    [DllImport("libX11.so.6")]
    static extern IntPtr XOpenDisplay(string? name);
    [DllImport("libX11.so.6")]
    static extern int XCloseDisplay(IntPtr display);
    [DllImport("libX11.so.6")]
    static extern IntPtr XDefaultRootWindow(IntPtr display);
    [DllImport("libX11.so.6")]
    static extern int XDisplayWidth(IntPtr display, int screen);
    [DllImport("libX11.so.6")]
    static extern int XDisplayHeight(IntPtr display, int screen);

    public Task<bool> StartRecord()
    {
        throw new NotImplementedException();
    }

    public Task<byte[]> StopRecord()
    {
        throw new NotImplementedException();
    }

    public Task<INativeImageDecoder?> CaptureScreen()
    {
        try
        {
            int screenW, screenH;

            var display = XOpenDisplay(null);
            if (display != IntPtr.Zero)
            {
                int screen = 0; // XDefaultScreen
                screenW = XDisplayWidth(display, screen);
                screenH = XDisplayHeight(display, screen);
                XCloseDisplay(display);
            }
            else
            {
                var gdkScreen = Gdk.Screen.Default;
                screenW = gdkScreen.Width;
                screenH = gdkScreen.Height;
            }

            using var bmp = new Bitmap(screenW, screenH);
            using var g = global::System.Drawing.Graphics.FromImage(bmp);
            g.CopyFromScreen(0, 0, 0, 0, new global::System.Drawing.Size(screenW, screenH));

            var rect = new Rectangle(0, 0, screenW, screenH);
            var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            byte[] pixels = new byte[screenW * screenH * 4];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            bmp.UnlockBits(data);

            return Task.FromResult<INativeImageDecoder?>(new NativeImageData(screenW, screenH, pixels));
        }
        catch (Exception ex)
        {
            DeviceServices.BaseApp.AddLog(LogType.Error, $"{DateTime.UtcNow} CaptureScreen {ex}");
            return Task.FromResult<INativeImageDecoder?>(null);
        }
    }

    public Task<INativeImageDecoder?> CaptureApp()
    {
        var tcs = new TaskCompletionSource<INativeImageDecoder?>();
        BaseApp.CaptureAppTcs = tcs;
        return tcs.Task;
    }

    public byte[] DecodeToWavPcm16(string path)
    {
        // No media decoder is wired up on Linux yet; FFmpeg or GStreamer would be the implementation.
        throw new NotImplementedException($"DecodeToWavPcm16 is not implemented on Linux: {path}");
    }
}

internal class LinuxDownloadService : IDownloadService
{
    // Exports on Linux go through the GTK save dialog instead of a fixed download
    // folder: there is no dependable "Downloads" location here. XDG defines one, but
    // plenty of systems - WSL and server installs in particular - have neither a
    // configured user-dirs.dirs nor an existing ~/Downloads (xdg-user-dir then falls
    // back to $HOME, and the desktop directory is just as often absent). A save dialog
    // mirrors the MacCatalyst DownloadSave behavior and lets the user pick a location
    // that actually exists - under WSL that can even be a Windows drive under /mnt,
    // where silently writing into the WSL home would leave the file invisible to them.
    public void DownloadSave(string category, string name, byte[] bytes, bool openFolder)
    {
        // Runs on the rendering thread - the only thread that owns the GTK main context
        // (RunCore's Gtk.Application.Init) - and blocks inside the dialog's nested loop
        // until the user decides, the same model as LinuxFileService.PickFiles/SaveFile.
        // Do not marshal this to the thread pool: Gtk dialog.Run is not thread-safe and
        // every caller is a click handler running on the render thread.
        try
        {
            using var stream = new MemoryStream(bytes);

            // Cancelled saves come back as an empty path (LinuxFileService semantics):
            // dismissing the dialog is a normal outcome and must not read as a failure.
            _ = DeviceServices.File.SaveFile(name, stream, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // A failed export must never tear down the app: this is called from click
            // handlers and the render/update loop.
            DeviceServices.BaseApp?.AddLog(LogType.Error, $"{DateTime.UtcNow} [DownloadSave] save dialog for {name} failed: {ex}");
        }

        // openFolder is intentionally ignored: the file lands wherever the user pointed
        // the dialog, so there is no separate folder to reveal.
    }

    public void DownloadDel(string category, string name)
    {
        // Dialog-based exports have no staging file to delete: each save starts from the
        // bytes the caller passes in, and overwrite confirmation is the save dialog's own
        // DoOverwriteConfirmation. Kept as a no-op so the shared "DownloadDel +
        // DownloadSave" export pattern stays cross-platform.
    }

    public void DownloadNew(string category, string name)
    {
        // Nothing is staged on disk before the save dialog runs, so there is no file or
        // directory to prepare (counterpart of DownloadDel above).
    }

    public void DownloadUpdate(string category, string name, string namenew)
    {
        // No staging directory exists and no caller renames exported files on Linux;
        // keep the contract quiet instead of throwing from a click handler.
    }

    public string Download(string url)
    {
        // Background url downloads are not wired up on Linux: nothing in the app calls
        // this, and exports go through DownloadSave's save dialog. Fail explicitly
        // rather than pretending a download started.
        throw new NotImplementedException("Background url downloads are not supported on Linux; exports use DownloadSave.");
    }

    public DownloadColumns DownloadQuery(string requestId, float time)
    {
        // Counterpart of Download(url): no download manager state exists on Linux.
        throw new NotImplementedException("Background url downloads are not supported on Linux; exports use DownloadSave.");
    }

    public void DownloadCancel(string requestId)
    {
        // Counterpart of Download(url): no download manager state exists on Linux.
        throw new NotImplementedException("Background url downloads are not supported on Linux; exports use DownloadSave.");
    }
}

internal class LinuxStoreService : IStoreService
{
    public async Task<(List<Product>, string)> Query()
    {
        throw new PlatformNotSupportedException();
    }

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
        // Free on Linux: there is no store to buy from. The premium entitlement is
        // seeded at startup (Foundation.Init) and Query(string) reports the product
        // as owned, so the purchase UI never reaches here on the happy path; if it
        // ever does, report a message instead of throwing - AINotice awaits this from
        // an async void click handler, where an escaping exception would abort the app.
        return Task.FromResult("Purchases are not available on this platform.");
    }

    public async Task<string> Review(string product, string url)
    {
        // url is optional and AINotice passes none on platforms without a store;
        // OpenLink would spawn xdg-open with a null argument, so skip instead.
        if (string.IsNullOrWhiteSpace(url))
        {
            return "";
        }

        await DeviceServices.File.OpenLink(url);

        return "";
    }

    public async Task<(int version, string desc)> CheckForUpdates()
    {
        var version = 0;
        var desc = "";

        return (version, desc);
    }
}

