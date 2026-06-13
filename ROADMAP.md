# SpriteStudioPlayer for Unity — Player Roadmap

This is the **C# Player** roadmap for SSPlayerForUnity. It is the counterpart to
`SpriteStudio7-SDK/ROADMAP.md`, which tracks the **Rust runtime/converter** (`ssruntime` / `ssconverter`).
Runtime-side work lives in the SDK roadmap; this file tracks the Unity Player work.

Most items **selectively bring** worthwhile capabilities from the legacy **SS6PlayerForUnity** into this
SS7-based player — reimagined for SS7, **not ported verbatim** (see *Design principle* below). Each task is
written to be actionable from a cold start: it names the target files, the SS6 reference, the FFI / FlatBuffer
accessors involved, concrete steps, and a "Done when".

## Design principle — selective, SS7-native porting

SS6PlayerForUnity and this player have **different architectures** (managed C# vs Rust `ssruntime` + Burst
geometry), so features rarely map 1:1. Treat SS6 as the **capability / use-case reference** — *what* a feature is
for — and design each one to **fit SS7's idioms** instead of copying its API shape. For every task: first decide
whether the capability is worth bringing, then design it SS7-native.

Adapt to SS7 conventions rather than copying verbatim:
- **Argument style**: prefer explicit methods/overloads and nullable/optional params over sentinel values
  (SS6 used e.g. `idParts 0`=root / `-1`=all, `float.NaN` / `int.MinValue`="keep previous").
- **Override semantics**: keep them minimal; adopt SS6's `KindIgnoreAttribute` 3-mode priority only if a concrete
  need appears.
- **Multi-track**: prefer exposing SS7's per-part blending + state-machine primitives (SDK Phase 3) directly,
  rather than reproducing SS6's `AnimationTrack` abstraction 1:1.
- **Instance multipliers**: SS6's `RateOpacity` / `RateScaleLocal` are parent→child plumbing tied to its instance
  model; re-evaluate against SS7's instance model and add only if justified.
- **Naming**: use C# properties (this player's established style) rather than `Get`/`Set`-suffixed methods.

When a task's "worth bringing?" answer is "no" or "unclear", note that in the task and leave it unstarted rather
than porting for completeness.

## Status legend

- ✅ **Done**
- ☐ **Ready** — Player-only, no SDK dependency; can start now
- ⛔ **Blocked on SDK** — needs an `SpriteStudio7-SDK/ROADMAP.md` phase first (phase noted)
- 🕒 **Deferred ("あとで")** — intentionally postponed; detailed here so it can be picked up later

---

## Cold-start primer (read first)

**Architecture (per `AGENTS.md`).** The player is a thin C# layer over the Rust `ssruntime` via FFI; geometry
is built with Burst jobs and uploaded zero-alloc.

**Key files**
- `package/Runtime/SpriteStudioPlayer.cs` — world/MeshRenderer player component.
- `package/Runtime/SpriteStudioGraphic.cs` — uGUI (Canvas) player component. Mirror every public API change here.
- `package/Runtime/SpriteStudioPlaybackCore.cs` — `internal sealed` shared playback logic; both components hold one
  (`_core`) and delegate. Exposes `Internal` (`SpriteStudioInternalPlayer`).
- `package/Runtime/SpriteStudioInternalPlayer.cs` — owns the native handle + FFI calls + `IDisposable`. Holds
  `_ssab` (`SsAnimeBinary`), `_currentAnimation` (`AnimationData`), `_partNameToIndex`. This is where new FFI
  wrappers / ssab reads go.
- `package/Runtime/SpriteStudioGeometryBuilder.cs` + `SpriteStudioMeshJob.cs` — per-frame mesh/vertex build.
- `package/Runtime/SpriteStudioMaterial.cs` / `SpriteStudioAnimeAsset.cs` — material/texture resolution
  (`ResolveMaterial`, `Textures`, `Materials`).
- `package/Runtime/Bindings/SSRuntimeMethods.g.cs` — **generated** FFI (`ss_runtime_*`). Do NOT hand-edit;
  regenerate with `./scripts/build-runtime.sh` (or `generate-runtime-code.sh`) after the SDK adds an FFI.
