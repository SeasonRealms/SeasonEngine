// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Microsoft.UI.Windowing;
using Microsoft.Win32;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Security.Principal;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace Season.Platforms.Windows;

internal class WindowsFeatures : IWindowsFeatures
{
    internal static DateTime? lastInActiveTime;

    internal static DateTime? lastActiveTime;

    internal static bool isActive;

    public bool IsActive(out DateTime? lastTime)
    {
        lastTime = isActive ? lastActiveTime : lastInActiveTime;

        return isActive;
    }

    public List<Process> GetChildProcesses(Process parentProcess)
    {
        int parentId = parentProcess.Id;
        List<Process> children = new List<Process>();

        Process[] allProcesses = Process.GetProcesses();

        foreach (Process process in allProcesses)
        {
            try
            {
                Process parent = GetParentProcess(process);
                if (parent != null && parent.Id == parentId)
                {
                    children.Add(process);
                }
            }
            catch
            {

            }
        }
        return children;
    }

    public Process GetForegroundProcess()
    {
        Process process = null;

        var hWnd = WindowsNative.GetForegroundWindow();

        if (hWnd == IntPtr.Zero)
        {

        }
        else
        {
            WindowsNative.GetWindowThreadProcessId(hWnd, out uint processId);

            if (processId == 0)
            {

            }
            else
            {
                try
                {
                    process = Process.GetProcessById((int)processId);
                }
                catch (ArgumentException ex)
                {

                }
            }
        }

        return process;
    }

    public Shortcut? ReadLnkFile(string file)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(file);

        var tempFile = StorageService.SubPath(StorageService.DirectoryBase, "temp.lnk");

        System.IO.File.Copy(file, tempFile, true);

        var shellType = Type.GetTypeFromProgID("WScript.Shell");

        dynamic shell = Activator.CreateInstance(shellType);

        dynamic shortcut0 = shell.CreateShortcut(tempFile);

        var shortcut = new Shortcut
        {
            Name = name,
            TargetPath = shortcut0.TargetPath,
            Arguments = shortcut0.Arguments,
            WorkingDirectory = shortcut0.WorkingDirectory,
            IconLocation = shortcut0.IconLocation
        };

        shortcut.IconLocation = shortcut.IconLocation.NullToStringTrim().Split(',')[0].NullToStringTrim();

