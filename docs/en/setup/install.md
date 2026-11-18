# 📥 Installation

[← Back to documentation index](../index.md)

This page explains how to add SpriteStudio Player for Unity to your project: install the released package, from npm or as a tarball.

## Supported Environment

| Item | Details |
|---|---|
| Unity version | **Unity 6.3 LTS (6000.3)** or later |
| Render pipeline | Universal (URP) / Built-in (BRP) |
| Dependent packages | Burst / Mathematics / Collections / Timeline / uGUI (automatically resolved by Package Manager) |
| Supported platforms | [The table below](#supported-platforms) |
| Unity Editor | Windows / macOS / Linux. The SS Importer window converts on Windows and macOS only; on Linux, convert with `ssconverter-cli` ([Conversion Features and CI/CD](../workflow/import.md)) and copy the output folder into the project |
| SpriteStudio projects | Authored in **SpriteStudio 7.6** or later |

All dependent packages are standard Unity packages, so Package Manager resolves them automatically when you add this package.

The project template you start from can be **either 2D or 3D**. What this package requires is that the render pipeline is URP or Built-in, not a particular template. On URP the animation draws under the **Universal Renderer** and under the **2D Renderer** the 2D template sets up alike. It draws unlit under both: Light 2D does not light it, as it does not light a sprite that uses Sprite-Unlit-Default.

> **If you are asked to restart during installation:** if the Burst version in your project changes while the editor is running, Unity shows a **Burst Package Update Detected** dialog. This is not an error specific to this package — restart the editor (**Restart Now**, or **OK** and then restart it yourself, depending on the Burst version). Burst compilation stays shut down until you do, which makes playback far slower.

### Supported Platforms

| Platform | Support |
|---|---|
| Windows / macOS / Linux | ✅ |
| Android / iOS | ✅ |
| WebGL | ✅ |
| Nintendo Switch / Nintendo Switch 2 | Planned |
| PlayStation 5 | Planned |
| Xbox Series X\|S | Planned |

Console builds are not part of the public package. To discuss a console, including one not yet supported, use the [inquiry form](https://www.webtech.co.jp/contact/en.html).

---

## Install the Released Package

This is the easiest and most reliable method. It uses the precompiled package each release publishes, on npm and as a tarball on GitHub Releases: the same package in both places. From npm, Package Manager fetches it by name; the tarball needs no setup.

### From npm

The package is on npm as [`jp.co.cri-mw.spritestudio.ssplayer-unity`](https://www.npmjs.com/package/jp.co.cri-mw.spritestudio.ssplayer-unity). Register npm once per project as the registry for the `jp.co.cri-mw` scope, then install the package by name.

1. Open **Edit > Project Settings > Package Manager**, add an entry under **Scoped Registries**, and save it:
   - **Name**: `npmjs` (any name will do)
   - **URL**: `https://registry.npmjs.org`
   - **Scope(s)**: `jp.co.cri-mw`
2. Open **Window > Package Manager**, click the **＋** button in the top left, and select **Install package by name...**.
3. Enter `jp.co.cri-mw.spritestudio.ssplayer-unity` as the name and the version to install, then click **Install**. The [versions on npm](https://www.npmjs.com/package/jp.co.cri-mw.spritestudio.ssplayer-unity?activeTab=versions) are the ones on [GitHub Releases](https://github.com/cri-middleware/SSPlayerForUnity/releases).

The same two settings can go into `Packages/manifest.json` instead, beside the entries already there, with `X.Y.Z` replaced by the version to install:

```json
{
  "scopedRegistries": [
    {
      "name": "npmjs",
      "url": "https://registry.npmjs.org",
      "scopes": ["jp.co.cri-mw"]
    }
  ],
  "dependencies": {
    "jp.co.cri-mw.spritestudio.ssplayer-unity": "X.Y.Z"
  }
}
```

### From a tarball

1. Download the package tarball. The stable link below always points to the latest release:
   - [`jp.co.cri-mw.spritestudio.ssplayer-unity.tgz`](https://github.com/cri-middleware/SSPlayerForUnity/releases/latest/download/jp.co.cri-mw.spritestudio.ssplayer-unity.tgz)
   - To pick a specific version instead, download the `.tgz` asset from the [GitHub Releases](https://github.com/cri-middleware/SSPlayerForUnity/releases) page.
2. Open **Window > Package Manager** from the Unity menu.
3. Click the **＋** button in the top left, select **Install package from tarball...**, and choose the downloaded file.

> **Verifying the download (optional):** every release also attaches a `SHA256SUMS` file. Since the package contains native binaries, you may want to check the tarball you downloaded matches:
>
> ```sh
> # with SHA256SUMS and the .tgz in the same folder
> sha256sum -c SHA256SUMS      # macOS: shasum -a 256 -c SHA256SUMS
> ```
>
> It covers both the version-pinned tarball and the stable alias, so it reports `OK` for whichever one you downloaded and `No such file or directory` for the other.

> **Note:** Package Manager's *Install package from git URL...* cannot install this package. The native plugin binaries are distributed only inside the released package, so install it from npm or with *Install package from tarball...*.

---

To work on the package itself, see [Using a Changed Player](../development/build.md#using-a-changed-player): its C# scripts need no Rust, and the native core under them is the rest of the [Build Guide](../development/build.md).

---

## Verifying the Installation

Once installation succeeds, the following become available.

- **Menu**: **Window > SpriteStudio > Importer** (the SS Importer window)
- **Components**: `SpriteStudio Player` / `SpriteStudio Graphic` (can be added via Add Component)

## Next Steps

After installation is complete, proceed to one of the following.

- If you don't yet have a `.ssab` (`SpriteStudioAnimeAsset`) → convert and generate one from a SpriteStudio project (`.sspj`) in [Asset Import and Editor Integration](../workflow/usage_asset_pipeline.md).
- If you already have an imported asset → place it in a scene and play it back in [Basic Usage](../workflow/usage_basic.md).
