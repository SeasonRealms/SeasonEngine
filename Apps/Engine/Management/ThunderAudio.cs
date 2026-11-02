// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Globalization;

namespace Engine.Management;

/// <summary>
/// 雷电天气的"雷"。闪电由 <see cref="WeatherVisualState"/> 逐帧调度，这里补上声音：
/// 播放在 Resources/Raw/Musics 下预先生成的三段雷声，按落雷点到听者的距离延迟与衰减。
/// </summary>
/// <remarks>
/// <para>音频不在这里合成。WAV 是构建期用 Stable Audio 3 生成的静态资源（生成工具见
/// .dbg/thunder_probe），运行时既不加载模型也不预读采样：每道雷只解析一次路径，
/// 交给 <see cref="IMediaPlayer"/> 自己去读文件——和背景音乐走的是同一条通道。</para>
/// <para>雷暴之外不出声；天气切走时已排队的条目直接作废，不补播。
/// 资源缺失时只记一次日志，闪电本身不依赖音频。</para>
/// </remarks>
internal sealed class ThunderAudio
{
    /// <summary>声速。雷声延迟就是落雷点到听者的距离除以它。</summary>
    const float SoundSpeed = 343f;

    /// <summary>三段预生成雷声，播放时随机取一段，避免每道雷听起来都一样。</summary>
    static readonly string[] Clips =
    [
        "Musics/ThunderClose.wav",
        "Musics/ThunderCrack.wav",
        "Musics/ThunderRoll.wav",
    ];

    /// <summary>已排队、等待到达的雷声。只在帧线程读写。</summary>
    readonly List<(double Due, string Clip, int Volume)> _pending = [];

    /// <summary>缺失的资源只报一次，避免每道雷都往日志里写同一行。</summary>
    readonly HashSet<string> _warned = [];

    readonly Random _random = new();

    double _clock;
    int _serialSeen = -1;

    /// <summary>
    /// 每帧调用一次，必须在 <see cref="WeatherVisualState.Update"/> 之后（落雷边沿在本帧已推进）。
    /// <paramref name="thunderstorm"/> 为假时既不排队也不播放：晴天不该有雷声。
    /// </summary>
    internal void Update(float seconds, WeatherVisualState visuals, Vector3 listener, bool thunderstorm)
    {
        if (visuals is null) return;

        _clock += float.IsFinite(seconds) && seconds > 0 ? seconds : 0;

        int serial = visuals.StrikeSerial;

        if (_serialSeen < 0)
        {
            // 首帧只做基线：启动前已经落过的雷不该在开场补一声。
            _serialSeen = serial;
        }
        else if (serial != _serialSeen)
        {
            _serialSeen = serial;

            if (thunderstorm) Enqueue(visuals, listener);
        }

        if (_pending.Count == 0) return;

        // 倒序删：同帧可能有两条到期。
        for (int i = _pending.Count - 1; i >= 0; i--)
        {
            var item = _pending[i];

            if (_clock < item.Due) continue;

            _pending.RemoveAt(i);

            // 天气已经切走：这条雷声作废，不补播（否则晴天后突然响一声）。
            if (!thunderstorm) continue;

            var path = Resolve(item.Clip);

            if (path is null) continue;

            DeviceServices.Media?.PlayMedia("Sound", path, item.Volume.ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// 排队一次雷声。延迟按真实声速算：落雷点到听者通常 200~1500 m，对应 0.6~4.4 s，
    /// 这正是"先见闪电、后闻雷"的间隔；音量随距离衰减，远处的雷只剩一声闷响。
    ///
    /// 距离取通道上离听者最近的那一段，而不是云内击穿点：一道横向斜出一公里的通道，
    /// 它最低的那一段可能就在听者几百米外，而击穿点还远在云里——两者算出的延迟能差一秒以上，
    /// 音量也差一档。云内闪没有通道，此时回退到击穿点距离，那正是"头顶闷雷"的正确形状。
    /// </summary>
    void Enqueue(WeatherVisualState visuals, Vector3 listener)
    {
        var clip = Clips[_random.Next(Clips.Length)];
        float distance = visuals.Channel.NearestDistance(listener);
        if (!float.IsFinite(distance)) distance = Vector3.Distance(visuals.StrikePoint, listener);

        if (!float.IsFinite(distance) || distance < 0) distance = 0;

        double delay = Math.Clamp(distance / SoundSpeed, 0.2f, 4.5f);
        // 云底落雷到地面听者约 700 m，音量落在 60 上下（背景音乐是 60）；再远只剩闷响。
        float audibility = Math.Clamp(1f - distance / 2000f, 0.2f, 1f);
        int volume = (int)MathF.Round(audibility * 90f);

        // 上限只防极端情况（区域被反复重建导致计数跳变），正常落雷间隔 2.6 s 起，排不出队列。
        if (_pending.Count >= 4) _pending.RemoveAt(0);

        _pending.Add((_clock + delay, clip, volume));
    }

    /// <summary>
    /// 把资源名解析成播放器能直接打开的真实路径。桌面与 Apple 是程序目录里的文件，
    /// Android 会把 APK assets 落进 CacheDir（MediaPlayer 只认真实文件），
    /// Web 端原样返回，由 WebMediaPlayer 拼成 wwwroot 下的 URL。
    /// </summary>
    string? Resolve(string clip)
    {
        var path = DeviceServices.Core?.LoadFilePath(clip);

        if (!string.IsNullOrEmpty(path)) return path;

        if (_warned.Add(clip))
        {
            DeviceServices.BaseApp?.AddLog(LogType.Error, $"{DateTime.UtcNow} 雷声：找不到资源 {clip}，这一段静音。");
        }

        return null;
    }
}
