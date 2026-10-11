# 📥 Installation

[← Back to documentation index](../index.md)

This page explains how to add SpriteStudio Player for Unity to your project. Choose one of the three installation methods that best fits your needs.

## Supported Environment

| Item | Details |
|---|---|
| Unity version | **Unity 6.3 LTS (6000.3)** or later |
| Render pipeline | Universal (URP) / Built-in (BRP) |
| Dependent packages | Burst / Mathematics / Collections / Timeline / uGUI (automatically resolved by Package Manager) |

All dependent packages are standard Unity packages, so Package Manager resolves them automatically when you add this package.

The project template you start from can be **either 2D or 3D**. What this package requires is that the render pipeline is URP or Built-in, not a particular template. On URP the animation draws under the **Universal Renderer** and under the **2D Renderer** the 2D template sets up alike. It draws unlit under both: Light 2D does not light it, as it does not light a sprite that uses Sprite-Unlit-Default.

> **If you are asked to restart during installation:** if the Burst version in your project changes while the editor is running, Unity shows a **Burst Package Update Detected** dialog. This is not an error specific to this package — restart the editor (**Restart Now**, or **OK** and then restart it yourself, depending on the Burst version). Burst compilation stays shut down until you do, which makes playback far slower.

---

## 1. Install the Released Package (Recommended)

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
3. Click the **＋** button in the top left, select **Add package from tarball...**, and choose the downloaded file.

> **Verifying the download (optional):** every release also attaches a `SHA256SUMS` file. Since the package contains native binaries, you may want to check the tarball you downloaded matches:
>
> ```sh
> # with SHA256SUMS and the .tgz in the same folder
> sha256sum -c SHA256SUMS      # macOS: shasum -a 256 -c SHA256SUMS
> ```
>
> It covers both the version-pinned tarball and the stable alias, so it reports `OK` for whichever one you downloaded and `No such file or directory` for the other.

> **Note:** Package Manager's *Add package from URL...* (git URL) cannot install this package. The native plugin binaries are distributed only inside the released package, so install it from npm or with *Add package from tarball...*.

---

## 2. Install from Local Source (for Customization)

This method is for when you want to modify the Unity-side C# scripts or work directly with the contents of the package. No Rust environment setup is required.

1. Clone or download the repository.
   ```sh
   git clone https://github.com/cri-middleware/SSPlayerForUnity.git
   ```
2. Run the setup script in the `scripts/` folder to download and place the native library (`libssruntime`).
   - **Windows**: `scripts\download-sdk.ps1`
   - **Mac/Linux**: `./scripts/download-sdk.sh`
3. Copy the prepared `package/` folder under your Unity project's `Packages/` directory, or add it via **Add package from disk...** in Package Manager by specifying its `package.json`.

---

## 3. Full Build from Source (for Plugin Developers)

These are the steps for developing or debugging the Rust core (`libssruntime`) itself.
Instead of using the setup script, you build the native library directly from source using your local Cargo environment.

1. Clone the repository including submodules.
   ```sh
   git clone --recursive https://github.com/cri-middleware/SSPlayerForUnity.git
   ```
2. Run `scripts/build-runtime.sh` (or `scripts\build-runtime.ps1` on Windows) to build the Rust core with Cargo and place it in the plugin folder.
3. For details, see the **[Build Guide](./build.md)**.

---

## Verifying the Installation

Once installation succeeds, the following become available.

- **Menu**: **Window > SpriteStudio > Importer** (the import window)
- **Components**: `SpriteStudio Player` / `SpriteStudio Graphic` (can be added via Add Component)

## Next Steps

After installation is complete, proceed to one of the following.

- If you don't yet have a `.ssab` (`SpriteStudioAnimeAsset`) → convert and generate one from a SpriteStudio project (`.sspj`) in [Asset Import and Editor Integration](../workflow/usage_asset_pipeline.md).
- If you already have an imported asset → place it in a scene and play it back in [Basic Usage](../workflow/usage_basic.md).
