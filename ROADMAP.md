# SpriteStudio Player for Unity — Roadmap

This file lists unfinished work only. Technical constraints live in the [limitations page](./docs/en/limitations.md);
runtime and converter work lives in the
[SpriteStudio-SDK roadmap](https://github.com/cri-middleware/SpriteStudio-SDK/blob/HEAD/ROADMAP.md).

## Playback & rendering

- [ ] **Events and sound while a Timeline plays** — a clip seeks the player to a frame and never advances it, so under
  a `SpriteStudioTrack` or a ControlTrack no UserData / Signal / Audio event fires and no built-in sound plays, in Play
  mode too. Advance through the frames a Playback evaluation crosses, as an AnimationTrack fires a clip's
  AnimationEvents, and keep scrubbing silent.
- [ ] **`SpriteStudioTrack` clips that know their animation** — a clip starts at Timeline's default length rather than
  the animation's, so Match Content has nothing to snap to; `ClipCaps.None` turns off Clip In, Speed Multiplier and
  Looping, none of which involve blending; and Animation Name is free text, with no list to pick from.
- [ ] **Flip / offset on `SpriteStudioGraphic`** — mirror or shift what the graphic draws without moving the
  `RectTransform`, as `SpriteStudioPlayer` does with `FlipX` / `FlipY` / `Offset`; the uGUI player exposes none of them.
- [ ] **Player-level tint / opacity, and a choice of clock** — `SpriteStudioPlayer` has no tint or opacity
  (`SpriteStudioGraphic` takes `Graphic.color`). The update loop advances every player by scaled `Time.deltaTime`, so
  `Time.timeScale = 0` freezes UI animation along with the world; unscaled time and a clock shared by a group of
  players are not available.
- [ ] **Per-player effect seed** — an effect part's seed comes from its part index and the effect name, so every
  player built from the same asset draws the identical particle pattern. Vary it per player behind a serialized seed
  field (0 = pick one), so a run stays reproducible; `ss_effect_slot_create` already takes the seed and the runtime
  honours an authored `is_lock_rand_seed` ahead of it, so the work is Player-side only.
- [ ] **Instance children's mesh jobs in one schedule** — every visible instance child schedules an `UpdateMeshJob` of
  its own from the main thread, about half a microsecond a child: with twenty players of overall `Instance`, 150
  children a frame, that is about a seventh of the frame. Starts as a study of whether children join their parent's job
  or small ones build on the main thread, without changing the order meshes are written and uploaded in; implemented
  once the study settles the design and measures the gain.
- [ ] **Instance children's frames on the worker threads** — top-level players fetch their frame in the parallel pass,
  but each instance child fetches its own on the main thread, inside its parent's `ConsumeFrameData`, one after
  another: about 6% of the frame with twenty players of overall `Instance` or `InstancePropagation`. A child's frame
  comes from its parent's evaluation, so the pass needs one more stage per level of nesting, and `Play`, `SetFrame` and
  the mask context handed down must keep their order. Starts as a design study; implemented once that settles.

## UI integration

Behaving like a UI element in the host's UI system, not merely drawing inside one.

- [ ] **`Nines` and `Text` parts** — the two part kinds authored for UI; `SpriteStudioGeometryBuilder` draws neither.
  Nines is a geometry path and nothing more, since the runtime hands over finished vertices, while Text arrives as a
  batch with no geometry plus the authored string, to map onto TextMeshPro with the string substitutable at runtime
  so a UI label can be localized.
- [ ] **`SpriteStudioVisualElement` for UI Toolkit** — uGUI and UI Toolkit are separate systems, and the graphic and
  the `SpriteStudio.UI` button, toggle and raycast filter reach only the former.
- [ ] **Multi-atlas on the Canvas path** — one `CanvasRenderer` is one mesh and one material, so the graphic draws
  the first atlas only and warns. A UI screen is exactly where several atlases meet.

## Distribution & verification

- [ ] **API reference page** — the docs site has no `api/` section, so the public surface is only discoverable by
  reading the usage pages or the source; the sibling players (Godot, Flutter, Web) each publish one. Either
  hand-write a `docs/*/api/player.md` covering `SpriteStudioPlayer` / `SpriteStudioGraphic` (as Godot does),
  or generate it from the XML doc comments, which are already written throughout `package/Runtime/`.
- [ ] **Restore full `.ssab` structural verification** — `LoadSsab` checks only the `"SSAB"` file identifier
  before handing the buffer to the native runtime: a format sanity check, not validation of the buffer's
  contents. Bump the FlatBuffers version `scripts/download-flatbuffers.sh` pulls and switch `LoadSsab` to a full
  `SsAnimeBinary.VerifySsAnimeBinary` gate, so a malformed or crafted `.ssab` is rejected with a friendly error
  instead of risking an out-of-bounds read in native code, and drop the partial-validation note from `SECURITY.md` (EN / JA)
  with it. Blocked on: the FlatBuffers verifier fix
  ([google/flatbuffers#8975], for [#8937]).

## Needs SDK

Wait on a runtime capability from the SDK roadmap.

- [ ] **Loops, ping-pong and direction under a Timeline** — clip time becomes a frame clamped to the section, so a
  looping animation under a ControlTrack plays once and holds its last frame, where a particle system under the same
  track keeps looping. The runtime's `set_frame_relative` applies the section, the loops, the style and the direction
  to an elapsed time, but the C ABI does not export it. Waits for: a C ABI export of `set_frame_relative`, which has no
  entry in the SDK ROADMAP.
- [ ] **Sequence playback** — play `.ssqb` playlists with step control. Waits for: State machine implementation
  (its Sequence playback entry).
- [ ] **Crowd / mesh sharing** — render one shared evaluation at many transforms. Waits for: Instance Lifecycle API
  (Animation Instancing).
- [ ] **Runtime instance swap** — change the animation mounted on an Instance part at runtime. Waits for: Instance
  Lifecycle API (Dynamic instance swap, built on the shared resource registry that item introduces).
- [ ] **Stretch a 9-slice part to a layout rect** — the step after drawing one at all (the UI-integration item above):
  the Override Layer covers color / cell / visibility only, so nothing can resize a Nines part from the host, and a UI
  panel that cannot follow its container is a picture of a panel. Waits for: Per-part size override.

[google/flatbuffers#8975]: https://github.com/google/flatbuffers/pull/8975
[#8937]: https://github.com/google/flatbuffers/issues/8937
