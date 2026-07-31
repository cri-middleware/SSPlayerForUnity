# SpriteStudioPlayer for Unity — Roadmap

The C# Player roadmap. Runtime and converter work lives in the
[SpriteStudio-SDK roadmap](https://github.com/cri-middleware/SpriteStudio-SDK/blob/main/ROADMAP.md);
items marked **needs SDK** wait on a runtime capability from there.

`[x]` = done · `[ ]` = planned

## Playback & rendering

- [x] Playback control — play / pause / resume / stop, seek, section playback, reverse, ping-pong, speed, frame rate.
- [x] World (MeshRenderer) and uGUI (Canvas) rendering, including mask / RectMask2D on Canvas.
- [x] Timeline support (`ITimeControl`).
- [x] Events — animation finished / looped, user data, signal, audio.
- [x] Flip / offset — mirror or shift what the player draws without moving the Transform, so children and
  colliders stay put. World renderer only so far; `SpriteStudioGraphic` (uGUI) does not expose them yet.
- [ ] Label / frame-range / index playback — play a named label range with a start offset, and play by animation index.
- [ ] Player-level tint / opacity and a custom delta-time hook (per-player slow-motion / pause groups).

## Parts & materials

- [x] Per-part runtime overrides — color, cell, and visibility, by part name or index, with priority modes.
- [ ] Runtime material / texture override — swap the material or texture for a texture key at runtime.
- [ ] Per-part colliders — auto-sized 2D / 3D colliders that follow parts, with enter / stay / exit callbacks.

## Needs SDK

Wait on a runtime capability from the SDK roadmap.

- [ ] Sequence playback — play `.ssqb` playlists with step control.
- [ ] Crowd / mesh sharing — render one shared evaluation at many transforms.
- [ ] Multi-track playback — layered per-part animation with crossfade transitions.
- [ ] Runtime instance swap — change the animation mounted on an Instance part at runtime.

## Documentation

- [ ] API reference page. The docs site has no `api/` section, so the public surface is only discoverable by
  reading the usage pages or the source — the sibling players (Godot, Flutter, Web) each publish one. Either
  hand-write a `docs/*/api/player.md` covering `SpriteStudioPlayer` / `SpriteStudioGraphic` (as Godot does),
  or generate it from the XML doc comments, which are already written throughout `package/Runtime/`.

## Backlog

- [ ] Restore full `.ssab` structural verification. `LoadSsab` currently checks only the `"SSAB"` file identifier
  before handing the buffer to the native runtime — a format sanity check, not validation of the buffer's
  contents. Once the FlatBuffers verifier fix ([google/flatbuffers#9179]) reaches the version
  `scripts/download-flatbuffers.sh` pulls, bump that version and switch `LoadSsab` to a full
  `SsAnimeBinary.VerifySsAnimeBinary` gate, so a malformed or crafted `.ssab` is rejected with a friendly error
  instead of risking an out-of-bounds read in native code.

[google/flatbuffers#9179]: https://github.com/google/flatbuffers/pull/9179
