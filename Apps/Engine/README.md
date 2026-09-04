# Engine

`Engine` is the reference application for `SeasonEngine`.

It is not a minimal demo. It is the place where the core library is exercised as a real runtime: scene composition, real-time rendering, compute effects, asset loading, interaction, debug views, and early editor-style workflows all meet here.

Github: https://github.com/SeasonRealms/SeasonEngine

Demo: https://apps.microsoft.com/detail/9NHDQ4F67MHM

## Purpose

This project exists to answer a practical question:

> What does `SeasonEngine` look like when its rendering model, scene model, and application model are used together in one real app?

`Engine` is therefore used as:

- a rendering showcase
- a systems integration reference app
- a regression target for backend and shader work
- a sandbox for interaction and tool workflows
- an early foundation for a future editor/runtime host

It should be read as a reference application built on top of `Season/`, not as the public API surface of the engine package itself.

## Relationship To SeasonEngine

- `Season/` is the reusable engine library
- `Apps/Engine` is the concrete application that drives it

This app is intentionally heavier than a starter template. It validates how the engine behaves when multiple subsystems run together:

- recursive panel composition
- 2D and 3D controls in the same app
- asynchronous loading
- real scene lighting
- compute-driven post effects
- object picking and collision
- overlay UI and debug views

## What The Project Demonstrates

### 1. A Real Scene, Not A Synthetic Test Room

The app builds a small world rather than a single isolated rendering test:

- procedural sky and atmosphere integration
- celestial lighting split into its own runtime driver
- grassland, sea, rocks, mountains, and shoreline composition
- player, house, room, birds, robots, spheres, props, and street lights
- indoor and outdoor content in the same app

The scene is deliberately mixed. It is meant to pressure the engine in ways that a tiny demo cannot.

### 2. Fixed-Pipeline Rendering With Compute Extensions

The app registers engine effects into known `FrameSchedule` phases instead of building a custom renderer from scratch.

Current effect coverage in the sample includes:

- `PlasmaEffect`
- `SceneColorCopyEffect`
- `BloomEffect`
- `DepthViewEffect`
- `GtaoEffect`
- `VelocityViewEffect`
- `TaaEffect`
- `Sdf3DViewEffect`
- `DdgiEffect`
- `SkyAtmosphereEffect`

This makes `Engine` a practical validation target for:

- off-screen scene color
- post processing
- motion vectors
- temporal accumulation
- AO and GI experiments
- 3D texture compute paths
- procedural atmosphere rendering

### 3. Shared Scene And Overlay Composition

`Engine` uses the same `Panel` and `Control` model as the core library.

That means:

- world content is composed through panels
- overlay UI is also composed through panels
- debug thumbnails, settings, movement controls, and editor-style widgets are not a separate UI stack
- the project exercises render-domain separation inside one app model

This is important because it tests one of SeasonEngine's core architectural claims: scene content and app UI should coexist inside one understandable runtime structure.

### 4. Interaction, Editing, And Runtime Inspection

The app is not just for passive viewing.

It currently includes:

- movement controls
- camera drag/orbit behavior
- world and character movement modes
- long-jump skill logic
- object picking and highlight
- collision against registered obstacles
- terrain-aware movement constraints
- settings panel for runtime tuning
- debug overlay views for render targets and compute outputs
- a painting toolbar that previews meshes and explores placement-style workflows

So while `Engine` is still used as a reference project, it already behaves partly like a lightweight runtime tool.

## Runtime Modes

The app exposes several runtime modes:

- `Show`  
  Minimal viewing state with interaction overlays hidden.

- `Play`  
  Main gameplay-style mode with movement, skill UI, and camera interaction.

- `Edit`  
  Editor-like mode with object picking and the bottom painting toolbar.

- `Debug`  
  Diagnostics mode with debug thumbnails for intermediate render outputs.

These modes are valuable because they let one application exercise different usage patterns of the engine without changing the project itself.

## First Run Guide

### What You See After Startup

On first launch, the app starts in `Play` mode.

You should typically see:

- an outdoor scene with sky, terrain, sea, mountains, props, and animated actors
- a controllable camera view over the sample world
- a settings button in the overlay
- direction controls for player movement
- a `Jump` skill button

Depending on platform, backend, and current feature support, some advanced effects may appear with different quality levels or fall back internally.

### Basic Interaction

The main interactions are:

- **Drag**  
  Rotate the camera view. In character movement mode, the camera behaves more like an over-the-shoulder orbit around the player.

- **Direction buttons**  
  Move the player through the world. Movement goes through collision checks rather than ignoring scene geometry.

- **Jump button**  
  Trigger the long-jump skill animation and movement behavior.

- **Scene observation**
  The app is also meant to be watched, not only controlled. Lighting, atmosphere, post effects, and object behavior are part of the demonstration.

### How To Switch Modes

Mode switching is done through the settings panel.

1. Click or tap the settings button in the overlay.
2. Open the `Mode` field.
3. Choose one of the runtime modes:
   - `Show`
   - `Play`
   - `Edit`
   - `Debug`

What each mode changes in practice:

- `Show`  
  Hides most interactive overlays and leaves a cleaner presentation view.

- `Play`  
  Shows movement and skill controls and keeps the sample in its main runtime state.

- `Edit`  
  Enables picking and the bottom painting toolbar for placement-style experiments.

