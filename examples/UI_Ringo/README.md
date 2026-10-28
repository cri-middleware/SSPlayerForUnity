# UI_Ringo — buttons, toggles and fit on a Canvas

A runnable Unity project that demonstrates the **UI integration**: Ringo as a uGUI button and a
toggle that show their state, a raycast filter that takes the pointer on the artwork only, and
`SpriteStudioGraphic`'s **Fit** inside a `RectTransform`.

- Unity **6000.3.25f1**, **URP**, the Input System. (The player also runs on the Built-in Render
  Pipeline; only this project's render-pipeline settings are URP-specific.)
- The package is referenced from this repository through a local path
  (`Packages/manifest.json` → `file:../../../package`), so the project always builds against the
  working tree — no separate install step.

## Run it

1. From the repository root, run `./scripts/download-sdk.sh` (the native libraries) and
   `./scripts/deploy-examples.sh` (the converted Ringo; no converted asset is committed) — `.ps1` on
   Windows. Do this **before** the project is first opened, or Unity deletes the committed `.meta`
   that carry the scene's references; see
   [CONTRIBUTING › Example projects](../../CONTRIBUTING.md#example-projects).
2. Open `examples/UI_Ringo` in Unity.
3. Open `Assets/Scenes/UI_Ringo.unity` and press Play.

| On screen | What to try |
|---|---|
| **RingoButton** (left) | Selected at the start, so it plays `wait2`, the focused state, until the arrow keys or a click select something else (uGUI does not move the focus with Tab). Hover: `walk`. Press: `attack1`. Untick **Interactable**: `dead`. Clicks on the transparent margin reach the background and are counted |
| **RingoToggle** (middle) | Takes the pointer on the `apple` part only. On: `joy`, and `dance` while hovered |
| **Reduced motion** | Every state holds its first frame; Ringo carries no `reduced_motion` label, so normal holds too |
| **Show outlines** | Draws each part's outline (`TryGetPartQuad`): green where the filter takes the pointer, yellow under the pointer |
| The row at the bottom | `Fit` = Contain, Cover (Align Top) and None in three rects; the slider sets their width |

Ringo has no labels named after the states, so the button and the toggle map each state to one of
its animations with **State Clips**. An animation authored for a button would carry `normal`,
`hover`, `pressed`, … labels instead and need no clips.

## What to read

`Assets/Scripts/UiDemo.cs` drives the panel and the status text; the button, the toggle and the
filters are set up in the scene's Inspector. `Assets/Scripts/PartOutlineOverlay.cs` draws the
outlines.

The components are documented in [Buttons & UI States](../../docs/en/workflow/ui_buttons.md), and
**Fit** in [Basic Usage](../../docs/en/workflow/usage_basic.md).
