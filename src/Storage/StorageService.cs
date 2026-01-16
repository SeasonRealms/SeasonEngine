// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Storage;

public static class StorageService
{
    static string directoryBase;

    public static string DirectoryBase
    {
        get
        {
            if (String.IsNullOrEmpty(directoryBase))
            {
                if (DeviceServices.Core.Platform is Basic.Platform.Linux)
                {
                    directoryBase = System.Reflection.Assembly.GetEntryAssembly().FullName.Split(',')[0];
                }
                else
                {
                    directoryBase = AppInfo.Name;
                }
            }

            return directoryBase;
        }
    }

    public static string SubPath(string directory, string res)
    {
        var path = Path(directory);

        if (String.IsNullOrEmpty(res))
        {
            
        }
        else
        {
            if (res.EndsWith("/"))
            {
                res = res.Substring(0, res.IndexOf('/'));
            }

            path = System.IO.Path.Combine(path, res);
        }

        //if (EndsInDirectorySeparator(dir))
        //{
        //}
        //else
        //{
        //    dir += System.IO.Path.DirectorySeparatorChar;
        //}

        return path;
    }

    public static bool IsDirectorySeparator(char c)
    {
        return c == System.IO.Path.DirectorySeparatorChar || c == System.IO.Path.AltDirectorySeparatorChar;
    }

    public static bool EndsInDirectorySeparator(string path)
    {
        return path.Length > 0 && IsDirectorySeparator(path[path.Length - 1]);
    }

    public static byte[] LoadBytes(string res)
    {
        var stream = DeviceServices.Core.LoadFile(res);

        return stream.ReadAllBytes();
    }

    public static string LoadText(string res)
    {
        var stream = DeviceServices.Core.LoadFile(res);

        using var reader = new StreamReader(stream);

        var str = reader.ReadToEnd();

        return str;
    }

    public static string LoadAssemblyText(Assembly assembly, string manifest, string res)
    {
        using StreamReader s = new StreamReader(LoadFile(assembly, manifest, res));

        return s.ReadToEnd();
    }

    public static string Path(string directory)
    {
        var path = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        if (string.IsNullOrEmpty(directory))
        {

        }
        else
        {
            path = System.IO.Path.Combine(path, directory);
        }

        return path;
    }

    public static bool DirectoryExist(string directory, string subDir)
    {
        var path = SubPath(directory, subDir);

        return Directory.Exists(path);
    }

    public static string[] SubDirectories(string directory, string subDir)
    {
        var basePath = SubPath(directory, "");

        var relativePath = SubPath(directory, subDir);

        if (!DirectoryExist(directory, subDir))
        {
            return null;
        }

        var dirs = Directory.GetDirectories(relativePath).NullToEmptyArray();

        dirs = dirs.Select(fi => fi.Replace(basePath, "")).NullToEmptyArray();

        for (var i = 0; i < dirs.Length; i++)
        {
            var file = dirs[i];

            if (file.StartsWith("\\") || file.StartsWith("/"))
            {
                dirs[i] = file.Substring(1);
            }
        }

        return dirs;
    }

    public static string[] SubFiles(string directory, string subDir, bool all)
    {
        var basePath = SubPath(directory, "");

        //if (basePath.EndsWith("\\"))
        //{
        //    basePath = basePath.Substring(0, basePath.Length - 2);
        //}

        var relativePath = SubPath(directory, subDir);

        if (!DirectoryExist(directory, subDir))
        {
            return null;
        }

        var files = Directory.GetFiles(relativePath, "", all ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly).NullToEmptyArray();

        files = files.Select(fi => fi.Replace(basePath, "")).NullToEmptyArray();

        for (var i = 0; i < files.Length; i++)
        {
            var file = files[i];

            if (file.StartsWith("\\") || file.StartsWith("/"))
            {
                files[i] = file.Substring(1);
            }
        }

        return files;
    }

