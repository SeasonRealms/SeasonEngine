# SeasonEngine Roadmap

## How to read this

Work is grouped into tracks that map to the questions the project actually faces:

| Track | Question it answers | Audience it serves |
|---|---|---|
| **A. Rendering** | Is the renderer genuinely next-gen? | The developers who evaluate the repo |
| **B. Host & worlds** | Can someone see what the engine does without reading the source? | Evaluators, and eventually creators |
| **C. AI** | Is the paid layer worth money, and does it help make games? | Buyers |
| **D. Foundations** | Can any of the above be proven, maintained, or shipped? | Everyone, including future contributors |
| **E. VR / XR** | Can the renderer claim reach the platforms where single-view assumptions stop applying? | A later audience, after the desktop story ships |

Track D is not in the original three. It exists because several items in A, B, and C are unprovable or unshippable
without it, and because the project's central differentiation claim — *architectural minimalism recovers the
performance a managed language costs you* — currently has no measurement behind it at all. Track E is newer still,
and it is deliberately last: its only pre-payment is A0.4, hoisted into Track A so the most expensive refactor is
absorbed while the scene pass is already open.

**Ordering inside each track is by dependency, not by appeal.** Where an item is blocked by another, it says so.
Several attractive features are deliberately placed late because their prerequisites are infrastructure work across
four backends.

Status vocabulary used throughout:

- **Shipped** — implemented and exercised by a running app
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
| **Scene normal buffer** | **Absent** | `RenderPass.cs` declares `ColorTarget`, `DepthTarget`, `VelocityTarget` only — GTAO derives normals from depth |
| **Indirect draw / dispatch** | **Absent** | Zero matches for `ExecuteIndirect`, `CommandSignature`, `DispatchIndirect`, `vkCmdDrawIndexedIndirect` across all backends |
| **Texture compression** (BC / ASTC / ETC2 / KTX2) | **Absent** | No compressed-format path in texture loading |
| Occlusion culling, Hi-Z | Absent | — |
| GPU particles | Absent | — |
| Screen-space reflections | Absent | DDGI covers indirect diffuse only |
| Decals | Absent | — |
| Volumetric fog / light shafts | Absent | Atmospheric scattering exists; local volumetrics do not |
| Water system | Absent | `Apps/Engine/Panels/Sea.cs` is scene content, not an engine system |
| Terrain system, heightmaps, tessellation | Absent | — |
| Mesh or terrain LOD, impostors | Absent | — |
| Motion blur | Absent | Velocity buffer already exists, so only the resolve is missing |
| **VR / XR of any kind** (OpenXR, stereo rendering, HMD tracking, controller input) | **Absent** | Zero matches for `OpenXR`, `XR_`, `stereo`, `HMD`, `foveated`; the only `Stereo` hit is an audio channel count in `LinuxImage.cs` |
| glTF **export** | Absent | Import only |
| Mesh processing (decimation, UV repair, normal repair) | Absent | — |
| Material instances / variants, batch merging by material | Absent | Materials come from glTF import |

Note on the render pipeline shape: the D3D12 pipeline states are organised as Opaque / Transparent / Fade with cull
variants (`Platforms/Windows/DirectX/Pipeline.cs`), i.e. **forward rendering**, with a storable depth target and a
velocity target. There is no G-buffer. This single fact sets the cost of several Track A items.

### Host, editor, worlds

| Capability | Status | Evidence |
|---|---|---|
| `Show` / `Play` / `Edit` / `Debug` mode enum | Shipped | `Apps/Engine/App.cs:7-12`, default `Mode.Play` |
| Object selection, per-instance picking, highlight | Shipped | `Season/Panels/ObjectPicker.cs` |
| Drag vs. click discrimination, object dragging | Shipped | `ObjectPicker.cs:53` — 20 px threshold separates click from drag |
| Property editing (position, size) | Partial | `ObjectPanel` in `ObjectPicker.cs:663+` — fixed fields, no generic property grid |
| Player collision, occluder fade | Shipped | `Apps/Engine/Management/PlayerCollider.cs`, `OcclusionFade.cs` |
| Island scene with dynamic behaviour (movement, jumping, birds, robots) | Shipped, hardcoded | `Apps/Engine/App.cs` `Create()`; behaviour in `Panels/Direction.cs`, `Skill.cs`, `Player.cs`, `Birds.cs` |
| Debug views for intermediate buffers | Shipped | `DepthView`, `VelocityView`, `Sdf3DView` |
| **Scene serialization, save, load** | **Absent** | No serializer, no scene format, no world file |
| **Empty / sceneless startup** | **Absent** | `Create()` builds the entire island unconditionally |
| Scene hierarchy inspection | Absent | — |
| Asset library or catalogue | Absent | Model paths are hardcoded per scene class |
| Undo / redo | Absent | — |

