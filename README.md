[**日本語**](./README.ja.md) | [**English**](./README.md)

# SpriteStudioPlayer for Unity

**Professional 2D animations for your Unity games. A plugin that balances intuitive usability with extreme performance.**

A high-performance plugin for playing back animations (`.ssab`) created with **[OPTPiX SpriteStudio 7](https://www.webtech.co.jp/spritestudio/)** inside [Unity](https://unity.com/). By combining Unity's powerful features with the expressive capabilities of a dedicated animation tool, it easily integrates raster-based 2D animations into your Unity projects.

## ✨ Why use SpriteStudio with Unity?

- **A purpose-built 2D animation editor, beyond Unity's built-in tools**
  SpriteStudio 7 is a dedicated editor for skeletal- and mesh-based 2D animation — bone rigging, mesh deformation, and particle effects made for animators. It unlocks expression that's tedious or impractical to build with Unity's native animation tools, and since the motion is authored outside Unity and exported as a `.ssab`, artists can iterate in their own tool without touching the engine.
- **Characters, effects, and whole scenes — all in one editor**
  Particle effects are a first-class part type, authored on the same timeline as your characters — there's no separate effect system to bolt on. And you're not limited to a single rigged character: combine characters, backgrounds, effects, and UI into an entire cutscene or full-screen sequence, and play it back in Unity as one animation.
- **Renders in both world space and on a uGUI Canvas**
  `SpriteStudioPlayer` draws in world space (MeshRenderer) and `SpriteStudioGraphic` draws on a uGUI Canvas (CanvasRenderer) — the same animation drops into either context like any other component, driven from C# or sequenced on Timeline. On a Canvas it automatically follows uGUI **Mask** / **RectMask2D**.
- **A seamless SpriteStudio ⇄ Unity asset pipeline**
  Drop a `.sspj` into Unity and it's automatically converted to an optimized `.ssab` and a ready-to-use `SpriteStudioAnimeAsset`; re-edit in SpriteStudio and your Unity assets update instantly. Because the animation lives in assets separate from the scene, you avoid the Git conflicts that come with scene-embedded animation.
- **A low-memory, high-performance native runtime**
  Assets are stored as FlatBuffers, so there's zero parsing at runtime and instant playback straight from memory. A Rust core with SIMD, combined with Unity's Job System (Burst) and zero-allocation mesh updates, keeps CPU and memory overhead low — staying smooth even on mobile or when rendering large numbers of characters. Sub-frame interpolation keeps playback fluid even on high-refresh-rate displays.

## 📚 Documentation

For detailed instructions, please refer to the documents in the `docs/` folder. The documentation is structured step-by-step according to the reader's needs.

- [**Documentation site (hosted)**](https://cri-middleware.github.io/SSPlayerForUnity/) — 🚧 live after the first release
- [**Documentation (English)**](./docs/en/index.md)
- [**ドキュメント (日本語)**](./docs/ja/index.md)
- [**SpriteStudio Docs (portal)**](https://cri-middleware.github.io/SpriteStudio-Docs/) — the SDK and every official player in one place — 🚧 live after the first release

### Quick Links
- [Installation](./docs/en/setup/install.md)
- [Basic Usage](./docs/en/workflow/usage_basic.md)
- [Asset Pipeline and Editor Integration](./docs/en/workflow/usage_asset_pipeline.md)
- [Scripting and Events](./docs/en/workflow/usage_scripting.md)
- [Import Automation](./docs/en/workflow/import.md)
- [Performance Tuning and Advanced Settings](./docs/en/workflow/tips.md)
- [Build Guide](./docs/en/setup/build.md)

## 🚀 Quick Start

We provide multiple paths depending on your needs. This player supports Unity 6.3 LTS and later, and works in both URP and BRP.

### 1. Import into your Project
1. **Install**: Download the package tarball from GitHub Releases (stable link, always the latest release) — [`jp.co.cri-mw.spritestudio.ssplayer-unity.tgz`](https://github.com/cri-middleware/SSPlayerForUnity/releases/latest/download/jp.co.cri-mw.spritestudio.ssplayer-unity.tgz) — then in Unity's Package Manager choose **Add package from tarball...** and select it. *(Package Manager's "Add package from URL..." / git URL does not work for this package, since the native binaries ship only in the tarball.)*
2. **Import**: Open **Window > SpriteStudio > Importer** from the Unity menu, then drag and drop your `.sspj` file to convert it automatically.
3. **Place**: Drag the generated `SpriteStudioAnimeAsset` from the Project window into the Hierarchy (or Scene view), pick `SpriteStudioPlayer` (world space) or `SpriteStudioGraphic` (uGUI Canvas), then set the **Animation Name** and press Play.

### 2. Try out the Sample Project
`examples/SampleProject` has the SpriteStudio package already linked and ships the SDK's **Ringo** animation, so you do not need a `.sspj` of your own to see something move.
1. **Get the Repo**: Clone this repository with `--recurse-submodules` (the sample's source project lives in `SpriteStudio-SDK/tests`).
2. **Prepare**: Run `./scripts/download-sdk.sh` (Mac/Linux) or `scripts\download-sdk.ps1` (Windows) to fetch and place the native library (`libssruntime`).
3. **Open**: Open `examples/SampleProject` in Unity Hub.
4. **Try it**: Open `Assets/Test.unity` and press Play.

The SDK's other four test projects (`overall`, `allAttributeV7`, `allPartsV7`, `ParticleEffect`) are not bundled — they are large, and the submodule already carries them. Drag any of them from `SpriteStudio-SDK/tests` onto **Window > SpriteStudio > Importer** when you want them.

For advanced setups like compiling from source or modifying the package, please refer to the [Installation Guide](./docs/en/setup/install.md).

## 💡 Overview

This plugin features a **powerful asset pipeline that seamlessly integrates SpriteStudio and the Unity Editor**, enabling automatic conversion and instant updates of your assets.

For details such as data flow diagrams, key features, and system requirements, please refer to the **[Documentation (English)](./docs/en/index.md)**.

## 🎬 Samples

Sample projects are available under the [examples folder](./examples/).

- [SampleProject](./examples/SampleProject) — The one to open. It bundles the SDK's Ringo animation, so it plays something without a `.sspj` of your own. Run `download-sdk` first (see Quick Start). Maintainers: `./scripts/deploy-examples.sh` regenerates the bundled Ringo output from the submodule, and should be run whenever `SpriteStudio-SDK/tests/Ringo` changes.
- [UnityTestProject](./examples/UnityTestProject) — A blank project with the package linked and nothing else. It is the scratch project used while developing the plugin; import your own `.sspj` into it to try something in a clean environment.

## 🔗 Related Repositories

- [SpriteStudio-SDK](https://github.com/cri-middleware/SpriteStudio-SDK) — The core SDK providing `libssruntime` and `libssconverter`.

## 📄 License

This project is licensed under the BSD-3-Clause License. See the [LICENSE.md](./LICENSE.md) file for details.

For the licenses of the bundled `SpriteStudio-SDK` and other third-party libraries used in this project (such as FlatBuffers), please refer to [THIRD_PARTY_NOTICES.md](./THIRD_PARTY_NOTICES.md).