- `package/Runtime/Format/ssab_generated.cs` — **generated** FlatBuffer accessors for the `.ssab` (animations,
  parts, labels, etc.). Player-side data reads go through these.
- `package/Editor/*Editor.cs` — custom inspectors. `SpriteStudioConversionRunner.cs` — `.sspj`→`.ssab` driver.

**Tests.** `package/Tests/Runtime/` (PlayMode). Fixtures are generated on demand (no `.ssab` is committed):
see `SpriteStudioRingoPlaybackTests.cs` / `SpriteStudioAudioEventTests.cs` for the
`SpriteStudioConversionRunner.Enqueue(sspj, outDir)` + poll pattern (SS7 SDK projects live at
`../SpriteStudio7-SDK/tests/<name>/<name>.sspj`). Components are `[ExecuteAlways]`, so
`AddComponent → AnimeAsset = … → Play("walk")` runs synchronously and native state can be asserted without
ticking. You cannot run Unity headlessly while the Editor is open — ask the user to run the Test Runner.

**Hard constraints (from `AGENTS.md`).** Zero GC alloc in the Tick/mesh loop; FFI safety (always
`JobHandle.Complete()` before freeing native); only standard Unity packages (Burst/Mathematics/Collections/
Timeline); keep struct layouts intact for Burst/FFI.

**Runtime gotchas (verified from `ssruntime`)**
- `ss_runtime_pause()` is a **toggle** of `is_pausing`; `ss_runtime_play()` **restarts from the start frame**
  (not a resume); `is_pausing` defaults `true`. To resume from the current frame, toggle `pause()` again. The
  Tier 1 `Pause`/`Resume`/`IsPaused` already encode this — reuse the same pattern for any new playback control.

**SS6 reference layout** — in the SS6PlayerForUnity repo, under `Assets/SpriteStudio6/`
- `Script/Root/FunctionParts.cs` — `HideSet`, `PartsColorChange`, `FlipSetParts`.
- `Script/Root/FunctionCell.cs` / `FunctionMaterial.cs` — `CellChangeParts`, material/texture swap.
- `Script/Root/FunctionAnimation.cs` — label-range / frame-offset play.
- `Script/Root/FunctionTrack.cs`, `Library/Control/AnimationTrack.cs` — multi-track.
- `Script/Script_SpriteStudio6_{Collider,Collider2D,Replicate,RootEffect,Sequence}.cs`, `Library/Library_SpriteStudio6.cs`.

---

> **Tier 1 (playback control) has shipped** and is intentionally not listed as a task here. It is the reference
> pattern for new playback APIs — see the primer's runtime gotchas and
> `package/Tests/Runtime/SpriteStudioPlaybackControlTests.cs` (`IsPlaying`/`IsPaused`/`Pause`/`Resume`/`Stop`/
> `CurrentFrame`/`TotalFrames`/`FrameRate`/`Reverse`/`PingPong`/`SetAnimationSection` on both components).

## ☐ Tier 4 — Label / frame-range / index playback  (Player-only — no SDK dependency)

- **Goal**: play a named label range and start-offset; play by animation index. SS6 equiv:
  `AnimationPlay(..., labelRangeStart, frameRangeOffsetStart, labelRangeEnd, frameRangeOffsetEnd, frameOffset)`,
  `IndexGetAnimation(name)`.
- **Key fact**: labels are already in the `.ssab`. `AnimationData.Labels(j)` / `LabelsLength` /
  `LabelsByKey(uint)`; each `ss.format.Label` = `{ Name, NameHash, Time }`. `PartAttributeInstance` also carries
  `StartLabelHash` / `EndLabelHash`. The Player already holds `_currentAnimation`. **No runtime/SDK change.**
