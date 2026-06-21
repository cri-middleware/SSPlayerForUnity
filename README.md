[**日本語**](./README.ja.md) | [**English**](./README.md)

# SpriteStudioPlayer for Unity

A high-performance plugin for playing back 2D animations created with [OPTPiX SpriteStudio 7](https://www.webtech.co.jp/spritestudio/) inside [Unity](https://unity.com/).
This plugin easily integrates raster-based 2D animations into your Unity projects with extreme performance.

## Documentation

For detailed instructions, please refer to the documents in the `docs/` folder. The documentation is structured step-by-step according to the reader's needs.

- [**Documentation (Japanese)**](./docs/ja/index.md) (English documentation is a work in progress)

### Quick Links (Japanese)

**Setup**
- [Installation](./docs/ja/setup/install.md)
- [Build Guide](./docs/ja/setup/build.md)

**Workflow**
- [Basic Usage](./docs/ja/workflow/usage_basic.md)
- [Asset Pipeline & Editor Integration](./docs/ja/workflow/usage_asset_pipeline.md)
- [Scripting & Events](./docs/ja/workflow/usage_scripting.md)

**Advanced**
- [Import Automation](./docs/ja/workflow/import.md)
- [Performance Tuning](./docs/ja/workflow/tips.md)

## Quick Start

We provide multiple paths depending on your needs. This player supports Unity 6.3 LTS and later, and works in both URP and BRP.

### 1. Try out the Sample Project
1. **Get the Repo**: Clone or download this repository.
2. **Prepare**: Run `./scripts/setup-download.sh` (Mac/Linux) or `scripts\setup-download.bat` (Windows) to download the native libraries.
3. **Open**: Open `examples/UnityTestProject` in Unity Hub.
4. **Check**: Open the `sprs.unity` scene and press Play to instantly see the animations in action.

### 2. Import into your Project
1. **Install**: Open Unity's Package Manager, choose "Add package from URL..." and enter the package URL from GitHub Releases (or download the tarball directly).
2. **Import**: Open **Window > SpriteStudio > Importer** from the Unity menu, then drag and drop your `.sspj` file to convert it automatically.
3. **Play**: Attach `SpriteStudioPlayer` to a GameObject, assign the generated `SpriteStudioAnimeAsset`, and play!

For advanced setups like compiling from source or modifying the package, please refer to the [Installation Guide](./docs/ja/setup/install.md).

## Overview

This plugin features a **powerful asset pipeline that seamlessly integrates SpriteStudio and the Unity Editor**, enabling automatic conversion and instant updates of your assets.

For details such as data flow diagrams, key features, and system requirements, please refer to the **[Documentation (Japanese)](./docs/ja/index.md)**.

## License

This project is licensed under the BSD-3-Clause License. See the [LICENSE.md](./LICENSE.md) file for details.

## Third-Party Notices

This project uses third-party open-source libraries. Please refer to [THIRD_PARTY_NOTICES.md](./THIRD_PARTY_NOTICES.md) for details.

## Related Repositories

- [SpriteStudio-SDK](https://github.com/cri-middleware/SpriteStudio-SDK) — The core SDK providing `libssruntime` and `libssconverter`.
- [SSPlayerForGodot](https://github.com/SpriteStudio/SSPlayerForGodot) — Godot Engine player utilizing the same Rust core.
