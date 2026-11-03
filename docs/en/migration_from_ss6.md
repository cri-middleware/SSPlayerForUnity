# 🔄 Migration from SS6Player for Unity

[← Back to documentation index](./index.md)

When migrating from the legacy **[SS6Player for Unity](https://github.com/SpriteStudio/SS6PlayerForUnity)** to this plugin, please note the following major changes. This version overhauls not only the internal implementation (the Rust core `libssruntime` + the Job System), but also the asset pipeline and the C# API. This is **not a drop-in replacement**: you will need to **re-import your assets and rewrite your scripts**.

## 1. Asset Format and Import Pipeline

- **SS6Player**: `.sspj` files were imported via `Tools > SpriteStudio6 > Importer`, generating a **`.prefab`** per animation plus a set of **`.asset` (ScriptableObject)** files such as `Script_SpriteStudio6_DataProject` / `DataAnimation` / `DataCellMap` / `DataEffect`. You also had to choose an import **mode** (`SS6PU` / `UNITY_NATIVE` / `UNITY_UI`, etc.).
- **Current version**: Generates a single **`SpriteStudioAnimeAsset` (ScriptableObject)** that wraps the optimized binary format **`.ssab`**. **No prefabs are generated**, and there is no import-mode selection (world vs. Canvas is chosen when you place the component).
  - Open `Window > SpriteStudio > Importer` and simply **drag & drop** your `.sspj`. The default output folder is `Assets/ssab_generated`.
  - The `.prefab` / `.asset` files produced by the old version **cannot be used** in this version. **Re-import from your `.sspj` sources.**
  - See [Basic Usage](./workflow/usage_basic.md) and [Asset Import & Editor Integration](./workflow/usage_asset_pipeline.md) for details.

## 2. Components and Placement

- **SS6Player**: You placed the **generated prefab** into the scene. The prefab carried `Script_SpriteStudio6_Root`: alone in the default `SS6PU` import mode (drawn through a MeshRenderer), or combined with `Script_SpriteStudio6_RootUnityNative` (`UNITY_NATIVE`, drawn by Unity's own renderers, one per part) or `Script_SpriteStudio6_RootUnityUI` (`UNITY_UI`, uGUI/Canvas). All classes live in the global namespace.
- **Current version**: Instead of prefabs, you use one of **two components** depending on the use case. The namespace is `SpriteStudio.Runtime`.

  | Use case | SS6Player | Current version |
  |---|---|---|
  | World space (3D/2D) | `Script_SpriteStudio6_Root` (`SS6PU`), or + `RootUnityNative` (`UNITY_NATIVE`) | **`SpriteStudioPlayer`** (MeshRenderer) |
  | Canvas (uGUI) UI | `Script_SpriteStudio6_Root` + `RootUnityUI` | **`SpriteStudioGraphic`** (CanvasRenderer) |

  - To place it, **drag & drop** the generated `SpriteStudioAnimeAsset` into the Hierarchy and pick `SpriteStudioPlayer` / `SpriteStudioGraphic` from the menu. You can also add the component manually.
  - Both components share the **same playback and event API**. Unlike SS6Player, where classes differed per mode, your scripting code is identical for world and Canvas.

## 3. Playback Control API

The API has moved from SS6Player's track-centric model (`indexTrack`) to a simple, Unity-idiomatic set of methods and properties.

### 3-1. Select animations by name, not index

- **SS6Player**: You resolved an index with `IndexGetAnimation("walk")` and passed that **index** to `AnimationPlay(indexTrack, indexAnimation, ...)`.
- **Current version**: You specify the **name** directly with `SetAnimation("walk")`, then call `Play()`.

```csharp
using SpriteStudio.Runtime;

[SerializeField] private SpriteStudioPlayer _player;

// SS6Player:
//   int idx = _root.IndexGetAnimation("walk");
//   _root.AnimationPlay(0, idx, 0);          // track 0, infinite loop
// Current version:
_player.SetAnimation("walk");
_player.LoopCount = -1;                        // infinite loop
_player.Play();
```

### 3-2. Method / property mapping

| SS6Player | Current version | Notes |
|---|---|---|
| `IndexGetAnimation(name)` + `AnimationPlay(track, index, …)` | `SetAnimation(name)` + `Play()` | Load and start are separated |
| `AnimationPlay(…, frameOffset)` | `Play(float startFrame)` | Start from a given frame. `startFrame` is a frame number in the animation, not an offset from the start of the section, and is not clamped to the section (`frameOffset` was relative to the play range) |
| `AnimationStop(track)` | `Stop()` | |
| `AnimationPause(track, true/false)` | `Pause()` / `Resume()` | Split in two: `Pause()` holds the frame, `Resume()` lifts the hold (`Play()` rewinds) |
| `CursorSet/Get(track, cursor)` | `FrameNo` (get/set) | Seek; fractional frames allowed |
| `RateTimeSet/Get(track, rate)` | `SpeedScale` | Playback speed multiplier |
| `AnimationPlay(…, timesPlay)` | `LoopCount` | **See the loop-count difference below** |
| `KindStylePlay.PINGPONG` | `PlaybackStyle` (enum) | Ping-pong playback |
| Negative `rateTime` for reverse | `PlaybackDirection` (enum) | **Speed and direction are separated** (see below) |
| `AnimationPlay(…, framePerSecond)` | `FrameRate` | Playback fps |
| `labelRangeStart / labelRangeEnd` | `SetAnimationSectionByLabel(string startLabel, string endLabel, int startOffset, int endOffset)` | `frameRangeOffsetStart` / `frameRangeOffsetEnd` are the offsets. `SetAnimationSection(int start, int end)` takes frames |
| `StatusIsPlaying` / `StatusGetPlay(track)` | `IsPlaying` | `true` even while paused |
| `StatusGetPause(track)` | `IsPausing` | |
| (none) | `IsPlayingForward` | The actual heading; `false` on a `PlaybackStyle.PingPong` return leg. Use it to gate audio |
| `CountGetAnimation()` | `AnimationNames.Length` | |

### 3-3. Loop count

`timesPlay` values `0`, `1` and `n` port across unchanged. SS6Player's default `-1` meant "keep the previous setting"; here `-1` is an infinite loop, and `LoopCount` simply keeps whatever you last set.

| Meaning | SS6Player `timesPlay` | Current version `LoopCount` |
|---|---|---|
| Infinite loop | `0` | `-1` (preferred) or `0` |
| Play once (no loop) | `1` | `1` |
| Play n times | `n` | `n` |

> `-1` is the canonical value for an infinite loop, and the default. `0` is accepted as a synonym for it — so a `timesPlay = 0` copied over still loops forever. There is no value meaning "do not play"; simply don't call `Play()`.

### 3-4. Reverse and speed are now separate

In SS6Player you reversed playback by passing a negative `rateTime`. In this version, **speed (`SpeedScale`) and direction (`PlaybackDirection`) are independent**.

```csharp
// SS6Player: reverse at 2x speed
//   _root.RateTimeSet(0, -2.0f);
// Current version:
_player.SetAnimation("walk");
_player.SpeedScale = 2.0f;   // speed is a positive multiplier
_player.PlaybackDirection = SpriteStudioPlaybackDirection.Backward;   // direction is an enum
_player.Play();
```

> `PlaybackDirection` / `PlaybackStyle` describe the playback rather than the clip, so they **survive** `SetAnimation()` — set them once (before `Play()`), not after every switch. A switch only re-arms the playhead and the loop budget.

## 4. Events and Callbacks

- **SS6Player**: You assigned functions to **delegate fields** on `Script_SpriteStudio6_Root` (`FunctionUserData` / `FunctionSignal` / `FunctionSound` / `FunctionPlayEnd`). `FunctionUserData`, `FunctionSignal` and `FunctionSound` took many arguments (`scriptRoot, nameParts, indexParts, …, ref userData, flagWayBack`); `FunctionPlayEnd(scriptRoot, objectControl)` returned a `bool` (`false` auto-destroyed the object).
- **Current version**: Provides both **C# `event`s and inspector-wireable `UnityEvent`s**, following Unity conventions. Payloads arrive as structs (`SpriteStudioUserData`, etc.).

### 4-1. Event mapping

| SS6Player (delegate) | Current version (C# event / UnityEvent) | Payload |
|---|---|---|
| `FunctionPlayEnd` | `OnAnimationFinished` / `OnAnimationFinishedEvent` | none |
| `FunctionUserData` | `OnUserData` / `OnUserDataEvent` | `SpriteStudioUserData` |
| `FunctionSignal` | `OnSignal` / `OnSignalEvent` | `SpriteStudioSignal` |
| `FunctionSound` | `OnAudio` / `OnAudioEvent` | `SpriteStudioAudio` |
| (new) | `OnAnimationStarted` / `OnAnimationLooped` | none |

### 4-2. Registration changes

```csharp
// SS6Player:
//   _root.FunctionUserData = HandleUserData;   // assign to a field
//   _root.FunctionPlayEnd  = HandlePlayEnd;

// Current version: subscribe/unsubscribe with += / -=
void OnEnable()
{
    _player.OnUserData          += HandleUserData;
    _player.OnAnimationFinished += HandleFinished;
}
void OnDisable()
{
    _player.OnUserData          -= HandleUserData;
    _player.OnAnimationFinished -= HandleFinished;
}

void HandleUserData(SpriteStudioUserData data)   // a single struct argument
{
    if (data.HasString) Debug.Log(data.StringValue);
    if (data.HasInt)    Debug.Log(data.IntValue);
}
void HandleFinished() { /* no return value; the auto-destroy behavior is gone */ }
```

> - The `FunctionPlayEnd` behavior where "returning `false` auto-destroys the GameObject" no longer exists. If you need that, call `Destroy()` explicitly inside `OnAnimationFinished`.
> - Use the `UnityEvent` variants (`OnUserDataEvent`, etc.) to wire methods on other components directly in the inspector.
> - For the event data structs (`SpriteStudioUserData` / `SpriteStudioSignal` / `SpriteStudioAudio`), see [Scripting & Events](./workflow/usage_scripting.md#structure-of-the-received-event-data).

## 5. Other Notes and Behavioral Differences

- **Multi-track / cross-fade**: SS6Player's multi-track layering (`TrackConnectParts()`) and `TrackTransition()` (cross-fade) become the track stack: `SetTrackAnimation()` lays an animation on a track over the one `SetAnimation()` selected, and `Crossfade()` switches a track with a fade. See [Motion Blending](./workflow/motion_blending.md).
- **Effects**: No standalone component like SS6Player's `Script_SpriteStudio6_RootEffect` is needed. Animations containing effect parts are **played inline** within `SpriteStudioPlayer` / `SpriteStudioGraphic`.
- **Attaching GameObjects to parts**: SS6Player's `UNITY_NATIVE` / `UNITY_UI` modes made every part a GameObject, so you parented under it. Here the animation is drawn as one mesh: use the **Part Trackers** feature of `SpriteStudioPlayer` (drive an arbitrary `Transform` to follow a named part). See [Scripting & Events](./workflow/usage_scripting.md#part-tracking-following-a-specified-part).
- **Timeline**: Still supported on the world path. `SpriteStudioPlayer` implements `ITimeControl`, so you can drive playback time from a Timeline track, or from a dedicated `SpriteStudioTrack`. `SpriteStudioGraphic` (Canvas) cannot be driven from Timeline; see [Limitations](./limitations.md).
- **Supported environment**: see [Installation](./setup/install.md#supported-environment).
- **Per-part Color / Cell / Visibility overrides**: SS6Player's `PartsColorChange()` / `CellChangeParts()` / `HideSet()` are provided as **Part Overrides (Override Layer API)**, which take precedence over keyframes and animation blending. See [Scripting & Events](./workflow/usage_scripting.md#part-overrides-part-color--cell--visibility).
- **Collider integration**: There is no direct equivalent to SS6Player's collider feature. If you need one, attach a collider to a GameObject driven to follow the target part (**Part Trackers**; see above).
