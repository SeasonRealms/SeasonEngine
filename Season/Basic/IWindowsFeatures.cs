// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Basic;

public interface IWindowsFeatures
{
    bool IsActive(out DateTime? lastTime);

    Task<bool> IsAutoStartEnabled(string taskId);

    Task<(bool?, string)> SetAutoStart(string taskId, bool enable);

    void OpenTaskbarSettings();

    void SetBlockingKeys(bool block);

    string ExtractIcon(string file);

    List<System.Diagnostics.Process> GetChildProcesses(System.Diagnostics.Process parentProcess);

    System.Diagnostics.Process GetForegroundProcess();

    Shortcut? ReadLnkFile(string file);

    string ConvertIcon(string icon);

    int GetVolume();

    bool SetVolume(int volume);

    bool SetForeground(string title);

    List<IconInfo> ListApps(List<string> ids, bool copyLogo);

    void Shutdown();

    void Reboot();

    (bool, int, int) GetLockScreens(out string errMsg);
}

public class IconInfo
{
    public string ID { get; set; }

    public string Name { get; set; }

    public string Desc { get; set; }

    public string Icon { get; set; }

    public string Image { get; set; }

    public string Version { get; set; }

    public string Publisher { get; set; }

    public string Path { get; set; }
}

public struct Shortcut
{
    public string Name { get; set; }

    public string TargetPath { get; set; }

    public string Arguments { get; set; }

    public string WorkingDirectory { get; set; }

    public string IconLocation { get; set; }
}
