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
- [ ] Label playback — play a named label range with a start offset. `SetAnimationSection` takes frame
  numbers only, so a label the animation already carries cannot be named from the host.
- [ ] Player-level tint / opacity and a custom delta-time hook (per-player slow-motion / pause groups).
- [ ] Multi-track playback — layered per-part animation with crossfade transitions. The runtime side is
  done: the SDK's track stack ships, and its FFI (`ss_runtime_crossfade` / `set_track_animation` /
  `track_add_source` / `fade_track`) is generated into `package/Runtime/Bindings/` with no C# caller. What
  is left is the Player surface on top of it.
- [ ] Per-player effect seed — an effect part is created with a seed derived from its part index and the
  effect name (`SpriteStudioInternalPlayer.cs:513`), so every player built from the same asset draws the
  identical particle pattern. Vary it per player instance, behind a serialized seed field (0 = pick one)
  so a run stays reproducible. The seed is already an `ss_effect_slot_create` argument and the runtime
  honours an authored `is_lock_rand_seed` ahead of it, so the work is Player-side only.

## Parts & materials

- [x] Per-part runtime overrides — color, cell, and visibility, by part name or index, with priority modes.
- [ ] Runtime material / texture override — swap the material or texture for a texture key at runtime.
- [ ] Per-part colliders — auto-sized 2D / 3D colliders that follow parts, with enter / stay / exit callbacks.

## UI integration

Behaving like a UI element in the host's UI system, not merely drawing inside one. The vocabulary is shared
with the sibling Players: `fit` / `align` for placement, a part rect read-out for hit areas, and a UI-state map.

- [ ] Layout-aware `SpriteStudioGraphic` — fit the animation into the `RectTransform` rect (`fit` =
  `contain` / `cover` / `none`, with `align`), and implement `ILayoutElement` so a `LayoutGroup` or
  `ContentSizeFitter` can size it from the animation's content box. The Canvas path draws at authored pixel
  size in the rect's local space and never reads `rectTransform`, so resizing the rect changes nothing today.
- [ ] Part rect read-out — `TryGetPartRect(part, out Rect)` in player-local space, built from the `PartState`
  fields the runtime already delivers every frame (`size_x` / `size_y`, `anchor`, `pivot`) and the part
  transform. Also what per-part colliders above need, so build it once and share it.
- [ ] Per-part hit testing — `HitTestPart(Vector2)` plus a `Graphic.Raycast` override that answers from part
  rects rather than the whole `RectTransform`, so an irregularly shaped button stops eating clicks around its
  artwork and a transparent margin lets the control behind it through.
- [ ] UI state binding — a component that drives the player from `Selectable` state (normal / highlighted /
  pressed / selected / disabled), each mapped to an animation or a label. Without it every project rewrites
  the same `IPointerEnterHandler` glue.
- [ ] The two part kinds authored for UI, neither of which draws today — `SpriteStudioGeometryBuilder.cs:474`
  takes Normal / Shape / Mesh and drops `Nines` and `Text` on the floor. **Nines** is the cheap one: the
  runtime hands over finished vertices, so it is a geometry path and nothing more. **Text** arrives as a
  batch with no geometry plus the authored string — map it onto TextMeshPro rather than growing a glyph
  atlas here, and allow substituting the string at runtime so a UI label can be localized.
- [ ] `SpriteStudioVisualElement` for UI Toolkit — uGUI and UI Toolkit are separate systems, and everything
  above reaches only the former.
- [ ] Multi-atlas on the Canvas path — one `CanvasRenderer` is one mesh and one material, so the graphic draws
  the first atlas only and warns. A UI screen is exactly where several atlases meet.

## Needs SDK

Wait on a runtime capability from the SDK roadmap.

- [ ] Sequence playback — play `.ssqb` playlists with step control.
- [ ] Crowd / mesh sharing — render one shared evaluation at many transforms.
- [ ] Runtime instance swap — change the animation mounted on an Instance part at runtime. Waits on the
  SDK roadmap's **Instance Lifecycle API** (Phase 3), which builds it on the shared resource registry the
  same item introduces.
- [ ] Stretch a 9-slice part to a layout rect — drawing one at all is the UI-integration item above; this is
  the step after it. The runtime evaluates the part's size from its keyframes and hands over finished
  vertices, and the Override Layer covers color / cell / visibility only, so nothing can resize one from the
  host — a UI panel that cannot follow its container is a picture of a panel. Waits on the SDK roadmap's
  **Per-part size override** (Phase 3).

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
