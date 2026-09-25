[**English**](./README.md) | [**日本語**](./README.ja.md)

# SpriteStudioPlayer for Unity

**Professional 2D animations for your Unity games. A plugin that balances intuitive usability with extreme performance.**

A high-performance plugin for playing back animations (`.ssab`) created with **[SpriteStudio](https://www.webtech.co.jp/spritestudio/)** inside [Unity](https://unity.com/). By combining Unity's powerful features with the expressive capabilities of a dedicated animation tool, it easily integrates raster-based 2D animations into your Unity projects.

## ✨ Why use SpriteStudio with Unity?

- **A purpose-built 2D animation editor, beyond Unity's built-in tools**
  SpriteStudio is a dedicated editor for skeletal- and mesh-based 2D animation — bone rigging, mesh deformation, and particle effects made for animators. It unlocks expression that's tedious or impractical to build with Unity's native animation tools, and since the motion is authored outside Unity and exported as a `.ssab`, artists can iterate in their own tool without touching the engine.
- **Characters, effects, and whole scenes — all in one editor**
  Particle effects are a first-class part type, authored on the same timeline as your characters — there's no separate effect system to bolt on. And you're not limited to a single rigged character: combine characters, backgrounds, effects, and UI into an entire cutscene or full-screen sequence, and play it back in Unity as one animation.
- **Renders in both world space and on a uGUI Canvas**
  `SpriteStudioPlayer` draws in world space (MeshRenderer) and `SpriteStudioGraphic` draws on a uGUI Canvas (CanvasRenderer) — the same animation drops into either context like any other component, driven from C# or sequenced on Timeline. On a Canvas it automatically follows uGUI **Mask** / **RectMask2D**. CanvasRenderer's "one mesh, one material" rule does narrow that path: it assumes a **single atlas**, and collapses every blend but **MIX / ADD** to MIX — the world path has neither limit. See [Limitations](./docs/en/limitations.md).
- **A seamless SpriteStudio ⇄ Unity asset pipeline**
  Drop a `.sspj` into Unity and it's automatically converted to an optimized `.ssab` and a ready-to-use `SpriteStudioAnimeAsset`; re-edit in SpriteStudio and your Unity assets update instantly. Because the animation lives in assets separate from the scene, you avoid the Git conflicts that come with scene-embedded animation.
- **A low-memory, high-performance native runtime**
  Assets are stored as FlatBuffers, so there's zero parsing at runtime and instant playback straight from memory. A Rust core with SIMD, combined with Unity's Job System (Burst) and zero-allocation mesh updates, keeps CPU and memory overhead low — staying smooth even on mobile or when rendering large numbers of characters. Sub-frame interpolation keeps playback fluid even on high-refresh-rate displays.

## 📚 Documentation

For detailed instructions, see the documents in the `docs/` folder — structured step by step, and where the data-flow diagram, key features and system requirements live.

- [**Documentation site (hosted)**](https://cri-middleware.github.io/SSPlayerForUnity/) — 🚧 live after the first release
- [**Documentation (English)**](./docs/en/index.md)
- [**ドキュメント (日本語)**](./docs/ja/index.md)
- [**SpriteStudio Docs (portal)**](https://cri-middleware.github.io/SpriteStudio-Docs/) — the SDK and every official player in one place — 🚧 live after the first release

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

### 1. Import into your Project

> 🚧 **Not released yet.** The tarball link below goes live once the first release is published.
> Until then, take the **sample project** route below — it runs from a checkout.

1. **Install**: Download the package tarball from GitHub Releases (stable link, always the latest release) — [`jp.co.cri-mw.spritestudio.ssplayer-unity.tgz`](https://github.com/cri-middleware/SSPlayerForUnity/releases/latest/download/jp.co.cri-mw.spritestudio.ssplayer-unity.tgz) — then in Unity's Package Manager choose **Add package from tarball...** and select it. *(Package Manager's "Add package from URL..." / git URL does not work for this package, since the native binaries ship only in the tarball.)*
2. **Import**: Open **Window > SpriteStudio > Importer** from the Unity menu, then drag and drop your `.sspj` file to convert it automatically.
3. **Place**: Drag the generated `SpriteStudioAnimeAsset` from the Project window into the Hierarchy (or Scene view), pick `SpriteStudioPlayer` (world space) or `SpriteStudioGraphic` (uGUI Canvas), then set the **Animation Name** and press Play.

### 2. Try out the Sample Project

> 🚧 This one is a **checkout**, not part of the install above — it is how to watch the player work
> until the package is published. `examples/SampleProject` has the SpriteStudio package already linked, and step 3 converts the SDK's **Ringo** animation into it, so you do not need a `.sspj` of your own to see something move.
1. **Get the Repo**: Clone this repository with `--recurse-submodules` (the sample's source project lives in `SpriteStudio-SDK/tests`).
2. **Prepare**: Run `./scripts/download-sdk.sh` (Mac/Linux) or `scripts\download-sdk.ps1` (Windows) to fetch and place the native library (`libssruntime`).
3. **Build the sample animation**: Run `./scripts/deploy-examples.sh` (Mac/Linux) or `scripts\deploy-examples.ps1` (Windows). No converted animation is committed, so this is the step that puts Ringo in the project — and it has to run **before** the project is opened in Unity, because Unity deletes the committed `.meta` that carry the scene's references the moment it finds them without their assets. (Already opened it? `git checkout -- examples/SampleProject/Assets` puts them back; then run the script.) It needs a Rust toolchain — without one, open the project and drag `SpriteStudio-SDK/tests/Ringo/Ringo.sspj` onto **Window > SpriteStudio > Importer** instead, then re-assign the imported asset on the player in the scene.
4. **Open**: Open `examples/SampleProject` in Unity Hub.
5. **Try it**: Open `Assets/Test.unity` and press Play.

The SDK's other five test projects (`overall`, `allAttributeV7`, `allPartsV7`, `ParticleEffect`, `Doll`) are not bundled — they are large, and the submodule already carries them. Drag any of them from `SpriteStudio-SDK/tests` onto **Window > SpriteStudio > Importer** when you want them.

For advanced setups like compiling from source or modifying the package, please refer to the [Installation Guide](./docs/en/setup/install.md).

## 🎬 Samples

Sample projects are available under the [examples folder](./examples/).

- [SampleProject](./examples/SampleProject) — The one to open. It plays the SDK's Ringo animation, so you need no `.sspj` of your own. Run `download-sdk` and then `deploy-examples` first, before the project is opened in Unity (see Quick Start); `deploy-examples` is also what to re-run whenever `SpriteStudio-SDK/tests/Ringo` changes.
- [Override_Ringo](./examples/Override_Ringo) — The per-part override layer, driven from `OverrideDemo.cs`: recolour, hide and swap a part at runtime, on the same Ringo character. `deploy-examples` fills this project too, and the same "before you open it" rule applies.
- [UnityTestProject](./examples/UnityTestProject) — A blank project with the package linked and nothing else. It is the scratch project used while developing the plugin; import your own `.sspj` into it to try something in a clean environment.

## 🔗 Related Repositories

- [SpriteStudio Docs](https://cri-middleware.github.io/SpriteStudio-Docs/) — the portal for the SDK and every official player
- [SpriteStudio-SDK](https://github.com/cri-middleware/SpriteStudio-SDK) — the core SDK providing `libssruntime` and `libssconverter`
- [SSConverterGUI](https://github.com/cri-middleware/SSConverterGUI) — desktop GUI for converting `.sspj` without a player

## 📄 License

This project is licensed under the BSD-3-Clause License. See the [LICENSE.md](./LICENSE.md) file for details.

For the licenses of the bundled `SpriteStudio-SDK` and other third-party libraries used in this project (such as FlatBuffers), please refer to [THIRD_PARTY_NOTICES.md](./THIRD_PARTY_NOTICES.md).
