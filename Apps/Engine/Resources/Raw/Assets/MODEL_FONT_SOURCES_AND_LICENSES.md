# Engine Asset Sources and Licenses

This file records the known provenance and license information for **model** and **font** assets under `Apps/Engine/Resources/Raw/Assets`.

Third-party image assets are intentionally excluded from this list. Generated images are recorded under _Generated Images_, because for those the prompt and seed are the only provenance there is.

## Models

| Asset file | Upstream source | License | Notes |
|---|---|---|---|

| `3DGodotRobot.glb` | `AGChow/3D-Godot-Robot-Platformer-Character` / itch.io `Godot 3D Robot Character` | `CC0-1.0` 
| https://captainripley.itch.io/godot-3d-robot-character

| `background_mountains.glb` | Sketchfab: `Background Mountains` | `CC BY 4.0`
| https://sketchfab.com/3d-models/background-mountains-8c439df28942483d8ed29c46a3a3487d

| `busterDrone.glb` | Sketchfab: `Buster Drone` | `CC BY 4.0`
| https://sketchfab.com/3d-models/buster-drone-294e79652f494130ad2ab00a13fdbafd

| `CampfireLogs.glb` | SeasonEngine asset pipeline: SeasonImage (FLUX.2 [klein] 4B) text-to-image, then SeasonThreeD (TRELLIS.2) image-to-mesh | Generated in-repo, no third-party license | 2966 tris, 697 KB, PBR atlas embedded. Prompt: "a small campfire woodpile of five charred oak logs stacked in a low teepee, bark texture, cut ends showing growth rings, isolated on a plain pure white background ...". Seed 20261031, 1024x1024 / 16 steps / CFG 1.0, then R512 / 12 steps / atlas 1024 / texture 512 / 3000 face budget. Generator: `.dbg/_campfire_assets`.

| `celling_lights.glb` | Sketchfab: `celling lights` | `CC BY 4.0`
| https://sketchfab.com/3d-models/celling-lights-8156ba1838e54478a5f7367c45a43aeb

| `EmberBed.glb` | SeasonEngine asset pipeline: SeasonImage (FLUX.2 [klein] 4B) text-to-image, then SeasonThreeD (TRELLIS.2) image-to-mesh | Generated in-repo, no third-party license | 1356 tris, 527 KB, PBR atlas embedded. Prompt: "a shallow round bed of charred charcoal chunks and grey ash with glowing orange embers, flat low pile wider than it is tall, isolated on a plain pure white background ...". Seed 20261032, 1024x1024 / 16 steps / CFG 1.0, then R512 / 12 steps / atlas 1024 / texture 512 / 1500 face budget. Generator: `.dbg/_campfire_assets`.

| `flying_seagull.glb` | Sketchfab: `Flying Seagull` | `CC BY-SA 4.0`
| https://sketchfab.com/3d-models/flying-seagull-07dde3ea7d9048588d3c4edfd37ac20d

| `halloween_haunted_house.glb` | Sketchfab: `Halloween Haunted House` | `CC BY 4.0`
| https://sketchfab.com/3d-models/halloween-haunted-house-402b277372424e75b4c7aec309889dda

| `MorphStressTest.glb` | Khronos `glTF-Sample-Assets/Models/MorphStressTest` | `CC BY 4.0`
| https://github.com/KhronosGroup/glTF-Sample-Assets/tree/main/Models/MorphStressTest

| `Rocks.glb` | Sketchfab: `Low Poly Stylized Rocks` | `CC BY 4.0`
| https://sketchfab.com/3d-models/low-poly-stylized-rocks-99544bd16600463fa5a34ecbd98eb980

| `street_lights.glb` | Sketchfab: `Street lights` | `CC BY 4.0`
| https://sketchfab.com/3d-models/street-lights-71853da424aa4b208e14f6cf430339ae

| `warcraft_3_style_pbr_grass.glb` | Sketchfab: `warcraft 3 style PBR grass` | `CC BY 4.0`
| https://sketchfab.com/3d-models/warcraft-3-style-pbr-grass-830a1d05b85b4e398994a023b40234bc

| `WaterBottle.glb` | Khronos `glTF-Sample-Assets/Models/WaterBottle` | `CC0-1.0`
| https://github.com/KhronosGroup/glTF-Sample-Assets/tree/main/Models/WaterBottle

## Generated Images

These three are decal textures for the D1 pond decals in `Apps/Engine/App.cs`. Each is a single 512x512 RGBA PNG: the RGB is the photo FLUX.2 [klein] 4B returned, the alpha is a matte authored in code because a diffusion model cannot draw one. The two Modulate textures are additionally re-exposed into a multiplier range, since a Modulate decal multiplies the already-lit ground and photographic albedo would darken it several times over.

