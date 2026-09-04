// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Platforms.Web;

internal class WebDeviceCore : IDeviceCore
{
    /// <summary>
    /// Web-side file cache: Blazor Wasm has no synchronous I/O, so LoadFileAsync downloads assets into this cache,
    /// after which synchronous LoadFile can hit it directly. It can also be prefilled through PreloadFile.
    /// Key = file name (for example, "Ravie.ttf"), Value = file byte array.
    /// </summary>
    static readonly Dictionary<string, byte[]> _fileCache = new();

    /// <summary>
    /// Caches file bytes in memory so LoadFile can read them synchronously later.
    /// Optional: LoadFileAsync already supports on-demand downloads, so preloading is not required.
    /// </summary>
    public static void PreloadFile(string fileName, byte[] data)
    {
        _fileCache[fileName] = data;
    }

    /// <summary>
    /// Checks whether the file is already cached.
    /// </summary>
    public static bool IsFileCached(string fileName) => _fileCache.ContainsKey(fileName);

    public Season.Basic.Platform Platform => Season.Basic.Platform.Web;

    public Channel Channel { get; set; } = Channel.None;

    public Orientation Orientation { get; set; } = Orientation.LandscapeLeft;

    public string GetLocalIP()
    {
        // The browser environment has no direct capability to retrieve a local IP address.
        return "";
    }

    public string LoadFilePath(string res)
    {
        // Web assets are loaded from wwwroot over HTTP, so there is no local file path.
        return res;
    }

    public bool LoadFileExists(string res)
    {
        // Check whether the resource exists in the cache.
        return _fileCache.ContainsKey(res);
    }

    public Stream LoadFile(string res)
    {
        // Only reading from the cache is supported here
        // (resources that were preloaded or already downloaded through LoadFileAsync).
        if (_fileCache.TryGetValue(res, out var bytes))
            return new MemoryStream(bytes);

        // Synchronous reads are not supported for uncached resources. Use LoadFileAsync instead.
        throw new PlatformNotSupportedException($"File '{res}' not cached. Use LoadFileAsync() or WebDeviceCore.PreloadFile() instead.");
    }

    /// <summary>
    /// Asynchronously reads an asset. On a cache miss, download it from wwwroot over HTTP and write it back into the cache.
    /// Note: in the single-threaded Wasm environment, callers must await this method and must never block with .Result or .Wait().
    /// </summary>
    public async Task<Stream> LoadFileAsync(string res)
    {
        if (_fileCache.TryGetValue(res, out var bytes))
            return new MemoryStream(bytes);

        var response = await WebApp.HttpClient.GetAsync(WebApp.ResolveAssetPath(res));
        response.EnsureSuccessStatusCode();
        bytes = await response.Content.ReadAsByteArrayAsync();
        _fileCache[res] = bytes;
        return new MemoryStream(bytes);
    }

    public bool IsDarkMode()
    {
        // TODO: query prefers-color-scheme through JSInterop
        return false;
    }

    public async Task<bool> RequestPermissionAsync(string[] permissions)
    {
        return await Task.FromResult(true);
    }
}

internal class WebMediaPlayer : IMediaPlayer
{
    public bool IsPlaying
    {
        get
        {
            return false;
        }
    }

    public void PlayMedia(string type, string id, string vol) { }

    public void SetVolume(int music, int sound) { }

    public void Pause() { }

    public void Resume() { }
}

internal class WebDialogService : IDialogService
{
    public async Task<string> ShowMessage(string title, string desc, string[] buttons, string text)
    {
        // TODO: implement window.alert / a custom dialog through JSInterop
        return await Task.FromResult(text);
    }

    public async Task<string> ShowKeyboard(string title, string desc, string[] buttons, string text)
    {
        // TODO: implement an input dialog through JSInterop
        return await Task.FromResult(text);
    }
}

internal class WebFileService : IFileService
{
    public async Task<string> PickFolder() => null;

    public async Task<List<TaskFile>> PickFiles(FileType fileType, string[] exts, bool multiple, bool open) => null;

    public async Task<string> SaveFile(string fileName, Stream stream, CancellationToken cancellationToken) => null;

    public async Task<string> OpenFile(string name, string category, byte[] bytes) => null;

    public async Task<bool> OpenLink(string name)
    {
        // TODO: call window.open through JSInterop
        return true;
    }

    public void OpenFolder(string name) { }
}

internal class WebStoreService : IStoreService
{
    public async Task<(List<Product>, string)> Query()
    {
        throw new PlatformNotSupportedException();
    }

    public async Task<Product> Query(string storeId)
    {
        return new Product { StoreId = storeId, InCollection = true };
    }

    public Task<string> Purchase(string product, Action<string> onResult)
        => throw new PlatformNotSupportedException();

    public async Task<string> Review(string product) => "";

    public async Task<(int version, string desc)> CheckForUpdates() => (0, "");
}
