# SeasonEngine

SeasonEngine is a cross-platform C# graphics engine and application framework for building real-time 2D and 3D apps with a unified API.

**A cross-platform C# engine for people who want real-time graphics architecture to stay visible, understandable, and usable.**

This repository is now organized around three top-level areas:

- `Season/` - the reusable `SeasonEngine` core library
- `Apps/` - official applications and reference runtimes built on top of the engine
- `Samples/` - smaller framework examples, including the `Creator` series

<img src="Apps/Sun shine.jpg" width="750" alt="Sun shine" />

The goal of the project is not just to provide a rendering wrapper, but to make engine architecture understandable while still supporting real scene composition, post effects, compute workflows, interaction, and cross-platform execution.

Github: https://github.com/SeasonRealms/SeasonEngine

Demo: https://apps.microsoft.com/detail/9NHDQ4F67MHM

## Where This Sits

SeasonEngine is code-first and editor-free by design. There is no scene editor to learn and no project format to adopt: you declare `Sprite2D`, `Model`, and `InstancedModel` in ordinary C#, and `AddControl` / `AddPanel` define load order and layering for 2D and 3D alike. You never call `Draw` by hand, but update order stays yours to override.

What makes it different from the nearest starting point is the renderer. PBR materials, glTF animation, shadows, and global illumination are the baseline rather than an upgrade path, and there is one implementation per domain — glTF for animation, MSDF for text, one graphics API per platform, no OpenGL or WebGL fallback. Platform divergence is confined to `DeviceServices`, so code and visual results stay consistent everywhere.

## What This Repository Contains

### `Season/`

The core library provides:

- a `BaseApp` application host
- a recursive `Panel` tree for scene and UI composition
- drawable `Control` types for 2D, 3D, text, models, shapes, and textures
- platform backends for desktop, mobile, and web
- a fixed render-pass pipeline with optional shadow, post, and compute stages
- asynchronous resource loading for controls and panels

If you want to understand the reusable engine layer, start here:

- [Core library README](Season/README.md)

<img src="Apps/Debug mode.jpg" width="750" alt="Debug mode" />

### `Apps/`

`Apps/` contains the official higher-level applications built on top of `SeasonEngine`.

Current applications include:

- `Apps/Engine`
- `Apps/EngineWasm`
- `Apps/EngineWeb`

`Engine` is not a minimal demo. It is the reference application used to validate how the core library behaves when multiple systems run together in one app:

- real scene composition
- lighting and atmosphere
- compute-driven rendering effects
- picking and collision
- movement and camera interaction
- debug views
- early editor-style workflows

If you want to see how the engine is used in practice, start here:

- [Engine application README](Apps/Engine/README.md)

### `Samples/`

`Samples/` remains the home for lighter application-framework examples.

Current sample applications include:

- `Samples/Creator`
- `Samples/CreatorWasm`
- `Samples/CreatorWeb`

These projects are intended to be easier onboarding and framework examples, rather than the main reference runtime used to validate the full graphics stack.

<img src="Apps/Global illumination.jpg" width="750" alt="Global illumination" />

## Open Source Foundation, Commercial AI Layer

The Season project is intentionally split between an open source foundation and a commercial application layer.

### MIT Open Source Foundation

The following parts are intended to stay broadly usable as MIT-licensed building blocks:

- `Season/` as the core graphics engine and application framework
- the `Apps/Engine` reference runtime used to demonstrate the engine in practice
- foundational Season AI libraries such as `SeasonAudio`, `SeasonGGML`, `SeasonONNX`, `SeasonTTS`, `SeasonVision`, and related lower-level components

These layers are about capabilities: rendering, runtime architecture, inference backends, and reusable AI primitives.

### SeasonAI As The Commercial Layer

`SeasonAI` sits above those foundations as a product-oriented orchestration layer.

It is not primarily a new inference backend. Its value is in making local AI practical inside a real application:

- task-oriented UI panels
- model loading and backend selection
- generation result handling
- media export and workflow integration
- the heavy UI scheduling and state management needed by local AI apps

That is the layer intended to support commercial monetization.

In other words:

- the engine and foundational AI libraries remain open and reusable
- `SeasonAI` is where the productized local-AI application experience is assembled

### How It Connects Today

The current bridge happens inside `Apps/Engine`, where the app can host `Season.AI.Panels.AIButton` and `Season.AI.Panels.AIPanel` directly in the engine UI.

That integration lets the reference runtime demonstrate how a Season-based application can surface AI features without forcing the engine itself to become a closed product.

### Commercial Plan

The planned commercial path is:

- open source builds continue to expose the engine and foundational libraries
- the open source `AIPanel` surface can act as a notice, preview, and upgrade entry point
- store builds of the SeasonEngine application can offer in-app purchase unlocking for SeasonAI features
- licensed customers can obtain the commercial SeasonAI source package under its own license terms

Planned store entry:

- Windows Store application page: coming soon

This model aims to keep the ecosystem technically open at the foundation level while still allowing a practical commercial offering at the application layer.

<img src="Apps/Island picker.jpg" width="750" alt="Island picker" />

## Why SeasonEngine Exists

SeasonEngine is shaped by a few deliberate choices:

- **Explicit structure over hidden automation**  
  Lifecycle, loading, render passes, and ownership boundaries are visible in code.

- **Shared engine contracts over ad hoc helpers**  
  `BaseApp`, `Panel`, `IControl`, `IGraphics`, and `FrameSchedule` form the core model.

- **Cross-platform consistency without pretending all backends are identical**  
  The app-facing model stays shared, while D3D12, Vulkan, Metal, and web paths remain free to differ internally.

- **Real runtime concerns treated as first-class architecture**  
  Loading, resize, off-screen targets, synchronization, and feature fallback are part of the design, not patchwork.

