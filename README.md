[**English**](./README.md) | [**日本語**](./README.ja.md)

# SpriteStudio Player for Unity

**Professional 2D animations for your Unity games. A plugin that balances intuitive usability with extreme performance.**

A high-performance plugin for playing back animations (`.ssab`) created with **[SpriteStudio](https://www.webtech.co.jp/spritestudio/)** inside [Unity](https://unity.com/). By combining Unity's powerful features with the expressive capabilities of a dedicated animation tool, it easily integrates raster-based 2D animations into your Unity projects.

## ✨ Why use SpriteStudio with Unity?

- **Bring your characters to life.** Breathing and blinking, swaying hair and clothes, flashy slash and magic effects — the motion is built visually in a dedicated editor (SpriteStudio) with bone rigs, mesh deformation and particles. Artists work in their own tool, and Unity just plays the result.
- **Not just characters — effects, UI and whole scenes.** Particle effects sit on the same timeline as the character, so there is no separate effect system to set up. Combine characters, backgrounds, effects and UI into a cutscene or a full-screen sequence, and play it in Unity as a single animation.
- **In your scene or on your UI.** `SpriteStudioPlayer` draws in the world and `SpriteStudioGraphic` on a uGUI Canvas, where it follows **Mask** / **RectMask2D**. Both are controlled from C#, and `SpriteStudioPlayer` can be sequenced on Timeline.
- **A seamless SpriteStudio ⇄ Unity round trip.** Drag a `.sspj` into Unity and it becomes a ready-to-use asset. To tweak it, click **Open SSPJ** in the Inspector, save in SpriteStudio, then click **Reconvert** — no manual export step. The animation lives in its own asset rather than the scene, so teammates don't collide over scene merges in Git.
- **Zero-parse loading, SIMD and Burst playback.** `.ssab` is a FlatBuffers binary read in place, so loading involves no parsing. Each frame, a SIMD-optimised Rust core and Burst jobs build the meshes zero-copy, with no per-frame GC allocations — light enough for mobile and for scenes with many characters. Turn on **sub-frame interpolation** and motion follows high-refresh-rate displays.

## 📚 Documentation

For detailed instructions, see the documents in the `docs/` folder — structured step by step, and where the data-flow diagram, key features and system requirements live.

- [**Documentation site (hosted)**](https://cri-middleware.github.io/SSPlayerForUnity/)
- [**Documentation (English)**](./docs/en/index.md)
- [**ドキュメント (日本語)**](./docs/ja/index.md)
- [**For AI assistants**](https://cri-middleware.github.io/SSPlayerForUnity/llms.txt) — these docs as Markdown: `llms.txt` indexes the pages and `llms-full.txt` holds all of them in one file; the installed package carries the same pages in its `Documentation~/` folder
- [**SpriteStudio Docs (portal)**](https://cri-middleware.github.io/SpriteStudio-Docs/) — the SDK and every official player in one place

### Quick Links
- [Installation](./docs/en/setup/install.md)
- [Basic Usage](./docs/en/workflow/usage_basic.md)
- [Limitations & Scope](./docs/en/limitations.md)

## 🚀 Quick Start

**Using it in your game?** Read on — install the package, drag a `.sspj` onto the Importer, place a component.
Everything after that (scripting, events, per-part overrides, Timeline) is the same path further in, in the
[documentation](./docs/en/index.md).
**Working on the Player, or the Rust runtime under it?** [CONTRIBUTING.md](./CONTRIBUTING.md) and the [Build Guide](./docs/en/setup/build.md).

This player supports Unity 6.3 LTS and later, and works in both URP and BRP. Projects have to be authored in **SpriteStudio 7.5 or later**.

1. **Install**: Download the package tarball from GitHub Releases (stable link, always the latest release) — [`jp.co.cri-mw.spritestudio.ssplayer-unity.tgz`](https://github.com/cri-middleware/SSPlayerForUnity/releases/latest/download/jp.co.cri-mw.spritestudio.ssplayer-unity.tgz) — then in Unity's Package Manager choose **Add package from tarball...** and select it. *(Package Manager's "Add package from URL..." / git URL does not work for this package, since the native binaries ship only in the tarball.)*
2. **Import**: Open **Window > SpriteStudio > Importer** from the Unity menu, then drag and drop your `.sspj` file to convert it automatically.
3. **Place**: Drag the generated `SpriteStudioAnimeAsset` from the Project window into the Hierarchy (or Scene view), pick `SpriteStudioPlayer` (world space) or `SpriteStudioGraphic` (uGUI Canvas), then set the **Animation Name** and press Play.

No `.sspj` of your own yet? SpriteStudio's [official sample data](https://www.webtech.co.jp/help/ja/spritestudio7/download/sample/) works as-is — the page is in Japanese, the downloads are not. [Ringo](https://www.webtech.co.jp/help/ja/spritestudio7/download/sample/#ringo) is a good character to start with, and [Particle](https://www.webtech.co.jp/help/ja/spritestudio7/download/sample/#Perticle_sample) a good one for effects.

For advanced setups like compiling from source or modifying the package, please refer to the [Installation Guide](./docs/en/setup/install.md).

## 🔗 Related Repositories

- [SpriteStudio Docs](https://cri-middleware.github.io/SpriteStudio-Docs/) — the portal for the SDK and every official player
- [SpriteStudio-SDK](https://github.com/cri-middleware/SpriteStudio-SDK) — the core SDK providing `libssruntime` and `libssconverter`
- [SSConverterGUI](https://github.com/cri-middleware/SSConverterGUI) — desktop GUI for converting `.sspj` without a player

## 📄 License

This project is licensed under the BSD-3-Clause License. See the [LICENSE.md](./LICENSE.md) file for details.

For the licenses of the bundled `SpriteStudio-SDK` and other third-party libraries used in this project (such as FlatBuffers), please refer to [THIRD_PARTY_NOTICES.md](./THIRD_PARTY_NOTICES.md).