    public static void DirectoryCopy(string directory, string from, string to)
    {
        if (!DirectoryExist(directory, from))
        {
            return;
        }

        if (!DirectoryExist(directory, to))
        {
            DirectoryCreate(directory, to);
        }

        var fromDir = SubPath(directory, from);

        var toDir = SubPath(directory, to);

        var files = Directory.GetFiles(fromDir, "", SearchOption.AllDirectories);

        foreach (var file in files)
        {
            var target = file.Replace(fromDir, toDir);

            if (EndsInDirectorySeparator(target))
            {
                if (!Directory.Exists(target))
                {
                    Directory.CreateDirectory(target);
                }
            }
            else
            {
                DirectoryCheckCreate(target);

                System.IO.File.Copy(file, target, true);
            }
        }
    }

    public static void DirectoryCreate(string directory, string name)
    {
        var path = SubPath(directory, name);

        if (Directory.Exists(path))
        {

        }
        else
        {
            Directory.CreateDirectory(path);
        }
    }

    public static void DirectoryDel(string directory, string name)
    {
        var path = SubPath(directory, name);

        if (Directory.Exists(path))
        {
            Directory.Delete(path, true);
        }
    }

    static void DirectoryCheckCreate(string file)
    {
        var path = System.IO.Path.GetDirectoryName(file);

        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
        }
    }

    public static Stream LoadFile(Assembly assembly, string manifest, string res)
    {
        var stream = assembly.GetManifestResourceStream(manifest + ".Resources." + res);

        return stream;
    }

    public static bool TryGetStream(string directory, string res, out Stream stream, out string errMsg)
    {
        var result = false;

        stream = null;

        errMsg = null;

        try
        {
            var file = SubPath(directory, res);

            stream = System.IO.File.OpenRead(file);

            result = true;
        }
        catch (Exception ex)
        {
            errMsg = ex.Message;
        }

        return result;
    }

    public static bool TryGetBytes(string directory, string res, out byte[] bytes, out string errMsg)
    {
        var result = false;

        bytes = null;

        errMsg = null;

        try
        {
            //if (Device.Instance.Platform is Platform.Web)
            //{
            //    var wasmJSRuntime = new WasmJSRuntime();  //IJSInProcessRuntime
            //    var provider = new BrowserStorageProvider(wasmJSRuntime);
            //    return wasmJSRuntime.Invoke<string>("localStorage.getItem", res);
            //}
            //else
            //{
            var file = SubPath(directory, res);

            bytes = System.IO.File.ReadAllBytes(file);

            result = true;
            //}
        }
        catch (Exception ex)
        {
            errMsg = ex.Message;
        }

        return result;
    }

    public static bool TryGetText(string directory, string res, out string str, out string errMsg)
    {
        var result = false;

        str = null;

        errMsg = null;

        try
        {
            var file = SubPath(directory, res);

            if (GetFileLength(directory, res) < 0)
            {
                
            }
            else
            {
                str = System.IO.File.ReadAllText(file);

                result = true;
            }
        }
        catch (Exception ex)
        {
            errMsg = ex.Message;
        }

        return result;
    }

    public static bool FileExist(string directory, string res)
    {
        var file = SubPath(directory, res);

        return System.IO.File.Exists(file);
    }

    public static int GetFileLength(string directory, string res)
    {
        var file = SubPath(directory, res);

        if (System.IO.File.Exists(file))
        {
            return (int)new FileInfo(file).Length;
        }
        else
        {
            return -1;
        }
    }

    public static void FileCopy(string directory, string from, string to)
    {
        var fiFrom = SubPath(directory, from);

        var fiTo = SubPath(directory, to);

        DirectoryCheckCreate(fiTo);

        System.IO.File.Copy(fiFrom, fiTo, true);
    }

    public static void FileMove(string directory, string from, string to)
    {
        var fiFrom = SubPath(directory, from);

        var fiTo = SubPath(directory, to);

        DirectoryCheckCreate(fiTo);

        System.IO.File.Copy(fiFrom, fiTo, true);

        System.IO.File.Delete(fiFrom);
    }

    public static void SaveFile(string directory, string res, byte[] bytes)
    {
        var file = SubPath(directory, res);

        DirectoryCheckCreate(file);

        System.IO.File.WriteAllBytes(file, bytes);
    }

    public static void SaveFileStream(string directory, string res, Stream stream, bool append)
    {
        var file = SubPath(directory, res);

        DirectoryCheckCreate(file);

        using (var fs = System.IO.File.Open(file, append ? FileMode.Append : FileMode.Create))
        {
            stream.Seek(0, SeekOrigin.Begin);

            var length = (int)(stream.Length < 4096 ? stream.Length : 4096);

            var array = new byte[length];

            int bytesRead = 0;

            while ((bytesRead = stream.Read(array, 0, length)) > 0)
            {
                fs.Write(array, 0, bytesRead);
            }
        }
    }

    public static void SaveText(string directory, string res, string text)
    {
        var file = SubPath(directory, res);

        DirectoryCheckCreate(file);

        System.IO.File.WriteAllText(file, text);
    }

    public static void AppendText(string directory, string res, string text)
    {
        var file = SubPath(directory, res);

        DirectoryCheckCreate(file);

        System.IO.File.AppendAllText(file, text);
    }

    public static void DelFile(string directory, string res)
    {
        var file = SubPath(directory, res);

        if (System.IO.File.Exists(file))
        {
            System.IO.File.Delete(file);
        }
    }

    public static void CreateZipFile(string directory, string subDir, string file)
    {
        var dir = SubPath(directory, subDir);

        var fi = SubPath(directory, file);

        if (System.IO.File.Exists(fi))
        {
            System.IO.File.Delete(fi);
        }

        ZipFile.CreateFromDirectory(dir, fi);
    }

    public static void ExtractZipFile(string directory, string name, Stream stream)
    {
        if (!DirectoryExist(directory, name))
        {
            DirectoryCreate(directory, name);
        }

        using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
        {
            foreach (var entry in archive.Entries)
            {
                if (EndsInDirectorySeparator(entry.FullName))
                {
                    DirectoryCreate(directory, entry.FullName);
                }
                else
                {
                    byte[] data;

                    using (var reader = new StreamReader(entry.Open()))
                    {
                        using (var ms = new MemoryStream())
                        {
                            reader.BaseStream.CopyTo(ms);

                            data = ms.ToArray();

                            var fullName = System.IO.Path.Combine(name, entry.FullName);

                            DelFile(directory, fullName);

                            SaveFile(directory, fullName, data);
                        }
                    }
                }
            }
        }
    }

    public static void ExtractZipFileServer(string name, byte[] bytes)
    {
        using (var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read))
        {
            foreach (var entry in archive.Entries)
            {
                if (EndsInDirectorySeparator(entry.FullName))
                {
                    Directory.CreateDirectory(System.IO.Path.Combine(name, entry.FullName));
                }
                else
                {
                    byte[] data;

                    using (var reader = new StreamReader(entry.Open()))
                    {
                        using (var ms = new MemoryStream())
                        {
                            reader.BaseStream.CopyTo(ms);

                            data = ms.ToArray();

                            var fullName = System.IO.Path.Combine(name, entry.FullName);

                            if (System.IO.File.Exists(fullName))
                            {
                                System.IO.File.Delete(fullName);
                            }

                            DirectoryCheckCreate(fullName);

                            System.IO.File.WriteAllBytes(fullName, data);
                        }
                    }
                }
            }
        }
    }

    public static void CopyToLocal(string file)
    {
        var fs = DeviceServices.Core.LoadFile(file);

        var bytes = fs.ReadAllBytes();

        using (var stream = new MemoryStream(bytes))
        {
            DelFile(DirectoryBase, file);

            SaveFileStream(DirectoryBase, file, stream, false);
        }
    }

    public static string Logs = "";

    public static void Log(string category, string ex)
    {
        lock (Logs)
        {
            var log = $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")}  {category}  {ex}\r\n\r\n";

            Logs = log + Logs;

            AppendText(DirectoryBase, "log.txt", log);
        }
    }

}
