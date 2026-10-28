# 🔘 Buttons & UI States

Make a SpriteStudio animation a uGUI button or toggle that shows what it is doing — hovered, pressed, disabled, focused, on or off — and, for an irregular shape, takes the pointer on the artwork only. This page is for setting that up on a Canvas; the graphic itself is covered in [Basic Usage](./usage_basic.md).

The three components live in an assembly of their own, `SpriteStudio.UI`, in the namespace `SpriteStudio.UI`. Nothing in the player calls into them, so a graphic without them works as it always did.

| Component | Add it to | What it does |
|---|---|---|
| **SpriteStudio Button** | the `SpriteStudioGraphic` | A uGUI `Button` that plays a section of the animation for each state |
| **SpriteStudio Toggle** | the `SpriteStudioGraphic` | A uGUI `Toggle` that does the same, with `selected_` sections while it is on |
| **SpriteStudio Raycast Filter** | the `SpriteStudioGraphic` | Lets only the parts' outlines take the pointer, instead of the whole `RectTransform` |

## Setting up a button

1. Place a **SpriteStudio Graphic** on a Canvas and give it its **Anime Asset** and **Animation Name**, as in [Basic Usage](./usage_basic.md).
2. On the same GameObject, **Add Component > SpriteStudio > SpriteStudio Button** (or **SpriteStudio Toggle**). Its **Transition** starts at **None**: the animation shows the state, and a tint on top of it would show it twice.
3. Hook **On Click ()** up as for any `Button`.

While the game runs, the button plays the graphic: it selects the animation, sets the section, and plays and stops it. Leave the graphic's playback to the button; the button turns the graphic's **Auto Play** off when it wakes. In edit mode the graphic shows its usual preview.

## Authoring the states

In one animation, put a label named after each state: `normal`, `hover`, `pressed`, `disabled`, `focused` — lower case, matched exactly. A state plays from its label to the frame before the next label of any name (or to the end of the animation), looping.

| uGUI | Plays | Without that label |
|---|---|---|
| none of the below | `normal` | the whole animation |
| pointer over it | `hover` | `normal` |
| pointer held down on it, or **Submit** from a keyboard or gamepad | `pressed` | `hover`, then `normal` |
| not **Interactable** (also through a `CanvasGroup`) | `disabled` | `normal` |
| selected by navigation, or by a click until something else is | `focused` | `normal` |

When several hold at once, one is shown: `disabled`, then `pressed`, `hover`, `focused`, and `normal` when none does. uGUI's own Selected beats Highlighted; here hover beats focus, as on the other SpriteStudio Players. A change shows at once: the new section starts from its first frame, and the section that was playing is dropped.

The labels are read from the animation the graphic has in **Animation Name**. Selecting another one from a script (`SetAnimation`) makes the states read that one from then on.

### Toggles

A **SpriteStudio Toggle** that is on reads the labels with `selected_` in front: `selected_normal`, `selected_hover`, and so on. Without one, it tries the other `selected_` labels before the plain ones, so a toggle that is on still looks on while it is hovered:

| On, and | Tries, in order |
|---|---|
| hovered | `selected_hover`, `selected_normal`, `hover`, `normal` |
| pressed | `selected_pressed`, `pressed`, `selected_hover`, `selected_normal`, `hover`, `normal` |

A press goes to `pressed` before the other `selected_` labels: it lasts only while the pointer is down, and it shows the press was taken.

## Using animations you already have

**State Clips** replaces the label of a state with a whole animation (**Animation** only), a label of the same animation (**Label** only), or a label of another animation (both). Tick **Selected** for a toggle's `selected_` state. States that fall back to a state take its clip too: a clip for `hover` is what a press plays when there is no `pressed` label or clip.

```csharp
using SpriteStudio.UI;

_button.StateClips = new[]
{
    new SpriteStudioUiClip(SpriteStudioUiState.Normal, false, "idle", ""),   // the whole animation
    new SpriteStudioUiClip(SpriteStudioUiState.Hover, false, "", "over"),    // a label of the animation
    new SpriteStudioUiClip(SpriteStudioUiState.Pressed, false, "push", "down"), // a label of another one
};
```

A section a clip names runs to the frame before the next label too. A clip naming an animation or a label the asset does not have is reported once in the Console, and the state plays as if it had no clip. The graphic's **Fit** stays on the canvas of the animation the labels are read from, so a clip that plays an animation with a bigger canvas does not shrink the artwork.

## Reduced motion

Unity cannot read the operating system's reduced-motion setting, so the game sets **Reduced Motion** (`ReducedMotion`) from its own options. While it is on, each state holds the first frame of its section, stopped, and no user data, signal or audio event fires.

If the animation has a `reduced_motion` label, `normal` — and the states that fall back to it — plays that section instead, looping and with its events: a calm idle in place of a still one. Every other state, and every `selected_` section, stays held, so a press, a disabled button and a toggle that is on still look the part.

## Taking the pointer on the artwork only

A Canvas takes a click anywhere in a graphic's `RectTransform`, transparent margin included. Add **SpriteStudio Raycast Filter** to the graphic and only the parts' outlines take it; a click on the margin goes to whatever is behind.

- **Parts** empty: every part with an outline.
- **Parts** listing names: only those parts. A name the asset does not have is skipped.

The outlines are those of [`TryGetPartQuad`](./usage_scripting.md#part-outlines-and-hit-testing), on the frame being shown. The filter never widens the rect, and keyboard and gamepad navigation are not narrowed. uGUI asks a parent's filters too, so a graphic below the button in the hierarchy, such as a label, takes the pointer only over the same parts. It answers only when uGUI asks, so it costs nothing per frame.

## Screen readers

By default the animation is decoration: nothing is read for it. Unity builds no accessibility node for a uGUI control, so a game that builds its own `AccessibilityHierarchy` reads **Accessibility Label** (`AccessibilityLabel`) from the button or toggle for that control's node. The role and the actions belong to that node.

## The state rules from a script

`SpriteStudioUiStates` holds the rules on their own, for a control of your own: `Of` picks the state, `Chain` lists the labels a state is read from, `TryGetLabelSection` reads a label's section from a `SpriteStudioLabelTable` (`GetLabelTable` on the graphic), and `TryResolve` returns the section a state plays, clips and reduced motion included.

## Example project

`examples/UI_Ringo` has a button and a toggle made of Ringo, with clips naming Ringo's animations, a filter on each, a panel that switches interactable and reduced motion, the parts' outlines drawn over them, and a row of rects showing **Fit**. See its README for how to run it.