### AI

| Capability | Status | Evidence |
|---|---|---|
| Text generation and translation | Shipped | `SeasonAI/Text.cs` — llama.cpp / GGUF |
| Image generation and editing (SD1.5, SDXL, SD3.5, Qwen-Image-Edit, Flux.2-Klein, Ovis, LongCat) | Shipped | `SeasonAI/Image.cs` (941 lines) |
| Video generation | Shipped | `AIVideo` in `Image.cs`; UI in `Panels/VideoPanel.cs` (1111 lines) |
| Music generation | Shipped | `SeasonAI/Music.cs` — StableAudio |
| Speech to text | Shipped | `SeasonAI/STT.cs` — Whisper / GGUF |
| Text to speech, dual ONNX + GGML backends | Shipped | `SeasonAI/TTS.cs` (766 lines) |
| Vision / OCR | Shipped | `SeasonAI/Vision.cs` |
| Model acquisition | By hand | Panels link to Hugging Face; the app does not download models |
| **Generated output flowing back into the engine as an asset** | **Absent** | Results are shown inside AI panels; nothing binds a generated image to a scene material |
| **Any network client** | **Absent** | No `HttpClient` anywhere in the repository |
| **3D generation, auto-rigging, animation synthesis** | **Absent** | — |

### Foundations

| Capability | Status |
|---|---|
| Unit tests | Absent |
| Benchmark scenes with recorded numbers | Absent |
| Screenshot / image-diff regression | Absent |
| Frame allocation budget or GC measurement | Absent |
| Feature-by-backend support matrix in docs | Absent |
| Asset import caching | Absent |

---

## Gate 0 — Release engineering

Not a track. A gate. These items block the open-source launch, the store submission, and every community post, and
they are tracked in detail in [`Store/Windows/SubmissionChecklist.md`](Store/Windows/SubmissionChecklist.md).

1. **B1 — conditional commercial reference.** `Apps/Engine/Engine.csproj` references `SeasonAI` unconditionally and
   the repository has no `.gitignore`. Publishing as-is either leaks the source being sold or leaves a repository
   that will not build after cloning. Both outcomes are fatal to the funnel, because the funnel depends on cloning
   and running. Fix with `Condition="Exists(...)"` plus an MIT stub panel, so the missing layer becomes an upgrade
   prompt rather than a compile error.
2. **B2 — store product identifiers.** All three add-on IDs return `""`, so no purchase can complete.
3. **B3 — MSIX packaging identity.** `WindowsPackageType=None`, and the manifest is still MAUI template
   boilerplate. Store in-app purchase requires package identity.
4. **S1 — source-code delivery.** The commercial archive ships inside the package with the decryption key in the
   same binary. Move delivery server-side before selling the source tier.
5. **S4 — capability declarations and privacy contact.** Add the `microphone` device capability; replace the
   placeholder contact address in `PRIVACY.md`.

Nothing in Track A/B/C/D should be prioritised over Gate 0. Every day the repository is unpublished, all four
tracks are being built for an audience that cannot see them.

---

## Track A — Rendering

The requested list is sound; the ordering needs to change, because three of the items are not features at all but
infrastructure that other items sit on top of, and one item is nearly free today.

### A0. Prerequisites

These unlock most of the rest. They are unglamorous and none of them produce a screenshot.

#### A0.1 Indirect draw and dispatch abstraction — *blocks A6, A7*