| Asset file | Upstream source | License | Notes |
|---|---|---|---|
| `DecalWetGrass.png` | SeasonEngine asset pipeline: SeasonImage (FLUX.2 [klein] 4B) text-to-image, then a 2x box filter and a code-authored blob matte | Generated in-repo, no third-party license | 512x512 RGBA, 890 KB. Prompt: "top-down close-up texture of damp dark green grass and muddy soil with small shallow puddles reflecting a grey overcast sky, wet blades pressed flat, macro photograph, flat even lighting, seamless texture tile, nothing else in frame". Alpha: lobed radial falloff. RGB: luminance compressed into [0.50, 1.00], hue preserved. Seed 20261101, 1024x1024 / 16 steps / CFG 1.0. Generator: `.dbg/_decal_assets`. |
| `DecalMoss.png` | SeasonEngine asset pipeline: SeasonImage (FLUX.2 [klein] 4B) text-to-image, then a 2x box filter and a code-authored band matte | Generated in-repo, no third-party license | 512x512 RGBA, 950 KB. Prompt: "top-down close-up texture of dense green moss and lichen creeping over wet stone, soft velvety surface, small dark crevices between tufts, macro photograph, flat even lighting, seamless texture tile, nothing else in frame". Alpha: ragged soft-edged band. RGB: luminance compressed into [0.62, 1.00], hue preserved. Seed 20261102, 1024x1024 / 16 steps / CFG 1.0. Generator: `.dbg/_decal_assets`. |
| `DecalCaustics.png` | SeasonEngine asset pipeline: SeasonImage (FLUX.2 [klein] 4B) text-to-image, then a 2x box filter and a luminance-derived matte | Generated in-repo, no third-party license | 512x512 RGBA, 435 KB. Prompt: "top-down photograph of sunlight caustics on the bottom of a shallow clear pond, bright pale cyan wavy light network, thin crossing filaments of light on a pure black background, high contrast, flat orthographic texture, no water surface, no reflections". Alpha: smoothstep over luminance, so black stays invisible. RGB kept as generated: an Additive decal adds its pixels as light. Seed 20261103, 1024x1024 / 16 steps / CFG 1.0. Generator: `.dbg/_decal_assets`. |

### Waterfall sheet textures

The three files below are the sheet textures for `Apps/Engine/Panels/Waterfall.cs`. The mask carries four independently authored channels; the normal and flow maps are derived or procedural. The R and G channels are FLUX.2 [klein] 4B luminance planes: generated at 1024x1024, 2x box filtered, then differenced against a 65-texel local mean so the photo's own composition (bright top half, dark bottom half) does not fade the top of the sheet. B and A are periodic code noise, so erosion and distortion stay seam-free if the sheet ever tiles. R and G are deliberately not tileable: the sheet maps u across exactly one texture and animates v with a two-phase crossfade whose phases reset at zero weight, so neither axis ever shows a wrap under normal use.

| Asset file | Upstream source | License | Notes |
|---|---|---|---|
| `WaterfallMask.png` | SeasonEngine asset pipeline: two SeasonImage (FLUX.2 [klein] 4B) runs, then a code-authored high pass, percentile stretch and knee | Generated in-repo, no third-party license | 512x512 RGBA, 775 KB. R: streak luminance. Prompt: "abstract texture of long thin white water filaments over deep dark teal, fine vertical threads and streaks filling the entire frame, flat even lighting, no perspective, no horizon, macro photograph, high contrast". Seed 20261123. G: foam luminance. Prompt: "macro photograph of white water foam and small bubbles clustered on dark teal water, dense round cells of many sizes filling the entire frame, bright white clusters on a dark background, high contrast, flat even lighting, abstract texture, nothing else in frame". Seed 20261122. Both 1024x1024 / 16 steps / CFG 1.0. B: periodic Worley cell-edge mask, 10 cells. A: periodic 4-octave value fbm. Generator: `.dbg/_waterfall_assets`. |
| `WaterfallNormal.png` | SeasonEngine asset pipeline: derived from `WaterfallMask.png` channel R plus periodic fbm | Generated in-repo, no third-party license | 512x512 RGBA, 556 KB. Height = 0.5 * blurred R + 0.5 * periodic fbm (10 cells, 3 octaves); Sobel gradient at strength 4.5. Encoded as (du, dv, out-of-sheet). Mean tilt 14.1 deg, p90 34.1 deg. |
| `WaterfallFlow.png` | SeasonEngine asset pipeline: procedural periodic curl field | Generated in-repo, no third-party license | 512x512 RGBA, 281 KB. RG = unit flow direction in sheet space (mostly down, lateral drift of +-0.35 from a 4-cell fbm); B = speed 0..1 that the shader maps to 0.7..1.3; A unused. Periodic in both axes. |

## Fonts

| Asset file | Upstream source | License | Notes |
|---|---|---|---|
| `NotoSansMono-VariableFont.ttf` | Google Fonts: `Noto Sans Mono` | `SIL Open Font License 1.1` | Source is referenced in `App.cs`. |
| `NotoSansSC-VariableFont_wght.ttf` | Google Fonts / Noto CJK: `Noto Sans SC` | `SIL Open Font License 1.1` | Source is referenced in `App.cs`. |
| `NotoSansTC-VariableFont_wght.ttf` | Google Fonts / Noto CJK: `Noto Sans TC` | `SIL Open Font License 1.1` | Source is referenced in `App.cs`. |
| `Twemoji.ttf` | Mozilla `twemoji-colr` | `Apache-2.0` for build/code, `CC BY 4.0` for emoji artwork/design | The distributed font is derived from Twemoji artwork via Mozilla's `twemoji-colr` project. Attribution to the Twemoji artwork source should be preserved. |

## Notes On Attribution

- `CC BY 4.0` and `CC BY-SA 4.0` assets require attribution to the original author/source.
- `CC BY-SA 4.0` may impose share-alike obligations when redistributing modified versions of the asset itself.
- `SIL OFL 1.1` fonts can be redistributed and modified, but the license text and reserved-font-name rules should be respected where applicable.
- `CC0-1.0` assets do not require attribution, but keeping provenance is still recommended.
- Generated assets carry no third-party obligation, but the prompt, seed and generator are recorded above so any of them can be regenerated byte-comparably.
- `Twemoji.ttf` has a mixed origin: the Mozilla build project is Apache-2.0, while the underlying Twemoji artwork is CC BY 4.0.
