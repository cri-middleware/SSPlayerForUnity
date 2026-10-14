# 📝 Script Control and Events

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

`SetAnimation()` loads the animation's data and **waits on the first frame** (it does not start playback). Calling `Play()` afterward starts playback from that point.

Your playback settings hold across the switch: `SpeedScale`, `FrameSkipEnabled`, a frame-rate override and `PlaybackDirection` / `PlaybackStyle` all describe the playback rather than the clip, so the runtime keeps them, and the component puts `LoopCount` back because that one is per-transition. What does reset is the playhead — it waits on the first frame — and the playback section, whose frame numbers mean nothing against a different animation.

## Playback Control API

| Member | Kind | Description |
|---|---|---|
| `SetAnimation(string animName)` | Method | Specify an animation and load its data (does not start playback). Returns `true` on success |
| `Play()` | Method | Start playing the set-up animation from the beginning |
| `Play(float startFrame)` | Method | Start playing the set-up animation from the specified frame |
| `Pause()` | Method | Hold playback on the current frame. Idempotent, and ignored while nothing is playing |
| `Resume()` | Method | Lift a `Pause()`, carrying on from the same frame. Idempotent. `Play()` rewinds to the beginning, so it cannot be used to resume |
| `Stop()` | Method | Stop playback. The playhead **stays where it stands** — nothing rewinds, so the frame it stopped on keeps drawing. `Play()` is what returns to the start |
| `StopAllSounds()` | Method | Stop every sound the built-in playback has started. Nothing else stops one (see the note under Event Notifications) |
| `IsPlaying` | `bool` (read) | Whether a playback session is active (`true` even while paused) |
| `IsPausing` | `bool` (read) | Whether currently paused |
| `IsFinished` | `bool` (read) | Whether every configured loop has played. A **state**, not a pulse — it latches on completion and holds until the next `Play()` / `SetAnimation()`, so `Play(); yield return new WaitUntil(() => player.IsFinished);` waits for the run you just started. Never true under an infinite `LoopCount`, and **not raised by `Stop()`** — a commanded stop is not a completion, which is what separates it from `!IsPlaying` |
| `IsPlayingForward` | `bool` (read) | Which way the playhead is **actually** travelling. `PlaybackDirection` is only the configured heading: on a `PlaybackStyle.PingPong` return leg this reads `false` while `PlaybackDirection` is still `Forward`. **Gate audio on it** — SpriteStudio has no reverse audio, so a key crossed on a backward leg is not meant to sound |
| `FrameNo` | `float` | Current frame. Use `set` to seek to an arbitrary frame (fractional allowed) |
| `TotalFrames` | `int` (read) | Frames in the range being played, both ends inclusive — the section when `SetAnimationSection` set one (`0` when not loaded) |
| `FrameRate` | `int` | Playback frame rate (fps). Use `set` to change at runtime. `0` or less is not a value but a **clearing of the override**: the rate falls back to the one the clip was authored at. The getter always reports the effective rate, so writing `0` and reading it back returns the clip's own fps (e.g. `60`) |
| `PlaybackDirection` | `enum` | The configured heading (`Forward` / `Backward`) |
| `PlaybackStyle` | `enum` | What the playhead does at the far end (`Normal` / `PingPong`) |
| `SetAnimationSection(int start, int end)` | Method | Limit the playback range by frame (frame-based; label specification is not supported) |

The players advance and build their meshes early in **LateUpdate** (`DefaultExecutionOrder(-100)`), after every `Update`, the Timeline and the Animator have run. A call made from your `Update` — `SetAnimation`, `Play`, a seek through `FrameNo` — is therefore drawn in the same frame, as a change to an Animator is; one made from your own `LateUpdate` is drawn in the next.

### Example: Pause and Resume

```csharp
_player.Pause();    // hold on the current frame
_player.Resume();   // carry on from it

// One-tap transport for a play/pause button:
if (_player.IsPausing) _player.Resume();
else if (_player.IsPlaying) _player.Pause();
else _player.Play();   // stopped — Play() starts over from the section start
```