There is currently **no indirect draw or indirect dispatch path on any backend.** GPU-driven culling means the GPU
decides what to draw, which requires the draw arguments to live in a GPU buffer. Without indirect draw, "GPU
culling" can only mean *compute a visibility mask on the GPU, read it back, and issue CPU draws* — which adds a
pipeline stall and is usually slower than the CPU frustum culling already shipping.

Scope: an `IndirectArgsBuffer` concept in the shared layer, plus `ExecuteIndirect` + command signature (D3D12),
`vkCmdDrawIndexedIndirect` (Vulkan), indirect command buffers (Metal), and `drawIndexedIndirect` (WebGPU). Metal and
WebGPU have the most awkward story here and should be allowed to lag behind an explicit capability flag rather than
holding back the desktop path.

This is the single largest piece of enabling work in Track A. It should be scheduled deliberately, not discovered
halfway through building particles.

#### A0.2 Scene normal target — *blocks A2, A5; improves GTAO*

The scene pass writes colour, depth, and velocity. GTAO reconstructs normals from depth because there is nothing
else to read. Depth-derived normals are flat-shaded per triangle and lose all normal-map detail, which is
survivable for ambient occlusion and **not** survivable for reflections or decals.

Adding an RGB10A2 or RG16 (octahedral) normal target to the scene pass is a four-backend change to the scene pass
attachment set, plus a shader output addition. It is modest, and it converts SSR and decals from "possible with
visible artefacts" into "straightforward".

Decide this before A2. Doing SSR against depth-derived normals and then adding the normal buffer afterwards means
writing the resolve twice.

#### A0.4 Per-view matrix split — *pre-requisite for Track E, useful on its own*

The per-object matrix constant buffer bundles World, View, and Projection together (`Season/Basic/Camera.cs`
`MatrixBuffer`), and every backend indexes its constant-buffer ring by frame index alone (`int fi = (int)
Device.FrameIndex` in the D3D12 groups). That bakes in two assumptions: one camera per frame, and each object drawn
at most once per frame with that camera. Both break the moment the scene is rendered twice — stereo eyes today,
rear-view mirrors or picture-in-picture views later.

The shadow pass already demonstrates the fix: `DrawShadowPrimitive` keeps per-object data (World) in b0 and moves the
view matrix into a separate per-view constant (`ShadowPassParams` root constant, written once per atlas quadrant in
`Pipeline.SetShadowViewProj`). A0.4 generalises that pattern into the scene pass: World stays per-object, View and
Projection move to a per-view constant written once per view, and the ring indexing gains a view dimension.

It touches all four shader sources (HLSL / GLSL / MSL / WGSL) but is mechanical, and it is the single change that
makes multi-camera rendering cheap for every future consumer — Track E included. Its cost is why it sits early in
the Track A ordering while nothing in Track A blocks on it.

#### A0.3 Texture compression and a container format — *not a visual feature*

Belongs here rather than at the end of the list, because it is not about image quality:

- **Package size.** The store submission already carries package-size risk. Uncompressed textures are the largest
  controllable contributor.
- **Web viability.** `Apps/EngineWasm` and `Apps/EngineWeb` fetch assets over the network. Uncompressed textures
  make the browser hosts a demo that nobody waits for.
- **VRAM and bandwidth.** Compressed formats are typically the largest single frame-time win available on
  integrated GPUs, which is what most evaluators will run this on.

Scope: BC7/BC5/BC6H on desktop, ASTC on mobile, ETC2 as the Android floor, KTX2 as the container so one asset file
serves every platform, and an offline transcode step rather than runtime compression. Runtime decode to
uncompressed is an anti-goal — it defeats the purpose.

### A1. Motion blur — *do this first*

The cheapest item on the entire list and it should not wait. The velocity buffer already exists and is already
consumed by TAA; `VelocityView` already visualises it. What is missing is one resolve kernel that gathers along the
velocity vector, with the usual depth-aware weighting to limit bleeding across silhouettes.

It also has outsized marketing value relative to cost: motion blur is instantly legible in a video capture, and the
store listing and community posts both need motion.

### A2. Screen-space reflections — *needs A0.2*

