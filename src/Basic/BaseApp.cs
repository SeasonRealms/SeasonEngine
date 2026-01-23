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

    public Vector2 DeviceResolution { get; internal set; }

    public float Scale { get; internal set; } = 1f;

    public bool IsActive { get; internal set; }

    internal float Time = 0f;

    public DateTime LastActiveTime { get; internal set; }

    public DateTime LastInActiveTime { get; internal set; }

    public static string Language
    {
        get
        {
            return CultureInfo.CurrentCulture.ToString();
        }
    }

    public static bool Debug
    {
        get
        {
            bool debug = false;
#if DEBUG
            debug = true;
#endif
            return debug;
        }
    }

    public List<Log> Logs = new List<Log>();

    public virtual async void Create()
    {

    }

    public virtual bool Update(float time)
    {
        Time += time;

        TouchService.Update(time, Scale);


        return false;
    }

    internal void ApplyResolution(int width, int height)
    {
        DeviceServices.BaseApp.DeviceResolution = new Vector2(width, height);

        var min = Math.Min(DeviceServices.BaseApp.DeviceResolution.X, DeviceServices.BaseApp.DeviceResolution.Y);

        var max = Math.Max(DeviceServices.BaseApp.DeviceResolution.X, DeviceServices.BaseApp.DeviceResolution.Y);

        if (DeviceServices.BaseApp.DeviceResolution.X > DeviceServices.BaseApp.DeviceResolution.Y)
        {
            DeviceServices.BaseApp.BasicResolution = new Vector2(max, min);
        }
        else
        {
            DeviceServices.BaseApp.BasicResolution = new Vector2(min, max);
        }

        var scaleX = Convert.ToSingle(DeviceServices.BaseApp.DeviceResolution.X) / DeviceServices.BaseApp.BasicResolution.X;

        var scaleY = Convert.ToSingle(DeviceServices.BaseApp.DeviceResolution.Y) / DeviceServices.BaseApp.BasicResolution.Y;

        DeviceServices.BaseApp.Scale = 1f;

        if (scaleX > scaleY)
        {
            DeviceServices.BaseApp.Scale = scaleY;

            DeviceServices.BaseApp.ExtendResolution = new Vector2(DeviceServices.BaseApp.DeviceResolution.X / DeviceServices.BaseApp.Scale, DeviceServices.BaseApp.BasicResolution.Y);
        }
        else
        {
            DeviceServices.BaseApp.Scale = scaleX;

            DeviceServices.BaseApp.ExtendResolution = new Vector2(DeviceServices.BaseApp.BasicResolution.X, DeviceServices.BaseApp.DeviceResolution.Y / DeviceServices.BaseApp.Scale);
        }
    }

}

public struct Log
{
    public DateTime DateTime { get; set; }

    public string Message { get; set; }
}
