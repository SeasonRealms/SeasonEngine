// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Basic;

public abstract class BaseApp
{
    public string Title { get; set; }

    public bool FullScreen { get; set; } = false;

    public bool HideSystemBars { get; set; } = false;

    public Vector2 BasicResolution { get; set; } = new Vector2(1280, 720);

    public Vector2 ExtendResolution { get; internal set; } = new Vector2(1280, 720);

    public List<Log> Logs = new List<Log>();

    public virtual async void Create()
    {

    }
}

public struct Log
{
    public DateTime DateTime { get; set; }

    public string Message { get; set; }
}