### Example: Seek and Section Playback

```csharp
_player.SetAnimation("attack");
_player.Play();
_player.SetAnimationSection(10, 30); // Play only the section from frame 10 to 30
_player.FrameNo = 10f;          // Seek to the start of the section
```

### Example: Reverse and Ping-Pong

```csharp
_player.SetAnimation("walk");
_player.PlaybackDirection = SpriteStudioPlaybackDirection.Backward;
_player.PlaybackStyle = SpriteStudioPlaybackStyle.PingPong;
_player.Play();
```

> `PlaybackDirection` / `PlaybackStyle` survive a `SetAnimation()`, so set them once. They have no inspector default, so a component starts Forward / Normal until a script says otherwise. What a switch does reset is the heading inside a round trip: a new animation always starts on the outward leg. `SetPlaybackDirection(direction, style)` sets both in one call.

## Playback Parameter Properties

These properties correspond to the inspector items. Changing them at runtime is reflected in the runtime immediately. The inspector items can also be keyed in an AnimationClip or a Timeline Animation Track — **Flip X**, **Offset**, **Speed Scale** and so on — and the player takes each keyed value up after the evaluation, as a `SpriteRenderer` takes a keyed **Flip X**.

| Property | Type | Description |
|---|---|---|
| `AnimeAsset` | `SpriteStudioAnimeAsset` | Playback target asset (swappable) |
| `CurrentAnimation` | `string` | Animation name (read-only: switch it with `SetAnimation()`) |
| `AutoPlay` | `bool` | Auto-play at startup and each time the component is enabled again |
| `LoopCount` | `int` | Loop count (`-1` or `0` = infinite / `n` = n times) |
| `SpeedScale` | `float` | Playback speed multiplier. Read back from the runtime, which clamps `0` and negatives to a **stop** (reverse is `PlaybackDirection`'s job), so `-1` reads back as `0` |
| `SubFrameEnabled` | `bool` | Sub-frame interpolation |
| `FrameSkipEnabled` | `bool` | Real-time tracking (see [Advanced Settings](./tips.md)) |

`SpriteStudioPlayer` (world) only: `SortingLayerName` / `SortingOrder` / `PixelsPerUnit`, plus the three below.

| Property | Type | Description |
|---|---|---|
| `FlipX` | `bool` | Mirror the drawn animation horizontally, in the manner of `SpriteRenderer.flipX` |
| `FlipY` | `bool` | Mirror it vertically |
| `Offset` | `Vector2` | Shift it by this many **world units** (applied after `PixelsPerUnit`) |

All three change only what the player draws — the `Transform` is untouched, so the GameObject's children and
colliders stay where they are. That is the point: negating `transform.localScale.x` to mirror a character
drags its weapon mount, hitboxes and particle systems along with it, while `FlipX` does not.

```csharp
// Face the way we are walking without disturbing anything parented to us.
_player.FlipX = _velocity.x < 0f;
```

Registered part trackers follow the mirrored pose, so an object pinned to a part stays pinned across a flip.
A mirror is not a rotation, though, so the mirroring itself reaches the tracked object only when its
`driveScale` is on (it arrives as a negative scale); position is exact either way, but orientation comes
with caveats — see ["What mirroring costs you"](#what-mirroring-costs-you).

`SpriteStudioGraphic` (uGUI) does not expose these yet.

## Event Notifications

You can receive six types of events as the animation progresses. Both `C# events` and inspector-wireable `UnityEvent`s are provided.

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

Every event carries `FrameNo`: the frame its key is **authored** on, which is not necessarily the
frame it was noticed on — one tick can step across several, and every key in between fires on it.
`Source` is the handle of the animation source that fired it; it matters only once animations are
stacked — see [Motion Blending](./motion_blending.md#events).

```csharp
// UserData: only the values that are set have their HasXxx set to true
struct SpriteStudioUserData
{
    string PartName; int PartIndex; int FrameNo; int Source;
    bool HasInt;    int    IntValue;
    bool HasString; string StringValue;
    bool HasRect;   Rect   RectValue;
    bool HasPoint;  Vector2 PointValue;
}

// Signal: parameters are typed (no boxing); read them via GetParam / TryGetParam (runtime only)
struct SpriteStudioSignal
{
    string PartName; int PartIndex; int FrameNo; int Source;
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
    string PartName; int PartIndex; int FrameNo; int Source;
    uint SoundListNameHash; uint SoundNameHash;
    string SoundName; int LoopNum;
}
```

> [!IMPORTANT]
> **Lifetime of Signal parameters**: `GetParam` / `TryGetParam` return a view into a buffer owned and recycled by the player, which is how they avoid allocating and boxing. A `SpriteStudioSignal` and the `SpriteStudioSignalParam` values read from it are only valid until the next event poll on that player — usually the next frame. If you need a value after the callback returns (storing it in a field, reading it from a coroutine, queueing it), **copy the value itself** (`p.AsInteger`, `p.AsText`, …); keeping the `SpriteStudioSignal` around will report another signal's parameters later, with no error.

> [!NOTE]
> **About Audio events**: `Play Audio` is on by default, so the component already produces the sound itself through pooled `AudioSource` voices. `OnAudio` is an **observation** channel beside that — use it to react to a sound without taking it over, or turn `Play Audio` off and play it yourself, through audio middleware for instance. The `AudioClip` is already resolved and held in `SpriteStudioAnimeAsset.Sounds`; `SpriteStudioAnimeAsset.ResolveAudioClip(SoundListNameHash, SoundNameHash)` looks up the one an event names.
>
> A sound that has started **outlives the playback that triggered it**. `Pause()`, `Resume()`, `Stop()`, a `SetAnimation()` switch and the end of the animation all leave it running, and a key crossed again while its sound still plays starts another voice rather than cutting the first. Only disabling or destroying the component, or `StopAllSounds()`, stops one. SpriteStudio has no infinite loop — `loop_num` is a play count — so every sound ends on its own. If one outlasts its animation, that is the animation data's business, not the component's.
>
> Sounds play for the keys the playhead crosses. A seek made while playing — setting `FrameNo` — does not sound the keys on the frame it lands on, though their `OnAudio` events still fire; the start frame's keys do sound after `Play()`.

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
    if (_player.CurrentAnimation == "attack")
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

A feature that makes a user-provided `Transform` (weapon, effect, hit detection, etc.) follow a specified part every frame. The player neither creates nor destroys GameObjects; it only drives the registered `Transform`s (constraint style), and you own the target's lifecycle.

> This feature is specific to `SpriteStudioPlayer` (world). It is not available on `SpriteStudioGraphic` (Canvas).

> [!NOTE]
> **Mesh (skinned) parts cannot be followed directly.** A bone-bound mesh part (a deforming part such as a hand or a face) has its vertices drawn by bone skinning, while the part's own transform stays at its setup node position — usually near the character root. So naming such a part in Part Name snaps the target near the root rather than onto the visible art.
>
> To follow the position of a mesh part, add a **NULL part (a bone-point / empty part)** at that spot in SpriteStudio and target that part's name instead. A bone (armature) or joint part works the same way.

Configure it by adding entries to the **Part Trackers** list of `SpriteStudioPlayer`.

| Setting | Description |
|---|---|
| **Part Name** | The name of the part to follow (the PartData name in the `.ssab`). The inspector offers a dropdown populated from the asset's part names, with mesh (skinned) parts greyed out |
| **Target** | The `Transform` to make follow. It can be directly under the player or in a separate hierarchy |
| **Drive Position** | When ON (default), the target follows the part's position |
| **Drive Rotation** | When ON (default), the target turns with the part |
| **Drive Scale** | When ON, inherited scale is also reflected (the part's own localScale is excluded). OFF (default) leaves the target's scale untouched |
| **On Hide** | Behavior on frames where the part is hidden. `FollowAlways` (keep following; default) / `Deactivate` (deactivate the target) |

### Registering from a Script

Trackers can also be registered and removed at runtime instead of through the inspector.

```csharp
var player = GetComponent<SpriteStudioPlayer>();

// Register at runtime (position and rotation only, by default)
player.RegisterPartTracker("hand_R", weapon.transform);

// Also reflect inherited scale, and deactivate the target on hidden frames
player.RegisterPartTracker("effect_root", effect.transform,
    driveScale: true, onHide: SpriteStudioPartTrackerHide.Deactivate);

// Remove (deletes every entry targeting that Transform)
player.UnregisterPartTracker(weapon.transform);
```

| Method | Description |
|---|---|
| `RegisterPartTracker(partName, target, driveScale, onHide, drivePosition, driveRotation)` | Adds a tracker and returns the created entry (the last four are optional; `drivePosition` / `driveRotation` default on, `driveScale` off) |
| `UnregisterPartTracker(target)` | Removes every entry targeting that `Transform` and returns the number removed |
| `TryGetPartHidden(partName, out bool hidden)` | Whether the part is hidden on the current frame. Returns `false` if the part is unknown |
| `TryGetPartTransform(partName, out Matrix4x4 matrix)` | The part's transform for the current frame (player-local, with `1 / PixelsPerUnit` already folded in) |

> When you only need the part's position at a single moment (a projectile spawn point, for example) rather than continuous following, calling `TryGetPartTransform()` directly is simpler than registering a tracker.

### What mirroring costs you

Once a part's pose is **mirrored**, part of it stops fitting into a `Transform`. This happens with `FlipX` / `FlipY`, and equally when a part or one of its ancestors carries a negative scale authored in SpriteStudio. It is not a workaround away — it is what a `Transform` can and cannot hold — so design around it.

**Position is always exact.** What suffers is orientation, and whether the target is mirrored at all.

- **Do not read `matrix.rotation` or `matrix.lossyScale` off what `TryGetPartTransform()` returns.** A `Quaternion` cannot represent a mirrored pose, so both are **undefined** there — the values are meaningless, not merely imprecise. Check the sign of `matrix.determinant` first. If you only want the position, column 3 is fine as it is.
- **Never assume that sign.** Whether a pose is mirrored is the *product* of any mirroring authored on the part or its ancestors and the player's `FlipX` / `FlipY`. Two mirrorings cancel, so a part that is already mirrored comes back with a **positive** determinant once you flip the player. Neither "no flip, so it is positive" nor "flipped, so it is negative" holds. Check every time.
- **A target with `Drive Scale` off (the default) never receives the mirroring**, because a mirror can only be expressed as a negative scale. The target itself stays unmirrored, and **`FlipX` additionally leaves its orientation 180 degrees away**; `FlipY` does not. The two axes differ because the decomposition into rotation plus negative scale puts the sign on the Y axis: expressing a horizontal mirror that way costs an extra 180-degree rotation, and that is what survives in the rotation.
- **Turning `Drive Scale` on delivers the mirroring, but a negative scale has side effects in Unity.** Colliders in particular **do not honour negative scale** (a PhysX limitation), so the artwork mirrors while the hitbox stays as it was, silently. Particles and 3D mesh children are affected too, through simulation space and face winding.

For hitboxes, the reliable options are to **keep the collider shape symmetric** — position alone is exact, so nothing else matters — or to **handle mirroring in your own game code**.

### Timing and Accuracy

Trackers are driven in **LateUpdate**, after the frame's mesh is finalized and before the render phase, so a `Renderer` attached to the target updates within the same frame.

The pose is written as a local TRS in the target's **parent space**, so how exactly it matches depends on where the target is placed.

- Placed as a **child** of the player, the part's transform is written as-is — an exact match. (Skew deforms the drawn vertices only; it is not carried in the part's coordinate frame, so it never reaches a follower.)
- Placed in a separate hierarchy (anywhere that is not a child of the player), **world position is always exact**. However, if the target's parent or the player has both rotation and non-uniform scale, **rotation and scale become approximate**.

> **A target in a separate hierarchy can lag by one frame.** The tracker writes using the player's transform as sampled at drive time, so it does not follow a move made to the player after that until the next frame. Driving happens early in LateUpdate (`DefaultExecutionOrder(-100)`), so take particular care when your own `LateUpdate` moves the player. A target placed as a child always follows, through hierarchy inheritance.

When `Drive Scale` is ON, only the **inherited** scale is reflected; the part's own localScale is divided out. The world player is 2D, so only X / Y are written and the target's Z scale is preserved.

### Notes

- **The player deactivates the target in the two cases below**, and activates it again once the condition clears — only a target it deactivated itself. A target you deactivate yourself, such as a weapon kept out of sight until it is picked up, stays inactive. A target you deactivate while the player already has it deactivated still comes back when the condition clears: nothing on the target tells the two apart.
    - The part name does not exist in the current animation (always deactivated, regardless of the `On Hide` setting)
    - The part is hidden on this frame and `On Hide` is `Deactivate`

    Replacing a tracker's `target`, or removing the tracker with `UnregisterPartTracker`, activates the target it had deactivated again.
- Part names resolve against the parts of the animation the player itself is running. **Parts inside an Instance part (the child animation) cannot be specified** (the Instance part itself can).
- If several parts share a name, the first one found is used.
- Swapping `AnimeAsset` re-resolves part names automatically, so trackers do not need to be registered again.
- In edit mode, trackers are applied once as well, when the static start-frame preview is built. They move the target but never change its active state there.

## Part Overrides (Part Color / Cell / Visibility)

Per-part runtime overrides let a script say "make this part this color / this cell / hidden **now**". An override wins over both the keyframe and any animation blending, so it does not have to fight the animation. Available on both `SpriteStudioPlayer` and `SpriteStudioGraphic`.

```csharp
var player = GetComponent<SpriteStudioPlayer>();

// Tint a part red (multiply). Applies to normal (image) parts.
player.SetPartColorOverride("body", Color.red, SpriteStudioBlendOp.Mul);

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
| `GetPartNames()` | Part names in part-index order — what turns an event's `PartIndex` back into something a human reads, and what fills a part picker. An unnamed part holds its slot as `""` so the array stays index-aligned; `Length` is the part count |
| `FindPartIndex(name)` | Part index, or `-1` if the part is not in the asset |
| `SetPartColorOverride(part, color, blendOp, priority)` | Part Color override (single color). For a per-corner gradient use `SetPartColorOverrideCorners(part, SpriteStudioCornerColors, blendOp, priority)` (left-top, right-top, left-bottom, right-bottom) |
| `SetPartCellOverride(part, cellmapName, cellName, priority)` | Draw a different cell |
| `SetPartVisibilityOverride(part, hide, cascade)` | Force-hide (`hide: false` reverts to the animation) |
| `ClearPartColorOverride` / `ClearPartCellOverride` / `ClearPartVisibilityOverride` | Clear one override on one part |
| `ClearAllPartOverrides()` | Clear every override on the player |
| `GetCellmapNames()` / `GetCellNames(cellmap)` | Discover the cellmap / cell names you can pass to a cell override |

The `part` argument of every method takes either a **part name** or a **part index**, and the call returns `false` when the part is unknown or the runtime rejects it.

> **Using part indices**: A part index is stable within one asset (the same `.ssab`), so if you set overrides frequently, we recommend resolving the part name to a part index once with `FindPartIndex` and reusing that index.

### Priority mode

Part Color and cell overrides conflict with the animation, so they take a `SpriteStudioOverridePriority` (visibility does not — it is a plain force-hide flag, and any new animation clears it):

| Priority | Behavior |
|---|---|
| `OverwriteOnNextKeyframe` | The override applies until the animation data updates that attribute |
| `HoldUntilNextAnimation` (default) | The override wins for the current animation, and is cleared when a new animation is set up |
| `Permanent` | The override applies as long as the same animation data (`.ssab`) is loaded (it survives animation changes) |

### Notes

- **Color** applies to normal parts, **cell** to normal and mask parts; other part types silently ignore the override (the call still returns `true`).
- Colors are interpreted in the same 8-bit sRGB space as the authored Part Color, and alpha is pre-multiplied by the runtime — pass the color as authored, without converting it yourself.
- A cell override is resolved when you set it, so an unknown cellmap / cell name fails immediately (returns `false`).
- Overrides live on the runtime, which owns their lifecycle. Do not re-apply them after an animation change; choose the priority mode that expresses what you want instead.

## Dynamic Texture Replacement (CellMap Overrides / Avatar Customization)

Replace the texture bound for a cellmap (external texture), **at runtime and for that player only**. This lets you implement color variations or costume / equipment changes (avatar customization) without authoring a separate asset per outfit. The asset and other players are untouched. Works on both `SpriteStudioPlayer` and `SpriteStudioGraphic`. This is a **separate** feature from the Part Overrides above (see the note on when to use which, below).

### Example: Changing an Outfit

```csharp
var player = GetComponent<SpriteStudioPlayer>();

// The cellmap name is the SpriteStudio-defined name. Discover the replaceable names with
// GetReplaceableTextureNames(); they also appear under "Cellmap Texture Overrides" in the inspector.
Texture2D sailorUniform = ...; // replacement (should share the original's size / atlas layout)
player.SetCellmapTexture("Clothes 1", sailorUniform);

// Restore the original texture (pass null).
player.SetCellmapTexture("Clothes 1", null);
```

You do not need a separate asset per outfit: one animation drives an efficient avatar system where only the texture is swapped.

| Method | Description |
|---|---|
| `SetCellmapTexture(cellmapName, texture)` | Replace the texture for the named cellmap. Pass `null` to restore the original |
| `GetCellmapTexture(cellmapName)` | The current **replacement** value (`null` when none is set — it never returns the original texture) |
| `GetReplaceableTextureNames()` | The replaceable names (cellmap names + external-texture names). Read from the assigned asset; empty when none is assigned |

Replacements set in the inspector's **"Cellmap Texture Overrides"** (a name / texture pair) are saved into the scene and re-applied by name at runtime and across an asset swap.

> **When to use `SetCellmapTexture()` vs `SetPartCellOverride()`**: `SetCellmapTexture()` replaces a **whole cellmap (texture)** and affects every part that uses it at once (good for outfits / color variations). `SetPartCellOverride()` replaces the drawn cell (the artwork) for **a single part** (e.g. a facial expression). Use whichever matches your goal.

### Notes

- It is **per player**. The replacement is kept by name and re-resolves to the new resource's hashes across an asset swap. It **does not propagate into Instance (sub-animation) children** — a child is an independent player with its own textures, so set it on the child too if needed.
- The replacement texture should share the **original's size / atlas layout**. UVs (cell rects) stay in the original cellmap's pixel space, so a texture of a different resolution or layout samples a different region.
- The swap applies to **every draw that binds the texture, mask writers included**. If a `draw_as_mask` part uses that cellmap, the replacement's alpha changes the mask shape.
- The uGUI path (`SpriteStudioGraphic`) draws a single atlas only, so replacing a name that resolves to a non-first atlas will not show.

## Driving from Timeline

`SpriteStudioPlayer` can be driven from a Timeline in two ways (`SpriteStudioGraphic` cannot):

- **SpriteStudio Track** — add a `SpriteStudioTrack`, bind the `SpriteStudioPlayer` to it, and place clips on it. Each clip's **Animation Name** names the animation it plays. The track does not blend, so Timeline does not let its clips overlap. A clip sets its animation up stopped, so in the gap after a clip the player holds that clip's last frame until a script calls `Play()`.
- **Control Track** — `SpriteStudioPlayer` implements `ITimeControl`, so a Control Track clip on its GameObject seeks the animation set in **Animation Name**.

A clip's time counts from the animation's first frame. The player keeps its own **Loop Count**, **Speed Scale** and **Frame Skipping** through the clips, so a player set to play once still plays once when a script plays it after the cutscene.

A clip seeks the player rather than advancing it, so under a clip no events fire, no sound plays and loops do not apply — see [Limitations](../limitations.md#timeline).
