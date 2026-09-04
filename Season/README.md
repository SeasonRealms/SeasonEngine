# SeasonEngine

SeasonEngine is the core `Season` graphics library in this repository.

This README is intentionally focused on the `Season/` package only. It does not try to document the higher-level applications under `Apps/` or the framework examples kept under `Samples/`.

Github: https://github.com/SeasonRealms/SeasonEngine

Demo: https://apps.microsoft.com/detail/9NHDQ4F67MHM

## Overview

SeasonEngine is a cross-platform C# graphics engine and application framework for building real-time 2D and 3D apps with a unified API.

The library combines:

- a `BaseApp` application host
- a recursive `Panel` tree for scene and UI composition
- drawable `Control` types for 2D, 3D, text, models, shapes, and textures
- platform backends for desktop, mobile, and web
- a fixed render-pass pipeline with optional shadow, post, and compute stages
- asynchronous resource loading for controls and panels

Everything above them — the `BaseApp` host, the panel/control tree, `FrameSchedule`, and the cross-backend contracts — is the engine's own layer.

The project is built around the idea of making rendering architecture understandable without reducing it to a toy sample.

## Core Capabilities

- Cross-platform app bootstrap for Windows, Linux, Android, iOS, Mac Catalyst, and Web
- 2D and 3D rendering through a shared control model
- glTF model loading, animation support, instancing, and picking helpers
- MSDF-based text rendering integrated into the engine
- Scene camera, lighting, environment, shadow, and atmosphere systems
- Compute-driven effects such as bloom, GTAO, TAA, DDGI, sky atmosphere, and debug views
- Unified device services for dialogs, files, media, gallery, recording, downloads, store, and ads
- Background loading queues that keep app startup and frame execution responsive

## Programming Model

The main abstraction layers are:

```text
BaseApp
  -> Panel tree
     -> Controls (Sprite2D, Texts, Shape, Mesh3D, Model, InstancedModel, ...)
        -> IGraphics backend
           -> platform renderer (Direct3D 12 / Vulkan / Metal / WebGPU path)
```

- `BaseApp` owns lifecycle, resolution, settings, logging, camera state, and the global loading queue.
- `Panel` is a recursive container for scene content and overlay UI.
- `IControl` represents a drawable leaf node that can be loaded, updated, drawn, and disposed independently.
- `FrameSchedule` orchestrates the render pipeline through fixed stages such as `Shadow`, `Scene`, `Post`, `FinalBlit`, and `Overlay`.

## Design Philosophy

SeasonEngine is shaped by a few deliberate engineering choices.

- **Prefer explicit structure over hidden automation**  
  The engine favors visible lifecycle stages, visible render passes, and visible ownership boundaries. `BaseApp`, `Panel`, `IControl`, `FrameSchedule`, and `DeviceServices` are meant to expose how the runtime is organized instead of collapsing everything into a single opaque abstraction.

- **Prefer a small number of strong engine contracts over many ad hoc helpers**  
  Instead of inventing separate models for UI, scene objects, loading tasks, and platform entry behavior, SeasonEngine reuses a few stable concepts and lets more features grow around them.

- **Prefer cross-platform consistency without forcing artificial sameness**  
  The app-facing model stays shared, but platform backends are allowed to remain honest about their internal differences. The goal is not to erase D3D12, Vulkan, Metal, and Web distinctions; the goal is to keep the engine contract stable above them.

- **Prefer real runtime concerns over tutorial-only simplifications**  
  Loading, disposal, resize, off-screen targets, synchronization boundaries, and feature fallback are treated as part of the architecture, not as afterthoughts to bolt on once samples become more ambitious.

- **Prefer understandable extensibility over maximal abstraction**  
  New features are expected to enter through known places such as render-pass slots, compute phases, control types, or shared rendering contracts. That keeps extension points discoverable and reduces the chance that the engine grows into a pile of one-off pathways.

## Architecture Tradeoffs

These choices lead to several intentional tradeoffs:

- **Fixed pass schedule instead of a full frame graph**  
  This gives up some scheduling generality, but makes the frame structure easier to inspect, debug, and align across backends.

- **Shared scene/UI composition model instead of separate frameworks**  
  This reduces conceptual fragmentation, but it also means the core abstractions need to carry both scene and overlay responsibilities cleanly.

- **Explicit load readiness instead of implicit lazy magic**  
  The engine asks types to participate in loading through `ILoadable`, which is more verbose than background auto-loading, but much clearer when dealing with resource lifetime and backend synchronization.

- **Backend-specific internals under shared engine contracts**  
  This avoids a lowest-common-denominator renderer, but it also means backend work remains substantial and feature parity may land incrementally.

- **Graceful degradation instead of pretending every target supports everything**  
  Some features can fall back, bypass themselves, or remain disabled on a given path. That adds branching in the engine, but keeps the library practical across a wider range of targets.

## Technical Architecture Highlights

SeasonEngine applies those philosophy decisions in a few visible ways:

