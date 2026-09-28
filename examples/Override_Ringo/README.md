# Override_Ringo — per-part runtime overrides

A runnable Unity project that demonstrates the **Override Layer API**: per-part runtime overrides of
**color**, **cell** and **visibility**. An override wins over both the keyframed animation and any
animation blending, so a script can say "make this part this color / this cell / hidden **now**"
without fighting the animation.

- Unity **6000.3.25f1**, **URP**. (The player also runs on the Built-in Render Pipeline; only this
  project's render-pipeline settings are URP-specific.)
- The package is referenced from this repository through a local path
  (`Packages/manifest.json` → `file:../../../package`), so the project always builds against the
  working tree — no separate install step.

## Run it

1. Open `examples/Override_Ringo` in Unity.
2. Open `Assets/Scenes/Override_Ringo.unity` and press Play.

The Ringo animation keeps playing while the demo cycles through the three overrides, showing the
current step in the top-left corner:

| Step | What it does |
|---|---|
| 1. Color | Tints the `apple` part blue (Mix). Normal parts only. |
| 2. Visibility | Force-hides `apple` and `heta`. |
| 3. Cell | Swaps the `apple` part's sprite to the `effect3` cell. |
| 4. Restore | `ClearAllPartOverrides()` — back to the plain animation. |

## What to read

`Assets/Scripts/OverrideDemo.cs` is the whole demo. It resolves the part index once with
`FindPartIndex`, then drives the overrides from a coroutine. Which part / cell it touches is
editable on the component, so the same script works on other animations.

The API is documented in [the scripting guide](../../docs/en/workflow/usage_scripting.md#part-overrides-part-color--cell--visibility).

## Notes on the scene

- The camera is **orthographic with size 540**, and the player's **Pixels Per Unit is 1**, so one
  pixel of the animation maps to one world unit: at a 1920x1080 game view the animation is drawn at
  its authored size, pixel for pixel. Set the Game view to 1920x1080 to see it that way.
- Post-processing is left off. A color override is easiest to judge against an unprocessed frame.
