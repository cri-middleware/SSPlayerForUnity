# SpriteStudio Player for Unity

Plays animations made with [SpriteStudio](https://www.webtech.co.jp/spritestudio/) (`.ssab`) in Unity 6.3 LTS and later, in URP and BRP: `SpriteStudioPlayer` in the world, `SpriteStudioGraphic` on a uGUI Canvas. You start without writing a script: drop a `.sspj` onto the importer, place the asset it makes, and press Play.

## Install

In **Project Settings > Package Manager**, add `https://registry.npmjs.org` as a scoped registry for `jp.co.cri-mw`, then install `jp.co.cri-mw.spritestudio.ssplayer-unity` with Package Manager's **Install package by name...**. The [Installation Guide](https://cri-middleware.github.io/SSPlayerForUnity/setup/install/) has the steps, and the tarball from GitHub Releases as the other route. *(Package Manager's "Install package from git URL..." does not work for this package, since the native binaries ship only in the released package.)*

## First frame

Projects have to be authored in **SpriteStudio 7.6 or later**.

1. Open **Window > SpriteStudio > Importer** and drag your `.sspj` onto the window. It is converted into the window's **Output Folder**, `Assets/ssab_generated/<sspj name>/` by default.
2. Drag the `.ssab` from there into the Hierarchy, and pick **SpriteStudioPlayer** (world space) or **SpriteStudioGraphic (Canvas UI)**.
3. Press Play. **Animation Name** starts on the asset's first animation; pick another from its list if you like.

No `.sspj` of your own yet? SpriteStudio's [official sample data](https://www.webtech.co.jp/help/ja/spritestudio7/download/sample/) works as-is: the page is in Japanese, the downloads are not.

**Read next:** [Installation Guide](https://cri-middleware.github.io/SSPlayerForUnity/setup/install/) · [Asset Import](https://cri-middleware.github.io/SSPlayerForUnity/workflow/usage_asset_pipeline/) · [Basic Usage](https://cri-middleware.github.io/SSPlayerForUnity/workflow/usage_basic/) · [Limitations & Scope](https://cri-middleware.github.io/SSPlayerForUnity/limitations/)

日本語のドキュメント: <https://cri-middleware.github.io/SSPlayerForUnity/ja/>

Source, issues and releases: <https://github.com/cri-middleware/SSPlayerForUnity> · License: BSD-3-Clause
