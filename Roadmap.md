# SeasonEngine Roadmap

> **Last audited against the source: 2026-11-07.** The previous audit was 2026-11-01. In the days between them, more
> work landed outside this plan than inside it: cloth, volumetric fog, decals, weather, water, GPU particles,
> screen-space reflections, multi-view rendering and the frame profiler all ship today. The baseline below was rewritten
> rather than appended to, because nine rows that said **Absent** were describing a repository that no longer exists.
> Section 0.5 records the unplanned work. Track F and Track P are new and are the two places where the next year of
> effort actually goes.

## How to read this

Work is grouped into tracks that map to the questions the project actually faces:

| Track | Question it answers | Audience it serves |
|---|---|---|
| **A. Rendering** | Is the renderer genuinely next-gen? | The developers who evaluate the repo |
| **B. Host & worlds** | Can someone see what the engine does without reading the source? | Evaluators, and eventually creators |
| **C. AI** | Is the paid layer worth money, and does it help make games? | Buyers |
| **D. Foundations** | Can any of the above be proven, maintained, or shipped? | Everyone, including future contributors |
| **F. Agent surface** | Can a machine drive the engine as well as a person can? | The next audience: tooling, automation, and the paid layer's own workflow |
| **P. Performance** | Does the frame budget hold, and can every claim above be measured? | Whoever runs this on a laptop GPU |
| **E. VR / XR** | Can the renderer claim reach the platforms where single-view assumptions stop applying? | A later audience, after the desktop story ships |
| **X. Simulation & network** | Can the engine carry physics and a network client without becoming a different engine? | Later users; decoupled on purpose so a contributor can start either at any time |

Track D is not in the original three. It exists because several items in A, B, and C are unprovable or unshippable
without it. Track F is newer, and it comes from a single observation: this engine's behaviour model — one C# class per
behaviour, a world record that is a type name plus construction parameters — is already a command surface. An agent
needs no node graph and no inspector to drive it; it needs three verbs and a way to see the result. An agent is also a
far cheaper way to keep a demonstration honest than a person clicking through it every release. Track P is what used to
be scattered across A and D: the work that decides whether the frame budget survives contact with integrated hardware,
where most of this engine's audience will actually run it. Track E stays deliberately last, and its only pre-payment —
A0.4, the per-view matrix split — has already shipped. Track X exists only to hold two decisions still — physics and
networking — and to state in one place that neither blocks anything above it.

**Ordering inside each track is by dependency, not by appeal.** Where an item is blocked by another, it says so.
Several attractive features are deliberately placed late because their prerequisites are infrastructure work across
four backends.

Status vocabulary used throughout:

- **Shipped** — implemented and exercised by a running app. Where a shipped item still has a named limit, the row says
  so; "shipped" is never a claim of completeness.
- **Partial** — exists but incomplete, or implemented on some backends only
- **Absent** — no code exists

---

## 0. Verified baseline

Audited against the source rather than against prior documentation. This section exists so that future planning does
not repeat the archive's mistake of describing goals without establishing the starting point.

### Rendering

| Capability | Status | Evidence |
|---|---|---|
| Fixed pass schedule: FrameStart Compute → Shadow → Scene → AfterScene Compute → Post → FinalBlit → Overlay | Shipped | `Season/Rendering/RenderPass.cs` (`FrameSchedule`) |
| Compute effect registration with graceful backend degradation | Shipped | `Season/Rendering/Compute.cs` — `RegisterCompute` returns false and leaves no residue |
| Four backends: D3D12 / Vulkan / Metal / WebGPU, each with its own shader implementation | Shipped | Three separate shader compilers plus WGSL |
| Bloom, GTAO, TAA, DepthView | Shipped, four backends | `Season/Rendering/Effects/` |
| DDGI (SDF volume + probe irradiance atlas, Chebyshev occlusion) | Shipped | `Effects/Ddgi.cs` (2790 lines) |
| Sky atmosphere, day/night cycle, procedural cloud noise | Shipped | `Effects/SkyAtmosphere.cs` (3267 lines), `Rendering/DayNightCycle.cs` |
| Cascaded shadow maps | Shipped | `Rendering/CascadedShadow.cs` — 3 cascades + 1 punctual, 4 atlas slots |
| Velocity buffer | Shipped | `RenderPass.cs` `VelocityTarget`; consumed by TAA |
| CPU frustum culling, per-instance broad phase, shadow culling | Shipped, on by default | `Rendering/Frustum.cs` (Gribb-Hartmann), `RenderQuality.FrustumCulling` |
| GPU instancing with per-instance skinning and morph targets | Shipped | `Controls/InstancedModel.cs`, `Controls/InstancedMesh3DBase.cs` |
| Per-instance surface picking and highlight modes | Shipped | `Season/Panels/ObjectPicker.cs`, `Rendering/Highlight.cs` |
| glTF loading and animation playback | Shipped | `Models/GltfAsset.cs`, `Models/GLTFAnimationPlayer.cs` |
| MSDF text rendering | Shipped | `Season/Fonts/`, glyph atlas manager across all backends |
| Scene normal target | Shipped | `RenderPass.cs` — MRT slot 2: octahedral normal, Toksvig roughness, SSR reflection weight |
| Indirect draw and dispatch | Shipped, four backends | `Rendering/Indirect.cs` — `DrawIndexedIndirect`, `vkCmdDrawIndexedIndirect`, indirect command buffers, `drawIndexedIndirect` |
| GPU-driven instance culling | Shipped, without occlusion | `Rendering/InstanceCull.cs` — 5 slots (3 cascades + punctual + main view), 16 primitives, indirect gather behind `SEASON_CULL_INDIRECT` |
| GPU particles | Shipped, unsorted | `Effects/Particles.cs` + four shader sources; flame, spark and smoke in `Apps/Engine/Panels/Campfire.cs` |
| Screen-space reflections | Shipped | `Effects/SsrEffect.cs`, with `Effects/HiZ.cs` as its depth pyramid and a DDGI fallback on miss |
| Decals | Shipped, screen-space | `DecalEffect.cs`, `DecalPipeline.cs`, `SceneDecals.cs`, four shader sources — modulate and additive, roughness written into the normal target |
| Volumetric fog and light shafts | Shipped, four backends | `Effects/VolumetricFog.cs` + four shader sources, `FogVolume.cs`, `SceneFog.cs`, `FogMath.cs`; aerial-perspective froxel volume |
| Weather regions, rain curtain, lightning | Shipped | `Rendering/Weather.cs`, `WeatherVisualState.cs`, `Effects/WeatherCurtain.cs`, `Effects/Lightning.cs`; `WeatherField` samples regions on the CPU and packs them for the GPU |
| Water surface, river ribbons, waterfalls | Shipped, no planar reflection | `WaterEffect.cs`, `WaterPipeline.cs`, `WaterRibbonEffect.cs`, `WaterfallEffect.cs`, four shader sets |
| Cloth simulation (XPBD) | Shipped, four backends | `Effects/Cloth.cs` + four shader sources — compute, three vertex streams, rim loop |
| Motion blur | Shipped | `Effects/MotionBlur.cs` |
| Depth of field | Shipped | `Effects/DepthOfField.cs` |
| Colour grading | Partial | `ColorGrading.cs` — seven coefficients and presets; no 3D LUT, deferred on purpose |
| MSAA 4x | Shipped, four backends | DX `MsaaSceneRT`, a private MSAA pair in Vulkan, `MTLMsaaTarget`, a private target on Web |
| Multi-view, up to 8 views | Shipped | `FrameState.cs`, `ViewTargets.cs`, `RenderView.cs`, `SecondaryView.cs`, `ViewSlot` |
| Per-view matrix split (A0.4) | Shipped | Zero `static Camera` left; view and projection are per view, not per object |
| GPU skinning | Shipped, four backends | `InstanceSkinning.cs` + four shader sources; the palm palette moved off the bone CBV onto a storage binding |
| Animation LOD | Shipped | `InstancedModel.AnimationLod.cs` — distance-tiered sampling at full, half and quarter rate |
| Frame profiler | Shipped, GPU timing on D3D12 only | `FrameProfiler.cs` — per-pass GPU and CPU time, p50/p95 over a 96-frame window, per-frame allocation, GC counts, HUD, CSV sink |
| **Texture compression** (BC / ASTC / ETC2 / KTX2) | **Absent** | No compressed-format path in texture loading |
| Occlusion culling | Absent | The Hi-Z pyramid is SSR's acceleration structure, not an occlusion test |
| Terrain system, heightmaps, tessellation | Absent | — |
| Mesh LOD, decimation, impostors | Absent | Animation LOD exists; geometry LOD does not |
| **VR / XR of any kind** (OpenXR, stereo rendering, HMD tracking, controller input) | **Absent** | Zero matches for `OpenXR`, `XR_`, `HMD`, `foveated`; the only `Stereo` hit is an audio channel count in `LinuxImage.cs` |
| glTF **export** | Absent | Import only |
| Mesh processing (decimation, UV repair, normal repair) | Absent | — |
| Material instances / variants, batch merging by material | Absent | Materials come from glTF import |
| Animation blending and state machines | Absent | One clip at a time, switched as a cut |

Note on the render pipeline shape: the D3D12 pipeline states are organised as Opaque / Transparent / Fade with cull
variants (`Platforms/Windows/DirectX/Pipeline.cs`), i.e. **forward rendering**, with a storable depth target, a
velocity target and — since A0.2 — a normal target. There is still no G-buffer. That single fact is why decals are a
screen-space pass rather than a deferred one, and why SSR reads `SceneNormal` instead of reconstructing it from depth.

The scene path is also no longer dual. `RenderPass.cs` now requires `ISegmentedSceneGraphics` and throws without it:
collect, then replay opaque, composites and transparent. The old parallel path is gone rather than flagged off, which is
why `split` can only insert composite passes now.

### Host, editor, worlds

| Capability | Status | Evidence |
|---|---|---|
| `Show` / `Play` / `Edit` / `Debug` mode enum | Shipped | `Apps/Engine/App.cs:7-12`, default `Mode.Play` |
| Object selection, per-instance picking, highlight | Shipped | `Season/Panels/ObjectPicker.cs` |
| Drag vs. click discrimination, object dragging | Shipped | `ObjectPicker.cs:53` — 20 px threshold separates click from drag |
| Property editing (position, size) | Partial | `ObjectPanel` in `ObjectPicker.cs:663+` — fixed fields, no generic property grid |
| Player collision, occluder fade | Shipped | `Apps/Engine/Management/PlayerCollider.cs`, `OcclusionFade.cs` |
| Island scene with dynamic behaviour (movement, jumping, birds, robots) | Shipped, hardcoded | `Apps/Engine/App.cs` `Create()`; behaviour in `Panels/Direction.cs`, `Skill.cs`, `Player.cs`, `Birds.cs` |
| Debug views for intermediate buffers | Shipped | `DepthView`, `NormalView`, `VelocityView`, `Sdf3DView` |
| Secondary views and rear-view mirrors | Shipped | `Rendering/SecondaryView.cs`, `Apps/Engine/Panels/RearViews.cs`, `Views.cs` |
| **Scene serialization, save, load** | **Absent** | No serializer, no scene format, no world file |
| **Empty / sceneless startup** | **Absent** | `Create()` builds the entire island unconditionally |
| Scene hierarchy inspection | Absent | — |
| Asset library or catalogue | Absent | Model paths are hardcoded per scene class |
| Undo / redo | Absent | — |

