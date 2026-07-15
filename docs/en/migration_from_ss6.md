# Migration from SS6Player for Unity

[← Back to documentation index](./index.md)

When migrating from the legacy **[SS6Player for Unity](https://github.com/SpriteStudio/SS6PlayerForUnity)** to this plugin, please note the following major changes. This version overhauls not only the internal implementation (the Rust core `libssruntime` + the Job System), but also the asset pipeline and the C# API. This is **not a drop-in replacement**: you will need to **re-import your assets and rewrite your scripts**.

## 1. Asset Format and Import Pipeline

- **SS6Player**: `.sspj` files were imported via `Tools > SpriteStudio6 > Importer`, generating a **`.prefab`** per animation plus a set of **`.asset` (ScriptableObject)** files such as `Script_SpriteStudio6_DataProject` / `DataAnimation` / `DataCellMap` / `DataEffect`. You also had to choose an import **mode** (`SS6PU` / `UNITY_NATIVE` / `UNITY_UI`, etc.).
- **Current version**: Generates a single **`SpriteStudioAnimeAsset` (ScriptableObject)** that wraps the optimized binary format **`.ssab`**. **No prefabs are generated**, and there is no import-mode selection (world vs. Canvas is chosen when you place the component).
  - Open `Window > SpriteStudio > Importer` and simply **drag & drop** your `.sspj`. The default output folder is `Assets/ssab_generated`.
  - The `.prefab` / `.asset` files produced by the old version **cannot be used** in this version. **Re-import from your `.sspj` sources.**
  - See [Basic Usage](./workflow/usage_basic.md) and [Asset Import & Editor Integration](./workflow/usage_asset_pipeline.md) for details.

## 2. Components and Placement

- **SS6Player**: You placed the **generated prefab** into the scene. The prefab carried `Script_SpriteStudio6_Root`, combined with `Script_SpriteStudio6_RootUnityNative` (mesh) or `Script_SpriteStudio6_RootUnityUI` (uGUI/Canvas) depending on the render mode. All classes live in the global namespace.
- **Current version**: Instead of prefabs, you use one of **two components** depending on the use case. The namespace is `SpriteStudio.Runtime`.

  | Use case | SS6Player | Current version |
  |---|---|---|
  | World space (3D/2D) | `Script_SpriteStudio6_Root` + `RootUnityNative` | **`SpriteStudioPlayer`** (MeshRenderer) |
  | Canvas (uGUI) UI | `Script_SpriteStudio6_Root` + `RootUnityUI` | **`SpriteStudioGraphic`** (CanvasRenderer) |

  - To place it, **drag & drop** the generated `SpriteStudioAnimeAsset` into the Hierarchy and pick `SpriteStudioPlayer` / `SpriteStudioGraphic` from the menu. You can also add the component manually.
  - Both components share the **same playback and event API**. Unlike SS6Player, where classes differed per mode, your scripting code is identical for world and Canvas.

## 3. Playback Control API

The API has moved from SS6Player's track-centric model (`indexTrack`) to a simple, Unity-idiomatic set of methods and properties.

### 3-1. Select animations by name, not index

- **SS6Player**: You resolved an index with `IndexGetAnimation("walk")` and passed that **index** to `AnimationPlay(indexTrack, indexAnimation, ...)`.
- **Current version**: You specify the **name** directly with `SetAnimation("walk")`, then call `Play()`. There is no track concept.

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
| `AnimationPlay(…, frameOffset)` | `Play(float startFrame)` | Start from a given frame |
| `AnimationStop(track)` | `Stop()` | |
| `AnimationPause(track, true/false)` | `Pause()` | **No argument — it toggles** pause/resume |
| `CursorSet/Get(track, cursor)` | `CurrentFrame` (get/set) | Seek; fractional frames allowed |
| `RateTimeSet/Get(track, rate)` | `SpeedScale` | Playback speed multiplier |
| `AnimationPlay(…, timesPlay)` | `LoopCount` | **See the loop-count difference below** |
| `KindStylePlay.PINGPONG` | `PingPong` (bool) | Ping-pong playback |
| Negative `rateTime` for reverse | `Reverse` (bool) | **Speed and direction are separated** (see below) |
| `AnimationPlay(…, framePerSecond)` | `FrameRate` | Playback fps |
| `labelRangeStart / labelRangeEnd` | `SetAnimationSection(int start, int end)` | **Frames only**; label ranges are not supported |
| `StatusIsPlaying` / `StatusGetPlay(track)` | `IsPlaying` | `true` even while paused |
| `StatusGetPause(track)` | `IsPaused` | |
| `CountGetAnimation()` | (query via `SpriteStudioAnimeAsset`) | |

### 3-3. Loop count

`timesPlay` values port across unchanged.

| Meaning | SS6Player `timesPlay` | Current version `LoopCount` |
|---|---|---|
| Infinite loop | `0` | `-1` (preferred) or `0` |
| Play once (no loop) | `1` | `1` |
| Play n times | `n` | `n` |

> `-1` is the canonical value for an infinite loop, and the default. `0` is accepted as a synonym for it — so a `timesPlay = 0` copied over still loops forever. There is no value meaning "do not play"; simply don't call `Play()`.

### 3-4. Reverse and speed are now separate

In SS6Player you reversed playback by passing a negative `rateTime`. In this version, **speed (`SpeedScale`) and direction (`Reverse`) are independent**.

```csharp
// SS6Player: reverse at 2x speed
//   _root.RateTimeSet(0, -2.0f);
// Current version:
_player.SetAnimation("walk");
_player.SpeedScale = 2.0f;   // speed is a positive multiplier
_player.Reverse    = true;   // direction is a bool
_player.Play();
```

> `SetAnimation()` resets to Forward / Normal (non-ping-pong) every time, so set `Reverse` / `PingPong` **after** `SetAnimation()`.

## 4. Events and Callbacks

- **SS6Player**: You assigned functions to **delegate fields** on `Script_SpriteStudio6_Root` (`FunctionUserData` / `FunctionSignal` / `FunctionPlayEnd`). Each delegate took many arguments (`scriptRoot, nameParts, indexParts, …, ref userData, flagWayBack`), and `FunctionPlayEnd` returned a `bool` (`false` auto-destroyed the object).
- **Current version**: Provides both **C# `event`s and inspector-wireable `UnityEvent`s**, following Unity conventions. Payloads arrive as structs (`SpriteStudioUserData`, etc.).

### 4-1. Event mapping

| SS6Player (delegate) | Current version (C# event / UnityEvent) | Payload |
|---|---|---|
| `FunctionPlayEnd` | `OnAnimationFinished` / `OnAnimationFinishedEvent` | none |
| `FunctionUserData` | `OnUserData` / `OnUserDataEvent` | `SpriteStudioUserData` |
| `FunctionSignal` | `OnSignal` / `OnSignalEvent` | `SpriteStudioSignal` |
| (new) | `OnAnimationStarted` / `OnAnimationLooped` | none |
| (new) | `OnAudio` / `OnAudioEvent` | `SpriteStudioAudio` |

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
> - For the event data structs (`SpriteStudioUserData` / `SpriteStudioSignal` / `SpriteStudioAudio`), see [Scripting & Events](./workflow/usage_scripting.md).

## 5. Other Notes and Behavioral Differences

- **Multi-track / cross-fade**: There is no direct equivalent to SS6Player's multi-track layering or `TrackTransition()` (cross-fade). If you need layered playback, **use multiple components** together.
- **Effects**: No standalone component like SS6Player's `Script_SpriteStudio6_RootEffect` is needed. Animations containing effect parts are **played inline** within `SpriteStudioPlayer` / `SpriteStudioGraphic`.
- **Attaching GameObjects to parts**: Instead of SS6Player's `TrackConnectParts()` / `InstanceGet()`, use the **Part Trackers** feature of `SpriteStudioPlayer` (drive an arbitrary `Transform` to follow a named part). See [Scripting & Events](./workflow/usage_scripting.md).
- **Timeline**: Still supported. `SpriteStudioPlayer` implements `ITimeControl`, so you can drive playback time from a Timeline track.
- **Supported environment**: This version targets **Unity 6.3 LTS (6000.3) or later** and supports URP / Built-in RP. See the [documentation index](./index.md) for details.
- **Cell swapping, colliders, and other advanced features**: Equivalents to SS6Player's `CellChangeParts()` or collider integration differ in availability in this version. Check the current documentation and public API for the latest support status.
