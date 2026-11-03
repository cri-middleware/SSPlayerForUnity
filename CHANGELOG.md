# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [7.0.0-beta.1] - Unreleased

### Added
- **Initial public release.** A Unity player for SpriteStudio animations (`.ssab`), driving the SpriteStudio-SDK Rust runtime (`libssruntime`) over csbindgen-generated FFI, with per-frame geometry built in Burst jobs.
- **Rendering**: world rendering (`SpriteStudioPlayer`, `MeshRenderer`) and uGUI rendering (`SpriteStudioGraphic`, Canvas), including Mask / RectMask2D support on Canvas. The graphic fits the animation's canvas into its `RectTransform` (`Fit` / `Align`) and gives uGUI layout the canvas's size. Universal (URP) and Built-in (BRP) render pipelines, in Gamma and Linear color space.
- **Feature coverage**: bone hierarchies, mesh & deform, particle effects, extended blending, and masks.
- **AddOn and custom shaders** (`SpriteStudioPlayer`): all 13 SpriteStudio AddOns (sepia, hsb, outline, blur, …) draw per part with no setup, and a **Shader Catalog** asset maps a part's shader id to a shader of your own.
- **Asset pipeline**: `.sspj` is converted to `.ssab` / `.ssqb` at import time by the bundled `libssconverter`, driven from a `ScriptedImporter`, and packaged as a `SpriteStudioAnimeAsset` (textures, materials, animation names, external references). Batch conversion with `ssconverter-cli` is supported for CI/CD.
- **Editor round trip**: the SS Importer window (**Window > SpriteStudio > Importer**) with folder batch import and Recent SSPJs; Open SSPJ and Reconvert from the asset's inspector and from the inspector of a player or graphic that plays it, with the `.ssab` ↔ `.sspj` binding shared through Git in `ProjectSettings/SpriteStudioSourceMap.json`; dragging an asset into the Hierarchy or Scene view places a player; in Edit mode, a static first-frame preview, **Play in Edit Mode** in the component's inspector (play, pause, scrub), and Timeline scrubbing.
- **Playback control**: play / pause / resume / stop, seek, section playback by frame or by label (`SetAnimationSectionByLabel`), reverse, ping-pong, speed, and frame rate, plus state queries (`IsPlaying`, `IsFinished`, `IsPlayingForward`, `FrameNo`) and what an asset holds (`AnimationNames`, `GetPartNames`, `GetLabelTable`, …). A setup that fails logs the runtime's own reason.
- **Motion blending**: a track stack on both components. Lay animations over the one `SetAnimation()` selects, weight or fade them, and `Crossfade()` from one animation into the next.
- **Events**: animation started / finished / looped, user data, signal, and audio, delivered as C# and Unity events; the keyed ones carry the authored frame number.
- **Built-in sound**: audio keys play their sounds with no code (**Play Audio**, **Audio Volume**, `StopAllSounds()`).
- **Timeline integration** via `ITimeControl` and a dedicated `SpriteStudioTrack`.
- **Per-part runtime overrides**: color, cell, and visibility, addressed by part name or index, with priority modes.
- **Runtime texture replacement**: `SetCellmapTexture()` swaps the texture bound for a cellmap on one player at runtime, for outfits and color variations without an asset per variant.
- **Flip & offset** (`SpriteStudioPlayer`): mirror or shift what the player draws without moving its `Transform`, so children and colliders stay put. Part tracking follows the mirrored pose.
- **Part tracking**: any GameObject can follow a specified part, with position, rotation and scale each switchable.
- **Part outlines and hit testing**: `TryGetPartQuad` reads the outline a part is drawn with, and `HitTestPart` the front-most part under a point, on both components.
- **UI integration** (`SpriteStudio.UI`): `SpriteStudioButton` and `SpriteStudioToggle` play a label section of the graphic's animation for each UI state (normal / hover / pressed / disabled / focused, and `selected_` while a toggle is on), with per-state clips and a reduced-motion switch; `SpriteStudioRaycastFilter` takes the pointer on the parts' outlines only. The inspector picks a clip's animation and label, and the filter's parts, from the asset.
- **Sub-frame interpolation** for smooth playback on high-refresh-rate displays and in slow motion.
- **Zero-allocation playback loop**: geometry is built in Burst jobs and uploaded via `SetVertexBufferData`, with no per-frame GC allocations. Players are evaluated in parallel on worker threads, and players of one `.ssab` share its parsed resource.
- **Distribution**: install from npm or a tarball, with native libraries for Windows, macOS, Linux, Android, iOS and WebGL.

### Known Limitations
- `LoadSsab` checks only the `"SSAB"` file identifier before handing the buffer to the native runtime; full structural verification is pending an upstream FlatBuffers fix (see [ROADMAP.md](./ROADMAP.md)).
- Constraints inherited from the shared runtime - `independent=true` parts under reverse playback and seeking, seeking not firing skipped events, no reverse audio, single-`.ssab` blending - are documented in [Shared Limitations](https://cri-middleware.github.io/SpriteStudio-Docs/sdk/limitations/).

[7.0.0-beta.1]: https://github.com/cri-middleware/SSPlayerForUnity/releases