### AI

| Capability | Status | Evidence |
|---|---|---|
| Text generation and translation | Shipped | `SeasonAIs/SeasonLLM` — llama.cpp / GGUF |
| Image generation and editing (SD1.5, SDXL, SD3.5, Qwen-Image-Edit, Flux.2-Klein, Ovis, LongCat) | Shipped | `SeasonAIs/SeasonImage`; panels in `SeasonStudio/Panels/ImagePanel.cs` |
| Video generation | Shipped | `SeasonStudio/Panels/VideoPanel.cs` |
| Music generation | Shipped | `SeasonAIs/SeasonMusic` — StableAudio |
| Speech to text | Shipped | `SeasonAIs/SeasonSTT` — Whisper / GGUF |
| Text to speech, dual ONNX + GGML backends | Shipped | `SeasonAIs/SeasonTTS`; panel in `SeasonStudio/Panels/TTSPanel.cs` |
| Vision / OCR | Shipped | `SeasonAIs/SeasonVision`, `SeasonAIs/SeasonOCR` |
| 3D generation (image to mesh) | Shipped, single backend | `SeasonAIs/SeasonThreeD` — a Trellis runner with session, progress and result parsing; no mesh post-processing |
| **AI layer as a separate host** | **Shipped** | `SeasonStudio/SeasonStudio.csproj` references `Season.csproj` plus ten `SeasonAIs` libraries; `Apps/Engine/Engine.csproj` references none of them |
| Model acquisition | By hand | Panels link to Hugging Face; the app does not download models |
| **Generated output flowing back into the engine as a scene asset** | **Partial** | `SeasonStudio/Controls/ModelPreview.cs` previews a generated mesh inside an engine control; nothing binds a generated image to a scene material |
| Network client | Partial | `Season/Net/WebClient.cs` — multipart upload and download with progress and cancellation; no streaming chat completion, no key storage |
| **Cloud LLM and runtime C# compilation** | **Absent** | Zero `Microsoft.CodeAnalysis` references anywhere in the solution |
| **Auto-rigging, animation synthesis** | **Absent** | — |

### Foundations

| Capability | Status | Evidence |
|---|---|---|
| Assertion-based tests, no framework | Partial | Five console projects under `Tests/` — `Season.Rendering.{Tests,DirectX.Tests,Vulkan.Tests,Metal.Tests,Web.Tests}` — with hand-rolled `Check` / `Near` / `Throws` helpers. Real assertions on layout and parameter contracts; no xUnit, no CI |
| Benchmark scene with recorded numbers | Absent | A fixed camera tour and a published table still do not exist |
| Screenshot A/B harness | Partial | `Apps/Engine/AbCapture.cs` plus `.dbg/ab_pixel_diff.ps1`; the S0 acceptance gate defines four rulers, not a CI job |
| Draw-order trace | Partial | `Rendering/DrawTrace.cs` — records submission order so a refactor cannot silently reorder state changes |
| CPU frame-time budget | Partial | `Rendering/CostProfile.cs`, `ICostProfileScene.cs` — per-scene CPU cost, no threshold and no regression gate |
| Frame allocation and GC measurement | Shipped, no target | `FrameProfiler.cs` records bytes allocated per frame and Gen0/1/2; there is no zero-allocation contract behind it yet |
| Feature-by-backend support matrix in docs | Absent | `docs/features` carries a hand-maintained matrix that is already out of date |
| Asset import caching | Absent | glTF is re-parsed on every run |
| CI of any kind | Absent | No workflow file, so none of the above runs on a commit |

---

## 0.5 Shipped, but never on this roadmap

Everything below was built without appearing in any prior version of this document. It is recorded separately rather
than folded into the baseline for one reason: a roadmap that quietly absorbs whatever happened cannot be used to argue
about what should happen next. Each row names what the feature is **and what it is not**, because most of the
misreadings in this repository come from the gap between the two.

| Shipped | What it is | What it is not |
|---|---|---|
| Cloth simulation (`Effects/Cloth.cs`, four shader sources) | XPBD on the GPU, compute-simulated, three vertex streams, a pinned rim for a flagpole | No collision against scene geometry, no self-collision, no cloth-to-cloth coupling. One solver, one material model |
| Weather (`Weather.cs`, `WeatherField`, `WeatherCurtain.cs`, `Lightning.cs`) | Regional weather field — axis-aligned ellipses with priority, edge feather and a stable sort by id — sampled on the CPU, packed into three `float4` rows per region and consumed by cloud, rain and snow shaders. Lightning is a channel with a flash term in the same frame header | Not a climate simulation, no wind advection of the field, no wetness response on materials. Regions are authored, not simulated |
| Volumetric fog (`Effects/VolumetricFog.cs`, four shader sources, `FogVolume.cs`, `SceneFog.cs`, `FogMath.cs`) | A froxel volume with scatter and extinction injection, a CPU reference implementation that the GPU must match, punctual-lamp shadowing so a lamp in a valley casts a shaft, and a depth-slice accumulation pass | Not a global participating-medium solution. There is no multi-scatter, no temporal reprojection of the froxel history |
| Decals (`DecalEffect.cs`, `DecalPipeline.cs`, `DecalVolume.cs`, four shader sources) | Screen-space projected boxes: 36 programmatic vertices, front-face culled so back faces cover each pixel once, depth sampled and never attached, roughness written into the normal target's blue channel only | Not deferred decals. Two blend modes only (modulate, additive), 64 active decals, no per-pixel nearest-wins rule |
| Water (`WaterEffect.cs`, `WaterRibbonEffect.cs`, `WaterfallEffect.cs`) | A surface pass with screen-space refraction from the depth target, river ribbons that follow a spline, waterfall sheets, all four backends | Not a water system. No planar reflection, no FFT or clipmap height field, no shoreline foam from wave energy |
| GPU particles (`Effects/Particles.cs`) | Compute simulation over a persistent buffer, indirect draw of the alive set, and flame, spark and smoke emitters in `Panels/Campfire.cs` | No sorting. Alpha-blended particles are therefore limited to additive and alpha-tested; sorted transparency is still unbuilt |
| Screen-space reflections (`SsrEffect.cs`, `HiZ.cs`) | A screen-space march with a four-level depth pyramid and a DDGI irradiance fallback on miss | **Not occlusion culling.** `HiZ.cs` is SSR's acceleration structure. Occlusion is still absent, and this is the most frequently misread file in the repository |
| GPU-driven culling (`InstanceCull.cs`) | Per-instance bounds in a storage buffer, a compute pass writing compacted lists into five slots, original-index indirection, indirect gather switched by `SEASON_CULL_INDIRECT` for A/B | No occlusion test and no measured justification. The A/B switch exists precisely because the baseline has not been recorded yet |
| Frame profiler (`FrameProfiler.cs`) | Per-pass GPU and CPU intervals, median and p95 over a 96-frame window, per-frame allocation bytes, Gen0/1/2 counts, a compact HUD string, a CSV sink, and `PinDelta` for reproducible scenarios | **The GPU column is empty on three of four backends.** Only D3D12 implements `IGpuFrameTimer`; the base `CreateGpuFrameTimer()` returns null elsewhere. This is why P0 exists |
| Multi-view up to 8, per-view matrices (`FrameState.cs`, `ViewTargets.cs`, `SecondaryView.cs`) | Per-view camera and lighting snapshots, per-view targets, `ViewSlot` / `ViewFeature` / `ViewShadowPolicy`, and zero `static Camera` remaining. Rear-view mirrors in the sample app are the first consumer | Not VR. There is no stereo submission, no per-eye pacing, no HMD pose path |
| MSAA 4x on four backends | DX `MsaaSceneRT`, a private MSAA pair in Vulkan, `MTLMsaaTarget`, a private target on Web — each backend resolving on its own terms | Not MSAA on every pass. Post-process chain targets stay single-sample |
| Animation LOD (`InstancedModel.AnimationLod.cs`) | Distance-tiered pose sampling at full, half and quarter rate, with a shadow-only tier for instances the camera cannot see | Not geometry LOD. There is no mesh LOD, no decimation and no impostor anywhere in the pipeline |
| AI layer moved out of the engine (`SeasonAIs/`, `SeasonStudio/`) | Ten AI libraries plus a MAUI studio app that references `Season.csproj`. `Apps/Engine/Engine.csproj` now references none of them | Not an AI feature in the engine. The engine repository no longer contains the commercial layer at all |

**What this cost.** Every one of those features landed as a new four-language shader set: one `.cs` driver plus
`X.Hlsl.cs`, `X.Glsl.cs`, `X.Msl.cs`, `X.Wgsl.cs`. The measured shader corpus grew from roughly 18.4K to 29.8K lines
since the previous audit, and the per-language line counts remain within a few percent of each other. That is the
honest price of "each backend owns its own shader": a new visual feature is four implementations, not one. It is also
the argument for P1 and P2 below, which attack the boilerplate around those implementations rather than the
implementations themselves.

---

## Gate 0 — Release engineering

Not a track. A gate. These items block the open-source launch, the store submission, and every community post.

1. **B1 — conditional commercial reference.** *Largely resolved, by a route nobody planned.* `Apps/Engine/Engine.csproj`
   no longer references the AI layer at all — the ten `SeasonAIs` libraries are now referenced by `SeasonStudio`
   instead — and the repository has a `.gitignore`. The funnel is therefore no longer broken by cloning. What is
   missing is the guarantee: nothing fails a build if the reference comes back. That is D6.
2. **B2 — store product identifiers.** The three add-on IDs still need to be real product ids before any purchase can
   complete. Unchanged and unverified in this audit.
3. **B3 — MSIX packaging identity.** `WindowsPackageType` is now `MSIX` (the `None` override is commented out), so the
   packaging half is done. What remains is confirming the manifest identity is no longer MAUI template text.
4. **S1 — source-code delivery.** A commercial archive that ships inside the package with the decryption key in the
   same binary is not a delivery mechanism. Move it server-side before selling the source tier.
5. **S4 — capability declarations and privacy contact.** Add the `microphone` device capability; replace the
   placeholder contact address in `PRIVACY.md`.

Nothing in Track A/B/C/D/F/P should be prioritised over Gate 0. Every day the repository is unpublished, every track
is being built for an audience that cannot see it.

---

## Track A — Rendering

The list was sound; the execution went further than the list. Ten of the thirteen items below are shipping code, and
the three that are not are the three that need something outside this track: a build pipeline, an asset pipeline, and a
recorded baseline. So this section is no longer a plan — it is a status table with the design record kept where it
still explains a decision.

### A0. Prerequisites

All three of these were infrastructure that other items sat on, none of them produce a screenshot, and all three are
done. They are kept because the reason each one had to come first is still the reason the next feature will be cheap or
expensive.

#### A0.1 Indirect draw and dispatch abstraction — **Shipped, four backends**

`Rendering/Indirect.cs` carries the shared argument concept, and all four backends have the path: `ExecuteIndirect`
with a command signature on D3D12, `vkCmdDrawIndexedIndirect` on Vulkan, indirect command buffers on Metal,
`drawIndexedIndirect` on WebGPU. The earlier prediction that Metal and WebGPU would lag behind a capability flag was
wrong; all four landed.