- **One app model across all targets**  
  Your app still starts from `BaseApp` regardless of whether the runtime is Windows, Linux, Android, iOS, Mac Catalyst, or Web. Platform bootstraps differ, but the app model stays the same.

- **A strict container/leaf split**  
  `Panel` is a recursive container, while drawable objects are `IControl` leaves. This keeps scene composition, UI composition, and rendering responsibilities clearly separated.

- **2D and 3D share the same scene tree**  
  Sprites, text, shapes, meshes, and models all participate in the same update and draw structure. Overlay UI is not a separate framework layered on top later; it is part of the same composition model with render-domain separation.

- **Loading is treated as a first-class lifecycle stage**  
  The `ILoadable` contract allows controls and selected panels to enter a shared asynchronous loading queue. That keeps resource preparation, readiness, disposal, and resize coordination explicit instead of scattering them across ad hoc background tasks.

- **Rendering is organized as a fixed pass pipeline**  
  Instead of hiding everything behind an opaque frame graph, SeasonEngine uses explicit pass slots such as `Shadow`, `Scene`, `Post`, `FinalBlit`, and `Overlay`. This keeps backend integration easier to reason about and makes the frame shape visible in engine code.

- **Compute effects plug into known phases**  
  Compute work is registered into fixed execution points such as `FrameStart` and `AfterScene`. Effects like bloom, GTAO, TAA, DDGI, depth debug views, and atmosphere passes extend the pipeline without changing the app-facing scene model.

- **Shared engine contracts with backend-specific implementations**  
  The public engine model is shared, while D3D12, Vulkan, Metal, and Web-specific code lives under `Platforms/`. This keeps high-level behavior aligned without pretending all backends are identical internally.

- **Capability fallback is part of the architecture, not a patch**  
  Features such as off-screen rendering, compute effects, motion vectors, cubemaps, or specific quality tiers can degrade cleanly when a backend or target does not support the full path.

## Supported Platforms

| Platform | Target | Entry Point | Backend Direction |
|----------|--------|-------------|-------------------|
| Windows | `net10.0-windows10.0.19041.0` | `WindowsApp.Run(app)` | Direct3D 12 |
| Linux | `net10.0` | `LinuxApp.Run(app)` | Vulkan |
| Android | `net10.0-android` | `AndroidApp.Run(app)` | Vulkan |
| iOS | `net10.0-ios` | `iOSApp.Run(app)` | Metal |
| Mac Catalyst | `net10.0-maccatalyst` | `MacCatalystApp.Run(app)` | Metal |
| Web | `net10.0-browser` | `WebApp.Run(app, ...)` | WebGPU-style web path |

## Backend Strategy

SeasonEngine is not a thin wrapper over a single graphics API. It is a shared engine layer with multiple rendering backends.

```text
Shared engine code
  -> app lifecycle
  -> panels and controls
  -> camera / lighting / picking
  -> render-pass scheduling
  -> compute effect registration

Platform backend code
  -> Windows: Direct3D 12
  -> Linux / Android: Vulkan
  -> iOS / Mac Catalyst: Metal
  -> Web: browser-hosted rendering path
```

In practice this means:

- engine-facing concepts like `Model`, `Mesh3D`, `Texts`, `RenderTarget`, `Camera3D`, and `SceneLighting` stay shared
- platform backends are responsible for resource creation, pass begin/end, uploads, synchronization, and presentation
- backend code can differ significantly internally while still conforming to the same engine contracts
- new rendering features usually land first as shared contracts, then get implemented backend by backend

This is another explicit tradeoff: the engine avoids collapsing everything into a fake universal renderer, because that usually hides the very details that matter most when graphics features become more advanced.

## Installation

Install from NuGet:

```bash
dotnet add package SeasonEngine
```

For local development inside this repository, reference `Season/Season.csproj` directly.

## Quick Start

Create a minimal app:

```csharp
using System.Numerics;
using Season.Basic;

public sealed class MyApp : BaseApp
{
    public MyApp()
    {
        Title = "My Season App";
        DesignResolution = new Vector2(1280, 720);
        BasicResolution = new Vector2(1280, 720);
        BackgroundColor = Colors.White;
    }

    public override void Create()
    {
        base.Create();

        // Add panels and controls here.
    }
}
```

Run it on a target platform:

```csharp
using Season.Platforms.Windows;

WindowsApp.Run(new MyApp());
```

```csharp
using Season.Platforms.Linux;

LinuxApp.Run(new MyApp());
```

```csharp
using Season.Platforms.Android;

AndroidApp.Run(new MyApp());
```

```csharp
using Season.Platforms.iOS;

iOSApp.Run(new MyApp());
```

```csharp
using Season.Platforms.MacCatalyst;

MacCatalystApp.Run(new MyApp());
```

Web uses `WebApp.Run(app, jsRuntime, httpClient, ...)` and is typically hosted from a browser entry project.

## Device Services

SeasonEngine exposes platform features through `DeviceServices`.

