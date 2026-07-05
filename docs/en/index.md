# SpriteStudioPlayer for Unity — Documentation

A high-performance plugin for playing back animations created with [OPTPiX SpriteStudio 7](https://www.webtech.co.jp/spritestudio/) on [Unity](https://unity.com/).
Playback is performed through `libssruntime`, the shared Rust core provided by [SpriteStudio-SDK](https://github.com/cri-middleware/SpriteStudio-SDK), and combined with Unity's Job System to achieve extreme performance.

## Contents

This documentation is organized by purpose, as follows.

- **For Users** — Install the package and play back animations
    - [Installation](./setup/install.md): Adding the package to your Unity project
    - [Basic Usage](./workflow/usage_basic.md): Placing components and playing back
    - [Asset Import and Editor Integration](./workflow/usage_asset_pipeline.md): Start here for your first `.sspj` import
    - [Script Control and Events](./workflow/usage_scripting.md): Control from C# / Timeline and receiving events
    - [Performance Tuning and Advanced Settings](./workflow/tips.md): Settings for large-scale rendering and optimization
- **Customization & Automation** — Integrate with CLI and CI/CD
    - [Conversion Features and CI/CD](./workflow/import.md): Batch conversion and automation with `ssconverter-cli`
- **For Player & SDK Developers** — Build the native components from source
    - [Build Guide](./setup/build.md): How to build `libssruntime` / `libssconverter`

## Key Features

This plugin is designed to fully bring out the expressiveness of SpriteStudio 7 on Unity while delivering top-class performance.

*   **Full Feature Support:** Supports the diverse expressions of SpriteStudio 7 out of the box, including bone hierarchies, mesh & deform, particle effects, extended blending, and masks.
*   **Seamless Integration and a Powerful Asset Pipeline:** Simply bring a `.sspj` file into the Unity editor and it is automatically converted in the background, ready to be used as a Unity prefab or ScriptableObject.
*   **Ultra-Fast, Zero-Allocation:** By combining a Rust-based core with Unity's Job System (Burst Compiler), per-frame GC allocations are completely eliminated. This delivers an extremely stable, high frame rate even in mobile environments that render many characters.
*   **Smooth Slow Motion:** Supports sub-frame interpolation, enabling smooth, stutter-free playback even on high-refresh-rate monitors and in slow-motion effects.
*   **Flexible UI and World Rendering:** Supports rendering in ordinary scene space (`SpriteStudioPlayer`) as well as on a uGUI (`SpriteStudioGraphic`) Canvas, and automatically follows Mask and RectMask2D.
*   **Timeline Integration:** Animations can be controlled directly from Timeline tracks, making cutscenes and complex staging easy to implement.
*   **Signals and Events:** Receive "user data" and "signals" set on the timeline as Unity events, allowing you to trigger SE playback and scripts with precise timing.
*   **Dynamic Part Following:** Any GameObject can follow (Constraint) a specific part of a character, making it easy to swap weapons or attach effects.

## Overview

The flow of playing back an animation using this plugin is shown below.

```mermaid
graph LR
    SS[" .sspj / Images<br>(Source Assets) "]

    subgraph Convert ["Conversion Process"]
        IMP[[" Importer Window<br>(Built into Unity Editor) "]]
        CLI[[" ssconverter-cli<br>(CLI Tool) "]]
    end

    subgraph Asset ["Import Artifacts (Assets/)"]
        BIN[" .ssab / .ssqb "]
        SO[[ SpriteStudioAnimeAsset ]]
    end

    subgraph Runtime ["Unity Runtime"]
        NODE[[ SpriteStudioPlayer /<br>SpriteStudioGraphic ]]
        RT(" libssruntime ")
    end

    SS -- "Drag & Drop" --> IMP
    SS -- "CI/CD or Manual" --> CLI
    IMP -. "Auto Convert" .-> BIN
    CLI -. "Generate" .-> BIN

    BIN -. "ScriptedImporter" .-> SO
    SO -- "Set" --> NODE
    NODE -. "FFI / Fast Playback" .-> RT
    NODE -- "Burst Job → Mesh" --> RENDER{{" Screen "}}

    classDef generated stroke-dasharray: 5 5;
    class BIN,SO generated;
```

- **Editor (at import time)**: Imports a `.sspj`, automatically converts it to `.ssab` / `.ssqb`, and generates a `SpriteStudioAnimeAsset` that bundles textures, materials, animation names, external references, and more. Pre-generation with `ssconverter-cli` is also possible.
- **Runtime (during playback)**: The player component delegates to the shared core `SpriteStudioPlaybackCore`, `libssruntime` computes the frame data, and a Burst job builds the vertices and uploads them directly to the Mesh.

## Supported Environment

| Item | Details |
|---|---|
| Unity Version | **Unity 6.3 LTS (6000.3)** or later |
| Render Pipeline | Universal (URP) / Built-in (BRP) |
| Dependencies | Burst / Mathematics / Collections / Timeline (all standard Unity packages) |
| Supported Platforms | Windows / macOS / Linux / Android / iOS / WebGL (for native libraries, see the [Build Guide](./setup/build.md)) |

> **Note:** The current package version is `7.0.0-alpha01`. The API may change without notice.

## License

This project is released under the BSD-3-Clause license. For details, see the [License](./license.md).

## Third-Party Notices

This project uses third-party open source libraries. For details, see the [Third-Party Notices](./third_party_notices.md).

## Related Repositories

- [SpriteStudio-SDK](https://github.com/cri-middleware/SpriteStudio-SDK) — The SDK itself, which provides `libssruntime` / `libssconverter`
