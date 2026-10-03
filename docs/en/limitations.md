# ⚠️ Limitations & Scope

Constraints and caveats to be aware of when shipping with the current SpriteStudio Player for Unity. For what *is* supported, see [Key Features](./index.md#key-features) and the workflow guides.

> [!NOTE]
> This is a living document. Items are added as they are confirmed; wording and coverage may change as the plugin evolves.

## Not Yet Implemented

> [!WARNING]
> - **Text parts** — not drawn. The runtime reserves the part's slot in the draw plan but supplies no glyphs (glyph layout and rasterization are the player's job), and the plugin has no Unity text pass yet.
> - **9-slice parts** — not drawn. The runtime builds the whole grid into its dedicated `Nines` buffers, but the plugin does not read them yet, so the part renders as nothing rather than as a stretched cell.

Text and 9-slice parts still occupy their place in the draw order, so an animation that uses them plays with those parts missing rather than failing. If you ship one, replace it with an ordinary part at authoring time.

## Canvas (uGUI) Path

`SpriteStudioGraphic` is implemented to fit the CanvasRenderer constraint of "1 renderer = 1 mesh + 1 material," which costs it the things below that the world path (`SpriteStudioPlayer`) does not have to give up. See [Performance Tuning and Advanced Settings](./workflow/tips.md) for the full description.

> [!WARNING]
> - **Single-atlas assumption.** For an animation that uses multiple atlases, only the first atlas is drawn, and parts from other atlases cannot sample the correct texture (a warning is logged). Consolidate into a single atlas, or use the world path.
> - **Blend modes are limited to MIX / ADD.** They are dispatched per-vertex inside the shader; every other blend collapses to MIX.
> - **`TexCoord1` and `TexCoord2` are switched on for you.** Part Color travels through TEXCOORD1 and TEXCOORD2, so the target Canvas's `additionalShaderChannels` gains both automatically.
> - **Mesh effects and per-part shaders do not apply.** The graphic uploads its mesh itself rather than through `OnPopulateMesh`, so `Shadow`, `Outline` and other `BaseMeshEffect` components on it change nothing, and the per-part AddOn / custom shaders (sepia, outline, blur, …) are not drawn on a Canvas — every part draws with the one Canvas material.
> - **No Timeline control.** `SpriteStudioGraphic` implements no `ITimeControl`, and the Timeline track binds `SpriteStudioPlayer` only.

If you need complex blends or multiple atlases, use the world path (`SpriteStudioPlayer`). It draws every blend type but two of the SS6-era "2" variants: **Mul2** draws as Mul and **Screen2** as Screen, while **Div2** and **Overlay2**, which need the colour already behind the part, draw as MIX — as they do on the other SpriteStudio players.

## Masking / Clipping

Of the stencil buffer, this plugin uses only the low 4 bits — the range URP leaves to users (the rest carry light shapes and material types, and writing into them breaks rendering). Those 4 bits are split in two by whether a mask has "mask influence" set, which puts a ceiling on how many of one kind may overlap.

> [!NOTE]
> - **Masks with "mask influence" OFF stack up to 7 deep over one pixel.** An eighth overlapping mask wraps that pixel's count around, and the mask appears to drop out there.
> - **Masks with "mask influence" ON have no such limit**, because they are resolved by inversion (odd/even) instead of counting.
>
> A mask inside an Instance counts as "mask influence" OFF when either it or the Instance part that draws it has the setting off. A clipping mask inside an Instance stays in effect until the end of the whole animation, so it overlaps with the masks drawn after that Instance too.
>
> The ceiling is only reachable when eight or more mask-influence-off masks overlap the same pixel within the same draw scope. Ordinary animations do not reach it. If yours does, merge the masks or revisit their "mask influence" settings.

## Part Tracking

Part Tracking (making a `Transform` follow a part) is a `SpriteStudioPlayer` (world) feature only — it is not available on `SpriteStudioGraphic` (Canvas). See [Script Control and Events](./workflow/usage_scripting.md) for the full guide.

> [!NOTE]
> **A mesh (skinned) part cannot be followed directly.** Its vertices are drawn by bone skinning while the part's own transform stays at its setup-node position — usually near the character root — so targeting it snaps the follower near the root rather than onto the visible art. Add a **NULL / bone / joint part** at that spot in SpriteStudio and follow that part instead.

## Editor & Import

> [!WARNING]
> - **Install from the tarball, not a git URL.** Package Manager's *Add package from URL…* cannot install this package: the native plugin binaries ship only inside the release tarball. Always use *Add package from tarball…* with the downloaded `.tgz` — see [Installation](./setup/install.md).
> - **`Open SSPJ` needs SpriteStudio on the machine.** The button launches the editor through the OS file association, so [SpriteStudio](https://www.webtech.co.jp/spritestudio/) must be installed on the PC you are working on. The `.ssab` ↔ `.sspj` binding itself travels with the project as a relative path in `ProjectSettings/SpriteStudioSourceMap.json` — see [Asset Import and Editor Integration](./workflow/usage_asset_pipeline.md).

> [!NOTE]
> - **Edit mode shows a static preview of the start frame only.** Check behavior in Play mode.
> - **The Mesh Renderer's Materials belong to the player.** It sets the list itself, one material per draw batch, whenever what it draws changes, so a material dropped into the list is replaced. To change how the animation draws, edit the shared `.mat` next to the atlas texture, or give the player a **Shader Catalog** for per-part shaders — see [Performance Tuning and Advanced Settings](./workflow/tips.md).

## Scripting API

> [!NOTE]
> - **Playback ranges are frame-based.** `SetAnimationSection(int start, int end)` takes frame numbers; specifying a label range is not supported.
> - **`Play()` rewinds.** To resume from the current frame, `Pause()` holds it and `Resume()` lifts the hold — `Play()` cannot be used to resume.
> - **Signals materialize managed strings.** The Tick / mesh-construction loop is zero-allocation, and numeric Signal parameters are decoded into a reusable typed buffer, but part names, command ids, and string-typed parameters must still become managed strings. Animations that fire Signals in large numbers or at high frequency can therefore cause minor hitches on mobile / WebGL.

## Playback Feature Constraints

These come from the shared `libssruntime` and therefore apply to every render path and platform. They apply to every official player too, so the full explanations — and the rest of the shared constraints (embedded mode, unvalidated `.ssab` input, and so on) — live in the portal: **[Shared Limitations](https://cri-middleware.github.io/SpriteStudio-Docs/sdk/limitations/)**.

> [!WARNING]
> - **`independent=true` parts (Instance / Effect) do not reverse or seek correctly.** Only forward playback (including forward frame-skips) is correct.
> - **Seeking does not fire the events it skips over.** Only the destination frame's `UserData` / `Signal` / `Audio` events fire.
> - **No reverse audio.** Sounds are skipped whenever the effective direction is backward (a reverse direction, or a ping-pong return leg). A negative speed is not one of those cases: speed is a multiplier, and a negative one stops the playhead rather than turning it around, as does `0`.
> - **Animation blending is same-`.ssab` only**, and every blended animation must assign the same Cell to the same part.
> - **WebAssembly SIMD is required for WebGL builds.** The runtime is built with WebAssembly SIMD instructions and will not run on browsers without SIMD support. Treat SIMD support as a browser requirement for a WebGL distribution.

## Platforms

Unity version, render pipelines, and the platform list are in [Supported Environment](./index.md#supported-environment). For the native libraries behind each platform, see the [Build Guide](./setup/build.md).
