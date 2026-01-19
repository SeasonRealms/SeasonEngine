# SeasonEngine

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://opensource.org/licenses/MIT)
[![.NET](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)

A cross-platform application framework built for understanding and rapid development.

## Overview

SeasonEngine provides a unified abstraction layer for building applications that run seamlessly across **Windows**, **Linux**, **macOS**, **Android**, and **iOS**. It handles platform-specific implementations behind clean interfaces, allowing developers to focus on application logic rather than platform differences.

## Supported Platforms

| Platform | Status | Entry Point |
|----------|--------|-------------|
| Windows | ✅ Supported | `WindowsApp.Run()` |
| Linux | ✅ Supported | `LinuxApp.Run()` |
| macOS (Catalyst) | ✅ Supported | `MacCatalystApp.Run()` |
| Android | ✅ Supported | `AndroidApp.Run()` |
| iOS | ✅ Supported | `iOSApp.Run()` |

## Architecture

```
┌─────────────────────────────────────────────────────┐
│                   Your Application                   │
│                     (BaseApp)                        │
├─────────────────────────────────────────────────────┤
│                  DeviceServices                      │
│   Core │ Media │ Dialog │ File │ Gallery │ Store    │
├─────────────────────────────────────────────────────┤
│              Platform Implementations                │
│   Windows │ Linux │ macOS │ Android │ iOS           │
└─────────────────────────────────────────────────────┘
```

## Installation

### NuGet Package

```bash
dotnet add package SeasonEngine
```

### Build from Source

```bash
git clone https://github.com/SeasonRealms/SeasonEngine.git
cd SeasonEngine
dotnet build src/Season.csproj
```

## Quick Start

### 1. Create Your Application

```csharp
using Season.Basic;

public class MyApp : BaseApp
{
    public MyApp()
    {
        Title = "My Application";
        FullScreen = false;
        BasicResolution = new Vector2(1280, 720);
    }

    public override async void Create()
    {
        // Your application initialization logic here
    }
}
```

### 2. Launch on Your Target Platform

**Windows:**
```csharp
WindowsApp.Run(new MyApp());
```

**Linux:**
```csharp
LinuxApp.Run(new MyApp());
```

**Android (MainActivity.cs):**
```csharp
AndroidApp.Run(new MyApp());
```

## Core Services

SeasonEngine provides the following services through `DeviceServices`:

| Service | Interface | Description |
|---------|-----------|-------------|
| **Core** | `IDeviceCore` | Platform info, orientation, dark mode detection |
| **Media** | `IMediaPlayer` | Audio playback and volume control |
| **Dialog** | `IDialogService` | Message boxes and keyboard input dialogs |
| **File** | `IFileService` | File/folder picker, open and save operations |
| **Gallery** | `IGalleryService` | Media gallery access and management |
| **Record** | `IRecordService` | Camera and audio recording |
| **Download** | `IDownloadService` | Background download management |
| **Store** | `IStoreService` | In-app purchases and app store integration |
| **Ads** | `IAds` | Advertisement integration |

### Usage Examples

**Get Platform Information:**
```csharp
var platform = DeviceServices.Core.Platform;        // e.g., Platform.Windows
var orientation = DeviceServices.Core.Orientation;  // e.g., Orientation.LandscapeLeft
var isDark = DeviceServices.Core.IsDarkMode();      // true or false
```

**Play Audio:**
```csharp
DeviceServices.Media.PlayMedia("Music", "path/to/audio.mp3", "80");
DeviceServices.Media.SetVolume(music: 80, sound: 100);
```

**Show Dialog:**
```csharp
var result = await DeviceServices.Dialog.ShowMessage(
    title: "Confirm",
    desc: "Are you sure?",
    buttons: new[] { "Yes", "No" },
    text: "This action cannot be undone."
);
```

**Pick Files:**
```csharp
var files = await DeviceServices.File.PickFiles(
    fileType: FileType.Image,
    exts: new[] { ".png", ".jpg" },
    multiple: true,
    open: true
);
```

**Access Media Gallery:**
```csharp
var assets = await DeviceServices.Gallery.MediaGallery();
foreach (var asset in assets)
{
    Console.WriteLine($"{asset.Name} - {asset.Type}");
}
```

## Project Structure

```
src/
├── Basic/                  # Core abstractions and interfaces
│   └── DeviceServices.cs   # Central service registry
├── Platforms/              # Platform-specific implementations
│   ├── Windows/
│   ├── Linux/
│   ├── MacCatalyst/
│   ├── Android/
│   ├── iOS/
│   └── Shared/             # Shared code between platforms
├── Storage/                # Database, localization, and storage
├── Net/                    # HTTP client with progress support
└── Utils/                  # Extension methods and utilities
```

## Platform-Specific Features

For Windows-only features, use `IWindowsFeatures`:

```csharp
if (DeviceServices.WindowsFeatures != null)
{
    var volume = DeviceServices.WindowsFeatures.GetVolume();
    DeviceServices.WindowsFeatures.SetVolume(50);
    
    var apps = DeviceServices.WindowsFeatures.ListApps(ids: null, copyLogo: true);
}
```

## Requirements

- **.NET 10.0** or later
- **Windows**: Windows 10 version 1809 (10.0.17763.0) or later
- **Android**: API Level 21 (Android 5.0) or later
- **iOS/macOS**: iOS 15.0 / macOS 15.0 or later

## Contributing

Contributions are welcome! Please feel free to submit a Pull Request.

1. Fork the repository
2. Create your feature branch (`git checkout -b feature/AmazingFeature`)
3. Commit your changes (`git commit -m 'Add some AmazingFeature'`)
4. Push to the branch (`git push origin feature/AmazingFeature`)
5. Open a Pull Request

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## Links

- **Repository**: [https://github.com/SeasonRealms/SeasonEngine](https://github.com/SeasonRealms/SeasonEngine)
- **Issues**: [https://github.com/SeasonRealms/SeasonEngine/issues](https://github.com/SeasonRealms/SeasonEngine/issues)

---

*Built with ❤️ for cross-platform development*
