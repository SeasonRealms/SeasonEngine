// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Platforms.Shared.Apple;

internal class AppleDownloadService : IDownloadService
{
    DownloadColumns downloadColumns = null;

    SeasonTask downloadTask = null;

    static string DownloadDir
    {
        get
        {
            // Downloads are staged inside the app container. Writing into the real ~/Downloads folder
            // (the container's Data/Downloads symlink) would require the entitlement
            // com.apple.security.files.downloads.read-write, which Apple does not allow, so
            // user-visible saves go through the system save panel in DownloadSave instead.
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

    public void DownloadSave(string directory, string name, byte[] bytes, bool openFolder)
    {
#if MACCATALYST
        // App Store compliance: the real ~/Downloads folder cannot be written to without the
        // entitlement com.apple.security.files.downloads.read-write, which Apple does not allow.
        // Hand the bytes to the system save panel (UIDocumentPicker ExportToService) instead: it
        // runs on com.apple.security.files.user-selected.read-write and lets the user pick the
        // destination folder and file name explicitly.
        Task.Run(async () =>
        {
            try
            {
                using var stream = new MemoryStream(bytes);

                await DeviceServices.File.SaveFile(name, stream, CancellationToken.None);
            }
            catch (Exception ex)
            {
                // Dismissing the save panel is a normal outcome and must not read as a failure.
                if (ex.Message is not "Cancelled")
                    DeviceServices.BaseApp?.AddLog(LogType.Error, $"{DateTime.UtcNow} [DownloadSave] save panel for {name} failed: {ex}");
            }
        });
#else
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
            // render/update loop.
            DeviceServices.BaseApp?.AddLog(LogType.Error, $"{DateTime.UtcNow} [DownloadSave] file={file} failed err={ex}");

            return;
        }

        // openFolder is intentionally ignored: iOS has no file manager UI to reveal a folder in.
#endif
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