The design record is worth keeping, because it is why the rest of the track was cheap: without indirect draw,
"GPU culling" can only mean *compute a visibility mask, read it back, and issue CPU draws*, which adds a stall and is
usually slower than the CPU frustum culling already shipping. Indirect had to come before culling, not after it.

#### A0.2 Scene normal target — **Shipped**

MRT slot 2 of the scene pass now carries an octahedral normal in `.xy`, Toksvig roughness in `.z` and the SSR
reflection weight in `.w`. GTAO no longer reconstructs normals from depth, and both SSR and the decal pass read the
real thing.

The judgement that made this cheap still matters: depth-derived normals are flat-shaded per triangle and lose all
normal-map detail, which survives ambient occlusion and does **not** survive reflections or decals. Doing SSR first and
adding the buffer afterwards would have meant writing the resolve twice.

#### A0.4 Per-view matrix split — **Shipped**

View and projection moved out of the per-object constant buffer into a per-view constant, the constant-buffer ring
gained a view dimension, and the scene pass stopped assuming one camera per frame. `FrameState.cs` holds per-view
camera and lighting snapshots, `ViewTargets.cs` holds per-view targets, `ViewSlot` / `ViewFeature` /
`ViewShadowPolicy` describe what a view is allowed to consume, and `MaxViews` is 8. There is no `static Camera` left
anywhere in the engine.

The first consumer was not VR — it was the rear-view mirrors in `Apps/Engine/Panels/RearViews.cs`, which is exactly
the test the roadmap predicted. Track E still needs E0 and E1; it no longer needs this.

#### A0.3 Texture compression and a container format — *moved out of this track, and merged*

Still not started, and now reclassified twice. It is not a rendering feature and it never was: its payoffs are package
size, web load time and VRAM bandwidth, which are build economics, so the item lives in Track P. It is also the same
project as the asset pipeline (D3) — the transcode step, the decimator and the import cache are one offline tool — so the
layering treats D3 and P3 as one row (L1.10) rather than two.

### A1. Motion blur — **Shipped**

`Effects/MotionBlur.cs` gathers along the velocity vector with depth-aware weighting to limit bleeding across
silhouettes, and `Effects/DepthOfField.cs` followed it in the same pass slot. Both were as cheap as predicted because
the velocity buffer and `VelocityView` already existed.

The prediction about the return on effort also held: motion in a capture is what makes the renderer legible in a video,
which is the format the store listing and every community post actually uses.

### A2. Screen-space reflections — **Shipped**

`Effects/SsrEffect.cs` runs as an AfterOpaque compute effect, marching the depth buffer in screen space and sampling
scene colour at the hit. It is faster than the plan assumed because the march is accelerated by a depth pyramid
(`Effects/HiZ.cs`, four levels, 1/16 stop) rather than a fixed-step walk, and it composites additively into the scene
colour with no separate resolve target.

On a miss it falls back to the DDGI irradiance atlas, which remains the architectural advantage worth naming: engines
without a GI solution ship a separate reflection probe system to avoid black misses. This one already had the volume.

The gap that remains is not SSR itself: it is that the water surface uses screen-space refraction and not reflection.
That is recorded under A4.

### A3. Volumetric fog and light shafts — **Shipped, and scoped further than planned**

The plan was a froxel grid sampling the cascades and compositing during the scene resolve. What shipped
(`Effects/VolumetricFog.cs`, four shader sources, `FogVolume.cs`, `SceneFog.cs`, `FogMath.cs`) also shadows the volume
against punctual lamps, so a street lamp in a valley casts a shaft and not just a glow, and it carries an
aerial-perspective froxel volume out to kilometres on the Web backend.

Two things made this affordable and both were already there: `SkyAtmosphere` had a working ray-march with 3D LUTs, and
`CascadedShadow` already had the shadow lookup. The CPU reference in `FogMath.cs` / `SceneFog.cs` is the reason the four
shader implementations can be checked against one another at all, and it is the pattern the other effects should copy.

What is missing is named rather than hidden: no multi-scatter, and no temporal reprojection of the froxel history.

### A4. Dynamic water — **Shipped, with one named gap**

The surface pass (`WaterEffect.cs`, `WaterPipeline.cs`) does screen-space refraction from the depth target, river
ribbons follow a spline (`WaterRibbonEffect.cs`) and waterfalls have their own sheet pass (`WaterfallEffect.cs`), all
four backends. `App.Create()`'s sea was migrated onto it, which was the test of whether "engine system" and "scene
content" separate cleanly — they did.

The gap is the one the original ordering predicted: water without reflections looks wrong in a way no amount of wave
detail fixes. There is no planar reflection and no FFT or clipmap height field, so the surface is a normal-mapped
screen-space effect rather than a simulated one. Closing it is a deliberate choice, not an oversight: planar reflection
is a second scene pass, and the scene pass is the expensive one.

### A5. Decals — **Shipped, screen-space**

Deferred decals need a G-buffer this renderer does not have. Forward projected decals — a decal list bound per object
and evaluated inside every material shader — push the cost into every material, need a per-object list, and still do
not scale to overlapping decals. The third option is the one that shipped: **a screen-space decal pass**, inserted
between the opaque scene pass and the `AfterOpaque` composites so it lands on the resolved scene colour before fog or
weather replaces it.

Shape of the pass:

- Each decal draws a programmatic 36-vertex box — six faces, two triangles each, positions derived from
  `vertex_index`. No vertex buffer, no instance data, one draw per decal.
- The pixel shader reconstructs world position from the scene depth at that pixel, transforms it into the box's local
  space, and shades only where the result falls inside the unit cube. `local.xz * 0.5 + 0.5` is the UV, so the decal
  projects along the box's own axis and the orientation of the surface underneath does not matter.
- Depth is a sampled source, never an attachment. The pass carries no depth attachment at all and the box is neither
  depth-tested nor depth-written.
- One texture per decal, capped at 64 active decals. The frame header and the decal table (12 `float4` each) are
  packed into one buffer and uploaded once per frame, so the draw loop carries nothing per decal except a slot index.

Three trade-offs set the ceiling, and all three were taken deliberately:

1. **No stencil, no per-draw sorting.** Every face is wound counter-clockwise as seen from outside the box and the
   rasterizer culls *front* faces, so the back faces of a convex box cover each pixel at most once — the property a
   stencil mask would otherwise buy, for free. Cost: overlapping decals are blended in draw order instead of being
   resolved per pixel by depth, and there is no "nearest decal wins" rule.
2. **v1 ships only Modulate and Additive.** Both are pure fixed-function blends (`dst * src`, `dst + src`) and the
   pixel shader never reads the scene colour, so the colour target does not also have to be sampleable — no copy, no
   extra target, and the same pipeline shape on all four backends. Alpha is preserved (`ZERO / ONE`), so the pass
   cannot disturb later alpha-based decisions. Cost: no normal-blended or overwrite decals; those need a target that
   can be sampled and written at the same time, which is a different pipeline shape.
3. **The roughness write reuses `SceneNormal` as a second colour attachment with a blue-only write mask.**
   `SceneNormal` already carries octahedral `.xy`, Toksvig roughness in `.z`, and metalness in `.w`. The decal pass
   opens only the B channel and lerps by coverage (`SRC_ALPHA / ONE_MINUS_SRC_ALPHA`), so normals and metalness come
   out untouched and no extra target is allocated. Cost: while any visible decal asks for roughness, `SceneNormal`
   is an attachment for the pass and cannot be sampled inside it — which is why the grazing-angle fade is derived
   from the depth-reconstructed normal rather than read from the normal target.

Backend notes, because the same contract lands differently on each API:

- Vulkan bakes clear/load into the render-pass object, so the decal pass is a dedicated render pass keyed by its own
  `RenderPassId`; Metal and WebGPU only see an attachment list, and DX12 binds two RTVs.
- Neither Metal nor WebGPU has push constants. Metal passes the slot as a small vertex/fragment constant; WebGPU
  issues `draw(36, 1, 0, slot)` and reads `instance_index`, which is defined to start at `firstInstance`.
- WebGPU must bake the attachment count into the pipeline object, so "write roughness" is a separate PSO rather than
  a branch, and the pass refuses the frame outright instead of degrading to one attachment.

Failure is deliberately boring: when a backend has no implementation (or the feature is off), the factory returns
null and the decal effect never registers; when the visible table is empty, `Prepare` returns false and the pass is
never begun. Either way the frame schedule is untouched, so a decal-free frame costs exactly what it did before.

### A6. GPU particles — **Shipped, exactly as scoped — which means unsorted**

`Effects/Particles.cs` and its four shader sources simulate over a persistent buffer and draw the alive set through
indirect arguments. `Panels/Campfire.cs` is the first real content: flame, spark and smoke in one emitter set.

The warning in the previous version of this document was correct and is still open: sorting for correct alpha blending
is a GPU radix sort, which is a real piece of work. What shipped is the additive and alpha-tested subset. Anything
that needs painter's-order blending is waiting on the sort.

### A7. GPU-driven culling — **Built, unproven. The honest status is Partial**

The one item on this list that was built *without* the evidence that was supposed to gate it. `Rendering/InstanceCull.cs`
has per-instance bounds in a storage buffer, a compute pass writing compacted lists into five slots (three shadow
cascades, one punctual slot, one main-view slot) over up to sixteen primitives, original-index indirection so the draw
still knows which instance it is, and an indirect gather path switched by `SEASON_CULL_INDIRECT` so the fixed and
indirect gathers can be compared frame for frame.

What is missing is the part the previous ordering demanded first, and it is still the right criticism:

1. **No recorded baseline.** CPU frustum culling ships and is on by default. Without a number there is no way to show
the GPU path is faster, and the A/B switch exists precisely because that has not been measured.
2. **No occlusion.** There is no Hi-Z test against the previous frame's depth. The compacted list is frustum-only, so
   the cost of drawing occluded geometry is unchanged.

**Sequencing warning, still live.** If the ten-thousand-unit scenario turns out to be CPU-bound in `Update` rather
than in draw submission, then A7 was the wrong fix and the right fix is an allocation budget (D2) or a data-oriented
update path for bulk entities. Building it was defensible — the switch makes it reversible — but shipping it as a win
without the measurement is not.

### A8. Mesh LOD — *an asset pipeline feature, not a renderer feature*

Selecting a LOD at draw time is trivial. Having LODs to select is not, and that is where the work lives:

- glTF has no core LOD concept; `MSFT_lod` is the de facto extension. Either adopt it or define a repository
  convention.
- Without mesh decimation in the pipeline, LOD chains must be authored externally, which contradicts the
  "generate assets locally" story in Track C.

This is where Track A and Track C meet: both need mesh processing. Build the decimator once and use it for both. That
is also why it is the one remaining unstarted item whose blocker is not inside this repository's control.

Do not confuse it with what already shipped. `InstancedModel.AnimationLod.cs` throttles *pose sampling* by screen size
— full, half and quarter rate, with a shadow-only tier — and it is a real optimisation, but it reduces CPU animation
work, not vertices submitted. A crowd of a thousand characters still draws a thousand full meshes.

### A9. Terrain and terrain LOD — *largest item, still last*