        return shortcut;
    }

    public string ConvertIcon(string icon)
    {
        string fileName = "";

        using (var multiSizeIcon = new Icon(icon, new System.Drawing.Size(256, 256)))
        {
            fileName = $"{Path.GetFileNameWithoutExtension(icon)}.png";

            var path = StorageService.SubPath(StorageService.DirectoryBase, fileName);

            multiSizeIcon.ToBitmap().Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }

        return fileName;
    }

    IAudioEndpointVolumeNative? _audioEndpointVolume;

    void EnsureAudioEndpointVolume()
    {
        if (_audioEndpointVolume != null)
        {
            return;
        }

        var enumerator = (IMMDeviceEnumeratorNative)new MMDeviceEnumeratorComObject();

        IMMDeviceNative device;
        int hr = enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out device);
        if (hr != 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }

        Guid iid = typeof(IAudioEndpointVolumeNative).GUID;
        object endpointObj;
        hr = device.Activate(ref iid, CLSCTX.ALL, IntPtr.Zero, out endpointObj);
        if (hr != 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }

        _audioEndpointVolume = (IAudioEndpointVolumeNative)endpointObj;
    }

    public int GetVolume()
    {
        try
        {
            EnsureAudioEndpointVolume();

            if (_audioEndpointVolume == null)
            {
                return 0;
            }

            float level;
            int hr = _audioEndpointVolume.GetMasterVolumeLevelScalar(out level);
            if (hr != 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }

            return (int)(level * 100);
        }
        catch (Exception ex)
        {
            return 0;
        }
    }

    public bool SetVolume(int vol)
    {
        try
        {
            EnsureAudioEndpointVolume();

            if (_audioEndpointVolume == null)
            {
                return false;
            }

            vol = Math.Clamp(vol, 0, 100);
            float scalar = vol / 100f;

            Guid eventContext = Guid.Empty;
            int hr = _audioEndpointVolume.SetMasterVolumeLevelScalar(scalar, ref eventContext);
            if (hr != 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }

            return true;
        }
        catch (Exception ex)
        {
            return false;
        }
    }

    //public bool SetForeground(string title)
    //{
    //    SetForeground(title);
    //    return true;
    //}

    private const int SW_RESTORE = 9;
    public bool SetForeground(string title)
    {
        bool result = false;

        var hWnd = WindowsNative.FindWindow(null, title);

        if (hWnd == IntPtr.Zero)
        {

        }
        else
        {
            WindowsNative.ShowWindow(hWnd, SW_RESTORE);

            WindowsNative.SetForegroundWindow(hWnd);

            result = true;
        }

        return result;
    }

    private static Process GetParentProcess(Process process)
    {
        try
        {
            var pbi = new ParentProcessInfo();

            int status = WindowsNative.NtQueryInformationProcess(
                process.Handle,
                0, // ProcessBasicInformation
                ref pbi,
                Marshal.SizeOf(pbi),
                out _
            );

            if (status != 0)
                return null;

            int parentPid = pbi.InheritedFromUniqueProcessId.ToInt32();
            if (parentPid <= 0)
                return null;

            return Process.GetProcessById(parentPid);
        }
        catch
        {
            return null;
        }
    }

    public void OpenTaskbarSettings()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "ms-settings:taskbar",
            UseShellExecute = true
        });
    }

    public async Task<bool> IsAutoStartEnabled(string taskId)
    {
        bool enabled = false;

        try
        {
            var startupTask = await StartupTask.GetAsync(taskId);

            enabled = IsStartupTaskStateEnable(startupTask.State);
        }
        catch (Exception ex)
        {

        }

        return enabled;
    }

    public async Task<(bool?, string)> SetAutoStart(string taskId, bool enable)
    {
        bool? result = null;

        string errMsg = null;

        try
        {
            var startupTask = await StartupTask.GetAsync(taskId);

            if (enable)
            {
                if (startupTask.State is StartupTaskState.Enabled)
                {
                    result = true;
                }
                else if (startupTask.State is StartupTaskState.Disabled)
                {
                    var newState = await startupTask.RequestEnableAsync();

                    result = IsStartupTaskStateEnable(newState);
                }
                else if (startupTask.State is StartupTaskState.DisabledByUser)
                {
                    await Launcher.TryOpenAsync(new Uri("ms-settings:startupapps"));

                    result = null;
                }
                else if (startupTask.State is StartupTaskState.DisabledByPolicy)
                {
                    result = false;

                    errMsg = "StartupTaskState is disabled by policy.";
                }
            }
            else
            {
                if (startupTask.State == StartupTaskState.Enabled)
                {
                    startupTask.Disable();

                    result = await IsAutoStartEnabled(taskId);
                }
            }
        }
        catch (Exception ex)
        {
            errMsg = ex.ToString();
        }

        return (result, errMsg);
    }

    bool IsStartupTaskStateEnable(StartupTaskState state)
    {
        bool enabled = false;

        if (state is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy)
        {
            enabled = true;
        }
        else if (state is StartupTaskState.Disabled or StartupTaskState.DisabledByUser or StartupTaskState.DisabledByPolicy)
        {

        }
        else
        {

        }

        return enabled;
    }

    public void SetBlockingKeys(bool block)
    {
        Dispatcher.GetForCurrentThread().Dispatch(() =>
        {
            if (block)
            {
                HookKeyTools.InstallKeyboardHook();
            }
            else
            {
                HookKeyTools.UninstallKeyboardHook();
            }
        });
    }

    public string ExtractIcon(string file)
    {
        string fileName = "";

        var icon = GetSystemIcon.GetIconByFileName(file);

        using (var multiSizeIcon = new Icon(icon, new System.Drawing.Size(256, 256)))
        {
            fileName = $"{Path.GetFileNameWithoutExtension(file)}.png";

            var path = StorageService.SubPath(StorageService.DirectoryBase, fileName);

            multiSizeIcon.ToBitmap().Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }

        return fileName;
    }

    public (bool, int, int) GetLockScreens(out string errMsg)
    {
        var result = false;

        var total = 0;

        var copied = 0;

        errMsg = null;

        var userName = Environment.UserName;

        var path = $@"C:\Users\{userName}\AppData\Local\Packages\Microsoft.Windows.ContentDeliveryManager_cw5n1h2txyewy\LocalState\Assets";

        if (Directory.Exists(path))
        {
            var files = Directory.GetFiles(path).NullToEmptyList();

            total = files.Count;

            for (var i = 0; i < total; i++)
            {
                var file = files[i];

                try
                {
                    var fi = new FileInfo(file);

                    if (fi.Length > 100 * 1024) // > 100k
                    {
                        string destFile = Path.Combine(WindowsFileService.DownloadDir, Path.GetFileName(file));

                        if (!File.Exists(destFile + ".jpg"))
                        {
                            File.Copy(file, destFile + ".jpg", true);

                            copied++;
                        }
                    }
                }
                catch (Exception ex)
                {

                }
            }

            Launcher.OpenAsync(WindowsFileService.DownloadDir);
        }
        else
        {
            errMsg = "Can't find lock screen images.";
        }

        return (result, total, copied);
    }

    public List<IconInfo> ListApps(List<string> ids, bool copyLogo)
    {
        ids = ids.NullToEmptyList().Select(id => id.NullToStringTrim().ToLower()).NullToEmptyList();

        var iconInfos = new List<IconInfo>();

        //string targetAUMID = "Microsoft.Paint_8wekyb3d8bbwe!App";

        var packageManager = new PackageManager();

        var packages = packageManager.FindPackagesForUser("");

        foreach (var package in packages)
        {
            var appListEntries = package.GetAppListEntries();
            foreach (var app in appListEntries)
            {
                //AUMID: PackageFamilyName!AppId
                //string currentAUMID = app.AppUserModelId; // $"{package.Id.FamilyName}!{app.AppUserModelId}";

                if (ids.Count is 0 || ids.Contains(app.AppUserModelId.NullToStringTrim().ToLower())) //  app.AppUserModelId.Equals(id, StringComparison.OrdinalIgnoreCase))
                {
                    var iconInfo = new IconInfo()
                    {
                        ID = app.AppUserModelId,
                        Name = app.DisplayInfo.DisplayName,
                        Desc = package.Id.FullName,
                        //Icon = package.Logo.AbsolutePath,
                        Version = package.Id.Version.ToString(),
                        Publisher = package.Id.Publisher,
                        Path = @$"shell:appsFolder\{app.AppUserModelId}"
                        //Name = app.DisplayInfo.DisplayName,
                        //AUMID = currentAUMID,
                        //PackageFullName = package.Id.FullName,
                        //PackageFamilyName = package.Id.FamilyName,
                        //Publisher = package.Id.Publisher,
                        //Version = $"{package.Id.Version.Major}.{package.Id.Version.Minor}.{package.Id.Version.Build}.{package.Id.Version.Revision}",
                        //InstallLocation = package.InstalledLocation.Path,
                        //IsDevelopmentMode = package.IsDevelopmentMode,
                        //IsFramework = package.IsFramework,
                        //Logo = package.Logo.AbsoluteUri
                    };

                    try
                    {
                        iconInfo.Icon = package.Logo.AbsolutePath;

                        var image = System.Net.WebUtility.UrlDecode(iconInfo.Icon);

                        var bytes = File.ReadAllBytes(image);

                        //var file = Path.GetFileName(image);

                        var ext = Path.GetExtension(image);

                        var file = iconInfo.ID + ext;

                        iconInfo.Image = file;

                        if (StorageService.FileExist(StorageService.DirectoryBase, file))
                        {

                        }
                        else
                        {
                            StorageService.SaveFile(StorageService.DirectoryBase, file, bytes);
                        }
                    }
                    catch (Exception ex)
                    {

                    }

                    iconInfos.Add(iconInfo);
                }
            }
        }

        return iconInfos;
    }

    public void Shutdown()
    {
        ExecuteCommand("shutdown /s /f /t 0");
    }

    public void Reboot()
    {
        ExecuteCommand("shutdown /r /f /t 0");
    }

    void ExecuteCommand(string command)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c {command}",
                CreateNoWindow = true,
                UseShellExecute = false,
                Verb = "runas"
            }
        };
        process.Start();
    }

    internal static IntPtr FindWindowByCaption(IntPtr zero, string lpWindowName)
    {
        return WindowsNative.FindWindow(null, lpWindowName);
    }

    public void TrySetWindow(bool? fullScreen, out string errMsg)
    {
        var err = "";

        Dispatcher.GetForCurrentThread()?.Dispatch(() =>
        {
            try
            {
                var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(WindowsApp.Window);

                var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(windowHandle);

                var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);

                var appWindowPresenterKind = AppWindowPresenterKind.Default;

                if (fullScreen is null)
                {
                    fullScreen = appWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen ? false : true;
                }

                appWindowPresenterKind = fullScreen is true ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Overlapped;

                appWindow.SetPresenter(appWindowPresenterKind);

                if (appWindow.Presenter is OverlappedPresenter overlappedPresenter)
                {
                    overlappedPresenter.Maximize();
                }
            }
            catch (Exception ex)
            {
                err = ex.ToString();
            }
        });

        errMsg = err;
    }

    static bool RequestAdminPrivilege()
    {
        if (IsAdministrator()) return true;

        var process = new Process();
        process.StartInfo.FileName = Assembly.GetEntryAssembly().Location;
        process.StartInfo.Verb = "runas";
        process.StartInfo.UseShellExecute = true;

        try
        {
            process.Start();
            Environment.Exit(0);
            return true;
        }
        catch
        {
            return false;
        }
    }

    static bool IsAdministrator()
    {
        var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }
}