`SceneColorCopy` already produces a downscaled scene colour, which is the other half of what an SSR march needs.
With A0.2 in place this is a contained AfterScene compute effect: march the depth buffer in screen space, sample
scene colour at the hit, fall back to the DDGI irradiance atlas on miss so that off-screen reflections degrade to
something plausible rather than to black.

That DDGI fallback is worth calling out as a genuine architectural advantage: engines without a GI solution have to
ship a separate reflection probe system to avoid black SSR misses. This project already has the volume.

### A3. Volumetric fog and light shafts

Reuses more existing infrastructure than it looks. `SkyAtmosphere` already contains a working ray-march with 3D
LUTs, and `CascadedShadow` already provides the shadow lookup needed to make shafts appear. The work is a
froxel-grid volume (scatter/extinction injection, then a depth-slice accumulation pass) sampling the existing
cascades for occlusion, composited during the scene resolve.

Medium cost, high visual payoff, and it pairs with the day/night cycle that is already the strongest thing to
demonstrate.

### A4. Dynamic water — *schedule after A2*

Water without reflections looks wrong in a way that no amount of wave detail fixes, so this follows SSR rather than
preceding it. Scope: FFT or Gerstner displacement on a GPU-computed height field, screen-space refraction using the
existing depth target, shoreline blending from depth difference, and reflection from A2.

There is an existing `Sea.cs` in the island scene to migrate onto the engine-level system, which conveniently makes
this the first test of whether "engine system" and "scene content" are cleanly separated.

### A5. Decals — *needs A0.2, constrained by forward rendering*

Deferred decals are the standard approach and this is a forward renderer, so be honest about the ceiling up front.
Two viable options:

- **Forward projected decals** — bind a decal list per draw and blend in the material shader. Works, costs shader
  complexity and a per-object decal list, and does not scale to many overlapping decals.
- **A deferred decal pass writing into a normal/albedo overlay** — requires more of a G-buffer than A0.2 alone
  provides.

Recommendation: ship the forward variant with a documented decal-count limit. Do not add a G-buffer for decals
alone; that is a pipeline-shape decision that should be driven by more than one feature.

### A6. GPU particles — *needs A0.1*

Simulation in a compute shader over a persistent particle buffer, with an indirect draw for the alive set. The
compute framework and `StorageBuffer` already exist, so the simulation half is well-supported today; the draw half
is exactly what A0.1 provides. Attempting this before A0.1 produces a CPU-readback design that will be thrown away.

Sorting for correct alpha blending is the hidden cost. A GPU radix sort is a real piece of work. Consider shipping
with additive and alpha-tested particles first, which need no sorting, and treating sorted alpha as a later step.

### A7. GPU-driven culling — *needs A0.1 and a benchmark from D1*

The highest-leverage item for the "tens of thousands of units" scenario, and the one most likely to be built without
evidence that it helped. Prerequisites in order:

1. A0.1, without which this cannot exist in a useful form.
2. **A measured benchmark scene (D1).** CPU frustum culling already ships and is on by default. Without a recorded
   baseline there is no way to show that the GPU path is faster, and no way to find the actual bottleneck — which,
   in a managed engine at ten thousand entities, is more likely to be per-entity `Update` cost and allocation
   pressure than draw submission.

Scope once unblocked: per-instance bounds in a GPU buffer, a compute pass writing a compacted visible-instance
list, Hi-Z pyramid from the previous frame's depth for occlusion, and indirect draw of the compacted list.

**Sequencing warning.** If the ten-thousand-unit scenario turns out to be CPU-bound in `Update` rather than in draw
submission, then A7 is the wrong fix and the right fix is in Track D (allocation budget) or in a data-oriented
update path for bulk entities. Measure before building.

### A8. Mesh LOD — *an asset pipeline feature, not a renderer feature*

Selecting a LOD at draw time is trivial. Having LODs to select is not, and that is where the work lives:

- glTF has no core LOD concept; `MSFT_lod` is the de facto extension. Either adopt it or define a repository
  convention.
- Without mesh decimation in the pipeline, LOD chains must be authored externally, which contradicts the
  "generate assets locally" story in Track C.

