# 🎮 SpriteStudio Player for Unity — Documentation

A plugin for playing back animations created with [SpriteStudio](https://www.webtech.co.jp/spritestudio/) on [Unity](https://unity.com/).
Drop a `.sspj` onto the **SS Importer** window, drag the asset it makes into the Hierarchy, and press Play — no code to reach the first frame.

!!! tip "No SpriteStudio data of your own yet?"
    The [official sample data](https://www.webtech.co.jp/help/ja/spritestudio7/download/sample/) works as-is — that page is
    Japanese only, but the downloads themselves are language-neutral.
    [Ringo](https://www.webtech.co.jp/help/ja/spritestudio7/download/sample/#ringo) is a good character sample, and
    [Particle](https://www.webtech.co.jp/help/ja/spritestudio7/download/sample/#Perticle_sample) is a good one if you want effects.

## Contents

This documentation is organized by purpose, as follows.

- **For Users** — Install the package and play back animations
    - [Installation](./setup/install.md): Adding the package to your Unity project
    - [Basic Usage](./workflow/usage_basic.md): Placing components and playing back
    - [Asset Import and Editor Integration](./workflow/usage_asset_pipeline.md): Start here for your first `.sspj` import
    - [Generated Assets and AssetBundle](./workflow/generated_assets.md): Per-file roles, dependencies, and how to split bundles
    - [Script Control and Events](./workflow/usage_scripting.md): Control from C# / Timeline and receiving events
    - [Motion Blending](./workflow/motion_blending.md): Layering animations on tracks and cross-fading between them
    - [Performance Tuning and Advanced Settings](./workflow/tips.md): Settings for large-scale rendering and optimization
    - [Migration from SS6Player](./migration_from_ss6.md): Migration guide from the legacy SS6Player for Unity
- **Customization & Automation** — Integrate with CLI and CI/CD
    - [Conversion Features and CI/CD](./workflow/import.md): Batch conversion and automation with `ssconverter-cli`
- **[Limitations & Scope](./limitations.md)** — What is not yet implemented, and the constraints to know before shipping
- **For Player & SDK Developers** — Build the native components from source, and find your way around the player
    - [Build Guide](./development/build.md): How to build `libssruntime` / `libssconverter`, use a changed package in a project and check it in Unity
    - [How the Player Is Put Together](./development/architecture.md): The layers, and why the player is built the way it is

## Key Features

This plugin is designed to fully bring out the expressiveness of SpriteStudio on Unity while delivering top-class performance.

*   **Almost Full Feature Support:** Supports the diverse expressions of SpriteStudio out of the box, including bone hierarchies, mesh & deform, particle effects, extended blending, and masks.
*   **An Importer in the Editor:** Drop a `.sspj` onto the SS Importer window (**Window > SpriteStudio > Importer**) and it becomes a `SpriteStudioAnimeAsset`; after editing in SpriteStudio, **Reconvert** brings the change in.
*   **No Per-Frame Garbage:** Playback allocates nothing on the managed heap each frame, so a scene full of characters does not stutter on garbage collection, on mobile included.
*   **Smooth Slow Motion:** Supports sub-frame interpolation, enabling smooth, stutter-free playback even on high-refresh-rate monitors and in slow-motion effects.
*   **Flexible UI and World Rendering:** Supports rendering in ordinary scene space (`SpriteStudioPlayer`) as well as on a uGUI (`SpriteStudioGraphic`) Canvas, and automatically follows Mask and RectMask2D.
*   **Timeline Integration:** Animations can be controlled directly from Timeline tracks, making cutscenes and complex staging easy to implement.
*   **Signals and Events:** Receive "user data" and "signals" set on the timeline as Unity events, allowing you to trigger SE playback and scripts with precise timing.
*   **Motion Blending:** Lay animations over one another on tracks and cross-fade between them, so a character slides from one motion into the next instead of cutting. See [Motion Blending](./workflow/motion_blending.md).
*   **Dynamic Part Following:** Any GameObject can follow (Constraint) a specific part of a character, making it easy to swap weapons or attach effects.
*   **Dynamic Texture Replacement (Avatar Customization / CellMap Overrides):** Swap a cellmap's texture at runtime, per player, to implement color variations or costume / equipment changes for a character.

## Overview

The flow of playing back an animation using this plugin is shown below.

```mermaid
graph LR
    SS[" .sspj / Images<br>(Source Assets) "]

    subgraph Convert ["Conversion Process"]
        IMP[[" SS Importer window<br>(Built into Unity Editor) "]]
        CLI[[" ssconverter-cli<br>(CLI Tool) "]]
    end

    subgraph Asset ["Import Artifacts (Assets/)"]
        BIN[" .ssab / .ssqb "]
        SO[[ SpriteStudioAnimeAsset ]]
    end

    subgraph Runtime ["Your Scene"]
        NODE[[ SpriteStudioPlayer /<br>SpriteStudioGraphic ]]
    end

    SS -- "Drag & Drop" --> IMP
    SS -- "CI/CD or Manual" --> CLI
    IMP -. "Auto Convert" .-> BIN
    CLI -. "Generate" .-> BIN

    BIN -. "Imported" .-> SO
    SO -- "Set" --> NODE
    NODE --> RENDER{{" Screen "}}

    classDef generated stroke-dasharray: 5 5;
    class BIN,SO generated;
```

- **Editor**: The SS Importer window converts a `.sspj` to `.ssab` / `.ssqb` and generates a `SpriteStudioAnimeAsset` that bundles textures, materials, animation names, external references, and more. `ssconverter-cli` produces the same files in CI.
- **Runtime**: `SpriteStudioPlayer` draws the asset in the scene, and `SpriteStudioGraphic` on a uGUI Canvas.

## License

This project is released under the BSD-3-Clause license. For details, see the [License](./license.md).

## Third-Party Notices

This project uses third-party open source libraries. For details, see the [Third-Party Notices](./third_party_notices.md).

## Related Repositories

- [SpriteStudio Docs](https://cri-middleware.github.io/SpriteStudio-Docs/) — the documentation portal for the SDK and its official players.
- [SpriteStudio-SDK](https://github.com/cri-middleware/SpriteStudio-SDK) — the SDK itself, providing `libssruntime` / `libssconverter`.
- [SSConverterGUI](https://github.com/cri-middleware/SSConverterGUI) — a standalone desktop GUI for converting `.sspj` without a player.