A terrain system is not one feature. It is heightmap representation, chunking, a CLOD or clipmap scheme, crack-free
stitching, splat-mapped multi-layer materials, and vegetation scattering.

The dependency picture changed in its favour: three of the four prerequisites now exist. Indirect draw, GPU particles
and GPU-driven instance culling are all shipping, so the submission path terrain would be built on is settled. The
remaining blocker is mesh LOD and decimation (A8), because a terrain chunk without a LOD chain is a fixed cost per
square kilometre and no amount of culling fixes that.

Recommendation unchanged: do not start terrain until A8 exists, and do not build vegetation scattering on top of a
particle system that cannot sort.

### A10. Occlusion culling — *the half of A7 that was not built*

Deliberately separated from A7, because they answer different questions and one is already shipping. Frustum culling
and instance compaction decide *what might be visible*; occlusion decides *what is certainly not*, from the previous
frame's depth pyramid.

The infrastructure is closer than it looks: `HiZ.cs` already builds a four-level min-depth pyramid every frame for SSR.
Reusing it as the occlusion test means the pyramid stops being an SSR detail and becomes a frame resource with two
consumers — which is a better design anyway.

The reasons to keep it behind a measurement are the same as A7's: it only pays off when overdraw of hidden geometry is
actually the cost, and in a scene that is not dense that is never true.

### A11. FrameSchedule, instantiated — *the static facade is now the only thing holding the frame together*

`FrameSchedule` is still a static class whose members are assigned from outside: `FrameSchedule.PrepareView =
OnPrepareView`, `FrameSchedule.AfterViews`, `FrameSchedule.SceneDepth`, `FrameSchedule.ActiveTargets`. That was the right
shape while there was one frame and one camera. It stopped being the right shape when A0.4 made views plural, because the
class answers "which frame?" with "the frame".

The work is mechanical: one instance per frame owner, held by the device and passed through the pass schedule instead of
read off a type. No behaviour change. It is on the list because every later item that wants a second frame running at the
same time — headless capture (F1), the editor's preview pane (F4), XR's dual view (E1) — first has to answer that
question, and today there is exactly one possible answer.

### A12. Binding layering — *split the block before generating code from it*

`SceneLightParams` measures 2464 bytes. Every feature that needed a parameter appended a `Vector4` at the tail, because
inserting in the middle moves every hardcoded offset in four shader sets. The tail has now grown past the point where the
struct is a lighting block: it is a frame-wide grab bag that happens to begin with a camera position.

Vulkan has already started the answer in the other direction — `SceneFrameInputs` holds per-frame bindings as named slots
instead of one set. The item is to finish that: lighting, shadows, sky and atmosphere, weather, reflection, as separate
blocks with separate lifetimes, so a feature that writes four fields stops replaying a 2.4 KB upload to change them.

Deliberately ordered before P1. Generating a four-language header from one 2464-byte struct only gives the drift a better
format; generating five small headers from five small blocks gives it a cheaper one.

### A13. Material system and material instances — *every material today is a glTF import*

There is no material asset. `MaterialParams` is the GPU-side struct, `MaterialFields` reads glTF's PBR block, and
`ShadowMaterialSharing` dedupes what comes out of it. A scene cannot declare a material, override one field of an imported
one, or share a variant across two meshes without editing the source asset.

The minimum viable shape is the one the rest of this engine already uses: a material is data — a type name plus
parameters — an instance is an override set, and the renderer resolves both into the struct it already uploads. No node
graph, no shader authoring; the four hand-written shader sets stay hand-written.

### A14. Streaming and asynchronous upload — *the prerequisite A9 and B4 both quietly assume*

Textures go up in batches (`TextureUploadBatch` on three of the four backends), on the frame that first needs them, at
their final size. There is no residency policy, no background transfer queue and no eviction. For the current island that
is invisible; for a terrain of any size it is the whole problem, and for a ten-thousand-unit world the hitch lands on
whichever frame first touches new geometry.

Deliberately after A13 rather than before: a streaming system without a material identity has nothing to key residency
on.

### Track A status summary

| Item | Status | What is left |
|---|---|---|
| A0.1 Indirect draw and dispatch | Shipped, four backends | — |
| A0.2 Scene normal target | Shipped | — |
| A0.3 Texture compression | Not started | Merged into the asset pipeline (L1.10) |
| A0.4 Per-view matrix split | Shipped | — |
| A1 Motion blur | Shipped | — |
| A2 Screen-space reflections | Shipped | — |
| A3 Volumetric fog | Shipped | No multi-scatter, no temporal froxel history |
| A4 Water | Shipped | No planar reflection, no FFT height field |
| A5 Decals | Shipped | Two blend modes, 64 decals, no per-pixel ordering |
| A6 GPU particles | Shipped | No sorting, so no true alpha blending |
| A7 GPU-driven culling | Partial | No occlusion, no recorded baseline |
| A8 Mesh LOD | Absent | Needs a decimator; needs the asset pipeline first |
| A9 Terrain | Absent | Blocked on A8 |
| A10 Occlusion culling | Absent | Should reuse `HiZ.cs`; needs A7's baseline first |
| A11 FrameSchedule instantiation | Absent | Static facade; needs one instance per frame owner |
| A12 Binding layering | Partial | `SceneFrameInputs` exists on Vulkan; the 2464-byte block is unsplit |
| A13 Material system | Absent | Materials are 100 % glTF-imported |
| A14 Streaming and async upload | Absent | Batched upload only; no residency, no eviction |

The shape of the remaining rendering work changed accordingly. The four original open items (A7–A10) are not rendering
features: each is an asset pipeline, a measurement, or an optimisation that has to be justified by one. The four added
here (A11–A14) are not features either — they are the structural debt that ten visual features in one year produced, and
they are the four places where the next ten would slow down. That is the difference between a renderer that is
unfinished and a renderer that is done and unproven.

---

## Track B — Host, world editing, and shareable worlds

The premise is correct and worth restating precisely, because it is a real architectural position and not a
compromise: **an editor is a demonstration surface, not the workflow.** Nobody reads an unfamiliar engine's source to
evaluate it, and a wall of code samples is a worse demonstration than one application in which the features are
visibly present and manipulable. The editor exists so the renderer can be seen. It does not exist to become the way
the engine is used.

The judgement that dynamic gameplay belongs in C# and that scene layout is only for static content is also correct,
and it has a precise technical consequence that should be locked in before any file format is designed. See B1.

### B0. Decouple the app shell from the scene — *the prerequisite for everything else in this track*

`App.cs` `Create()` builds the entire island unconditionally. Two separate goals both require this to change, and
recognising that they share one prerequisite is the most useful scheduling insight in this track:

- **"Promote AI to the home screen."** Today the AI panel is reachable without entering the island, but the island
  still loads. Making AI a first-class destination means being able to run the host with no world loaded at all.
- **"Create a new world."** A new world is an empty world, which is the same capability.

Scope: a shell/launcher state that owns mode selection and destinations (`Official sample` / `New world` /
`Open world` / `AI studio`), with world construction moved behind an interface that the shell invokes rather than
something the app constructor performs. The existing `Mode` enum is the right skeleton to hang this on.

This is a refactor with no new visible feature, and it is the highest-value item in Track B. Do it first. Doing B1
or B2 before it means building world loading against a host that cannot represent "no world".

### B1. World serialization — *the anti-fragmentation contract*

This is where the design decision matters most, so it is stated as a rule rather than as a goal:

> **A serialized world record is a C# type name plus construction parameters. It is never a bag of properties with
> attached behaviour fragments.**

That single constraint is what keeps this from becoming Unity's model. In Unity, an object's behaviour is assembled
from components configured in the inspector, which is why behaviour ends up scattered across scene files, prefabs,
and scripts, and why large strategy-scale scenes become hard to reason about. Here, an object's behaviour lives
entirely in one C# class; the world file records *which class* and *where*. Layout is data; behaviour is code; the
boundary is the type name.

Concrete consequence to design for on day one — **two tiers of world, with different sharing semantics**:

| Tier | Contents | Shareable as a file? | Who can open it |
|---|---|---|---|
| **Static world** | Built-in types only, plus bundled assets | Yes | Anyone with the app |
| **Code-bearing world** | References project-defined types | No — share the C# project | Anyone with the source |

This distinction has to be explicit in the format and visible in the UI, or the project inherits the prefab
dependency problem it is trying to avoid: a shared world that silently fails to load because a type is missing. A
static world should be a single self-contained file. A code-bearing world should be honest that what is being shared
is a project.

Scope: a stable type registry with stable identifiers, transform and construction-parameter serialization, an asset
reference scheme that survives relocation, forward-compatible versioning, and a load path that reports missing types
by name instead of failing opaquely.

### B2. Editing affordances

Build on what exists rather than starting an editor from scratch. `ObjectPicker` already provides per-instance
picking, highlight modes, and click-versus-drag discrimination; `ObjectPanel` already edits position and size.

In dependency order:

1. Placement — instantiate a registered type into the world from a palette, drop onto geometry.
2. Transform gizmos — translate, then rotate, then scale, in that order of usefulness.
3. A generic property editor over construction parameters, driven by reflection over the type's parameters, so that
   adding a type does not require writing editor UI. **Deliberately limited to construction parameters.** Exposing
   arbitrary runtime fields for per-instance tweaking is the exact door to fragmentation that B1 closes.
4. Hierarchy inspection of the live panel tree — this doubles as a debugging tool and costs little.
5. Undo/redo over world mutations. Late, because a command-based mutation model is easier to introduce once the
   mutation set has stopped changing shape.

### B3. Asset catalogue

Bundled models currently have hardcoded paths inside scene classes. A palette needs a manifest: a catalogue of
bundled meshes with thumbnails, categories, and licence attribution.

The licence attribution column is not optional. Bundled assets travel with shared worlds, and the repository is
about to become public.

### B4. A second reference world: mass unit scheduling

The single most valuable *demonstration* item in the roadmap, and it does double duty as the benchmark that A7
requires.

The positioning argument is that mainstream engines struggle with strategy-scale entity counts and that this is why
Warcraft-class and Total War-class engines are bespoke. That argument is currently unsupported by anything in the
repository — the island scene does not demonstrate scale. A scene with tens of thousands of independently updated,
individually animated units, with a recorded frame time and allocation figure, converts the claim from an assertion
into evidence.

It should be built as a `Samples/` world, not folded into the island, and it should be instrumented from the start.

### Explicit design rules for this track

- No visual scripting, no node graphs, no behaviour trees in the editor.
- No per-instance runtime property overrides beyond construction parameters.
- Static placement is serialized; entities that are spawned procedurally stay in code.
- The editor is never required to run, build, or ship a project built on the engine.

---

## Track C — AI

The three-part shape here is right — output-to-engine, cloud reasoning, 3D generation — and the layer itself moved
while nobody was updating this document. It is no longer inside the engine: ten libraries under `SeasonAIs/` and a MAUI
studio app at `SeasonStudio/` now own it, and `Apps/Engine/Engine.csproj` references none of them. That is a better
outcome than the one this track planned for, because it means the open repository cannot accidentally depend on the
commercial layer.

The ordering argument below still matters, but the practice inverted it: 3D generation shipped first. The section
records why that was the easy half and why C2 — cloud reasoning plus runtime C# — is still the one that unlocks
something no competitor has.

