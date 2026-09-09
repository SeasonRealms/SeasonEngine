// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace Season.Platforms.Web;

/// <summary>
/// Direct [JSImport] bindings to seasonMedia.js.
/// Every JS function is synchronous, so the C# signatures stay synchronous
/// (same convention as WebAudioInterop.CopyPcm). The play() promise rejection
/// path (browser autoplay policy) is handled entirely inside the JS side,
/// where NotAllowedError parks the channel for a gesture-based retry.
/// </summary>
[SupportedOSPlatform("browser")]
internal static partial class WebAudioPlayerInterop
{
    /// <summary>Starts playback on the given channel ('music' or 'sound').</summary>
    [JSImport("globalThis.seasonAudioPlayer.play")]
    internal static partial bool Play(string kind, string url, float volume);

    /// <summary>Sets both channel volumes (0..1 floats).</summary>
    [JSImport("globalThis.seasonAudioPlayer.setVolume")]
    internal static partial void SetVolume(float music, float sound);

    [JSImport("globalThis.seasonAudioPlayer.pause")]
    internal static partial void Pause();

    [JSImport("globalThis.seasonAudioPlayer.resume")]
    internal static partial void Resume();

    [JSImport("globalThis.seasonAudioPlayer.isPlaying")]
    internal static partial bool IsPlaying();
}

/// <summary>
/// Web-platform music/sound playback service.
/// Two lazy HTMLAudioElement players in seasonMedia.js mirror the
/// two-channel structure of the native platforms (MediaPlayer/AVPlayer pairs).
/// The id passed to PlayMedia is a wwwroot-relative asset name (for example
/// "Musics/Cozy.wav"); it is resolved through WebApp.ResolveAssetPath so the
/// assetBasePath prefix and absolute http(s)/blob URLs keep working.
/// </summary>
/// <remarks>
/// The Web platform sources also compile into the net10.0 (Linux) TFM, where the
/// generated [JSImport] entry points do not exist, so every member is gated by
/// OperatingSystem.IsBrowser() (same pattern as WebDeviceCore.RequestPermissionAsync).
/// </remarks>
internal sealed class WebMediaPlayer : IMediaPlayer
{
    public bool IsPlaying
    {
        get
        {
            if (!OperatingSystem.IsBrowser())
            {
                return false;
            }

            try
            {
                return WebAudioPlayerInterop.IsPlaying();
            }
            catch (Exception ex)
            {
                DeviceServices.BaseApp?.AddLog(LogType.None, $"{DateTime.UtcNow} [WebMediaPlayer] IsPlaying error: {ex.Message}");

                return false;
            }
        }
    }

    public void PlayMedia(string type, string id, string vol)
    {
        if (!OperatingSystem.IsBrowser())
        {
            return;
        }

        try
        {
            var kind = type is "Music" ? "music" : "sound";

            var url = WebApp.ResolveAssetPath(ResolveMediaId(id));

            var volume = 1f;

            if (!string.IsNullOrEmpty(vol) && float.TryParse(vol, out var parsed))
            {
                volume = parsed / 100;
            }

            WebAudioPlayerInterop.Play(kind, url, volume);
        }
        catch (Exception ex)
        {
            DeviceServices.BaseApp?.AddLog(LogType.None, $"{DateTime.UtcNow} [WebMediaPlayer] PlayMedia error: {ex.Message}");
        }
    }

    public void SetVolume(int music, int sound)
    {
        if (!OperatingSystem.IsBrowser())
        {
            return;
        }

        try
        {
            WebAudioPlayerInterop.SetVolume(music / 100f, sound / 100f);
        }
        catch (Exception ex)
        {
            DeviceServices.BaseApp?.AddLog(LogType.None, $"{DateTime.UtcNow} [WebMediaPlayer] SetVolume error: {ex.Message}");
        }
    }

    public void Pause()
    {
        if (!OperatingSystem.IsBrowser())
        {
            return;
        }

        try
        {
            WebAudioPlayerInterop.Pause();
        }
        catch (Exception ex)
        {
            DeviceServices.BaseApp?.AddLog(LogType.None, $"{DateTime.UtcNow} [WebMediaPlayer] Pause error: {ex.Message}");
        }
    }

    public void Resume()
    {
        if (!OperatingSystem.IsBrowser())
        {
            return;
        }

        try
        {
            WebAudioPlayerInterop.Resume();
        }
        catch (Exception ex)
        {
            DeviceServices.BaseApp?.AddLog(LogType.None, $"{DateTime.UtcNow} [WebMediaPlayer] Resume error: {ex.Message}");
        }
    }

    /// <summary>
    /// Normalizes the PlayMedia id into a wwwroot-relative asset name.
    /// Callers may pass a StorageService.SubPath product such as
    /// "SeasonEngine/Musics/Cozy.wav" (Wasm has no real file system but the
    /// shared App code builds the same path), so strip the DirectoryBase prefix
    /// when present. Absolute http(s)/blob URLs pass through unchanged.
    /// </summary>
    static string ResolveMediaId(string id)
    {
        var normalized = id?.Replace('\\', '/');

        if (string.IsNullOrEmpty(normalized))
        {
            return normalized;
        }

        if (Uri.TryCreate(normalized, UriKind.Absolute, out _))
        {
            return normalized;
        }

        var directoryBase = Season.Storage.StorageService.DirectoryBase;

        if (!string.IsNullOrEmpty(directoryBase))
        {
            var index = normalized.IndexOf(directoryBase, StringComparison.OrdinalIgnoreCase);

            if (index >= 0)
            {
                normalized = normalized.Substring(index + directoryBase.Length).TrimStart('/');
            }
        }

        return normalized;
    }
}
