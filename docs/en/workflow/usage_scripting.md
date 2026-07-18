# Script Control and Events

[← Back to documentation index](../index.md)

This page explains how to control `SpriteStudioPlayer` / `SpriteStudioGraphic` from Unity C# scripts. In line with Unity's design philosophy, it provides playback control via simple method calls, and event integration through **both C# events and inspector-wireable `UnityEvent`s**, making it easy to embed into game logic.

`SpriteStudioPlayer` (world) and `SpriteStudioGraphic` (Canvas) share **the same playback control API and event notification API**. The code on this page works as-is on either component.

The namespace is `SpriteStudio.Runtime`.

```csharp
using SpriteStudio.Runtime;
```

## Starting Playback

```csharp
[SerializeField] private SpriteStudioPlayer _player;

// Specify an animation and load its data (does not start playback yet)
_player.SetAnimation("walk");
// Start playback
_player.Play();
```

`SetAnimation()` loads the animation's data and **waits on the first frame** (it does not start playback). Calling `Play()` afterward starts playback from that point. `SetAnimation()` does **not** apply playback settings (loop/speed/skip/direction). Because it resets every time to **first frame, Forward, Normal (non-ping-pong), and the runtime default loop/speed/skip**, the loop/speed/skip configured in the inspector are applied at startup, but when you switch animations at runtime from a script via `SetAnimation()`, re-set `LoopCount` / `SpeedScale` / `SkipFrames` / `Reverse` / `PingPong` after `SetAnimation()` as needed (see below).

## Playback Control API

| Member | Kind | Description |
|---|---|---|
| `SetAnimation(string animName)` | Method | Specify an animation and load its data (does not start playback). Returns `true` on success |
| `Play()` | Method | Start playing the set-up animation from the beginning |
| `Play(float startFrame)` | Method | Start playing the set-up animation from the specified frame |
| `Pause()` | Method | Toggle pause/resume (retains the current frame. `play()` rewinds to the beginning, so it cannot be used to resume) |
| `Stop()` | Method | Stop playback |
| `IsPlaying` | `bool` (read) | Whether a playback session is active (`true` even while paused) |
| `IsPaused` | `bool` (read) | Whether currently paused |
| `CurrentFrame` | `float` | Current frame. Use `set` to seek to an arbitrary frame (fractional allowed) |
| `TotalFrames` | `int` (read) | Total frame count of the current animation (`0` when not loaded) |
| `FrameRate` | `int` | Playback frame rate (fps). Use `set` to change at runtime |
| `Reverse` | `bool` | Whether reverse playback (Backward) |
| `PingPong` | `bool` | Whether ping-pong (PingPong) playback |
| `SetAnimationSection(int start, int end)` | Method | Limit the playback range by frame (frame-based; label specification is not supported) |

### Example: Pause and Resume

```csharp
// Pause() toggles pause/resume (pauses if playing, resumes if paused)
_player.Pause();
```

### Example: Seek and Section Playback

```csharp
_player.SetAnimation("attack");
_player.Play();
_player.SetAnimationSection(10, 30); // Play only the section from frame 10 to 30
_player.CurrentFrame = 10f;          // Seek to the start of the section
```

### Example: Reverse and Ping-Pong

```csharp
_player.SetAnimation("walk");
_player.Reverse = true;   // Switch to reverse playback
_player.PingPong = true;  // Switch to ping-pong playback
_player.Play();
```

> Because `SetAnimation()` resets to Forward / Normal every time, set `Reverse` / `PingPong` **after `SetAnimation()`** (either before or after `Play()` is fine).

## Playback Parameter Properties

These properties correspond to the inspector items. Changing them at runtime is reflected in the runtime immediately.

| Property | Type | Description |
|---|---|---|
| `AnimeAsset` | `SpriteStudioAnimeAsset` | Playback target asset (swappable) |
| `AnimationName` | `string` | Animation name |
| `AutoPlay` | `bool` | Auto-play at startup |
| `LoopCount` | `int` | Loop count (`-1` or `0` = infinite / `n` = n times) |
| `SpeedScale` | `float` | Playback speed multiplier |
| `SubFrameEnabled` | `bool` | Sub-frame interpolation |
| `SkipFrames` | `bool` | Real-time tracking (see [Advanced Settings](./tips.md)) |

`SpriteStudioPlayer` (world) only: `SortingLayerName` / `SortingOrder` / `PixelsPerUnit`.

## Event Notifications