### C1. Generated output into the engine — **Partial**

The gap narrowed but did not close. `SeasonStudio/Controls/ModelPreview.cs` puts a generated mesh inside an engine
control, so one direction — 3D output, previewed by the renderer — now exists. The other direction does not: no
generated image becomes a scene material, no generated audio becomes a scene source, and no generated asset lives in a
folder a project can reference.

The framing still holds. The paid tier's premise is that developers want generated assets *in their game*; a studio
that generates and previews but cannot bind is still, functionally, a local generation utility that happens to link a
renderer.

Scope, all of it small:

- Generated image → texture, applied to a selected object's material through `SetTexture`.
- Generated image → sprite in the 2D layer.
- Generated music/audio → a playable audio source in the scene.
- A generated-assets folder with a stable on-disk layout so a project can reference outputs directly.
- The reverse direction: pick an object, send its current texture to image-to-image as the reference.

That last item is the one that makes the integration feel like an engine feature rather than a bundled tool, and it
is the difference between "an AI panel" and "AI in the engine". It is also the strongest available answer to *why
buy this instead of using ComfyUI, which is free* — ComfyUI cannot select an object in your scene.

**This is the highest return-on-effort item in the entire roadmap**, measured against the paid tier's credibility.

### C2. Cloud LLM and runtime C# generation — *now the highest-value item in this track*

Two capabilities that combine into something neither has alone, and the first one is now half-built.

**Cloud model access.** `Season/Net/WebClient.cs` already provides multipart upload and download with progress
reporting and a cancellation token — the plumbing that the previous version of this section said the repository lacked
entirely. What is still missing is the shape a chat completion actually needs: streaming response parsing, user-supplied
API key storage, and a retry/backoff policy. Still a few hundred lines, not a pipeline. Commercially it has an
attractive property: **the user brings their own key, so inference cost never lands on the vendor.** That is a
materially better position than subsidising tokens.

**Runtime C# compilation.** This is where the pure-C# architecture pays a dividend that is genuinely unavailable to
the engines being compared against. Roslyn (`Microsoft.CodeAnalysis.CSharp.Scripting`) compiles C# at runtime, and
the architecture is already shaped to receive the result: a behaviour is a `Panel` or `Control` subclass, so a
generated class only has to be instantiated and added. There is no serialization format to satisfy, no component
model to register with, no editor metadata to emit.

This also closes the loop with B1. If a world record is *a type name plus parameters*, then a language model
producing gameplay is producing *the source of that type*. Natural-language world creation and the world format are
the same design, approached from two ends. That coherence is not available to an engine whose behaviour is assembled
from inspector-configured components.

Constraints to state plainly:

- Roslyn runtime compilation is **unavailable under full AOT**, which rules out iOS and WASM. This feature is
  desktop-only by nature. That aligns with the Windows-first commercial plan rather than fighting it.
- Dynamic compilation inside an MSIX package needs verification. Test early; do not discover it at submission.
- Generated code executes with full application trust. The security and consent story must be designed in, not
  retrofitted.

### C3. 3D asset generation — **Shipped the easy half, as predicted**

The scope warning was correct down to the ordering. `SeasonAIs/SeasonThreeD` wraps a Trellis runner — session
lifecycle, runtime location, native invocation, progress and output parsing, typed results — which is exactly step 1
below, "model integration, real, and the easiest step". It shipped first, and it is a single backend with no fallback.

What has not been built is everything after it:

1. ~~Model integration~~ — done.
2. **Mesh post-processing** — generated meshes arrive with poor topology, unconstrained triangle counts, and no
   usable UV layout. Decimation, UV unwrapping and normal repair are required, and the repository still contains **no
   mesh processing code at all**.
3. **glTF export** — the engine consumes exactly one format, and only the *import* half exists. Writing an exporter
   is new work.
4. Only then: something usable in a scene without a preview window holding it.

That is a mesh pipeline, not a feature. It is also where A8 (mesh LOD) meets this track — both need a decimator, so
build it once.

Recommendation: ship as **static prop generation**, clearly scoped, and do not attach rigging to the same
deliverable.

### C4. Automatic rigging — research, not a commitment

Skeleton inference and skin weight prediction are substantially less mature than mesh generation, and they are
sensitive to exactly the topology quality that generated meshes lack. Auto-rigged output typically requires manual
correction, which is the worst possible property for something sold as a feature: the buyer discovers the manual
work after paying.

Keep this in research. Do not put it on a store listing until output quality is verified against real characters.

### Why C2 should precede C3 — and why the practice disagreed

The original framing places 3D generation near-term and cloud reasoning long-term. Four reasons were given to invert
that, and then the code inverted them back: 3D generation shipped, cloud reasoning did not. The four reasons survive
that, and one of them has grown stronger.

1. **Effort asymmetry.** C2 is an HTTP client plus a Roslyn host. C3 is a mesh processing pipeline plus a glTF
   exporter plus a model. C3 is several times the work.
2. **Output-quality risk.** C2's quality is supplied by frontier models and improves for free. C3's quality is
   capped by what local 3D models produce today, which is not yet "drop it in a game" quality. Selling the second
   as a headline feature invites refunds.
3. **Architectural leverage.** C2 exploits a property this engine has and its competitors do not — behaviour as a
   single compilable C# class. C3 exploits nothing specific to this project; any engine can import a mesh.
4. **It converges with Track B.** C2 and B1 are the same insight from two directions. Building them near each other
   produces one coherent design; building them years apart produces two that must be reconciled.

The counter-argument deserves acknowledgement: 3D assets are the harder bottleneck for solo developers, and
therefore the thing people would more readily pay for. That is true, and it is why C3 stays on the roadmap rather
than being cut. It is an argument about eventual value, not about sequence.

---

## Track D — Foundations

Not in the original three. Every item here is either a prerequisite for proving a Track A/B/C claim, or a
prerequisite for the project surviving contact with contributors.

### D1. Benchmarks and screenshot regression — **Half built, and the half that exists is good**

The S0 acceptance gate (`docs/acceptance/s0-baseline.md`) defines four rulers and all four now have code behind them:

| Ruler | Question it answers | Tool |
|---|---|---|
| Layout freeze | Are the bytes the CPU writes the bytes the shader reads? | `Tests/Shared/MaterialParamsLayoutChecks.cs`, shared into two test hosts |
| Draw-order trace | Did the order of state changes and draws move? | `Rendering/DrawTrace.cs` |
| Screenshot A/B | Did the pixels move? | `Apps/Engine/AbCapture.cs` + `.dbg/ab_pixel_diff.ps1` |
| CPU frame time | Did the hot path get slower? | `Rendering/CostProfile.cs`, `ICostProfileScene.cs` |

That is the right shape, and it was extracted from the right failures — this codebase's accumulated pitfalls are
exactly the kind a reference-image comparison catches instantly and a compile check never catches: per-frame buffer
cross-talk, uncleared atlas memory, struct layout drift, shader divergence between four APIs.

What is still missing is the part that makes it a gate rather than a tool:

- **Recorded numbers.** A fixed camera tour, a fixed frame count, and a published table of frame time, draw count and
  allocation per scene. A7 cannot be justified without it and B4 cannot support the scale claim without it.
- **CI.** Nothing runs on a commit. Every ruler above is currently something a person remembers to run.

This also has a specific and growing value: it is the only mechanism that makes machine-generated changes to shader and
backend code safe to accept — which is Track F's whole premise.

### D2. Frame allocation budget — *measured, not yet a budget*

The differentiating argument is that architectural minimalism recovers what a managed runtime costs. Pure C# engines
are dismissed on exactly one axis — **GC pauses** — and that dismissal will appear in the first community thread.

The measurement half shipped by accident, as part of `FrameProfiler`: it records bytes allocated per frame and
Gen0/1/2 collection counts over the same 96-frame window as the timings. That means the answer is no longer a research
task; it is a published number waiting to be read off a fixed scenario.

The half that did not ship is the contract:

- An explicit **zero-steady-state-allocation** target for the frame loop, treated as a rule rather than an aspiration.
- A failure on regression inside the benchmark harness — not a graph a person has to interpret.
- A published figure: allocated bytes per frame and GC counts over a fixed benchmark run, for both the island and a
  mass-unit scene.

"Zero bytes allocated per frame at ten thousand animated instances" ends that argument in one line. Without the target,
the profiler only tells you what you already suspected.

### D3. Asset pipeline

Absorbs several items that appear elsewhere as features: texture compression (A0.3), LOD chain generation (A8), and
mesh decimation (A8, C3). Plus import caching, so that startup does not re-parse glTF and re-generate atlases on
every run, and dependency tracking so incremental rebuilds are correct.

The distinction to preserve is
that the runtime must keep loading plain glTF and plain PNG directly, with the pipeline as an optimisation and never
as an entry requirement. Retaining that property is the actual differentiator; avoiding all offline processing is
not.

### D4. Documentation and the sample ladder

The observation that nobody will read the source is accurate, and the editor is only a partial answer to it. The
other half is documentation shaped as recipes rather than as architectural prose:

- A minimal `BaseApp` that renders one object, under 50 lines.
- One-concept samples: text, sprites, model loading, picking, a custom compute effect, a custom render pass.
- **"How to add a compute effect"** as a written walkthrough. This is the extension point most likely to attract a
  contributor and it is currently discoverable only by reading `Ddgi.cs`.
- A feature-by-backend support matrix, generated from capability flags rather than maintained by hand.
- An honest production-status statement per subsystem.

### D5. Platform honesty

The archive listed "fallback paths" for web as a goal. That conflicts with the stated principle of one
implementation per domain and no fallbacks — and the principle is the better position. Replace it with capability
reporting: features declare requirements, unsupported features are visibly disabled with a stated reason, and the
matrix in D4 is generated from the same source.

### D6. Maintaining the commercial boundary

The boundary moved, and it moved in the right direction: the AI layer now lives outside the engine repository entirely
(`SeasonAIs/`, `SeasonStudio/`), so `Apps/Engine/Engine.csproj` references none of it and the open repository builds
without it. That happened by relocation, not by policy — which is exactly why it needs a test.

A CI job that builds the open repository with no `SeasonAIs` present is still the cheapest possible protection against
the engine quietly re-acquiring a dependency on the commercial layer. It costs one workflow file, and it is the only
item in this track that can be finished in an afternoon.

---

---

## Track F — Agent surface and the lightweight editor

This track exists because of one property the engine already has, and which most engines being compared against do not.
A behaviour here is **one C# class**. A world record is **a type name plus construction parameters**. There is no
component model, no inspector-configured behaviour, no prefab graph to satisfy. That means an agent does not need to
understand an editor to drive this engine — it needs to emit a class, name it, and place it.

The second reason is cheaper to state and harder to argue with: an agent is the only realistic way to keep a
demonstration honest. Every release needs the island, the weather, the fog, the cloth and the water to still look right
on four backends, and a person clicking through that is a person who will eventually stop. A replayable command stream
with a screenshot diff behind it is the difference between a demo and a regression suite.

### F0. One command surface, two clients — *the design rule, not a feature*

> **Every editor action is a command. The agent is a second client of the same command stream.** There is no
> agent-only API and no UI-only mutation path. If a person can do it, the agent calls the same command; if an agent
> does it, the editor shows it and can undo it.

