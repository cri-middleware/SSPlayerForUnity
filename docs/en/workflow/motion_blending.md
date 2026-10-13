# 🎚️ Motion Blending

One player can play several animations at once: lay a limp over a walk, nod the head by a tracked value, or slide from idle into an attack instead of cutting. Animations are stacked on **tracks**, and the runtime folds them into one pose. Nothing changes in the `.ssab`; every animation comes from the asset assigned to the player.

Everything on this page is a method of both `SpriteStudioPlayer` and `SpriteStudioGraphic`, with the same names and the same behaviour.

## The track stack

Track 0 is the animation `SetAnimation()` selects. Tracks above it are laid over it in order, each on top of the pose the tracks below produced:

```
result = setup pose (the bare pose the animation's .ssae defines)
track 0  ->  laid on   (the main animation: SetAnimation)
track 1  ->  laid on   (an overlay)
track 2  ->  laid on   (another one)
```

Two numbers adjust a stack, and **which one you reach for matters**:

| | What it does | Per |
|---|---|---|
| **alpha** (`SetTrackAlpha`) | How much of the track lays over what is below. **This is the strength control.** | track |
| **weight** (`SetSourceWeight`) | The proportions between several animations sharing **one** track | source |

A track only acts on what its animation keys. An overlay that keys only the head leaves the legs alone, with no mask to set up — so **an overlay reaches exactly as far as it was animated**.

Track 0 carries the **structure**: cells, part colour, shaders, flips, hide, draw priority and textures all come from track 0's main animation, as do the events by default. Overlays blend the continuous attributes (position, rotation, scale, alpha and the rest).

## Laying an overlay

```csharp
[SerializeField] SpriteStudioPlayer _player;

void Start()
{
    _player.SetAnimation("idle");               // track 0
    _player.SetTrackAnimation(1, "look_around");
    _player.SetTrackAlpha(1, 0.4f);             // 40 %
    _player.Play();
}
```

Tracks are created as needed: setting track 3 creates tracks 1 and 2 empty. `alpha` is not clamped to 0–1; above 1 it extrapolates.

### Additive tracks

A `SpriteStudioTrackBlend.Additive` track adds its offset **from the setup pose** instead of pulling toward its own pose, so what is below keeps showing through. It suits an axis driven by a value — author the axis so that its middle frame is the setup pose, then scrub it:

```csharp
_player.SetTrackAnimation(1, "lean");
_player.SetTrackBlend(1, SpriteStudioTrackBlend.Additive);
_player.SetTrackSpeed(1, 0f);                   // driven by hand, not by the clock

void Update()
{
    _player.SetTrackFrame(1, 30f + _yaw * 30f);
}
```

## Switching with a crossfade

`Crossfade()` fades a track from what it plays to another animation. The incoming animation takes the track over **from the call on**; everything the track was playing fades out together, keeping its proportions, and is released when the fade completes. Durations are in **seconds**, as `Time.deltaTime` counts them:

```csharp
_player.Crossfade(0, "run", 0.3f);
```

| Case | What happens |
|---|---|
| A track playing nothing | No fade: the animation starts at once |
| A track already fading | The fade in flight is taken over as it stands, so the pose does not jump |
| A track mixing several sources, or left part-way by `CancelTrackFade` | Everything on it fades out together |
| `duration` of 0 or less, or a name the asset does not have | Refused: `false` |
| Stopping a fade part-way | `CancelTrackFade(track)` — alpha and weight stay where they are |

`GetTrackFadeProgress(track)` reads 0 to 1 during a fade and **-1** when the track is not fading.

`FadeTrack()` is the other fade: it changes how much a track counts, not what it plays. Fading track 0 to 0 settles the character onto the setup pose:

```csharp
_player.FadeTrack(1, 0f, 0.2f);                 // fade the overlay out over 0.2 s
```

### What a crossfade on track 0 changes, and when

The incoming animation takes track 0 over **when the fade begins**, not when it ends:

- `CurrentAnimation` — and the inspector's **Animation Name** — name it straight after the call. `OnAnimationStarted` is not raised: the switch carries on track 0's play state. `SetAnimation()` with the name the fade left behind switches back; with the incoming name it changes nothing, and the fade carries on.
- Its cells, hide and the other values that cannot be blended show from the first frame of the fade. `IsPlaying`, `IsFinished`, `JustLooped`, `FrameNo` and `OnAnimationFinished` / `OnAnimationLooped` report it: the outgoing animation running out during the fade does not finish the character.
- It plays with the player's `SpeedScale`, `FrameRate`, `FrameSkipEnabled`, playback direction and `LoopCount`, as if `SetAnimation()` had switched to it. Its section is its own whole length.
- Its events fire from the start of the fade, and the outgoing animation's stop — see [Events](#events) below.

`SetTrackAnimation(0, ...)` switches track 0 the same way, without the fade.

## Mixing animations on one track

To blend by proportion — a walk and a run by the character's speed — put both on one track and set their weights:

```csharp
_player.SetAnimation("walk");
int walk = _player.GetTrackMainSource(0);
int run = _player.AddTrackSource(0, "run", 0f);

void Update()
{
    _player.SetSourceWeight(walk, 1f - _speedRatio);
    _player.SetSourceWeight(run, _speedRatio);
}
```

Each animation on a track is a **source**, named by an integer handle (`-1` for none; `0` is a valid handle). A handle stays valid until its source is released — a crossfade completing, `ClearTrack()`, or `SetAnimation()` to another animation — and is then reused, so do not keep one past its source. Weights are ratios: `(0.7, 0.3)` and `(7, 3)` mix the same.

## Starting in phase (`trackSync`)

