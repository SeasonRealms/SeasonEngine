# SeasonXNA

**SeasonXNA is an XNA-style core 2D drawing compatibility layer for existing MonoGame/XNA code, hosted by the [SeasonEngine](../Season/README.md) runtime.**

It provides the `Microsoft.Xna.Framework` and `Microsoft.Xna.Framework.Graphics` types that real sprite, image and text code uses most — `SpriteBatch.Draw`, `SpriteBatch.DrawString`, `MeasureString`, and the value types behind them — so a port can keep the majority of its 2D drawing call sites while the application itself moves to Season's native application model (`BaseApp`, panels, controls and platform hosts).

Github: https://github.com/SeasonRealms/SeasonEngine

SeasonXNA is deliberately **not** a complete XNA or MonoGame emulation:

- It publishes a small, explicitly listed subset (**14 public types**) under the original XNA namespaces.
- Unsupported capabilities are **absent at compile time or rejected at runtime**; nothing "accepts the parameter and silently does nothing".
- Non-core concerns — windowing, the game loop, input, audio, XNB content, effects, render targets, screenshots and pixel processing — stay in Season's native services.

Dependency direction is fixed and one-way:

```text
your application  ->  SeasonXNA  ->  Season (SeasonEngine core)
```

SeasonXNA depends only on the Season core. It does not reference MonoGame, SpriteFontPlus or any XNA assembly, and it must not be mixed with MonoGame inside the same process — see [Reusing existing code](#reusing-existing-code).

## Table of contents

1. [Status](#status)
2. [Requirements](#requirements)
3. [Installation](#installation)
4. [Quick start](#quick-start)
5. [How it works](#how-it-works)
6. [Migrating from MonoGame](#migrating-from-monogame)
7. [The Content pipeline and what replaces it](#the-content-pipeline-and-what-replaces-it)
8. [Reusing existing code](#reusing-existing-code)
9. [Supported API surface](#supported-api-surface)
10. [Unsupported functions and rejected inputs](#unsupported-functions-and-rejected-inputs)
11. [Resource ownership](#resource-ownership)
12. [Alpha, tints and color rules](#alpha-tints-and-color-rules)
13. [Text and fonts](#text-and-fonts)
14. [Verification](#verification)
15. [Known limitations](#known-limitations)
16. [Repository layout](#repository-layout)
17. [License](#license)

## Status

SeasonXNA is in a **bounded first-version state**: the core 2D subset described in this document is implemented and carries the verification evidence listed below; everything outside that subset is intentionally missing.

| Platform | Target framework | Immediate-2D backend | Verification status |
| --- | --- | --- | --- |
| Windows | `net10.0-windows10.0.19041.0` | Direct3D 12 | Reference platform: CPU contracts, GPU images/text/animation, resize and all lifecycle paths pass |
| Linux | `net10.0` | Vulkan | Shared Vulkan backend verified in WSL with the software `llvmpipe` driver and Khronos validation + synchronization validation enabled; not a hardware-GPU performance claim |
| Android | `net10.0-android` | Vulkan (shared with Linux) | Host lifecycle wired and compiling; on-device verification deferred |
| iOS / Mac Catalyst | `net10.0-ios`, `net10.0-maccatalyst` | Metal | Shared Metal backend and host cleanup implemented; managed compilation and CPU contracts pass; no Mac native link, MSL compilation or GPU evidence yet |
| Web | `net10.0-browser` | WebGPU | Core images/text and lifecycle paths pass in a browser; the large-CJK-font stress path does not pass yet, so this platform is not declared fully verified |

Multi-targeting does not imply that every target is runtime-verified. See [Verification](#verification) for exactly what was checked.

## Requirements

- **.NET 10 SDK**.
- The platform workload/SDK for your target(s): Windows App SDK/WinUI for Windows, the Android workload, the Apple workloads for iOS/Mac Catalyst, or the browser target for Web.
- The **SeasonEngine core** (`Season`), which SeasonXNA references as a project/package dependency.
- A graphics backend supported by Season on that platform (Direct3D 12, Vulkan, Metal or WebGPU). There is no OpenGL fallback.

## Installation

Install the NuGet package:

```bash
dotnet add package SeasonXNA
```

The `SeasonEngine` core is restored automatically as a package dependency. If the package is not available yet for your feed, or you are building from this repository, reference the project instead:

```xml
<ItemGroup>
  <ProjectReference Include="..\SeasonXNA\SeasonXNA.csproj" />
</ItemGroup>
```

## Quick start

SeasonXNA has no game class of its own: your app is a normal Season `BaseApp`, and SeasonXNA plugs into its `Draw2D` callback.

```csharp
using System.Numerics;
using Season.Basic;
using Season.Controls;
using Season.Rendering;
using SeasonXNA.Hosting;
using SeasonXNA.Interop;
using SpriteBatch = Microsoft.Xna.Framework.Graphics.SpriteBatch;
using Texture2D   = Microsoft.Xna.Framework.Graphics.Texture2D;
using SpriteFont  = Microsoft.Xna.Framework.Graphics.SpriteFont;
using XnaColor    = Microsoft.Xna.Framework.Color;
using XnaRectangle = Microsoft.Xna.Framework.Rectangle;
using XnaVector2  = Microsoft.Xna.Framework.Vector2;

public sealed class MyApp : BaseApp
{
    private readonly DrawContext _drawing = new();
    private readonly SpriteBatch _sprites;
    private Texture2D? _texture;
    private SpriteFont? _font;

    public MyApp()
    {
        _sprites = new SpriteBatch(_drawing);
        Title = "My Season App";
        RenderDomain = RenderDomain.Overlay;
        DesignResolution = BasicResolution = new Vector2(1280, 720);
    }

    // Loading phase: preload everything once, before the first frame.
    public override void Create()
    {
        base.Create();
        _texture = SeasonResources.LoadTexture("Content/Textures/Logo.png");
        _font = SeasonResources.LoadFont("Content/Fonts/GameFont.ttf", 24);
    }

    // Recording phase: this callback only records commands into the native canvas.
    public override void Draw2D(Draw2D canvas)
    {
        if (_texture is null || _font is null) return;
        using var frame = _drawing.Bind(canvas);
        try
        {
            _sprites.Begin();  // LinearClamp by default; required for MSDF text
            _sprites.Draw(_texture, new XnaRectangle(24, 24, 320, 180), XnaColor.White);
            _sprites.DrawString(_font, "Hello from SeasonXNA", new XnaVector2(24, 224), XnaColor.White);
            _sprites.End();

            // Native Season shapes may be interleaved between ended batches,
            // which preserves their order relative to the queued batches above.
            canvas.FillRectangle(new Rect2D(24, 264, 320, 2), new Vector4(1, 1, 1, 1));
        }
        catch
        {
            _drawing.CancelPendingBatches();
            throw;
        }
    }

    public override void Dispose()
    {
        var texture = _texture;
        var font = _font;
        _texture = null;
        _font = null;
        try
        {
            _sprites.Dispose();
            font?.Dispose();
            texture?.Dispose();
        }
        finally { base.Dispose(); }
    }
}
```

Key points:

- **One `DrawContext` per host.** Bind it inside `Draw2D` with `using`, and never keep the frame scope or canvas across `await`, threads or frames.
- **Preload, never lazily load.** `LoadTexture`/`LoadFont` belong to the loading phase; loading inside `Draw` is not supported and fails explicitly.
- **Always balance `Begin`/`End`.** In exception paths call `CancelPendingBatches()` before rethrowing; an unfinished batch is cleaned up and reported when the frame scope exits.
- **Text batches must be linear.** `DrawString` throws inside a `PointClamp` batch; end the point-sampled image batch and start a default (LinearClamp) batch for text.
- **Name collisions are expected.** MAUI/engine types such as `Color`, `Rectangle` or `Vector2` exist alongside the XNA ones; use explicit aliases (`using XnaColor = ...`) at the host boundary instead of changing engine-global usings.

Attach the app through a Season platform host:

```csharp
using Season.Platforms.Windows;
WindowsApp.Run(new MyApp());          // Season.Platforms.{Windows, Linux, Android, iOS, MacCatalyst}.Run(app)
```

Web hosts are asynchronous and preload through a callback; only start the frame loop after the complete resource set is ready:

```csharp
await WebApp.Run(app, jsRuntime, httpClient, "season-canvas", assetBasePath: "Assets", preload: async () =>
{
    var image = await Image2D.LoadAsync(imagePath, System.Numerics.Vector2.One); // Season.Rendering.Image2D
    Texture2D? texture = null;
    SpriteFont? font = null;
    try
    {
        texture = SeasonResources.TakeTextureOwnership(image);
        font = SeasonResources.BorrowFont(await Font.CreateAsync(fontPath, 24), 24); // Season.Fonts.Font
        app.PublishPreloadedSet(texture, font); // your own publication step
    }
    catch
    {
        font?.Dispose();
        if (texture is null) image.Dispose(); else texture.Dispose();
        throw;
    }
});
```

Publish the prepared set to the app only after every resource succeeded; on failure, release everything acquired in that attempt.

## How it works

SeasonXNA sits between your drawing code and the native immediate-2D recording canvas:

```text
your app (SeasonXNA calls)
  -> SeasonXNA  (parameter contracts, Deferred queue, text layout, D01-A tint, ownership)
    -> Season.Rendering.Draw2D        (recording canvas: transform stack, frame references)
      -> IImmediate2DBackend
        -> Direct3D 12 / Vulkan / Metal / WebGPU
```

SeasonXNA never owns a device, swap chain, descriptor or platform handle, and there is no platform-specific `SpriteBatch`. The frame schedule stays entirely in Season:

```text
BeginFrame -> app.Draw2D (record) -> EndFrame -> Prepare -> Overlay Submit -> CompleteFrame
```

Rules that follow from that contract:

- `Draw`/`DrawString` only **queue** parameters; the commands are recorded to the native canvas at `End`, in `End` order (Deferred only, see below).
- Do not call native `BeginFrame`/`EndFrame`/`Prepare`/`Submit` yourself, and do not treat `CompleteFrame` as "GPU finished".
- Do not change the native transform/clip while a batch is open and expect already queued draws to keep their immediate order or landing position. Native drawing belongs between ended batches.
- `End` validates that every texture and font wrapper is still alive before it records anything, so a batch either records as a whole or fails as a whole.
- Wrappers must stay alive until `End`. After `End`, the native frame holds its own references and you may dispose the wrappers.

## Migrating from MonoGame

### Recommended sequence

1. Keep the old MonoGame version read-only and keep a reference build of its real visuals/interaction for comparison. Start with **one page or one small scene**.
2. Create a native Season `BaseApp` host with explicit `Update`, `Draw2D` and resource load/release boundaries. Do not implement a second `Game.Run`.
3. In the loading phase, create/borrow images and fonts through SeasonXNA/Season and keep the application's own resource table, keys and path resolution.
4. Give the host one `DrawContext` and one `SpriteBatch`; bind them only inside the `Draw2D` callback Season provides.
5. Keep the supported XNA drawing `using`s and call sites; concentrate changes in constructors, `Begin`, and resource/font wrappers. **Do not silence the compiler with empty API stubs** — unsupported types are intentionally absent so you can find and replace every affected call site.
6. Replace non-supported platform features (input, audio, screenshots, pixel processing) with native Season services. Networking decisions are separate; "removing MonoGame" is not "deleting business code".
7. Compare page by page: image, input, saves and gameplay. Passing SeasonXNA's own acceptance does not mean the whole game is migrated.

### Replacement table

| Old MonoGame behavior | Replacement | Important difference |
| --- | --- | --- |
| `Game`, `GraphicsDeviceManager`, fixed timestep | Native Season `BaseApp` / platform host; your code owns its time policy | Not a full XNA `Game` lifecycle |
| `Content.Load` / XNB / game resource tables | Application-side preloading, path resolution and caching over `SeasonResources` or native loaders | No XNB content pipeline, no directory scanning |
| PNG -> `GetData`/`Byte4` -> premultiply -> `SetData` | Load straight-alpha images directly; drop the load-time premultiply pass | No dynamic pixel write-back emulation |
| Scaled tint via `Color * alpha` | Keep the XNA four-channel multiply; SeasonXNA converts at draw time (D01-A) | Raw `new Color(255, 0, 0, 128)` is **not** a valid tint (see [Alpha rules](#alpha-tints-and-color-rules)) |
| Semi-transparent red tint | `Color.Red * 0.5f` or `Color.FromNonPremultiplied(255, 0, 0, 128)` | Byte quantization can differ; intent is never guessed |
| `DynamicSpriteFont.FromTtf`, lazy `TextManager` loading | Preload `SpriteFont` (font + size) once; `DrawString`/`MeasureString` share one layout | No full SpriteFontPlus; adjust the central font entry points |
| Old text wrapping driven by "size" | Set `font.LineSpacing = size` explicitly where the old layout requires it | Default line height comes from font metrics; measurement is the advance box |
| Tab / rich text / automatic word wrap | Resolve in application code before drawing, or use the native glyph-run API | Business layout stays out of the compatibility layer |
| Game-side `ShapeDrawer` | Native `FillRectangle` + affine line segments (see the native shapes sample approach) | No extension of the `GraphicsDevice` primitive pipeline |
| Sprite-sheet animation | Advance an explicit animation clock in `Update`, choose the source rectangle in `Draw` | No timers inside SeasonXNA |
| `RenderTarget2D` sync capture, `GetData`/`SaveAsPng` | `DeviceServices.Record.CaptureApp()` async readback, then `DeviceServices.Image.SaveImage` | Never block inside `Draw`; synchronous screen capture is a different feature |
| CPU pixel processing (e.g. avatars) | Native `IImageService` / `INativeImageDecoder` pixel sources plus your own resource pipeline | No promise of writing back into a live `Texture2D` |
| XNA `Keyboard`/`Mouse`/`Touch` | Native Season keyboard/touch services and host input plumbing | Watch design space, DPI and frame edges; key-value numbering is not the same |
| `Song`/`SoundEffect`/`MediaPlayer` | `DeviceServices.Media` and the native audio interfaces that fit the app | The media service is not a full sound-instance/channel emulation |
| `Effect`, depth, wrap and other special states | Evaluate the native rendering API or drop the non-core effect | No "accept the parameter and do nothing" placeholders |
| XNA values inside old Newtonsoft/XML saves | Round-trip tests with real saves; write explicit converters where needed | CPU JSON tests are not a guarantee for real legacy save files |

If a business need is not covered by any native service, treat it as a separate feature request — do not silently upgrade the gap into a SeasonXNA obligation.

## The Content pipeline and what replaces it

SeasonXNA deliberately ships **no content pipeline**:

- **No MGCB, no `.xnb`.** The MonoGame Content Builder, compiled `Content` artifacts and `ContentManager.Load<T>` are not supported and never will be emulated. SeasonXNA does not read XNB files.
- **Assets stay in their source formats.** Images are straight-alpha PNG/JPEG; fonts are TrueType/OpenType (`.ttf`/`.otf`). Text is rendered from fonts at runtime with MSDF; sprite fonts baked into XNB do not exist here.
- **Loading is explicit and preload-only.** Call `SeasonResources.LoadTexture(path)` / `SeasonResources.LoadFont(path, size)` in the loading phase. Season's native services handle decoding and upload; SeasonXNA never scans directories, never maintains a content manifest, and never loads during `Draw`.
- **Paths belong to your app.** Path resolution, asset deployment (Android assets, Web `wwwroot`, desktop content folders) and the resource table are application concerns.
- **Sprite sheets are ordinary images.** Keep the sheet PNG and select regions with `sourceRectangle`; SeasonXNA caches up to 256 region descriptors per texture handle.
- **Fonts are runtime objects.** A `SpriteFont` is a native Season font plus a requested pixel size. Size-dependent metrics are derived from the typeface (ascender/descender/line gap), never from the font's construction size, and `LineSpacing` can be set to preserve legacy layout policies.
- **No runtime pixel readback or write-back.** There is no `GetData`/`SetData`. If the old project premultiplied textures at load time, remove that step (see below); other pixel work belongs in the native image tooling.
- **Web loads asynchronously.** Use `Image2D.LoadAsync` and `Font.CreateAsync` inside the host's `preload` callback before the frame loop starts; the synchronous path only serves already-prepared cache entries, and a cache miss explicitly requires the async API.

Practical migration order for assets: delete the MGCB build step, keep the original PNG/TTF files, port the path table, and replace every `Content.Load` with the preload APIs above.

## Reusing existing code

The short answer: **existing class libraries can be reused at source level, but not at binary level, and only for the supported subset.**

**Source-level reuse (supported):**

- SeasonXNA declares the original namespaces (`Microsoft.Xna.Framework`, `Microsoft.Xna.Framework.Graphics`), so existing code and class libraries can be recompiled against it and keep their `using` statements and call sites wherever the subset covers them.
- Types outside the subset simply do not exist. Compile errors are intentional: they are your worklist of call sites to port. Do not add lookalike stubs to make code compile — replace or remove the call.

**Binary-level reuse (not supported):**

- SeasonXNA is **not a drop-in replacement for MonoGame's assemblies.** You cannot swap DLLs, consume a library that was compiled against MonoGame without recompiling it, or load a MonoGame-built third-party package (such as SpriteFontPlus) next to SeasonXNA.
- MonoGame and SeasonXNA declare the same type names in the same namespaces. Referencing both in one process creates two incompatible object models, and mixing framework objects is unsupported.
- Numeric comparison against MonoGame exists only inside isolated verification tooling; that does not make mixed use a supported configuration.

**Central entry points to rewrite:** object construction, `SpriteBatch.Begin`, resource and font factories, and platform services (input/audio/screenshot). Port these once, centrally, instead of touching every drawing call site.

## Supported API surface

### Public types

The production assembly publishes exactly 14 types:

| Namespace | Types |
| --- | --- |
| `Microsoft.Xna.Framework` | `Vector2`, `Rectangle`, `Color`, `Matrix` |
| `Microsoft.Xna.Framework.Graphics` | `Texture2D`, `SpriteFont`, `SpriteBatch`, `SpriteEffects`, `SpriteSortMode`, `BlendState`, `SamplerState` |
| `SeasonXNA.Hosting` | `DrawContext`, `DrawContext.FrameScope` |
| `SeasonXNA.Interop` | `SeasonResources` |

`Texture2D` and `SpriteFont` have no public constructor; create them through `SeasonResources`. `SpriteBatch` has exactly one public constructor: `SpriteBatch(DrawContext)` — never a `GraphicsDevice`.

### SpriteBatch

```csharp
var sprites = new SpriteBatch(drawContext);

sprites.Begin(
    SpriteSortMode sortMode = SpriteSortMode.Deferred,
    BlendState?     blendState = null,
    SamplerState?   samplerState = null,
    Matrix?         transformMatrix = null);
```

- **Sorting is Deferred only.** The other `SpriteSortMode` values exist for source compatibility but throw `NotSupportedException`.
- **`BlendState.AlphaBlend` is a marker** for the D01-A tint adaptation, not a mutable GPU blend state. There is no premultiplied-pipeline emulation.
- **Samplers:** `LinearClamp` (default) and `PointClamp` for images. No `Wrap`, no anisotropic filtering. MSDF text always requires `LinearClamp`.
- **`transformMatrix`** must be a planar affine matrix (no perspective, no Z coupling; `M33 = M44 = 1`, Z translation 0).
- **`layerDepth`** must be finite and in `[0, 1]`, but Deferred does not sort by it and writes no Z.

`Draw` supports 7 overload groups:

| # | Overload |
| --- | --- |
| 1 | `Draw(texture, position, color)` |
| 2 | `Draw(texture, position, sourceRectangle, color)` |
| 3 | `Draw(texture, destinationRectangle, color)` |
| 4 | `Draw(texture, destinationRectangle, sourceRectangle, color)` |
| 5 | `Draw(texture, position, sourceRectangle, color, rotation, origin, float scale, effects, layerDepth)` |
| 6 | `Draw(texture, position, sourceRectangle, color, rotation, origin, Vector2 scale, effects, layerDepth)` |
| 7 | `Draw(texture, destinationRectangle, sourceRectangle, color, rotation, origin, effects, layerDepth)` |

`DrawString` supports 6 overload groups — `string` and `StringBuilder`, each with a basic, a full scalar-scale and a full `Vector2`-scale form. `MeasureString` supports 2 (`string`, `StringBuilder`) and shares layout with `DrawString`.

Geometry rules:

- `sourceRectangle` is a real pixel region: `null` means the whole image; zero, negative or out-of-bounds rectangles are rejected (no implicit clipping).
- `position` overloads draw at the source's pixel size; SeasonXNA does not consult `Image2D.DesignSize` or any game resource table.
- `origin` is in source pixels; rotation is in radians; `scale` may be negative (mirroring is supported through scale/effects, but negative `destinationRectangle` sizes are rejected).
- `SpriteEffects` flips UVs (`None`/`FlipHorizontally`/`FlipVertically`/both); unknown bits are rejected. Text effects mirror the whole logical layout box, including glyph outlines.
- Invisible sprites (zero scale, zero destination area, transparent black) are elided **after** validation, so invalid input never disappears silently.

### Value types

- **`Vector2`** — `X`/`Y`, single/double-value constructors, `Zero`/`One`/`UnitX`/`UnitY`, arithmetic and equality, `Length`/`LengthSquared`, `Distance`/`DistanceSquared`/`Dot`, `Transform` (including `ref`/`out`).
- **`Rectangle`** — `X`/`Y`/`Width`/`Height` (public fields), edges, `Empty`/`IsEmpty`, `Contains` (3 forms), `Intersects`/`Intersect`, `Offset(int,int)`, `Inflate(int,int)`, equality.
- **`Matrix`** — 16 fields and the full constructor, `Identity`, 3-argument `CreateScale` (with `out`), 3-argument `CreateTranslation`, `CreateRotationZ`, multiplication (with `ref`/`out`), equality. No single-argument `CreateScale`, no `Invert` and similar completed-set members.
- **`Color`** — int/float RGB(A) and packed `uint` constructors, RGBA byte properties, `PackedValue`, `FromNonPremultiplied(int...)`, `Multiply` and `operator *`, equality. 15 named colors: `Transparent`, `Black`, `White`, `Red`, `Green`, `Blue`, `Cyan`, `Yellow`, `Gray`, `Silver`, `Orange`, `OrangeRed`, `DarkBlue`, `DarkOrange`, `DarkRed`.

When you need more math than this, convert to `System.Numerics` at the boundary of your own code — completing this value-type set is not a goal.

### Native interop escape hatches

`SeasonResources` also exposes `GetImage`, `GetFont`, `GetFontSize` and `GetBaselineOffset`. They return the original native references (no new ownership) and exist for host-level work, not as additional public XNA signatures.

## Unsupported functions and rejected inputs

**Not provided at all (compile-time absence):** `Game`, `GameTime`, `GraphicsDevice`, `GraphicsDeviceManager`, `ContentManager`, `RenderTarget2D`, `Effect`, `DepthStencilState`, `RasterizerState`, `Point`, `Vector3`/`Vector4`, `Byte4`, all input classes, all audio classes, and SpriteFontPlus. The value types are a minimal subset; SeasonXNA is not a binary replacement for the XNA assemblies.

**Provided but rejected at runtime:**

| Input | Result |
| --- | --- |
| Non-`Deferred` sort mode | `NotSupportedException` |
| `DrawString` inside a `PointClamp` batch | `NotSupportedException` |
| Source rectangle with zero/negative size or out of bounds | `ArgumentOutOfRangeException` |
| Negative destination rectangle size | `ArgumentOutOfRangeException` |
| Tint with `R > A`, `G > A` or `B > A`; `A = 0` with non-zero RGB | `NotSupportedException` |
| Non-finite (NaN/Infinity) position, origin, scale, rotation, depth or matrix | `ArgumentOutOfRangeException` |
| Transform overflow (projected corners non-finite) | `ArgumentOutOfRangeException` |
| Matrix with perspective or Z coupling | `NotSupportedException` |
| Unknown `SpriteEffects` bits | `ArgumentOutOfRangeException` |
| `layerDepth` outside `[0, 1]` | `ArgumentOutOfRangeException` |
| Missing glyph without `DefaultCharacter`; control characters; invalid UTF-16 | `ArgumentException` |
| Nested `DrawContext.Bind`, cross-thread use, `Begin` without `End`, `Draw`/`End` without `Begin` | `InvalidOperationException` |
| Unfinished batch at frame-scope exit | Batch discarded + `InvalidOperationException` |
| Use after `Dispose` | `ObjectDisposedException` |

**Deliberately different semantics (documented differences, not bugs):**

- Blend output is `SrcAlpha`/`InvSrcAlpha` for RGB with alpha `One`/`Zero`; this is **not** full XNA `AlphaBlend` target-alpha accumulation, and premultiplied-texture filtering edges are not guaranteed to match.
- Text has no kerning, shaping, bidi, automatic wrapping or rich text; `MeasureString` returns the advance box and ink may overhang it.
- There is no automatic font fallback chain; missing glyphs fail unless you set `DefaultCharacter`.
- Sampling is Clamp-only with a half-texel inset; no Wrap or anisotropic behavior is emulated.
- Screenshot/readback evidence validates RGB; original target-alpha equality with MonoGame is not claimed.

## Resource ownership

| Entry point | Who releases the native image |
| --- | --- |
| `SeasonResources.LoadTexture(path)` | The returned `Texture2D` (the factory releases the image if wrapping fails) |
| `SeasonResources.TakeTextureOwnership(image)` | The `Texture2D` after success; on failure the original caller keeps responsibility |
| `SeasonResources.BorrowTexture(image)` | The original caller; disposing the borrowed wrapper does **not** release the image |

- Borrowing adds no native lease: the owner must outlive the borrower. Never take ownership of the same image twice.
- `Width`/`Height`/`Bounds` always use the real pixel size, not a design size.
- Wrappers must remain valid until `SpriteBatch.End`. After `End`, the native frame protects the resources until it completes; disposing a wrapper afterwards does not affect recorded frames.
- `SpriteFont.Dispose` only clears the wrapper's reference and its own layout cache; the engine keeps ownership of shared glyph atlases. Native fonts are managed objects — their cache lifetime follows real references, not the wrapper.
- Do not load or release resources inside `Draw`, and do not race disposal against recording.

## Alpha, tints and color rules

SeasonXNA feeds Season's straight-alpha pipeline. Two rules follow:

1. **Textures must be straight alpha.** SeasonXNA never detects, unpremultiplies or repairs premultiplied images. Remove any load-time premultiply step from the old project.
2. **Tints entering a draw must be bounded premultiplied values:**

```text
R <= A, G <= A, B <= A        (reject otherwise)
A = 0  -> only RGBA all-zero is accepted (straight transparent black)
A > 0  -> converted to (R/A, G/A, B/A, A/255)
```

`Color` itself remains a plain RGBA value and can represent anything, including `RGB > A`. But `new Color(255, 0, 0, 128)` is not a legal tint; for a semi-transparent red use `Color.Red * 0.5f` or `Color.FromNonPremultiplied(255, 0, 0, 128)`. The two forms may differ by byte quantization — intent is never guessed. `Color * float` scales all four channels (XNA behavior) and is not changed to alpha-only scaling.

## Text and fonts

- A `SpriteFont` is created from a native font and a fixed pixel size (`SeasonResources.LoadFont(path, size)` / `BorrowFont(font, size)`); multiple sizes can share one native typeface.
- Default `LineSpacing` is derived from font metrics and can be set to any positive integer for layout compatibility. Changing it affects subsequent layouts only.
- `MeasureString` returns the logical advance box: width = widest line's advance, height = line count × `LineSpacing`; the empty string measures `(0, 0)`. Trailing spaces and trailing empty lines are preserved.
- `CR`, `LF` and `CRLF` each count as one line break. Text is traversed as Unicode scalars; lone surrogates, invalid UTF-16 and control characters (including Tab) are rejected — expand tabs in application code.
- Missing glyphs fail by default. `DefaultCharacter` can name an existing, non-control BMP glyph of the same font; replacement participates in measurement. There is no automatic fallback across fonts.
- Layout caches are bounded per wrapper: at most 256 entries and 16,384 UTF-16 units; single strings longer than 4,096 units are not cached. Cached hits allocate no managed memory on the hot path, but the library does not promise zero allocation per frame.
- `StringBuilder` overloads snapshot the text at call time; mutating the builder afterwards does not change queued text.
- Fonts must not be mutated (typeface swapped/modified) while queued frames reference them.

## Verification

SeasonXNA is verified as a bounded contract, not by "the game compiles". The verification tool suite (kept in the development workspace, not shipped in the package) includes:

- **Inventory/audit tool** — reproducible call-site inventory and old/new API difference reports.
- **Value type + real DLL surface checks** — 9 groups, 120,742 assertions. The shipped assembly is checked to publish exactly 14 public types with `Draw` 7 / `DrawString` 6 / `MeasureString` 2 overload groups, a single `SpriteBatch` constructor, no public `Texture2D`/`SpriteFont` constructors, and no MonoGame/SpriteFontPlus/game/test assembly references.
- **Resource/drawing/text/sample CPU contracts** — cumulative 12 groups, 655 assertions after the cross-platform batches, covering binding, lifetimes, ownership, caches, exception recovery, draw-order, the shared cross-platform scenes and the asynchronous loading contract.
- **Windows GPU acceptance** — three real screenshots with 54 RGB checks (images, shapes, Update-driven animation), four groups of Chinese native-vs-compatible text (per-channel tolerance 4/255, measured maximum difference 0), and resize; plus five lifecycle paths (normal exit, `Create`/`Update`/`Draw` exceptions, `Dispose` exactly once).
- **Linux (WSL, `llvmpipe`)** — the shared Vulkan backend with Khronos validation and synchronization validation enabled: three-round image/text checks, multi-page glyph atlas (1,800 CJK glyphs across 2 pages), 64-frame transient resource release, six lifecycle paths, and a final empty-lease / deferred-release check.
- **Web (WebGPU)** — browser runs of the shared scenario: 54 RGB checks, four text comparisons (max difference 0), concurrent same-name loading, cache hits, corrupt-image failure and retry, real device-loss and stop paths, and JS/GPU resource count returning to zero. The large-CJK-font stress path does **not** pass yet.
- **Metal (iOS/Mac Catalyst)** — managed compilation against Apple reference assemblies plus 298 parameter/RGBA CPU assertions; **no** MSL compile, native link, Metal validation or GPU evidence yet.

Screenshot-based checks validate RGB of visible output; they do not prove original target-alpha equivalence with MonoGame.

## Known limitations

- Not verified: DPI settings other than 125%, multiple monitors/GPUs, device loss, long-run stress, and large-scale real UI/save/gameplay behavior.
- GPU debug-layer queues are not part of the automated checks; "screenshots look correct" is not "the API is error-free".
- The composited result is not guaranteed to match XNA's target-alpha accumulation; only straight-alpha RGB/visible-alpha behavior is contracted.
- Android on-device scenarios (suspend/rotate/surface recreation) and all of Mac/Metal runtime validation are still pending.
- The Web large-font path and page-internal device recreation remain open.
- Old save formats containing XNA values need application-side converters and real-save round-trip tests.

These are scope limits. They are documented rather than hidden behind placeholder APIs or relaxed validation.

## Repository layout

```text
SeasonXNA/
├── Framework/    XNA value types (Vector2, Rectangle, Matrix, Color)
├── Graphics/     SpriteBatch, Texture2D, SpriteFont, blend/sampler states, enums
├── Hosting/      DrawContext and its frame scope
├── Interop/      SeasonResources: native loading, borrowing and escape hatches
└── Internal/     CPU contracts and text layout (not public API)
```

The package depends on the `Season` core project ([core library README](../Season/README.md)); the shared verification tooling and per-platform acceptance hosts live outside the shipped package.

## License

SeasonXNA is part of the SeasonEngine repository and is licensed under the [MIT License](../LICENSE).
