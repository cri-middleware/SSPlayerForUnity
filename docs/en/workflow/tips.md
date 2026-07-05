# Performance Tuning and Advanced Settings

[← Back to documentation index](../index.md)

This page covers settings for adjusting display and performance, along with more advanced topics such as custom shaders and Canvas path constraints.

## Coordinate Scale (Pixels Per Unit / PPU)

The **Pixels Per Unit (PPU)** of `SpriteStudioPlayer` (world) is the factor that converts SpriteStudio's pixel coordinates into Unity's world units.

- Default value `1` … pixel-to-unit 1:1 (SS pixel coordinates map directly to world units).
- `100`, etc. … set this when you want to match Unity's sprite convention (1 unit = 100 px).

> Canvas (`SpriteStudioGraphic`) is drawn in the RectTransform coordinate system, so it has no PPU setting.

## Frame Progression: Skip Frames (Real-Time Tracking)

**Skip Frames** switches how frames advance.

| Setting | Behavior | Use case |
|---|---|---|
| ON (default / timed) | Advances frames by the elapsed time. Even if frames are dropped under load, the **playback time stays constant** | Normal playback |
| OFF (fixed) | Always advances exactly one frame per update (frame-by-frame) | When you need to reliably draw every frame, such as for verification or recording |

## Sub-Frame Interpolation (Sub Frame Enabled)

Enabling **Sub Frame Enabled** interpolates between frames for smoother rendering. It is useful when you want to make low-frame-rate animations look smooth at a high refresh rate.

## Playback Speed (Speed Scale)

**Speed Scale** is the playback speed multiplier. `2.0` plays at 2x speed, and `0.5` plays at half speed. Changing it via the property at runtime takes effect immediately.

## Choosing Between SSAB and SSQB

There are two types of imported binaries.

- **SSAB (Animation Binary)** … standard animation data, imported as `SpriteStudioAnimeAsset`. Use this in most cases.
- **SSQB (Sequence Binary)** … multiple animations (e.g. `walk` → `run` → `jump`) concatenated on a timeline as a "sequence" on the SpriteStudio side, imported as `SpriteStudioSequenceAsset`.

> [!IMPORTANT]
> **Current state of SSQB**: **Importing `.ssqb` (recognizing and referencing the data) is supported**, but **sequence playback is not yet provided** (planned for the future). For continuous playback in the meantime, use the `OnAnimationFinished` event described in [Script Control and Events](./usage_scripting.md) to switch to the next animation.

## About Zero-Allocation

This plugin's Tick / mesh construction loop produces no GC allocations (it reuses `NativeArray` and writes directly to `Mesh.SetVertexBufferData`).

However, **event notifications** are an exception. In particular, Signal event parameters (the `Params` dictionary) allocate managed memory at notification time. In animations that fire Signals in large numbers or at high frequency, this can cause hitches on mobile / WebGL. It is not something that happens constantly; it scales with the number and burstiness of Signals.

## Custom Shaders / AddOn (Per-Part)

Supports AddOns set on SpriteStudio parts (sepia / hsb / outline / blur / pixelate / noise / wave, etc.).

- Assigning a `SpriteStudioShaderCatalog` to the **Shader Catalog** of `SpriteStudioPlayer` enables per-part shader assignment.
- The built-in AddOns are pre-registered in the catalog bundled with the package. Leaving it empty disables per-part shader assignment.
- It does not use `MaterialPropertyBlock`; instead it supplies materials from a material pool keyed by `(shaderId, blend)`, so it **does not obstruct the SRP Batcher**.

> The AddOn path supports both URP / Built-in.

## Masking and Clipping

Supports SpriteStudio's masking / clipping (per-part) using the stencil buffer.

- **World path (`SpriteStudioPlayer`)**: Draws with the stencil using SpriteStudio's internal mask mechanism. On URP, the stencil bits used are limited so that masking does not break when combined with SSAO / Deferred / Decal.
- **Canvas path (`SpriteStudioGraphic`)**: SpriteStudio's internal mask is suppressed; instead it follows uGUI's **Mask** / **RectMask2D** (placing a mask on a parent clips this graphic).

## Canvas (uGUI) Path Constraints

`SpriteStudioGraphic` is implemented to fit the CanvasRenderer constraint of "1 renderer = 1 mesh + 1 material." Note the following points.

- **Single-atlas assumption**: For animations that use multiple atlases, only the first atlas is drawn, and parts from other atlases cannot sample the correct texture (a warning is logged). Either **consolidate into a single atlas** or **use the world path (`SpriteStudioPlayer`)**.
- **Part draw modes**: MIX and ADD are dispatched per-vertex within the shader. Other blends are collapsed into MIX.
- **Enabling TexCoord1**: To pass part-color data via TEXCOORD1, `TexCoord1` is automatically added to the target Canvas's `additionalShaderChannels`.

If you want to correctly use complex blends, multiple atlases, or all of the extended blend types, we recommend using the world path (`SpriteStudioPlayer`).

## Render Pipeline

Supports both Universal (URP) / Built-in (BRP).

- Immediately after importing on URP, material references may become inconsistent and rendering may turn pink. In most cases, restarting the editor or reimporting resolves this.
- In edit mode, only a static preview of the start frame is shown. Perform behavior checks in Play mode.