An animation laid on a track starts from the head of its section. Laid over a walk half-way through its stride, a run puts its feet somewhere else. Set the track to `SpriteStudioTrackSync.Start` **before** laying, and what is laid next starts at the **phase** track 0's animation stands at — how far through its section it is, as a proportion — so animations of different lengths line up:

```csharp
_player.SetTrackSync(0, SpriteStudioTrackSync.Start);
_player.Crossfade(0, "run", 0.3f);              // the run starts in step with the walk
```

| | `SpriteStudioTrackSync.Free` (default) | `SpriteStudioTrackSync.Start` |
|---|---|---|
| Where a laid animation starts | The head of its section | Track 0's phase |
| Suited to | One-shot motions (a blink, an attack) | Cyclic motions laid together (walk and run) |

It applies to `SetTrackAnimation`, `Crossfade` (track 0 included, where the incoming animation starts at the outgoing one's phase) and `AddTrackSource`. It aligns once, when the animation is laid; both then run on the same clock, so they stay together only while their lengths and speeds match. A track that has nothing on it yet can take the setting.

## One clock for the whole character

Every track runs on the character's one clock, and the playback controls act on all of it:

| Call | Effect on the stack |
|---|---|
| `Play()` | Heads **track 0** out to the start of its section and starts the clock. The other tracks keep their playheads |
| `Pause()` / `Resume()` | Hold / release every track and every fade |
| `Stop()` | Stops the whole character where it stands. Only `Play()` restarts it |

- **Track 0 finishing does not stop the clock.** Overlays and fades carry on after track 0 has played its last loop. `IsPlaying`, `IsPausing`, `IsFinished` and the `OnAnimation*` events describe track 0, and report its end as usual.
- The playback section is track 0's alone. The other playback settings are each track's — see the next section.

## A track's playback settings

The speed, the frame-rate override, frame skip and the playback direction belong to the **track**, not to the animation on it: they reach every animation on the track, both sides of a crossfade included, and every one laid on it later. Track 0's are the player's own `SpeedScale`, `FrameRate`, `FrameSkipEnabled` and `SetPlaybackDirection()` — `SetTrackSpeed(0, ...)` and `SpeedScale` set the same value.

```csharp
_player.SetTrackSpeed(1, 0.5f);
_player.SetTrackFrameRate(1, 24);
_player.SetTrackPlaybackDirection(1,
    SpriteStudioPlaybackDirection.Forward,
    SpriteStudioPlaybackStyle.PingPong);
```

The **loop count** is the animation's instead: an animation laid on a track loops without end, so state the count after laying it. A track that has played its count out holds its last frame and stays mixed in until you fade or clear it:

```csharp
_player.SetTrackAnimation(2, "hit_react");
_player.SetTrackLoopCount(2, 1);                // once, then hold the last frame

void Update()
{
    if (_player.IsTrackFinished(2)) _player.ClearTrack(2);
}
```

Track 0's loop count is `LoopCount` (`SetTrackLoopCount(0, ...)` writes the same), and the player puts it back on every animation track 0 switches to. `TrackJustLooped(track)` is true for the frame in which the track wrapped, as `JustLooped` is for track 0. Neither raises an event; only track 0's do (`OnAnimationFinished`, `OnAnimationLooped`).

## SetAnimation() folds the stack

`SetAnimation()` to a **different** animation puts the stack back to track 0 alone: every overlay, every source and every track setting (alpha, blend, sync, and the playback settings of the tracks above 0) goes with it. Lay the overlays again after switching. Selecting the animation already playing changes nothing. Editing **Animation Name** in the inspector during Play mode is a `SetAnimation()`, so it folds the stack too.

## Events

By default **only track 0's animation fires events** (`OnUserData`, `OnSignal`, `OnAudio`). Another source fires once its weight reaches its event threshold, which starts at `+Infinity`:

```csharp
int overlay = _player.GetTrackMainSource(1);
_player.SetSourceEventThreshold(overlay, 1f);   // fires while its weight is 1 or more
```

Every event carries the handle of the source that fired it in `Source` (`SpriteStudioUserData.Source`, `SpriteStudioSignal.Source`, `SpriteStudioAudio.Source`), so events from two sources can be told apart.

From the moment a crossfade on track 0 begins, the incoming animation is the one that fires, and the outgoing side falls silent. To keep the outgoing side firing for part of the fade, give the track an outgoing event threshold: the outgoing side fires while its share of the track, `1 - progress`, is at least the threshold.

```csharp
_player.SetTrackOutgoingEventThreshold(0, 0.5f);   // the outgoing side fires for the first half
```

The threshold is the track's setting and stays set. Its default, `+Infinity`, silences the outgoing side as the fade begins. **If you count events**, note that a crossfade therefore stops the outgoing animation's events at once.

## Timeline

While a Timeline drives the player, the clip decides track 0: it seeks the player rather than advancing it, so fades and overlays do not move, and a switch of track 0 made from a script is undone at the clip's next evaluation. Use motion blending from scripts on a player the Timeline is not driving.

## Limits

- **One `.ssab`.** Only the animations of the asset assigned to the player can be stacked.
- **Instance and Effect parts are outside motion blending.** They belong to the `.ssab`, not to one animation, so a crossfade on track 0 keeps them running instead of setting them up again. Which key drives such a part across the switch is not blended: where both animations key it on the same frame, it may carry on with the outgoing animation's setting.
- **Give every stacked animation the same cell on a part.** A cell's size and origin come from track 0, so a different cell on an overlay distorts the part.
- See also the runtime's own [animation mixing guide](https://github.com/cri-middleware/SpriteStudio-SDK/blob/HEAD/libs/ssruntime/docs/50_features/10_animation_mixing.md), which every official player follows.