- `Debug`  
  Shows debug thumbnails for intermediate render outputs such as compute and post-processing views.

### Mode And UI Matrix

| Mode | Main Purpose | Settings Button | Movement Controls | Jump Skill | Picking Highlight | Painting Toolbar | Debug Views |
|------|--------------|-----------------|-------------------|------------|-------------------|------------------|-------------|
| `Show` | Clean presentation view | Hidden | Hidden | Hidden | Hidden | Hidden | Hidden |
| `Play` | Main runtime interaction | Visible | Visible | Visible | Hidden | Hidden | Hidden |
| `Edit` | Picking and placement experiments | Visible | Hidden | Hidden | Visible | Visible | Hidden |
| `Debug` | Render and compute diagnostics | Visible | Visible | Visible | Visible | Hidden | Visible |

This table reflects the current overlay behavior driven by `App.Update`, where each mode changes panel visibility by adjusting the active UI set rather than rebuilding the whole scene.

### Other Runtime Settings

The same settings panel also exposes additional runtime controls such as:

- movement mode
- camera FOV
- movement step size
- log-related options

That makes the sample useful not only for visual inspection, but also for runtime tuning and regression checking.

## Architecture Highlights

`Engine` is useful as documentation because its code reveals how `SeasonEngine` is expected to be used in practice.

### App-Level Orchestration

`App.cs` acts as the runtime coordinator:

- configures camera defaults and render quality defaults
- registers compute effects
- constructs the scene graph
- queues background loading work
- drives per-frame update order
- gates mode-specific overlays and tool panels
- coordinates picking, collision, and camera follow behavior

This makes the sample a good reference for how a non-trivial `BaseApp` can be structured.

### Asynchronous Scene Population

Heavy content is not forced into the startup path.

Examples in this sample:

- mountain extraction and instance preparation
- rock extraction and instance preparation
- font creation
- preview-asset extraction in the painting panel

This helps validate the engine's explicit loading model instead of relying on hidden lazy behavior.

### Separation Of Responsibilities

Several systems are intentionally split into focused units:

- `CelestialLighting` owns lighting, weather, and day-night state
- `Sky` owns visual skybox and celestial marker presentation
- `PlayerCollider` owns obstacle collection, sweep resolution, and terrain-aware movement blocking
- `Views` owns render-target debug visualization
- `Painting` owns asset preview and placement-style tooling

That structure is a useful signal for future engine and tool architecture.

## Project Layout

```text
Apps/Engine/
├── App.cs              Main runtime orchestration
├── Panels/             Scene panels, overlay panels, and tool panels
├── Management/         Runtime management helpers such as collision
├── Platforms/          Platform-specific app bootstrap
├── Resources/Raw/      Models, textures, icons, fonts, and other sample assets
└── Engine.csproj       Multi-target application project
```

Notable panel groups include:

- world panels such as `Ground`, `Sea`, `Sky`, `Mountains`, `Rocks`, `House`, `Room`, `Birds`, and `Robots`
- interaction panels such as `Direction`, `Skill`, and `Setting`
- tooling panels such as `Views` and `Painting`

## Building And Running

This project targets multiple platforms through .NET MAUI plus the Season platform backends.

Current target frameworks include:

- `net10.0`
- `net10.0-windows10.0.19041.0`
- `net10.0-android`
- `net10.0-ios`
- `net10.0-maccatalyst`

Typical commands:

```bash
dotnet build Apps/Engine/Engine.csproj
```

```bash
dotnet run --project Apps/Engine/Engine.csproj -f net10.0-windows10.0.19041.0
```

```bash
dotnet run --project Apps/Engine/Engine.csproj -f net10.0
```

Platform-specific workloads, SDKs, signing setup, graphics drivers, and native dependencies still apply.

## Asset And Dependency Notes

- The app includes local assets under `Resources/Raw/Assets`. The model and font assets used by this demo are documented, with their upstream sources and licenses, in [MODEL_FONT_SOURCES_AND_LICENSES.md](Resources/Raw/Assets/MODEL_FONT_SOURCES_AND_LICENSES.md).
- It references `Season/Season.csproj` directly.
- It currently also references a local `SeasonAI` project for AI panel integration inside the sample app.

The README for `Engine` is still focused on the graphics/runtime side of the project rather than documenting every optional integration in detail.

## Relationship To Samples

`Engine` now lives under `Apps/` because it has grown beyond the role of a lightweight sample and functions as an official reference application and runtime host for the engine.

The `Samples/` directory remains important, but it serves a different purpose: it keeps smaller application-framework examples, including the `Creator` series, which are better suited for onboarding and focused feature demonstrations.

## Why This Sample Matters

Many engines look clean in isolated examples and become much harder to understand once real features interact.

`Engine` is valuable because it keeps those interactions visible:

- the render pipeline is exercised under real scene content
- backend work can be checked against an application with actual visual expectations
- collision, camera, UI, tools, and assets coexist in one runtime
- new features can be validated without first inventing a separate test harness

In short, `Engine` is where `SeasonEngine` stops being a library in theory and starts behaving like a real engine runtime.

## Current Status

This project is still evolving.

It already functions as a strong reference application, but parts of it also point toward a future editor/runtime direction. Because of that, some systems are showcase-oriented, some are debugging-oriented, and some are early tooling experiments.

That mixed character is intentional at this stage.
