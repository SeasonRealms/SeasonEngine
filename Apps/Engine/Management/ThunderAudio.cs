// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Globalization;

namespace Engine.Management;

/// <summary>
/// The "thunder" of thunderstorm weather. Lightning is scheduled frame by frame by <see cref="WeatherVisualState"/>;
/// this fills in the sound: three pre-generated thunder clips under Resources/Raw/Musics, played with a delay and
/// attenuation based on the distance from the strike point to the listener.
/// </summary>
/// <remarks>
/// <para>The audio is not synthesised here. The WAV files are static assets generated at build time with Stable Audio 3
/// (generator tool in .dbg/thunder_probe); at runtime no model is loaded and no sample is pre-read: each thunder resolves
/// its path once and hands it to <see cref="IMediaPlayer"/> to read the file itself - the same channel as the background
/// music.</para>
/// <para>Nothing plays outside a thunderstorm; when the weather switches away, already queued entries are dropped without a
/// catch-up play. A missing asset is logged once; the lightning itself does not depend on the audio.</para>
/// </remarks>
internal sealed class ThunderAudio
{
    /// <summary>Speed of sound. The thunder delay is the strike-to-listener distance divided by it.</summary>
    const float SoundSpeed = 343f;

    /// <summary>Three pre-generated thunder clips; one is picked at random per play so each strike does not sound identical.</summary>
    static readonly string[] Clips =
    [
        "Musics/ThunderClose.wav",
        "Musics/ThunderCrack.wav",
        "Musics/ThunderRoll.wav",
    ];

    /// <summary>Queued thunder awaiting arrival. Read and written only on the frame thread.</summary>
    readonly List<(double Due, string Clip, int Volume)> _pending = [];

    /// <summary>A missing asset is reported once, so every strike does not write the same line to the log.</summary>
    readonly HashSet<string> _warned = [];

    readonly Random _random = new();

    double _clock;
    int _serialSeen = -1;

    /// <summary>
    /// Called once per frame and must run after <see cref="WeatherVisualState.Update"/> (the strike edge has already
    /// advanced this frame). When <paramref name="thunderstorm"/> is false it neither queues nor plays: a fair sky
    /// should have no thunder.
    /// </summary>
    internal void Update(float seconds, WeatherVisualState visuals, Vector3 listener, bool thunderstorm)
    {
        if (visuals is null) return;

        _clock += float.IsFinite(seconds) && seconds > 0 ? seconds : 0;

        int serial = visuals.StrikeSerial;

        if (_serialSeen < 0)
        {
            // The first frame only takes a baseline: a strike that happened before startup should not ring out at the opening.
            _serialSeen = serial;
        }
        else if (serial != _serialSeen)
        {
            _serialSeen = serial;

            if (thunderstorm) Enqueue(visuals, listener);
        }

        if (_pending.Count == 0) return;

        // Remove in reverse: two entries may fall due in the same frame.
        for (int i = _pending.Count - 1; i >= 0; i--)
        {
            var item = _pending[i];

            if (_clock < item.Due) continue;

            _pending.RemoveAt(i);

            // The weather has already switched away: this thunder is void and not replayed (otherwise a fair sky would suddenly ring out).
            if (!thunderstorm) continue;

            var path = Resolve(item.Clip);

            if (path is null) continue;

            DeviceServices.Media?.PlayMedia("Sound", path, item.Volume.ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// Queues one thunder. The delay uses the real speed of sound: the strike-to-listener distance is usually 200~1500 m,
    /// which is 0.6~4.4 s - exactly the gap of "see the flash first, hear the thunder after"; the volume falls off with
    /// distance, so a remote strike is only a low rumble.
    ///
    /// The distance is taken from the nearest point of the channel to the listener, not the in-cloud breakdown point: for a
    /// channel slanting a kilometre sideways, its lowest reach may be only a few hundred metres from the listener while the
    /// breakdown point is still far inside the cloud - the two compute delays that can differ by over a second, and volumes
    /// that differ by a step. An intra-cloud flash has no channel, so it falls back to the breakdown-point distance, which is
    /// exactly the right shape for a rumble overhead.
    /// </summary>
    void Enqueue(WeatherVisualState visuals, Vector3 listener)
    {
        var clip = Clips[_random.Next(Clips.Length)];
        float distance = visuals.Channel.NearestDistance(listener);
        if (!float.IsFinite(distance)) distance = Vector3.Distance(visuals.StrikePoint, listener);

        if (!float.IsFinite(distance) || distance < 0) distance = 0;

        double delay = Math.Clamp(distance / SoundSpeed, 0.2f, 4.5f);
        // A cloud-base strike to a ground listener is about 700 m, putting the volume around 60 (the background music is 60); further out is only a rumble.
        float audibility = Math.Clamp(1f - distance / 2000f, 0.2f, 1f);
        int volume = (int)MathF.Round(audibility * 90f);

        // The cap only guards against extreme cases (a region rebuilt repeatedly, jumping the counter); normal strikes are 2.6 s apart or more, so the queue never builds up.
        if (_pending.Count >= 4) _pending.RemoveAt(0);

        _pending.Add((_clock + delay, clip, volume));
    }

    /// <summary>
    /// Resolves an asset name to a real path the player can open directly. On desktop and Apple it is a file in the program
    /// directory; on Android the APK assets are extracted into CacheDir (MediaPlayer only accepts real files); on the web it
    /// is returned as-is, and WebMediaPlayer makes it a URL under wwwroot.
    /// </summary>
    string? Resolve(string clip)
    {
        var path = DeviceServices.Core?.LoadFilePath(clip);

        if (!string.IsNullOrEmpty(path)) return path;

        if (_warned.Add(clip))
        {
            DeviceServices.BaseApp?.AddLog(LogType.Error, $"{DateTime.UtcNow} Thunder: asset {clip} not found, staying silent for this one.");
        }

        return null;
    }
}