This is where Track A and Track C meet: both need mesh processing. Build the decimator once and use it for both.

### A9. Terrain and terrain LOD — *largest item, schedule last*

A terrain system is not one feature. It is heightmap representation, chunking, a CLOD or clipmap scheme, crack-free
stitching, splat-mapped multi-layer materials, and vegetation scattering — and vegetation scattering wants A6 and A7
to already exist.

Recommendation: do not start terrain until A0.1, A7, and A8 are in place. Starting earlier produces a terrain
implementation that has to be rewritten once GPU-driven culling changes how geometry is submitted.

### Track A ordering summary

| Order | Item | Blocked by | Relative cost | Visible in a screenshot |
|---|---|---|---|---|
| 1 | A1 Motion blur | — | Low | Yes |
| 2 | A0.3 Texture compression | — | Medium | No |
| 3 | A0.2 Normal target | — | Low–Medium | No |
| 4 | A0.4 Per-view matrix split | — | Medium, four backends | No |
| 5 | A2 SSR | A0.2 | Medium | Yes |
| 6 | A3 Volumetrics | — | Medium | Yes |
| 7 | A0.1 Indirect draw | — | High | No |
| 8 | A6 GPU particles | A0.1 | Medium | Yes |
| 9 | A7 GPU-driven culling | A0.1, D1 | High | No |
| 10 | A4 Water | A2 | Medium | Yes |
| 11 | A5 Decals | A0.2 | Medium | Yes |
| 12 | A8 Mesh LOD | Mesh processing | Medium | No |
| 13 | A9 Terrain | A0.1, A7, A8 | Very high | Yes |

A0.4 is placed at position 4 despite blocking nothing in Track A: it is a four-backend refactor best done while the
scene pass is being touched anyway (A0.2), and deferring it until Track E starts would make VR pay the whole cost at
once. A1 stays first because it is a one-kernel visual win with marketing value, not because A0.4 is less important.

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

The three-part shape here is right — output-to-engine, cloud reasoning, 3D generation — but the **relative ordering
of 3D generation and cloud LLM should be reversed** from the original framing. The reasoning is below, after the
items, because it is the one substantive disagreement in this document.

### C1. Generated output into the engine — *do this first, it is nearly free*

The most under-valued gap in the whole repository. Eight generation capabilities ship, and **none of their output can
become an engine asset.** Results are displayed inside the AI panels and stop there.

Meanwhile the paid tier's entire premise is that developers want generated assets *in their game*. Today the app is,
functionally, a local generation utility that happens to be bolted to a renderer.

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

### C2. Cloud LLM and runtime C# generation — *desktop only*

Two capabilities that combine into something neither has alone.

**Cloud model access.** Requires an HTTP client, since the repository has none, plus streaming response parsing,
user-supplied API key storage, and cancellation. A few hundred lines, not a pipeline. Commercially it has an
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

### C3. 3D asset generation — *static props only, no rigging promise*

Valuable and genuinely wanted; the scope needs to be honest about where the work actually is. The generation model
is the *small* part.

What has to exist that does not exist today:

1. **Model integration** — a local image-to-3D or text-to-3D model. Real, and the easiest step.
2. **Mesh post-processing** — generated meshes arrive with poor topology, unconstrained triangle counts, and no
   usable UV layout. Decimation, UV unwrapping, and normal repair are required, and the repository contains **no
   mesh processing code at all**.
3. **glTF export** — the engine consumes exactly one format, and only the *import* half exists. Writing a glTF
   exporter is new work.
4. Only then: something usable in a scene.

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

### Why C2 should precede C3

The original framing places 3D generation near-term and cloud reasoning long-term. Four reasons to invert that:

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

### D1. Benchmarks and screenshot regression — *the highest-leverage engineering investment available*

Two things, both absent:

**Benchmark scenes with recorded numbers.** A7 cannot be justified without a baseline, B4 cannot support the scale
claim without one, and the differentiation argument is unmeasured today. Each benchmark needs a fixed camera path, a
fixed frame count, and recorded frame time, draw count, and allocation figures.