You can receive five types of events as the animation progresses. Both `C# events` and inspector-wireable `UnityEvent`s are provided.

| Event | C# event | UnityEvent field | Payload | Trigger timing |
|---|---|---|---|---|
| Playback started | `OnAnimationStarted` | `OnAnimationStartedEvent` | None | When playback starts after calling `Play()` |
| Playback finished | `OnAnimationFinished` | `OnAnimationFinishedEvent` | None | When every configured loop has played. Never under an infinite `LoopCount`, and not on `Stop()` |
| Loop | `OnAnimationLooped` | `OnAnimationLoopedEvent` | None | Each time a loop returns to the beginning. The final loop finishes instead |
| User data | `OnUserData` | `OnUserDataEvent` | `SpriteStudioUserData` | When a frame with a UserData key is reached |
| Signal | `OnSignal` | `OnSignalEvent` | `SpriteStudioSignal` | When a frame with a Signal command is reached |
| Audio | `OnAudio` | `OnAudioEvent` | `SpriteStudioAudio` | When a frame with an Audio event is reached |

> These correspond to the keys (UserData / Signal / Audio) set on the animation in SpriteStudio.

### Registering Events from C#

```csharp
void OnEnable()
{
    _player.OnAnimationFinished += HandleFinished;
    _player.OnUserData += HandleUserData;
}

void OnDisable()
{
    _player.OnAnimationFinished -= HandleFinished;
    _player.OnUserData -= HandleUserData;
}

void HandleFinished()
{
    Debug.Log("Playback finished");
}

void HandleUserData(SpriteStudioUserData data)
{
    Debug.Log($"UserData: part={data.PartName}");
    if (data.HasInt)    Debug.Log($"  int = {data.IntValue}");
    if (data.HasString) Debug.Log($"  str = {data.StringValue}");
    if (data.HasPoint)  Debug.Log($"  point = {data.PointValue}");
    if (data.HasRect)   Debug.Log($"  rect = {data.RectValue}");
}
```

`UnityEvent`s (such as `OnAnimationFinishedEvent`) can be wired to methods on other components in the inspector.

### Structure of the Received Event Data

```csharp
// UserData: only the values that are set have their HasXxx set to true
struct SpriteStudioUserData
{
    string PartName; int PartIndex;
    bool HasInt;    int    IntValue;
    bool HasString; string StringValue;
    bool HasRect;   Rect   RectValue;
    bool HasPoint;  Vector2 PointValue;
}

// Signal: parameters are typed (no boxing); read them via GetParam / TryGetParam (runtime only)
struct SpriteStudioSignal
{
    string PartName; int PartIndex;
    bool Active; string CommandId;
    int ParamCount;                                    // number of command parameters
    SpriteStudioSignalParam GetParam(int index);       // 0 <= index < ParamCount
    bool TryGetParam(string id, out SpriteStudioSignalParam p);
}
// One parameter: read the accessor matching Type (Bool / Index / Integer / Floating / Text)
struct SpriteStudioSignalParam
{
    string Id; SpriteStudioSignalParamType Type;
    bool AsBool; int AsIndex; int AsInteger; float AsFloating; string AsText;
}

// Audio: identifies the target sound by SoundName
struct SpriteStudioAudio
{
    string PartName; int PartIndex;
    uint SoundListNameHash; uint SoundNameHash;
    string SoundName; int LoopNum;
}
```

> [!NOTE]
> **About Audio events**: This plugin **notifies** the occurrence of Audio. Perform the actual sound playback (producing sound via `AudioSource`) on the application side. The `AudioClip` needed for playback is already resolved and held in `SpriteStudioAnimeAsset.Sounds`.

### Implementation Example: Continuous Animation Playback

An implementation example that automatically transitions to the "idle" animation when the "attack" animation finishes.

```csharp
void OnEnable()
{
    _player.OnAnimationFinished += HandleFinished;
}

void OnDisable()
{
    _player.OnAnimationFinished -= HandleFinished;
}

void HandleFinished()
{
    // Return to the idle state when the attack finishes
    if (_player.AnimationName == "attack")
    {
        _player.SetAnimation("idle");
        _player.Play();
    }
}
```

### Implementation Example: Triggering Events Using UserData

An implementation example that receives UserData set in SpriteStudio (playing footsteps, triggering attack hit detection, etc.) and processes it on the game side.

