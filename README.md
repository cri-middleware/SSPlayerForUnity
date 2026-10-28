[**English**](./README.md) | [**日本語**](./README.ja.md)

# SpriteStudio Player for Unity

**Professional 2D animations for your Unity games. A plugin that balances intuitive usability with extreme performance.**

A high-performance plugin for playing back animations (`.ssab`) created with **[SpriteStudio](https://www.webtech.co.jp/spritestudio/)** inside [Unity](https://unity.com/). By combining Unity's powerful features with the expressive capabilities of a dedicated animation tool, it easily integrates raster-based 2D animations into your Unity projects.

## ✨ Why use SpriteStudio with Unity?

- **Bring your characters to life.** Breathing and blinking, swaying hair and clothes, flashy slash and magic effects — the motion is built visually in a dedicated editor (SpriteStudio) with bone rigs, mesh deformation and particles. Artists work in their own tool, and Unity just plays the result.
- **Not just characters — effects, UI and whole scenes.** Particle effects sit on the same timeline as the character, so there is no separate effect system to set up. Combine characters, backgrounds, effects and UI into a cutscene or a full-screen sequence, and play it in Unity as a single animation.
- **In your scene or on your UI.** `SpriteStudioPlayer` draws in the world and `SpriteStudioGraphic` on a uGUI Canvas, where it fits its `RectTransform` and follows **Mask** / **RectMask2D**, and can be a button or a toggle that plays a section for each state. Both are controlled from C#, and `SpriteStudioPlayer` can be sequenced on Timeline.
- **A seamless SpriteStudio ⇄ Unity round trip.** Drop a `.sspj` onto the **SS Importer** window and it becomes a ready-to-use asset. To tweak it, click **Open SSPJ** in the Inspector, save in SpriteStudio, then click **Reconvert** — no manual export step. The animation lives in its own asset rather than the scene, so teammates don't collide over scene merges in Git.
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

**Using it in your game?** Read on — install the package, drag a `.sspj` onto the SS Importer, place a component.
Everything after that (scripting, events, per-part overrides, Timeline) is the same path further in, in the
[documentation](./docs/en/index.md).
**Working on the Player, or the Rust runtime under it?** [CONTRIBUTING.md](./CONTRIBUTING.md), the [Build Guide](./docs/en/development/build.md) — which also covers using a changed package in a project and checking it in Unity — and [How the Player Is Put Together](./docs/en/development/architecture.md) for the layers and the reasons that span files.

This player supports Unity 6.3 LTS and later, and works in both URP and BRP. Projects have to be authored in **SpriteStudio 7.6 or later**.

1. **Install**: In **Project Settings > Package Manager**, add `https://registry.npmjs.org` as a scoped registry for `jp.co.cri-mw`, then install `jp.co.cri-mw.spritestudio.ssplayer-unity` with Package Manager's **Install package by name...** — the [Installation Guide](./docs/en/setup/install.md) has the steps. Or download the package tarball from GitHub Releases (stable link, always the latest release) — [`jp.co.cri-mw.spritestudio.ssplayer-unity.tgz`](https://github.com/cri-middleware/SSPlayerForUnity/releases/latest/download/jp.co.cri-mw.spritestudio.ssplayer-unity.tgz) — and choose **Install package from tarball...**. *(Package Manager's "Install package from git URL..." / git URL does not work for this package, since the native binaries ship only in the released package.)*
2. **Import**: Open **Window > SpriteStudio > Importer** from the Unity menu, and drag your `.sspj` onto the window from Explorer or Finder — the box in the middle lights up while you drag. It is converted into the window's **Output Folder**, `Assets/ssab_generated/<sspj name>/` by default:

   ```
   Ringo/                       ← your SpriteStudio project, anywhere on disk
   ├── Ringo.sspj               ← drag this onto the SS Importer window
   ├── Ringo.png
   └── …

   MyGame/                      ← your Unity project
   └── Assets/
       └── ssab_generated/      ← Output Folder (the default)
           └── Ringo/           ← named after the .sspj
               ├── Ringo.ssab   ← the SpriteStudioAnimeAsset
               ├── Ringo.png
               ├── Ringo.mat
               └── …
   ```

3. **Place**: Drag the `SpriteStudioAnimeAsset` (`Ringo.ssab` above) from the Project window into the Hierarchy (or Scene view), and pick **SpriteStudioPlayer** (world space) or **SpriteStudioGraphic (Canvas UI)**. **Animation Name** starts on the pack's first animation; pick another from its list if you like, and press Play.

No `.sspj` of your own yet? SpriteStudio's [official sample data](https://www.webtech.co.jp/help/ja/spritestudio7/download/sample/) works as-is — the page is in Japanese, the downloads are not. [Ringo](https://www.webtech.co.jp/help/ja/spritestudio7/download/sample/#ringo) is a good character to start with, and [Particle](https://www.webtech.co.jp/help/ja/spritestudio7/download/sample/#Perticle_sample) a good one for effects.

**Read next:** [Installation Guide](./docs/en/setup/install.md) · [Asset Import](./docs/en/workflow/usage_asset_pipeline.md) · [Basic Usage](./docs/en/workflow/usage_basic.md) · [Limitations & Scope](./docs/en/limitations.md)

## 🔗 Related Repositories

- [SpriteStudio Docs](https://cri-middleware.github.io/SpriteStudio-Docs/) — the portal for the SDK and every official player
- [SpriteStudio-SDK](https://github.com/cri-middleware/SpriteStudio-SDK) — the core SDK providing `libssruntime` and `libssconverter`
- [SSConverterGUI](https://github.com/cri-middleware/SSConverterGUI) — desktop GUI for converting `.sspj` without a player

## 📄 License

This project is licensed under the BSD-3-Clause License. See the [LICENSE.md](./LICENSE.md) file for details.

For the licenses of the bundled `SpriteStudio-SDK` and other third-party libraries used in this project (such as FlatBuffers), please refer to [THIRD_PARTY_NOTICES.md](./THIRD_PARTY_NOTICES.md).