This is the same weight of decision as B1's anti-fragmentation contract, and it has four immediate consequences:

1. **Undo is promoted.** It stops being "late" and becomes the command layer's first requirement, because a command
   stream *is* an undo log. B2's item 5 moves to the front of B2.
2. **Objects need addresses, not references.** `ObjectPicker` currently returns a live object; a command needs a stable
   id that survives a save and a reload. That falls out of B1's type registry, not out of a new system.
3. **Every command needs a dry run.** The agent proposes a diff, the human reads it, and only then does it apply. That
   is the same shape as `git diff`, and it is the reason this track is not "give the model the keyboard".
4. **The editor is not the API.** If the shell is the only client, the API will grow shell-shaped — modal state,
   selection, focus. Two clients from day one keeps it honest.

### F1. Headless run and capture

The cheapest of the three enabling pieces, because most of it exists: `Apps/Engine/AbCapture.cs` already captures a
frame, `Rendering/FrameProfiler.cs` already produces a compact summary string and a CSV sink, and `PinDelta` already
makes a scenario reproducible by fixing the timestep. What is missing is a way to ask for them from outside a window.

Scope: a `--headless` run mode that loads a world, steps N fixed frames, writes a PNG per requested view slot, writes
the profile summary as JSON, and exits with a status code. Nothing in that sentence is new capability; it is packaging.

Once it exists, D1's reference images stop being a hand-run ritual, and B4's benchmark stops being a scene someone
has to watch.

### F2. World commands

The part that depends on B0 and B1, and the reason this track cannot go first. The command vocabulary is deliberately
tiny, because every verb added here is a verb the UI must also implement:

| Verb | Answers | Exists today |
|---|---|---|
| `world.list` | What is in the world, by id, type and transform | No — needs B1's registry |
| `world.spawn` | Create a registered type at a transform | Partially — `ObjectPicker` placement, by hand |
| `world.set` | Change a construction parameter | Partially — `ObjectPanel`, fixed fields |
| `world.remove` | Delete by id | No |
| `world.save` / `world.load` | Round-trip a world file | No — B1 |
| `view.set` | Bind a camera to a view slot | Yes — `SecondaryView`, `Views.cs` |
| `frame.capture` | Render and write a PNG | Yes — `AbCapture.cs` |
| `frame.profile` | Timings, allocation and GC for the last window | Yes — `FrameProfiler` |
| `scene.measure` | Draw count, instance count, triangle count | No — `DrawTrace.cs` counts order, not volume |
| `asset.generate` | Produce a texture, mesh or audio file from a prompt | Partially — `SeasonAIs`, `SeasonThreeD` |
| `code.compile` | Compile a C# snippet and report diagnostics | No — needs the Roslyn host from C2 |
| `code.apply` | Hot-replace a behaviour instance | No — same dependency |

Two rows in that table are the whole track. `code.compile` plus `code.apply`, sitting on top of `world.set`, is the
loop that makes natural-language world creation real: describe a behaviour, get a class, apply it to an instance, look
at a screenshot, keep or discard. Everything above them is plumbing that exists or is already scheduled.

### F3. What the agent actually gets

An MCP-shaped tool surface, over stdio or a localhost socket, backed by exactly the verbs above — no special cases.
The tool list is short on purpose:

- **Inspection** — `world.list`, `frame.profile`, `scene.measure`. Read-only, cheap, and the thing that stops an agent
  from guessing at state it cannot see.
- **Mutation** — `world.spawn`, `world.set`, `world.remove`, each with a dry-run mode that returns a diff.
- **Perception** — `frame.capture`, returning an image path rather than bytes, so the caller decides what to do with it.
- **Generation** — `asset.generate`, `code.compile`, `code.apply`, in that order of risk.
- **Verification** — `test.image_diff` against a reference, which is how an agent checks its own work without a person
  in the loop.

### F4. The lightweight editor is the same surface, rendered

Not a new application, and not a step toward becoming an editor-centric engine. `SeasonStudio` already is the shape
this needs: a MAUI shell that references `Season.csproj`, hosts engine controls, and has ten tool panels behind one
window. What it does not have is a **scene** — it has `Controls/ModelPreview.cs`, which is a model in a box.

The smallest honest version is four panes: a viewport bound to a view slot, a hierarchy driven by B1's type registry, a
property grid over construction parameters only, and a command palette that shows the same command strings the agent
sends. That is B0 + B1 + B2 + B3, with the command layer as the spine — which is exactly why it should not be a separate
project with a separate plan.

### F5. Safety, stated plainly

- **No network listener by default.** Stdio, or a localhost socket with a token in a file the user can see. A control
  surface that can spawn objects and compile code is remote code execution if it is ever bound to `0.0.0.0`.
- **Generated code runs with full application trust**, because it is a normal C# class in the normal app. There is no
  sandbox and C2 says so. Consent is therefore per session, visible, and revocable.
- **The agent never gets a capability the UI lacks.** That is not a security property, it is an auditability one: if a
  command did something a person could not, there would be no way to review it.

### What this changes about the rest of the roadmap

- **B2's undo moves first**, because a command stream is an undo log.
- **D1 stops being optional**, because an agent without a screenshot diff is an agent that generates visual regressions
  at machine speed.
- **C2 is promoted** from "the paid layer's next step" to "Track F's enabler". That is the strongest argument for it:
  the feature that looked like a nice-to-have is the one the automation loop depends on.
- **Nothing about the non-goals changes.** No visual scripting, no node graph, no behaviour assembled from fragments. The
  agent writes the same C# class a person writes, which is the only reason this is defensible at all.

---

## Track P — Performance and build economics

Everything here answers the same question: does the frame budget hold on the hardware the audience actually owns, and
can the answer be shown rather than asserted. It is separate from Track A because none of it is a rendering feature,
and separate from Track D because it is not about proving the project works — it is about making it fast and small.

### P0. Measurement on all four backends — *do this first, it gates the rest*

`FrameProfiler` is real: per-pass GPU and CPU intervals, p50/p95, allocation bytes, GC counts, a HUD and a CSV sink.
But `IGpuFrameTimer` has exactly one implementation, `Platforms/Windows/DirectX/DXGpuFrameTimer.cs`, and the base
`CreateGpuFrameTimer()` returns null everywhere else. The GPU column is therefore empty on Vulkan, Metal and WebGPU —
which are the three backends where a managed engine's frame budget actually needs defending, because that is where the
integrated GPUs and the browsers are.

Scope: timestamp queries per backend (`vkCmdWriteTimestamp` with a query pool, `MTLCommandBuffer` sample buffers,
`timestamp-query` where the WebGPU adapter offers it), the same fence-gated readback ring D3D12 already uses, and an
explicit "unsupported" state rather than a silent NaN. Then one recorded run per backend of the same scene.

Until this exists, every item below can only be argued. After it, each one is either a number or it does not ship.

### P1. Contract code generation

`IGraphics` is a large interface — roughly seventy-five methods — and each backend implements the whole surface by hand.
Measured similarity between backends ranges from about 30 % to about 90 % per object family, and the low end is not
shared code waiting to be extracted: it is D3D12-specific logic that grew there for a reason. So the goal is not
"delete half the lines".

The goal is to **shrink the surface a change has to touch**. One declarative description of the object families and
their parameters, with per-backend forwarding and validation generated from it, turns "add a method and edit four
files" into "add a method and regenerate". The realistic saving is three to five thousand lines, not the six to eight
that a similarity ratio suggests — but the real payoff is that the four implementations cannot drift apart silently,
because they are generated from one description.

This is also what makes the four-language shader corpus survivable. The shaders stay hand-written per backend; the
boilerplate around them stops being hand-written.

The shared core is built in three steps, and the order matters because each step is useful on its own: bodies that are
already byte-identical move first, then bodies that differ only in data layout, then the skeletons that differ in control
flow. The first step is free, the second is mechanical, and the third is the only one where a behaviour change is
possible — which is exactly the boundary at which the work should be reviewed.

### P1b. Struct and constant mirroring

A second and much smaller generation problem lives next to the first, and is easy to mistake for it. `SceneLightParams`
measures 2464 bytes and is mirrored by hand in two directions: shader-side declarations in HLSL, GLSL, MSL and WGSL, and
one hardcoded byte length (`SCENE_LIGHT_BYTES`) in the web host's JavaScript. Every other size in the system is derived
(`Unsafe.SizeOf`), so that constant is the one place where a layout change fails silently — the upload is discarded with
no error at either end.

Scope: generate the four shader-side block declarations and the JS constant from the C# struct definitions, and fail the
build when the generated length disagrees. It depends on A12, because generating headers for one unsplit 2464-byte struct
would document the problem rather than reduce it.

### P2. Shader precompile and PSO disk cache

All four backends compile at startup: FXC to DXBC on D3D12, glslang to SPIR-V on Vulkan, Metal source compile, WGSL on
Web. The in-process cache prevents recompiling within a session; nothing survives a restart, D3D12 has no pipeline
library and Vulkan does not persist a `VkPipelineCache`. The cost is a first-run hitch and a startup latency that a
store reviewer notices before they notice anything else.

Scope: a content-addressed shader and pipeline cache on disk, keyed by source hash plus pipeline state, with the runtime
compilers still present as the fallback. The rule from D3 applies unchanged: the cache is an optimisation and never an
entry requirement — cloning the repository and running it must still work with an empty cache.

### P3. Texture compression and a KTX2 container

Formerly A0.3. Unchanged in content, changed in category. BC7/BC5/BC6H on desktop, ASTC on mobile, ETC2 as the Android
floor, KTX2 as the container so one file serves every platform, and an offline transcode step. Runtime decode to
uncompressed stays an anti-goal.

The reason it belongs in this track and not in Track A is the shape of the payoff: package size, web load time, VRAM and
bandwidth. None of those is a picture. All of them are the difference between a browser build someone waits for and one
they close.

Execution note: this is not a separate project from D3. The transcode step, the decimator and the import cache are the
same offline tool, so the layering keeps them as one row (L1.10) instead of two that would drift apart.

### P4. Temporal upscaling and dynamic resolution

TAA already owns everything an upscaler needs: jittered history, a velocity buffer, variance clipping and a resolve.
A TAAU-style upscale from an internal resolution of 67 % or 50 % is a small increment on an effect that already works,
and it is the largest single GPU win available on the integrated hardware most evaluators will run this on.

Dynamic resolution is the other half, and it is not a rendering feature either: it is the feedback loop that keeps the
frame budget under a target when the scene changes under you. It is also the mechanism E5's Quest preset will need,
because a fixed preset on a mobile GPU is either too slow or too ugly depending on the room.

Order: build the upscale against a fixed scale first and measure it, then add the controller. A dynamic resolution
system without a profiler behind it oscillates, and P0 is what stops that.

---

## Track E — VR / XR

The single-view objection that used to open this section is gone. A0.4 shipped: view and projection are per view, the
frame state carries per-view camera and lighting snapshots, `ViewTargets` holds per-view targets, up to eight views
render in one frame, and the sample app already renders rear-view mirrors through the same path. That was the expensive
half, and it was paid for while the scene pass was open for other reasons — which is exactly what the previous ordering
predicted.