```csharp
void HandleUserData(SpriteStudioUserData data)
{
    // Example: branch processing based on a string
    if (data.HasString && data.StringValue == "play_footstep")
    {
        _audioSource.Play();
    }
    else if (data.HasString && data.StringValue == "attack_hit")
    {
        // Example of passing damage amount, etc. as an integer value
        if (data.HasInt) SpawnHitbox(data.IntValue);
    }
}
```

## Part Tracking (Following a Specified Part)

A feature that makes a user-provided `Transform` (weapon, effect, hit detection, etc.) follow a specified part every frame. The player does not create GameObjects; it is a linkage method that drives registered `Transform`s.

Configure it by adding entries to the **Part Trackers** list of `SpriteStudioPlayer`.

| Setting | Description |
|---|---|
| **Part Name** | The name of the part to follow (the PartData name in the `.ssab`) |
| **Target** | The `Transform` to make follow. It can be directly under the player or in a separate hierarchy |
| **Drive Scale** | When ON, inherited scale (excluding localScale) is also reflected. OFF (default) is position and rotation only |
| **On Hide** | Behavior on frames where the part is hidden. `FollowAlways` (keep following) / `Deactivate` (hide the target) |

- Placing the target as a **child** of the player makes the local transform match exactly. When placed in a separate hierarchy, world position and rotation match, and scale is approximate.
- If you specify a part name that does not exist in the animation, the target is automatically deactivated.

## Part Overrides (Color / Cell / Visibility)

Per-part runtime overrides let a script say "make this part this color / this cell / hidden **now**". An override wins over both the keyframe and any animation blending, so it does not have to fight the animation. Available on both `SpriteStudioPlayer` and `SpriteStudioGraphic`.

```csharp
var player = GetComponent<SpriteStudioPlayer>();

// Tint a part red (multiply). Applies to normal (image) parts.
player.SetPartColorOverride("body", SpriteStudioBlendOp.Mul, Color.red);

// Make a part draw a different cell (cellmap name is written without ".ssce").
player.SetPartCellOverride("body", "Ringo", "effect3");

// Force-hide a part, cascading to its descendants.
player.SetPartVisibilityOverride("body", hide: true, cascade: true);

// Revert
player.ClearPartColorOverride("body");
player.ClearAllPartOverrides();
```

| Method | Description |
|---|---|
| `FindPartIndex(name)` | Part index, or `-1` if the part is not in the asset |
| `SetPartColorOverride(part, blendOp, color, priority)` | Color override (single color, or a four-corner overload: left-top, right-top, left-bottom, right-bottom) |
| `SetPartCellOverride(part, cellmapName, cellName, priority)` | Draw a different cell |
| `SetPartVisibilityOverride(part, hide, cascade)` | Force-hide (`hide: false` reverts to the animation) |
| `ClearPartColorOverride` / `ClearPartCellOverride` / `ClearPartVisibilityOverride` | Clear one override on one part |
| `ClearAllPartOverrides()` | Clear every override on the player |
| `GetCellMapNames()` / `GetCellNames(cellmap)` | Discover the cellmap / cell names you can pass to a cell override |

Every method takes either a **part name** or a **part index**, and returns `false` when the part is unknown or the runtime rejects the call. Part indices are stable within one asset, so resolve a name once with `FindPartIndex` and reuse the index if you set overrides frequently.

### Priority mode

Color and cell overrides conflict with the animation, so they take a `SpriteStudioOverridePriority` (visibility does not — it is a plain force-hide flag, and any new animation clears it):

| Priority | Behavior |
|---|---|
| `OverwriteOnNextKeyframe` | The keyframe reclaims the attribute on any frame the animation drives it, so the override only shows on frames the animation leaves that attribute alone |
| `HoldUntilNextAnimation` (default) | The override wins for the current animation and is cleared when a new animation is set up |
| `Permanent` | The override wins and survives animation changes |

### Notes

- **Color** applies to normal parts, **cell** to normal and mask parts; other part types silently ignore the override (the call still returns `true`).
- Colors are interpreted in the same 8-bit sRGB space as the authored Part Color, and alpha is pre-multiplied by the runtime — pass the color as authored, without converting it yourself.
- A cell override is resolved when you set it, so an unknown cellmap / cell name fails immediately (returns `false`).
- Overrides live on the runtime, which owns their lifecycle. Do not re-apply them after an animation change; choose the priority mode that expresses what you want instead.

## Driving from Timeline

`SpriteStudioPlayer` implements `ITimeControl`, allowing playback time to be controlled from a Unity Timeline track. Timeline tracks / assets are also included.