- **Steps**:
  1. `SpriteStudioInternalPlayer`: add `bool TryResolveLabelFrame(string name, out float frame)` — iterate
     `_currentAnimation.Value.Labels(i)` matching `Label.Name` (robust; avoids hash-algorithm assumptions), return
     `Label.Time`. (Optionally also a hash overload via `LabelsByKey`.)
  2. Components: add `bool PlayRange(string startLabel, int startOffset, string endLabel, int endOffset)` that
     resolves both labels to frames, calls `SetAnimationSection(start, end)`, seeks `CurrentFrame = start`, and
     starts playback. Add `bool PlayByIndex(int index)` (resolve via `GetAnimationName(index)` then `Play`).
  3. Optional `Play(string anim, float startFrame)` overload (frame-offset start) — note `play()` restarts, so set
     the frame after starting.
- **Done when**: a PlayMode test loads a fixture with named labels, resolves one, and asserts the cursor stays
  within `[start, end]` while playing.

## ☐ Tier 5 — Hierarchical color / scale & custom time  (Player-only — needs design)

- **Goal**: port SS6 `AdditionalColor` (hierarchical per-vertex color/opacity multiply), `RateOpacity` /
  `RateScaleLocal` (parent→child multipliers), and `FunctionTimeElapse` (per-player custom delta time).
- **SS6 reference**: `FunctionMisc.cs` / `Library_SpriteStudio6.cs` (callbacks), `Script_SpriteStudio6_Root.cs`
  (`RateOpacity`, `RateScaleLocal`).
- **Steps / investigation**:
  1. **Custom delta**: `SpriteStudioUpdateManager` passes `deltaTime` into `ISpriteStudioRenderable.PrepareFrame`.
     Add an optional `System.Func<float,float>` (or `TimeScale`) on the component applied to that delta before
     `_core` advances — enables slow-mo / pause-groups independent of `SpeedScale`. (`SpeedScale` already covers
     simple scaling; only add if per-player wall-clock control is needed.)
  2. **Opacity/tint**: uGUI `SpriteStudioGraphic` already multiplies by `Graphic.color` (partial). For the world
     player, investigate the vertex-color path in `SpriteStudioMeshJob` and expose a player-level tint/opacity —
     via `MaterialPropertyBlock` or a uniform — rather than per-vertex CPU writes (keep zero-alloc).
  3. **RateScaleLocal**: `SpriteStudioInternalPlayer.VertexScale` already applies a uniform 1/PPU scale; a
     parent-controlled local-scale multiplier can fold in there. Mostly relevant once nested players exist
     (see Multi-track / Replicate) — low priority standalone.
- **Done when**: world-player tint/opacity verified visually + a test on the delta hook; document any SS6 feature
  intentionally dropped.

## ⛔ Tier 2 — Per-part runtime overrides (color / cell / visibility)  (SDK Phase 2)

- **Blocked on**: `SpriteStudio7-SDK/ROADMAP.md` Phase 2 **Override Layer API** (runtime state + FFI:
  set/clear per-part color, cell-ref, visibility). Track the FFI names there; regenerate bindings after it lands.
- **Goal**: port SS6 `PartsColorChange` (blend op Mix/Add/Sub/Mul, single or 4-vertex RGBA, opacity),
  `CellChangeParts` (cell-map + cell index, sentinel = follow data), `HideSet` (force show/hide, cascade to
  children). SS6 reference: `FunctionParts.cs`, `FunctionCell.cs`.
- **Steps (after SDK FFI exists)**:
  1. Regenerate `SSRuntimeMethods.g.cs`; add thin wrappers in `SpriteStudioInternalPlayer` (resolve part name→index
     via `ResolvePartIndex`).
  2. Add component API mirroring SS6 names, incl. the `KindIgnoreAttribute` priority mode (overwrite-on-next-key /
     hold-until-next-anim / permanent) — the SDK item calls this the shared "priority mode".
  3. Mirror on `SpriteStudioGraphic`.
- **Done when**: tests set an override mid-playback and assert it survives/clears per the priority mode.
- **Note**: **Flip** (`FlipSetParts`) is intentionally NOT part of this — SDK ROADMAP keeps flip as Player-side
  post-processing (scale negation + UV swap). Implement it here as a separate small Player task if needed.

## ☐/⛔ Tier 3 — Runtime texture / material swap