This makes the project feel closer to a small real-time runtime than to a loose collection of rendering utilities.

## Core Capabilities

- Cross-platform app bootstrap for Windows, Linux, Android, iOS, Mac Catalyst, and Web
- 2D and 3D rendering through one composition model
- glTF model loading, animation support, instancing, and picking helpers
- MSDF-based text rendering
- scene camera, lighting, environment, shadow, and atmosphere systems
- compute-driven effects such as bloom, GTAO, TAA, DDGI, and sky atmosphere
- unified device services for dialogs, files, media, gallery, recording, downloads, store, and ads
- asynchronous loading queues for smoother startup and scene population

## Repository Architecture

At a high level, the repository is organized like this:

```text
SeasonEngine
├── Season/            Reusable core engine library
│   ├── Basic/
│   ├── Controls/
│   ├── Fonts/
│   ├── Models/
│   ├── Panels/
│   ├── Platforms/
│   ├── Rendering/
│   ├── Storage/
│   └── Utils/
├── Apps/
│   ├── Engine/        Reference runtime application
│   ├── EngineWasm/    WebAssembly-oriented engine host
│   └── EngineWeb/     Web-oriented engine host
└── Samples/
    ├── Creator/       Application-framework example
    ├── CreatorWasm/   WebAssembly-oriented framework example
    └── CreatorWeb/    Web-oriented framework example
```

And the runtime model looks like this:

```text
BaseApp
  -> Panel tree
     -> Controls (2D, 3D, text, models, shapes, ...)
        -> IGraphics backend
           -> Direct3D 12 / Vulkan / Metal / Web path
```

`Apps/Engine` builds on top of this model and shows what it looks like when scene content, overlay UI, debug tooling, and compute effects all run together in one application, while `Samples/Creator` and its web variants stay focused on framework-level examples.

## Rendering Model

SeasonEngine uses a fixed pass schedule rather than a full frame graph.

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

This is a deliberate tradeoff. It gives up some scheduling generality, but keeps the frame structure legible, easier to debug, and easier to align across multiple rendering backends.

## The Engine App

The `Engine` app is where the repository becomes more than a library.

It demonstrates:

- a mixed outdoor and indoor sample world
- procedural sky and atmosphere integration
- sea, mountains, rocks, props, and animated characters
- movement, jump skill, picking, and collision
- debug overlays for intermediate render targets
- runtime modes such as `Show`, `Play`, `Edit`, and `Debug`

It is best understood as a reference app and regression target, not as a beginner template.

By contrast, the `Creator` series under `Samples/` is better suited for application-framework examples and onboarding.

The model and font assets used in the demo scene are third-party works; their upstream sources and licenses are recorded in [MODEL_FONT_SOURCES_AND_LICENSES.md](Apps/Engine/Resources/Raw/Assets/MODEL_FONT_SOURCES_AND_LICENSES.md).

<img src="Apps/Image generation.jpg" width="750" alt="Image generation" />

## Getting Started

### Use The Core Library

Install from NuGet:

```bash
dotnet add package SeasonEngine
```

Or reference the project directly in this repository:

```xml
<ProjectReference Include="..\Src\Season.csproj" />
```

Minimal app:

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
    }
}
```

Run on Windows:

```csharp
using Season.Platforms.Windows;

WindowsApp.Run(new MyApp());
```

### Run The Reference App

Build the app:

```bash
dotnet build Apps/Engine/Engine.csproj
```

Run on Windows:

```bash
dotnet run --project Apps/Engine/Engine.csproj -f net10.0-windows10.0.19041.0
```

Run on Linux:

```bash
dotnet run --project Apps/Engine/Engine.csproj -f net10.0
```

For Engine-specific controls and runtime modes, see the [Engine application README](Apps/Engine/README.md).

## Supported Platforms

| Platform | Core Library Target | Backend Direction |
|----------|---------------------|-------------------|
| Windows | `net10.0-windows10.0.19041.0` | Direct3D 12 |
| Linux | `net10.0` | Vulkan |
| Android | `net10.0-android` | Vulkan |
| iOS | `net10.0-ios` | Metal |
| Mac Catalyst | `net10.0-maccatalyst` | Metal |
| Web | `net10.0-browser` | Web rendering path |

Actual runtime support also depends on the selected target, graphics backend status, platform SDKs, and native dependencies.

## Who This Repository Is For

This repository is a good fit if you want:

- a C# engine with visible runtime structure
- one composition model for scene content and overlay UI
- multi-backend rendering without giving up backend-specific control
- a reference app that exercises the engine in realistic ways
- smaller framework examples for onboarding and application scaffolding
- a codebase that prioritizes architecture clarity over tutorial-style minimalism

Two foundational libraries carry the low-level work, and the engine uses them deeply rather than wrapping them away:

- [**Silk.NET**](https://github.com/dotnet/Silk.NET) supplies the Direct3D 12 and Vulkan bindings behind the platform backends.
- [**SharpGLTF**](https://github.com/vpenades/SharpGLTF) powers glTF loading and animation — the one animation format the engine supports.

`Season/` keeps its own architecture above them: the render-pass model, the control tree, and the cross-backend contracts are the engine's, not the bindings'.

## Project Status

SeasonEngine is under active development.

The core architecture is already substantial, and the `Engine` app is a strong reference application, but APIs, backend coverage, and higher-level workflows are still evolving.

[`Roadmap.md`](Roadmap.md) is the current plan. It audits what is actually implemented against what is not — including the absent pieces, such as indirect draw, texture compression, and scene serialization — and orders future work by dependency rather than by appeal. Read it before assuming a capability exists.

## License

SeasonEngine is licensed under the MIT License.
