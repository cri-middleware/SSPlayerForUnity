# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [7.0.0-beta.1] - Unreleased

### Added
- **Initial public release (work in progress).** A Unity "Body" that plays OPTPiX SpriteStudio 7 animations (`.ssab`) by driving the SpriteStudio-SDK Rust runtime (`libssruntime`) over csbindgen-generated FFI, with per-frame geometry built in Burst jobs.
- **Rendering**: world rendering (`SpriteStudioPlayer`, `MeshRenderer`) and uGUI rendering (`SpriteStudioGraphic`, Canvas), including Mask / RectMask2D support on Canvas. Universal (URP) and Built-in (BRP) render pipelines.
- **Feature coverage**: bone hierarchies, mesh & deform, particle effects, extended blending, and masks.
- **Asset pipeline**: `.sspj` is converted to `.ssab` / `.ssqb` at import time by the bundled `libssconverter`, driven from a `ScriptedImporter`, and packaged as a `SpriteStudioAnimeAsset` (textures, materials, animation names, external references). Batch conversion with `ssconverter-cli` is supported for CI/CD.
- **Playback control**: play / pause / resume / stop, seek, section playback, reverse, ping-pong, speed, and frame rate.
- **Events**: animation finished / looped, user data, signal, and audio, delivered as Unity events.
- **Timeline integration** via `ITimeControl`.
- **Per-part runtime overrides**: color, cell, and visibility, addressed by part name or index, with priority modes.
- **Flip & offset** (`SpriteStudioPlayer`): mirror or shift what the player draws without moving its `Transform`, so children and colliders stay put. Part tracking follows the mirrored pose.
- **Part tracking**: any GameObject can follow a specified part.
- **Sub-frame interpolation** for smooth playback on high-refresh-rate displays and in slow motion.
- **Zero-allocation playback loop**: geometry is built in Burst jobs and uploaded via `SetVertexBufferData`, with no per-frame GC allocations.

### Known Limitations
- The API may change without notice while the package is in beta.
- `LoadSsab` checks only the `"SSAB"` file identifier before handing the buffer to the native runtime; full structural verification is pending an upstream FlatBuffers fix (see [ROADMAP.md](./ROADMAP.md)).
- Constraints inherited from the shared runtime - `independent=true` parts under reverse playback and seeking, seeking not firing skipped events, no reverse audio, single-`.ssab` blending - are documented in [Shared Limitations](https://cri-middleware.github.io/SpriteStudio-Docs/sdk/limitations/).

[7.0.0-beta.1]: https://github.com/cri-middleware/SSPlayerForUnity/releases