- **Goal**: SS6 `TextureSetCellMap` (swap a cell-map's texture) and per-instance material replacement.
- **Split**:
  - **Material/texture override (☐ Player-only)**: the component resolves materials in `UpdateSubmeshMaterials`
    (world) / `UpdateCanvasMaterials` (uGUI) via `_animeAsset.ResolveMaterial(textureHash, blend)`. Add a per-key
    override map (textureHash[/blend] → Material or Texture) consulted there, plus a public setter. Owns instances
    like the existing per-part AddOn material instances (destroy on disable).
  - **Cell-reference swap (⛔ SDK Phase 2)**: changing *which cell* a part draws is part of the Override Layer
    API (Tier 2), not a texture swap.
- **Done when**: a test swaps a material/texture at runtime and the submesh material list reflects it.

## ☐ Tier 6 — Inspector / editor UX  (Player-only)

- **Goal**: port SS6's inspector affordances. Files: `package/Editor/SpriteStudioPlayerEditor.cs`,
  `SpriteStudioGraphicEditor.cs`.
- **Steps**:
  1. **Interactive playback preview** (SS6's headline editor feature): the player already builds a static
     start-frame preview in edit mode (`_editPreviewDirty` / `BuildEditPreview`). Add Play/Pause + a frame
     scrubber + rate to the inspector, driving the edit-mode preview each editor tick (advance the internal
     player, `ScheduleMeshUpdate`, `ApplyAndRender`, `Repaint`).
  2. Toggles/fields backed by Tier 1/4 API: Reverse, PingPong, label-range (Start/End label + offset), a Reset
     button. These need serialized fields — coordinate with the existing `_loopCount`/`_speedScale` pattern.
- **Done when**: preview plays/pauses/scrubs in the inspector without entering Play mode.

## ☐ Collider / Collider2D + collision callbacks  (Player-only — sizeable)

- **Goal**: port SS6 `Script_SpriteStudio6_Collider` / `Collider2D` — per-part Unity colliders sized each frame
  from the part, with Enter/Stay/Exit callbacks.
- **Key difference**: SS7 parts are not GameObjects (see `SpriteStudioPartTracker` — it drives a user Transform
  from `TryGetPartLocalMatrix`). SDK keeps per-part AABB off-roadmap ("Player can derive from vertex positions").
- **Design note (worth it?)**: SS6 attaches a Unity collider (via a GameObject) per part. In SS7, first check
  whether the use case is already covered by `SpriteStudioPartTracker` (the user attaches their own Unity collider
  to a tracked Transform). Add a dedicated component only if auto-sized per-part colliders + callbacks are
  genuinely needed, and keep it lean (avoid a GameObject-per-part where possible).
- **Steps**:
  1. New `SpriteStudioCollider`/`SpriteStudioCollider2D` component: for each configured part, maintain a child
     GameObject with a `BoxCollider`/`CapsuleCollider` (3D) or `BoxCollider2D`/`CircleCollider2D` (2D).
  2. Each frame, resolve the part (`ResolvePartIndex` + `TryGetPartLocalMatrix`/`TryGetPartState`) and size/place
     the collider from the part's bounds (derive AABB from the part's quad/vertices).
  3. Relay Unity `OnTrigger*`/`OnCollision*` (and 2D) to user events, mirroring SS6's
     `FunctionCollider{Enter,Exit,Stay}` payload (part name/id + collision info).
- **Done when**: a PlayMode test with two overlapping configured parts fires the enter callback.

---

## 🕒 Deferred ("あとで")

These are intentionally postponed. Detailed for later pickup.

### 🕒 Effect player component

- **SS6**: `Script_SpriteStudio6_RootEffect` — play an effect/particle animation; `AnimationPlay/Stop/Pause`,
  deterministic seed (`SeedOffsetSet`).
- **SS7 status**: runtime effect/particle emission is **done** (SDK Phase 1 `[x]`); the geometry builder already
  renders effect parts embedded in animations (`SpriteStudioEffectPlanBuffer`).
- **Task**: decide whether a standalone effect-only component is needed (vs effects embedded in normal
  animations). If yes, add a C# component + play/stop/seed API over the existing effect FFI/geometry. Investigate
  the seed FFI surface first.

### 🕒 Sequence player component  (⛔ SDK Phase 3)

- **SS6**: `Script_SpriteStudio6_Sequence` — `.ssqe` playlists; `Play/Stop/PauseSet/StepSet`, `FunctionDecodeStep`
  (conditional next-step jump), loop types Last/Keep/Top.
- **Blocked on**: SDK Phase 3 **State machine → Sequence (`.ssqb`) playback** (runtime auto-advancing playlist).
- **SS7 status**: importer/data side partly exists — `SpriteStudioSequenceAsset.cs`, `Format/ssqb_generated.cs`,
  `Editor/SpriteStudioSsqbImporter.cs`.
- **Task (after SDK)**: thin C# component wrapping the runtime sequence/state-machine API; surface step control +
  a decode-step callback.

### 🕒 Replicate (crowd / mesh sharing)  (⛔ SDK Phase 3 — shared FrameData)

- **SS6**: `Script_SpriteStudio6_Replicate` — mirrors another player's `sharedMesh` + `MaterialPropertyBlock` at
  this object's transform; `OriginalSet(Root/RootEffect/Sequence)`, `Stop()`, static `ReplicateGet`.
- **Blocked on**: SDK Phase 3 **Instance Lifecycle → Animation Instancing** — a *shared evaluation context* that
  lets **multiple Players share one computed `FrameData`** (animate once, render N times at different transforms).
  This is the intended SS7 crowd path; build Replicate **on top of it, not before it**.
- **Task (after SDK)**: a component that binds to a shared eval context (or to an "original" player that drives it)
  and renders the shared `FrameData`/mesh at its own transform; provide `OriginalSet` / `Stop` /
  `ReplicateGet`-equivalents.
- **Note**: SS6 did this purely Player-side by copying the original's `_sharedMesh` + `MaterialPropertyBlock`
  every frame. That approach needs no SDK change, but is not the chosen path for SS7 — prefer waiting for the
  shared-`FrameData` feature so each frame is evaluated once and reused, instead of every replica re-copying a mesh.

### 🕒 Multi-track + transition (SS6 `AnimationTrack`)  (⛔ SDK Phase 3 for full per-part)

- **SS6**: N parallel animation tracks; `TrackReboot(count)`, `TrackConnectParts(idParts, track)`,
  `TrackTransition(primary, secondary, time)` (TRS crossfade), per-track Play/RateTime/Pause.
- **SS7 mapping** (runtime pieces are in SDK ROADMAP Phase 3, decomposed — no single "multi-track" item):
  - N weighted animations → `Animation Mixing/Blending` (whole-animation weighted blend) — **`[x]` done**.
  - Different animation per part (`TrackConnectParts`) → `Per-part animation blending (layered / per-bone mask)` —
    Phase 3 `[ ]`. (A per-part mask at weight 1 degenerates to SS6's selection semantics.)
  - Crossfade (`TrackTransition`) → `State machine` / crossfade primitives — Phase 3 `[ ]`.
- **Task (Player side)**: once per-part blending + crossfade FFI exist, build the SS6-style **track abstraction**
  in C# (`AnimationTrack`, `TrackReboot`, `TrackConnectParts`, `TrackTransition`, per-track playback) as an
  ergonomic layer over those primitives. The abstraction itself is untracked Player work.

### 🕒 Instance (nested-animation) runtime swap  (⛔ SDK Phase 3)

- **SS6**: `AnimationChangeInstance` — replace the animation mounted on an Instance part at runtime.
- **Blocked on**: SDK Phase 3 **Instance Lifecycle → Dynamic instance swap** (uses the shared SSAB registry).
- **Task (after SDK)**: expose a component API to swap an Instance part's animation by name/hash.

---

*Keep this roadmap tidy: delete a task once it ships (don't leave checked-off entries), and update the SDK
phase references if `SpriteStudio7-SDK/ROADMAP.md` changes.*