What still blocks VR is the part that was never in this track's prepayment: there is no OpenXR binding, no stereo
submission and no per-eye pacing; the frame loop still paces on window vsync (`Present(SyncInterval: 1)`); input is
pointer-only (`TouchService`, no gamepad); and the UI is screen-space (`Panels/Input.cs`). This track stays last because
it is a new platform claim, not a feature on the existing one.

Ordering here is by dependency, and one dependency is deliberately hoisted out into Track A:

| Item | Content | Depends on |
|---|---|---|
| **E0. OpenXR runtime binding** | Session lifecycle, swapchain image import (`XR_KHR_D3D12_enable` sharing the existing device/queue), event polling, action sets; frame pacing moves from window vsync to `xrWaitFrame` | Desktop maturity |
| **E1. Dual-view rendering** | Two eye passes over the scene (or a multiview broadcast — D3D12 view instancing, `VK_KHR_multiview`, Metal vertex amplification); per-eye matrix constants, per-eye ring indexing | A0.4 |
| **E2. Per-eye render state** | Texture-array render targets (or dual RT sets); per-eye depth/velocity; per-eye TAA history or MSAA in its place; per-eye chain textures for GTAO/Bloom; DDGI and CSM stay shared — they are world/light-space and are the forward+DDGI architecture's VR advantage | E1 |
| **E3. XR input and interaction** | 6DoF head/hand poses, action-based buttons and haptics, ray picking (building on `Picking.cs`, which already unprojects screen rays — the ray origin just becomes a controller pose) | E0 |
| **E4. VR UI** | Screen-space panels do not exist in VR: world-space anchored panel quads, or OpenXR composition layers for 2D surfaces | E0 |
| **E5. Quest (Android)** | OpenXR Loader plus the Android Activity lifecycle under the MAUI host; Vulkan swapchain path (`Shared/LinuxAndroid/Vulkan`); an XR quality preset that drops DDGI/GTAO tiers via the existing `RenderQuality` ladder, which is exactly what that ladder was built for | E2, E3 |

A0.4 has already been paid for, and it is why this track can start with the session rather than with a refactor. The
shadow pass was the working precedent — `Pipeline.SetShadowViewProj` already wrote a per-view matrix as a root constant
per atlas quadrant — and the scene pass now follows the same pattern. E1's remaining work is submission and pacing, not
argument layout.

---

## Track X — Simulation and networking

Both were named as "later features" in the previous analysis and are recorded here so that they stop being re-proposed as
new ideas. Both sit outside the layering for the same reason: nothing in Tracks A–P depends on them, so neither can be
used to justify delaying a layer, and neither has to wait for one.

| Item | What it is | What it is not | Suggested basis |
|---|---|---|---|
| **X0. Physics and collision** | Rigid bodies and a character controller integrated into the existing frame loop. Physics touches no backend, so the four-backend cost that governs everything else in this document does not apply here | A second scene graph, or physics-driven animation | BepuPhysics v2 — pure C#, allocation-conscious, no native dependency to package per platform |
| **X1. Networking** | A client that connects, exchanges world and unit state, and reconciles. Server authority is the default, not an option | Rollback netcode, a shard system, or a hosted service | LiteNetLib — small, no code generation, no reflection |

`Season/Net/WebClient.cs` already does progress-reporting downloads with cancellation. That is the top of this iceberg,
not the bottom — but it does mean the engine has an HTTP client and no excuse to grow a second one.

The one link worth naming: both want B1 if their state is to be saved. A physics body that cannot be written to a world
file and a session that cannot be resumed are demos, which is a further argument for B1 being early rather than an
argument for moving X0 or X1 up.

---

## Dependency map

```
Gate 0 ──> everything

SHIPPED, and their edges are now historical:
  A0.1 indirect draw ──> A6 particles
                     └─> A7 GPU culling
  A0.2 normal target ──> A2 SSR
                     └─> A5 decals
  A3 volumetrics ──> A4 water
  A0.4 per-view split ──> E1 dual-view

STILL OPEN:
D1 recorded baseline ──> A7 (justification) ◄── B4-lite stress scene
                     ├─> A10 occlusion
                     ├─> D2 allocation contract
                     ├─> B4 scale claim
                     └─> P4 dynamic resolution controller

P0 GPU timers on four backends ──> P1, P2, P3, P4 are arguments until this lands

A12 split the 2464-byte block ──> P1b shader-side struct headers
                            └───> P1 IGraphics codegen + shared middle core
mesh decimation (in the asset pipeline) ──> A8 mesh LOD ──> A9 terrain
                                        └─> C3 (the half that is still missing)
A13 materials ──> A14 streaming ──> A9 terrain, B4
            └─> C1 generated image to material ──> B3 catalogue

B0 shell/scene split ──> B1 serialization ──> F0 commands (undo first) ──> F2 world verbs
                                          │                             ├─> F4 editor panes
                                          │                             └─> B2 editing ──> B3 catalogue
                                          └─> C2 runtime C# ──> F3 code.compile / code.apply
A11 frame instance ──> F1 capture, F4 preview pane, E1 dual view

A0.4 (shipped) ──> E1 dual-view ──> E2 per-eye state ──> E5 Quest
E0 OpenXR binding ──> E3 input, E4 UI
RenderQuality ladder ──> E5 XR quality preset (Quest)

X0 physics, X1 networking ──> depend on nothing above; want B1 only to persist state
```

Convergences worth designing for deliberately rather than discovering later:

- **Mesh decimation** is required by both A8 and C3. One implementation, and it is now the single largest shared blocker
  left in the document.
- **B1's type-name-plus-parameters record**, **C2's generated class** and **F0's command payload** are the same idea three
  times. A world record, a generated behaviour and a `world.spawn` argument should all be describable by one schema, or
  the project will end up maintaining three.
- **The Hi-Z pyramid already exists** for SSR. A10 should consume it rather than build a second one, which also means
  occlusion culling becomes a frame-resource change instead of a new effect.
- **A0.4 landed before Track E needed it**, which is the only example in this document of a prepayment actually being
  collected in time. The pattern is worth repeating for P0: everything in Track P is an argument until the GPU timers
  exist on all four backends.

---

## Priority layering

Two naming notes first, because "P" has been doing two jobs in this document.

1. The layers are **L0–L3**, not P0–P3. Track P numbers its own items P0–P4, so the earlier proposal's "P0" meant both
   *the GPU timers* and *the top priority*. Layers take L; Track P keeps its item numbers.
2. A layer is a **dependency band, not a date**. Everything in L0 can start today. Nothing in L1 can be finished before
   its L0 dependency lands. L2 and L3 are deliberately undated, because what they cost depends on how L1 lands.

### What survived from the proposed table

The proposed table had sixteen rows. One was already done when it was written down: *update the Roadmap status table so
the document stops contradicting the source*. That was done in the 2026-11-07 audit, and it is the only row that can be
struck.

The removal step is therefore short and unflattering: **fifteen of sixteen rows are still open, and the one that closed
was documentation.** The table read as a plan whose first step was complete. It was a plan whose first step was
describing the plan.

Two rows are corrected as well as re-ranked:

- **Contract code generation** turned out to be two projects sharing a name. Track P's P1 generates per-backend
  forwarding for `IGraphics`, which is the 3–5K line item. The proposal's "C# struct and constants → four-language
  headers" is different and smaller: the shader-side declarations for `SceneLightParams`, a struct now measured at 2464
  bytes whose only external mirror is one hand-maintained constant (`SCENE_LIGHT_BYTES`) in one web file. They are
  separated below as P1 and P1b, because collapsing them is how the cheap one gets deferred behind the expensive one.
- **The shared middle layer** still carried the old 6–8K line estimate. The audited figure is 3–5K, and the return is not
  the line count: it is that a change stops being made four times without drifting. It is ranked inside P1, because the
  generated forwarding and the shared core are one argument, not two.

The proposed table also omitted everything outside performance and renderer architecture: A8, B0, B2 through B4, C1
through C4, D3 through D5, F1 through F4, all of Track E, and Gate 0. The layering below is the union — thirty-seven
rows, plus Gate 0.

### Gate 0 — before L0

Unchanged: product ids, manifest identity, source delivery, privacy contact. Not engineering, not optional. Every layer
below would otherwise be measured on a build that cannot be distributed.

One Gate 0 item closed by the audit rather than by this pass: the commercial-reference check in `Engine.csproj`. The
engine no longer references the AI layer at all, so what remains is a build guard (D6), not a migration.

### L0 — Make the claims measurable

No new picture comes out of this layer. It exists because fifteen of the rows below are arguments until it lands, and
because the proposed table gave measurement three rows out of sixteen — one of which was the document itself — while the
other thirteen were the things measurement was supposed to judge.

| # | Item | Depends on | Cost | Why this rank |
|---|---|---|---|---|
| L0.1 | **D1 — recorded numbers and CI**: a fixed camera path, per-feature toggles, the four acceptance rulers run by a commit | — | Scene script plus a CI job | The tools exist (`AbCapture`, `FrameProfiler`, the `RenderQuality` switches). The proposed table had this as two rows — "fixed camera path" and "per-feature cost table" — and they are one artefact |
| L0.2 | **P0 — GPU timers on Vulkan, Metal and WebGPU** | The D3D12 implementation as the template | 150–250 lines per backend | Coverage goes from 1/4 to 4/4 with the interface, the statistics and the readback already present |
| L0.3 | **B4-lite — a 10k-instance stress scene** | Nothing; a scene file is enough | Scene plus a script | A7 cannot be judged on the island. At fifty instances every culling strategy is free |
| L0.4 | **A7 — prove or demote** | L0.1, L0.2, L0.3 | One A/B run | `SEASON_CULL_INDIRECT` already exists. Either the GPU path wins on the stress scene, or A7 is deleted — both outcomes are worth more than the feature |
| L0.5 | **D2 — frame allocation budget** | L0.1 | One document, one test | The GC objection is already instrumented; what is missing is a number to compare against |
| L0.6 | **D6 — the build guard** | The CI job from L0.1 | One job step | Keeps the repository's most expensive possible mistake from returning silently |

Outcome: every row below cites a number instead of an intention, and one feature is decided rather than carried.

### L1 — Make the world data

The premise of Track F is that this engine's behaviour model — one C# class per behaviour, a world record that is a type
name plus construction parameters — is already a command surface. L1 turns that observation into a fact. It is also the
same work Track B needs, which is why those two tracks share a layer instead of running in sequence.