public static class GetSystemIcon
{
    public static Icon GetIconByFileName(string fileName, bool isLarge = true)
    {
        int[] phiconLarge = new int[1];

        int[] phiconSmall = new int[1];


        WindowsNative.ExtractIconEx(fileName, 0, phiconLarge, phiconSmall, 1);

        IntPtr IconHnd = new IntPtr(isLarge ? phiconLarge[0] : phiconSmall[0]);

        return Icon.FromHandle(IconHnd);

    }

    public static Icon GetIconByFileType(string fileType, bool isLarge)
    {

        if (fileType == null || fileType.Equals(string.Empty)) return null;

        RegistryKey regVersion = null;

        string regFileType = null;

        string regIconString = null;

        string systemDirectory = Environment.SystemDirectory + "\\";

        if (fileType[0] == '.')
        {
            regVersion = Registry.ClassesRoot.OpenSubKey(fileType, true);

            if (regVersion != null)
            {
                regFileType = regVersion.GetValue("") as string;

                regVersion.Close();

                regVersion = Registry.ClassesRoot.OpenSubKey(regFileType + @"\DefaultIcon", true);

                if (regVersion != null)
                {
                    regIconString = regVersion.GetValue("") as string;

                    regVersion.Close();
                }
            }

            if (regIconString == null)
            {
                regIconString = systemDirectory + "shell32.dll,0";
            }
        }
        else
        {
            regIconString = systemDirectory + "shell32.dll,3";
        }

        string[] fileIcon = regIconString.Split(new char[] { ',' });

        if (fileIcon.Length != 2)
        {
            fileIcon = new string[] { systemDirectory + "shell32.dll", "2" };
        }

        Icon resultIcon = null;

        try
        {
            int[] phiconLarge = new int[1];

            int[] phiconSmall = new int[1];

            uint count = WindowsNative.ExtractIconEx(fileIcon[0], Int32.Parse(fileIcon[1]), phiconLarge, phiconSmall, 1);

            IntPtr IconHnd = new IntPtr(isLarge ? phiconLarge[0] : phiconSmall[0]);

            resultIcon = Icon.FromHandle(IconHnd);
        }
        catch 
        {
            
        }

        return resultIcon;
    }
}