| Service | Interface | Purpose |
|---------|-----------|---------|
| Core | `IDeviceCore` | Platform info, orientation, dark mode, basic environment data |
| Media | `IMediaPlayer` | Audio playback and volume |
| Dialog | `IDialogService` | Message dialogs and text input |
| File | `IFileService` | Open, save, and pick files |
| Gallery | `IGalleryService` | Media gallery access |
| Record | `IRecordService` | Camera and audio recording |
| Download | `IDownloadService` | Download and export workflows |
| Store | `IStoreService` | Store integration and purchases |
| Ads | `IAds` | Advertising integration |

Example:

```csharp
var platform = DeviceServices.Core.Platform;
var isDarkMode = DeviceServices.Core.IsDarkMode();

var files = await DeviceServices.File.PickFiles(
    fileType: FileType.Image,
    exts: new[] { ".png", ".jpg" },
    multiple: true,
    open: true);
```

Windows-specific helpers are available through `DeviceServices.WindowsFeatures`.

## Rendering Pipeline

SeasonEngine uses a fixed pass schedule instead of a full frame graph.

Typical flow:

```text
FrameStart Compute
  -> Shadow
  -> Scene
  -> AfterScene Compute
  -> Post
  -> FinalBlit
  -> Overlay
```

This structure keeps backend integration explicit while still allowing higher-end features such as:

- off-screen scene color
- shadow maps
- HDR scene rendering
- bloom
- GTAO
- TAA and motion vectors
- DDGI experiments
- procedural sky and atmosphere compute passes

The tradeoff is deliberate: a fixed schedule is less open-ended than a graph-driven renderer, but it makes the pipeline legible, easier to align across backends, and easier to evolve step by step.

A few implementation choices are especially important:

- render targets are explicit engine objects rather than hidden transient state
- size-dependent targets can be rebuilt on resize while preserving the higher-level schedule
- post and compute stages are optional and leave no required residue when disabled
- antialiasing and quality features are driven through `RenderQuality`, allowing the pipeline to scale from simpler paths to more advanced ones

## Scene, UI, And Resource Lifetime

SeasonEngine is built around long-lived engine objects rather than one-off draw calls.

- `BaseApp` owns global state such as camera, settings, logs, and lighting
- `Panel` instances organize scene and UI composition recursively
- controls become drawable only after their load stage completes
- disposal and resize are explicit parts of the engine lifecycle
- text, models, textures, and compute resources are managed as backend-owned resources behind shared C# objects

This gives the engine a structure that is closer to a small real-time runtime than to a collection of rendering helpers. That bias is intentional: SeasonEngine favors stable object lifetime and explicit ownership over short-lived convenience wrappers.

## Assets, Text, And Scene Content

SeasonEngine includes several engine-facing systems that matter in day-to-day app code:

- `Controls/` for sprites, shapes, text, meshes, models, and instanced content
- `Models/` for glTF import, animation playback, picking data, and helper utilities
- `Fonts/` for MSDF-backed glyph generation and layout metrics
- `Rendering/` for camera, bounds, frustum, lighting, environment, picking, and effects
- `Panels/` for scene/UI composition and editor-style interaction helpers
- `Storage/` for settings, localization, file storage, and touch input state

## Project Layout

```text
Season/
├── Basic/        Core app host, device services, graphics abstractions
├── Controls/     Drawable 2D and 3D control types
├── Fonts/        Font loading and MSDF text support
├── Models/       glTF loading, animation, picking helpers
├── Net/          Networking helpers
├── Panels/       Recursive containers and interaction panels
├── Platforms/    Platform bootstrap and backend implementations
├── Rendering/    Render pipeline, camera, lighting, effects
├── Storage/      Settings, localization, storage, touch services
├── Utils/        Math, JSON, collections, and helper extensions
└── Season.csproj Package definition for SeasonEngine
```

## Relationship To Apps And Samples

This package is the reusable core library.

The repository also contains:

- `Apps/`, which hosts official applications built on top of SeasonEngine, including the `Engine` reference runtime
- `Samples/`, which keeps lighter framework-oriented examples such as the `Creator` series

Those projects are important for validation and onboarding, but they are not the API surface of the package itself. This README stays focused on the reusable `Season/` layer.

Two foundational libraries are part of the engine's core rather than an add-on:

- [**Silk.NET**](https://github.com/dotnet/Silk.NET) provides the low-level Direct3D 12 and Vulkan bindings used by those two `IGraphics` backends.
- [**SharpGLTF**](https://github.com/vpenades/SharpGLTF) powers glTF model loading and animation playback — the single animation format the engine supports.

## Requirements

- .NET 10
- Windows 10 version `10.0.17763.0` or later for the Windows target
- Android API level `33` or later for the Android target
- iOS `15.0` or later for the iOS target
- macOS `15.0` or later for the Mac Catalyst target

Actual runtime support also depends on the graphics backend and native dependencies used by the selected target.

## Status

SeasonEngine is under active development. The architecture is already substantial, but some APIs and feature combinations are still evolving alongside the sample applications and backend work.

## License

SeasonEngine is licensed under the MIT License.