**Image-diff regression.** For a project whose entire pitch is *the renderer is real*, a silent visual regression is
the most expensive possible bug — and this codebase's accumulated pitfalls are overwhelmingly of exactly the kind
that a reference-image comparison catches immediately and a compile check never catches: per-frame UBO cross-talk
between controls, atlas memory not cleared before use, backend-specific struct layout drift, shader output divergence
between the four APIs. Render a fixed set of scenes headless per backend, compare against references with a
perceptual threshold, fail the build on divergence.

This also has a specific and growing value: it is the only mechanism that makes AI-assisted changes to shader and
backend code safe to accept.

### D2. Frame allocation budget — *make the central claim measurable*

The differentiating argument is that architectural minimalism recovers what a managed runtime costs. Pure C# engines
are dismissed on exactly one axis — **GC pauses** — and that dismissal will appear in the first community thread.

The answer cannot be prose. It has to be a number:

- An explicit **zero-steady-state-allocation** target for the frame loop, treated as a contract rather than an
  aspiration.
- Allocation measurement in the benchmark harness, with failure on regression.
- A published figure: allocated bytes per frame, and GC collection counts over a fixed benchmark run.

"Zero bytes allocated per frame at ten thousand animated instances" ends that argument in one line. Nothing else
does.

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

Once Gate 0's conditional reference lands, the two-tier licence split becomes something that can silently break. A CI
job that builds the open repository *without* `SeasonAI` present is the cheapest possible protection against the open
engine quietly acquiring a dependency on the commercial layer.

---

## Track E — VR / XR

The renderer is single-view by construction: the camera is LookAt plus symmetric FOV
(`Rendering/Camera3D.cs`), the per-object matrix buffer bundles View with World (`Basic/Camera.cs`), the render
target chain is a set of static singletons (`FrameSchedule.SceneColor/SceneDepth/SceneVelocity`), D3D12 render
targets are created with `DepthOrArraySize = 1`, the frame loop paces itself on window vsync (`Present(SyncInterval:
1)`), input is pointer-only (`TouchService`, no gamepad), and the UI is screen-space (`Panels/Input.cs`). Every one
of those is an assumption VR breaks, so this track is deliberately last: it is a new platform claim, not a feature on
the existing one, and none of it should be started before the desktop story ships.

Ordering here is by dependency, and one dependency is deliberately hoisted out into Track A:

| Item | Content | Depends on |
|---|---|---|
| **E0. OpenXR runtime binding** | Session lifecycle, swapchain image import (`XR_KHR_D3D12_enable` sharing the existing device/queue), event polling, action sets; frame pacing moves from window vsync to `xrWaitFrame` | Desktop maturity |
| **E1. Dual-view rendering** | Two eye passes over the scene (or a multiview broadcast — D3D12 view instancing, `VK_KHR_multiview`, Metal vertex amplification); per-eye matrix constants, per-eye ring indexing | A0.4 |
| **E2. Per-eye render state** | Texture-array render targets (or dual RT sets); per-eye depth/velocity; per-eye TAA history or MSAA in its place; per-eye chain textures for GTAO/Bloom; DDGI and CSM stay shared — they are world/light-space and are the forward+DDGI architecture's VR advantage | E1 |
| **E3. XR input and interaction** | 6DoF head/hand poses, action-based buttons and haptics, ray picking (building on `Picking.cs`, which already unprojects screen rays — the ray origin just becomes a controller pose) | E0 |
| **E4. VR UI** | Screen-space panels do not exist in VR: world-space anchored panel quads, or OpenXR composition layers for 2D surfaces | E0 |
| **E5. Quest (Android)** | OpenXR Loader plus the Android Activity lifecycle under the MAUI host; Vulkan swapchain path (`Shared/LinuxAndroid/Vulkan`); an XR quality preset that drops DDGI/GTAO tiers via the existing `RenderQuality` ladder, which is exactly what that ladder was built for | E2, E3 |

A0.4 exists in Track A so that the hardest refactor — separating per-view matrices from per-object state across four
shader sources — is paid for long before this track starts. The shadow pass is the working precedent
(`Pipeline.SetShadowViewProj` already writes a per-view matrix as a root constant per atlas quadrant).