public class HookKeyTools
{
    private static IntPtr _hookID = IntPtr.Zero;
    private static LowLevelKeyboardProc _hookProc;

    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;

    public static void InstallKeyboardHook()
    {
        if (_hookID == IntPtr.Zero)
        {
            using (var curProcess = Process.GetCurrentProcess())
            using (var curModule = curProcess.MainModule)
            {
                _hookProc = HookCallback;
                _hookID = WindowsNative.SetWindowsHookEx(
                    WH_KEYBOARD_LL,
                    _hookProc,
                    WindowsNative.GetModuleHandle(curModule.ModuleName),
                    0);

                if (_hookID == IntPtr.Zero)
                {
                    int errorCode = Marshal.GetLastWin32Error();
                    throw new Exception($"Failed: {errorCode})");
                }
            }
        }

        var err = Marshal.GetLastWin32Error();

        var ex = new Win32Exception(err);

        if (ex != null)
        {

        }
    }

    public static void UninstallKeyboardHook()
    {
        if (_hookID != IntPtr.Zero)
        {
            WindowsNative.UnhookWindowsHookEx(_hookID);
            _hookID = IntPtr.Zero;
        }
    }

    static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var kbd = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            int vkCode = kbd.vkCode;

            if (vkCode == VK_LWIN || vkCode == VK_RWIN)
            {
                if (wParam == (IntPtr)0x0100 || // WM_KEYDOWN
                    wParam == (IntPtr)0x0104 || // WM_SYSKEYDOWN
                    wParam == (IntPtr)0x0101 || // WM_KEYUP
                    wParam == (IntPtr)0x0105)   // WM_SYSKEYUP
                {
                    return (IntPtr)1;
                }
            }
        }

        return WindowsNative.CallNextHookEx(_hookID, nCode, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct KBDLLHOOKSTRUCT
    {
        public int vkCode;
        public int scanCode;
        public int flags;
        public int time;
        public IntPtr dwExtraInfo;
    }

    const int WH_KEYBOARD_LL = 13;
}