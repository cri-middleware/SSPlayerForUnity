# ⚡ Performance Tuning and Advanced Settings

[← Back to documentation index](../index.md)

This page covers settings for adjusting display and performance, along with more advanced topics such as custom shaders and Canvas path constraints.

## Coordinate Scale (Pixels Per Unit / PPU)

The **Pixels Per Unit (PPU)** of `SpriteStudioPlayer` (world) is the factor that converts SpriteStudio's pixel coordinates into Unity's world units. Set it from the `PixelsPerUnit` script property — it is not shown in the Inspector.

- Default value `1` … pixel-to-unit 1:1 (SS pixel coordinates map directly to world units).
- `100`, etc. … set this when you want to match Unity's sprite convention (1 unit = 100 px).

> Canvas (`SpriteStudioGraphic`) has no PPU setting: the animation's canvas is fitted into the `RectTransform`'s rect (**Fit** / **Align**, see [Basic Usage](./usage_basic.md)).

## Frame Progression: Frame Skipping (Real-Time Tracking)

**Frame Skipping** in the Inspector (`FrameSkipEnabled` from a script) switches how frames advance.

| Setting | Behavior | Use case |
|---|---|---|
| ON (default / timed) | Advances frames by the elapsed time. Even if frames are dropped under load, the **playback time stays constant** | Normal playback |
| OFF (fixed) | Always advances exactly one frame per update (frame-by-frame) | When you need to reliably draw every frame, such as for verification or recording |

## Sub-Frame Interpolation

Enabling **Sub-Frame Interpolation** in the Inspector (`SubFrameEnabled` from a script) interpolates between frames for smoother rendering. It is useful when you want to make low-frame-rate animations look smooth at a high refresh rate.

## Playback Speed (Speed Scale)

**Speed Scale** is the playback speed multiplier. `2.0` plays at 2x speed, and `0.5` plays at half speed. Changing it via the property at runtime takes effect immediately.

## Choosing Between SSAB and SSQB

There are two types of imported binaries.

- **SSAB (Animation Binary)** … standard animation data, imported as `SpriteStudioAnimeAsset`. Use this in most cases.
- **SSQB (Sequence Binary)** … multiple animations (e.g. `walk` → `run` → `jump`) concatenated on a timeline as a "sequence" on the SpriteStudio side, imported as `SpriteStudioSequenceAsset`.

> [!IMPORTANT]
> **Current state of SSQB**: **Importing `.ssqb` (recognizing and referencing the data) is supported**, but **sequence playback is not supported** (tracked in the [ROADMAP](https://github.com/cri-middleware/SSPlayerForUnity/blob/HEAD/ROADMAP.md)). To play animations back to back, use the `OnAnimationFinished` event described in [Script Control and Events](./usage_scripting.md) to switch to the next animation.

## About Zero-Allocation

This plugin's Tick / mesh construction loop produces no GC allocations.

**Event notifications** allocate only where unavoidable. Signal command parameters are decoded into a reusable, typed buffer (read via `SpriteStudioSignal.GetParam` / `TryGetParam`), so numeric parameters allocate nothing. What remains is the managed strings that must be materialized for part names, command ids, and string-typed parameters — unavoidable, and scaling with the number and burstiness of Signals, so animations that fire Signals in large numbers or at high frequency can still cause minor hitches on mobile / WebGL.

## Custom Shaders / AddOn (Per-Part)

On the world path (`SpriteStudioPlayer`), AddOns set on SpriteStudio parts are supported (sepia / hsb / outline / blur / pixelate / noise / wave, etc.). `SpriteStudioGraphic` draws every part with the one Canvas material, so AddOn and custom shaders do not apply on a Canvas.

- The built-in AddOns are pre-registered and need no setup: the package ships a default catalog at `Runtime/Resources/SpriteStudioAddOnCatalog.asset` that resolves them automatically.
- To add a custom shader, give the player a catalog of your own: create one with **Assets > Create > SpriteStudio > Shader Catalog**, add an entry whose **Id Name** matches the shader id set on the part in the SpriteStudio editor, with a **Shader** that follows the AddOn contract (`SpriteStudioAddOn.hlsl`), and assign the catalog to the player's **Shader Catalog** field. The player looks an id up there first, and whatever it does not list falls through to the built-in AddOns. The default catalog belongs to the installed package, which cannot be edited.
- Each AddOn part draws with a material of its own rather than a `MaterialPropertyBlock`, so it **does not obstruct the SRP Batcher**.

> The AddOn path supports both URP / Built-in.

## Masking and Clipping

Supports SpriteStudio's masking / clipping (per-part) using the stencil buffer.

- **World path (`SpriteStudioPlayer`)**: Draws with the stencil using SpriteStudio's internal mask mechanism. On URP, the stencil bits used are limited so that masking does not break when combined with SSAO / Deferred / Decal.
- **Canvas path (`SpriteStudioGraphic`)**: SpriteStudio's internal mask is suppressed; instead it follows uGUI's **Mask** / **Rect Mask 2D** (placing a mask on a parent clips this graphic, and a **Mask** on the graphic itself clips its children).

On the world path, masks resolve as SpriteStudio 7.5 draws them. A part set to draw inside the mask is hidden wherever no mask covers it, even in an animation with no mask at all. The rules for masks inside an Instance part are in the runtime's [mask guide](https://github.com/cri-middleware/SpriteStudio-SDK/blob/HEAD/libs/ssruntime/docs/40_parts/40_mask.md).

## Canvas (uGUI) Path Constraints

`SpriteStudioGraphic` is implemented to fit the CanvasRenderer constraint of "1 renderer = 1 mesh + 1 material." Note the following points.

- **Single-atlas assumption**: For animations that use multiple atlases, only the first atlas is drawn, and parts from other atlases cannot sample the correct texture (a warning is logged). Either **consolidate into a single atlas** or **use the world path (`SpriteStudioPlayer`)**.
- **Part draw modes**: MIX and ADD are dispatched per-vertex within the shader. Other blends are collapsed into MIX.
- **Enabling TexCoord1 / TexCoord2**: Part-Color data travels in TEXCOORD1 and TEXCOORD2, so `TexCoord1` and `TexCoord2` are automatically added to the target Canvas's `additionalShaderChannels`. The vertex colour is left to uGUI: it carries the graphic's **Color**, like an `Image`'s.
- **Per-part shaders and mesh effects do not apply**: the graphic uploads its mesh itself, so `Shadow`, `Outline` and other `BaseMeshEffect` components on it change nothing, and per-part AddOn / custom shaders are not drawn. Use the world path (`SpriteStudioPlayer`) for them.

If you want to correctly use complex blends or multiple atlases, we recommend using the world path (`SpriteStudioPlayer`). It draws Mul2 as Mul and Screen2 as Screen, and Div2 / Overlay2 as MIX (see [Limitations](../limitations.md)).

## Render Pipeline

Supports both Universal (URP) / Built-in (BRP).

- In edit mode, only a static preview of the start frame is shown. Perform behavior checks in Play mode.