---

## Dependency map

```
Gate 0 ──> everything

A0.1 indirect draw ──> A6 particles
                   └─> A7 GPU culling ──> A9 terrain
A0.2 normal target ──> A2 SSR ──> A4 water
                   └─> A5 decals
mesh decimation ──> A8 mesh LOD ──> A9 terrain
                └─> C3 3D generation

D1 benchmarks ──> A7 (justification)
              ├─> D2 allocation budget
              └─> B4 scale claim

B0 shell/scene split ──> B1 serialization ──> B2 editing ──> B3 catalogue
                                          └─> C2 runtime C# (shared design)

C1 output-to-engine ──> raises the value of the existing paid tier immediately

A0.4 per-view matrix split ──> E1 dual-view rendering ──> E2 per-eye state ──> E5 Quest
E0 OpenXR binding ──> E3 input, E4 UI
RenderQuality ladder ──> E5 XR quality preset (Quest)
```

Convergences worth designing for deliberately rather than discovering later:

- **Mesh decimation** is required by both A8 and C3. One implementation.
- **B1's type-name-plus-parameters format** and **C2's generated C# class** are the same idea. Design them together.
- **A0.4's per-view matrix split** and any eventual multiview broadcast (E1) share the same per-view constant layout.
  Land A0.4 first; it is the non-negotiably portable half, and multiview can stay a per-backend acceleration on top.

---

## Phase view

Dates are intent, not commitment.

### Phase 0 — Ship something (2026 H2)

Nothing here is new capability. All of it is the difference between a private repository and a public project.

- Gate 0 in full
- **A1 motion blur** — the one cheap visual win, and both the store listing and community posts need motion
- **C1 generated output into the engine** — makes the paid tier defensible
- **D1 benchmark harness**, even minimally, so later work has a baseline
- Store submission; first community posts once Gate 0's B1 is fixed

Outcome: the funnel exists and the paid tier has a reason to be bought.

### Phase 1 — Demonstrability (2027 H1)

- **B0 shell/scene decoupling**, then **B1 world serialization**
- **A0.2 normal target** and **A0.4 per-view matrix split**, then **A2 SSR** — the scene pass is open for A0.2, so
  pay for the matrix split in the same visit
- **A0.3 texture compression** — required before the web hosts are worth showing to anyone
- **D4 sample ladder** and the compute-effect walkthrough
- **D2 allocation budget** established as a measured contract

Outcome: someone can create and save a world, and the performance claim has a number attached.

### Phase 2 — Scale (2027 H2)

- **A0.1 indirect draw** across the desktop backends
- **A6 GPU particles**
- **A7 GPU-driven culling**, justified against the Phase 1 baseline
- **B4 mass-unit reference world**
- **A3 volumetrics**

Outcome: the strategy-scale claim is demonstrated rather than asserted.

### Phase 3 — Creation (2028 H1)

- **C2 cloud LLM plus Roslyn runtime C#**, desktop only
- **B2 editing affordances** and **B3 asset catalogue**
- **A4 water**, **A5 decals**
- **D3 asset pipeline** consolidated

Outcome: natural-language world creation sitting on a world format that was designed for it.

### Phase 4 and beyond

- **A8 mesh LOD**, **A9 terrain**
- **C3 3D static prop generation**
- **C4 auto-rigging**, if output quality justifies it
- **Track E** starting as a Windows + D3D12 pilot (E0 → E1 → E2 → E3 → E4); A0.4 is already in place from Phase 1
- **E5 Quest**, after the desktop pilot proves the session, dual-view, and input abstractions hold
- Mac and mobile store presence
- **Console technical adaptation** — acknowledged, not scheduled; see the section below

Track E is the last thing here, not a first-class platform claim: it is a new audience for an engine that must first
win the audience it already targets. Console adaptation sits even further out, and for a different reason — see the
section below — because it is gated on SDK access, which is gated on the engine's standing, not on engineering effort
alone.

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
scale-and-standing gate, not an engineering gate, which is exactly why nothing here carries a phase.

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
- When Track E starts, the same rule extends to XR: OpenXR only, no runtime-specific integration layers.