| # | Item | Depends on | Cost | Why this rank |
|---|---|---|---|---|
| L1.1 | **B0 — decouple the app shell from the scene** | — | Small | Nothing in this track can be run twice in one process until it lands. It was missing from the proposed table entirely, although B1 depends on it |
| L1.2 | **B1 — world serialization**: type name plus construction parameters, a world file, a scene registry | B0 | Medium | The agent foundation, the terrain and streaming prerequisite, and the thing that makes undo possible |
| L1.3 | **F0 — the command layer**, with undo as its first requirement | L1.2 | Medium | Undo has to live in the command record, not be added to it later. This is the order that "B2 editing" had backwards |
| L1.4 | **F2 — the world verbs**: spawn, remove, set, query, save, load | L1.2, L1.3 | Medium | The same payload schema as B1's record, on purpose. Three schemas is how this project would fail at this |
| L1.5 | **F1 — headless run and capture** | — | Small | Packaging on `AbCapture`, and the cheapest way to keep a demonstration honest |
| L1.6 | **A12 — split the 2464-byte block** | — | Medium | Cheap before P1b generates headers from it, expensive afterwards |
| L1.7 | **P1b — struct and constant mirroring**: the shader-side declarations for the split blocks, and `SCENE_LIGHT_BYTES`, generated from the C# structs | L1.6 | Small | One hand-maintained constant is all that stands between a layout change and a silently discarded upload |
| L1.8 | **P1 — contract code generation and the shared middle layer**: one declarative description of the `IGraphics` object families, per-backend forwarding generated, then byte-identical bodies → data layout → skeleton | L1.6 | Medium | Net −3–5K lines. The return is not the lines; it is that a change stops needing four edits |
| L1.9 | **A13 — material assets and instances** | — | Medium | C1's material half and B3's catalogue both need an identity for a material that is not "whatever the glTF said" |
| L1.10 | **Asset pipeline and texture compression** (D3 and P3 as one project): BC7/BC5/BC6H, ASTC, ETC2, KTX2, an offline transcode step, mesh decimation | — | Large | Three thresholds at once — package size, web load time, and the decimator A8 and C3 both wait on. It was two rows in two tracks and was always one project |
| L1.11 | **C1 — completion**: generated image to material, generated audio to source, a generated-assets folder | L1.9 | Small | The paid tier's reason to be bought, closed off cheaply |
| L1.12 | **D4 — sample ladder and a generated backend matrix** | L0.1 | Small | The hand-maintained matrix on the features page is already out of date; generation is the only fix that stays fixed |

Outcome: a world is a file, an agent can drive it, a person can undo it, a material has an identity, and a texture costs
a quarter of what it did.

### L2 — Renderer architecture and the agent loop

| # | Item | Depends on | Cost | Why this rank |
|---|---|---|---|---|
| L2.1 | **A11 — FrameSchedule, instantiated** | — | Medium | The static facade is the last frame-scoped global state in the render path, and F1, F4 and E1 all need a second frame to exist |
| L2.2 | **A14 — streaming and asynchronous upload** | L1.9 | Medium | A9 and B4 both quietly assume residency exists. Keyed on material identity, hence after L1.9 |
| L2.3 | **A10 — occlusion culling on the existing Hi-Z pyramid** | L0.4 | Medium | Turns an SSR accelerator into a frame resource with two consumers. Still needs A7's baseline first |
| L2.4 | **P4 — temporal upscaling**, then dynamic resolution | L0.1, L0.2 | Medium | The largest single GPU win on the integrated hardware most of this audience runs; the controller half is worthless without the profiler |
| L2.5 | **C2 — cloud LLM and Roslyn runtime C#** | L1.2, L1.4 | Medium | The enabler for F3, and it closes the loop F2 opens: a verb that is missing can be written instead of requested |
| L2.6 | **P2 — shader precompile and PSO disk cache** | — | Large | Removes the first-run hitch and the `glslang` payload inside the APK. First row to defer if L2 slips; nothing below depends on it |
| L2.7 | **D5 — platform honesty** | L0.1, L0.2 | Small | The backend matrix becomes a claim only when the fourth column is populated |

Outcome: the frame path has no static state, the first launch is not a compile, and an agent can extend the engine rather
than only drive it.

### L3 — Scale, tools, and new audiences

L3 is large and it is not one thing. The three groups below do not compete for the same work, which is why they can run
in any interleaving once L2 lands.

#### L3a — Content scale

| # | Item | Depends on | Cost | Why this rank |
|---|---|---|---|---|
| L3a.1 | **A8 — mesh LOD** | L1.10 (the decimator) | Medium | Stops being the shared blocker for A9 and C3 |
| L3a.2 | **A9 — terrain and terrain LOD** | L3a.1, L2.2, L2.3, L1.10 | Large | Four prerequisites, and three of them are now scheduled rather than assumed |
| L3a.3 | **B4 — the mass-unit reference world** | L0.3, L2.2 | Large | The scene that makes the strategy-scale claim falsifiable, and the real A7 workload rather than the synthetic one |
| L3a.4 | **C3 — mesh post-processing and glTF export** | L1.10 | Medium | So generated meshes stop living in a preview window |

#### L3b — Production tools

| # | Item | Depends on | Cost | Why this rank |
|---|---|---|---|---|
| L3b.1 | **B2 — editing affordances** | L1.3 (undo), L1.9 | Medium | Undo already exists by the time this starts, which is the entire point of the ordering |
| L3b.2 | **B3 — asset catalogue** | L1.9, L1.10 | Medium | It is a view over identities that L1 creates |
| L3b.3 | **F3 — what the agent actually gets**: dry-run diffs, twelve verbs, no capability the editor lacks | L2.5 | Medium | The safety rule and the tool surface are one design, not two |
| L3b.4 | **F4 — the four-pane editor** | L3b.1, L3b.2, L2.1 | Medium | The same surface, rendered. Last in its track because it adds no verbs |

#### L3c — New audiences

| # | Item | Depends on | Cost | Why this rank |
|---|---|---|---|---|
| L3c.1 | **Track E, E0 → E4**: OpenXR binding, dual view, per-eye state, XR input, world-space UI | Desktop maturity; A0.4 shipped | Large | A new platform claim, not a feature on the existing one |
| L3c.2 | **E5 — Quest** | L3c.1, L2.4 | Large | The XR quality preset is a `RenderQuality` configuration, which is why L2.4 comes first |
| L3c.3 | **C4 — auto-rigging** | L3a.4 | Research | Not a commitment. Listed so it stops being re-proposed as new |
| L3c.4 | **X0 / X1 — physics and networking** | Only B1, and only for persistence | Large | Outside the layer dependencies by design; see Track X |

Console technical adaptation stays outside every layer: acknowledged, not scheduled, and gated on SDK access rather than
on engineering effort. See the section below.

### What the layering changes about the previous answer

1. **A7 moved from "later" to L0.** It is cheap to decide and expensive to carry. A feature that exists, is switchable,
   and has never been measured is worse than a feature that does not exist, because it gets quoted in the README.
2. **Undo moved from B2 to F0.** The previous order built editing and then added undo to it. A command layer without
   undo is an API with extra steps, and retrofitting undo into a world format that stores references instead of addresses
   does not work — which is why B1's record has to carry addresses from the first commit.
3. **Texture compression and the asset pipeline merged**, and so did contract code generation and the shared middle
   layer. Each pair was two rows in two tracks and was always one argument.
4. **Physics and networking left the schedule.** They carry no dependency in either direction, so a layer that contains
   them is a layer that can be redrawn at will. They are recorded in Track X and deliberately not ranked.

One thing the layering does not do: it does not put a date on L2 or L3. The previous phase view did, and the last audit
found nine shipped features that no phase had scheduled. A layer states what must be true before it starts, which
survives that; a date does not.

---

## Consoles — acknowledged, not scheduled

Xbox, PlayStation, and Switch are listed here once so that the question is answered and parked, not so that work
begins.

The positioning that matters: **SeasonAI is a service for developers, not a runtime toy for players.** It exists only
on Windows, Mac, and Linux. On consoles the product is a finished game built with this engine — so the runtime code
generation and local-model policy problems that would haunt a console port of the AI layer simply do not arise: the
AI layer never ships into a console build.

Two responsibilities are being separated here, and the separation is what makes this a distant topic rather than a
blocked one:

- **Game certification is the game developer's job.** Store policy, TRC/lotcheck, age ratings, platform fees — all
  of it is the publisher's work once an engine user ships a title. It is not part of an engine's roadmap.
- **Technical adaptation is the engine vendor's job.** A new graphics backend, platform audio/input/storage/achievement
  services, and a console host in place of the WinUI3 or SDL window. That is real engineering: PS5 means a fifth
  graphics backend (proprietary AGC/PSSL, no Vulkan), Switch means another plus the tightest memory ceiling of all
  four, Xbox reuses the most (D3D12 family, but the WinUI3 host still becomes a GDK host).

What neither side controls alone: the proprietary graphics/audio/storage APIs sit behind NDAs and vendor SDK
agreements. Getting access is a conversation with the console vendors, and that conversation only becomes productive
once the engine has enough users shipping games that the vendors have a reason to take it seriously. That is a
scale-and-standing gate, not an engineering gate, which is exactly why nothing here carries a layer or a date.

Steam is the deliberate exception and is not part of this section: it is not a console, the Linux + Vulkan backend
already runs, and Steamworks is a public SDK. It is a distribution channel decision, evaluable on its own and far
easier than any of the above.

---

## Non-goals

Unchanged in spirit from the archive, with additions from this analysis:

- Competing on feature count with editor-centric engines.
- Visual scripting, node graphs, or inspector-assembled behaviour, in any form.
- A generalised frame graph before the fixed schedule is genuinely limiting.
- Runtime texture compression, or any decode-to-uncompressed path.
- A centralised template or asset marketplace.
- Rebuilding around ECS for trend alignment. If bulk entity updates prove to be the bottleneck in B4, the answer is
  a targeted data-oriented path for bulk entities, not a paradigm change across the whole engine.
- Fallback rendering paths. Capability reporting instead.
- Promising auto-rigging quality before it is verified.
- Any XR runtime besides OpenXR. No SteamVR/Oculus-native integration layer appears, even as an "optimised path" —
  OpenXR is the one implementation per domain, same as the rule that killed OpenGL fallbacks.
- Mobile-quality double pipelines for VR: the Quest preset is a `RenderQuality` configuration, not a second shader set.
- Console certification, lotcheck, or store policy as engine work. Porting the engine is our side of the line;
  certifying a shipped game is the game developer's. The roadmap acknowledges console technical adaptation and
  schedules none of it.
- Shipping the AI layer into console builds. SeasonAI is a developer service for Windows/Mac/Linux; console builds
  carry the finished game, nothing more.
- An agent-only mutation API, or a control surface bound to a network interface by default. One command surface, two
  clients — and the agent never gets a capability the editor lacks.
- A second shader toolchain. P1 generates the C# around the shaders; it never generates the shaders. Each backend keeps
  its own hand-written source, because that independence is the one architectural decision that has paid for itself
  repeatedly.
- A second Hi-Z pyramid, or a separate occlusion hierarchy, while SSR's already runs every frame.

---

## Success criteria

The roadmap is working if:

- The renderer claim is **provable**: benchmark numbers and reference images are published, not described.
- The GC objection is **answerable in one number**.
- Someone can evaluate the engine in ten minutes without reading engine source.
- Generated assets end up in scenes, not in a folder.
- Adding a world type does not require writing editor code.
- The open repository builds without the commercial layer, verified by CI on every commit.
- `Season/` stays smaller and clearer than the feature list would suggest.
- One implementation per domain still holds — no OpenGL fallback appears, no second animation format, no second text
  renderer.
- Every editor action is also a command, and every command can be undone.
- The GPU profile column is populated on all four backends for the same scene, so "it got faster" is a measurement
  rather than a memory.
- Adding a visual feature stops costing four driver files of boilerplate. The shaders stay four; the plumbing around
  them does not.
- When Track E starts, the same rule extends to XR: OpenXR only, no runtime-specific integration layers.
