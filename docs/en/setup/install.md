# Installation

[← Back to documentation index](../index.md)

This page explains how to add SpriteStudioPlayer for Unity to your project. Choose one of the three installation methods that best fits your needs.

## Supported Environment

| Item | Details |
|---|---|
| Unity version | **Unity 6.3 LTS (6000.3)** or later |
| Render pipeline | Universal (URP) / Built-in (BRP) |
| Dependent packages | Burst / Mathematics / Collections / Timeline / uGUI (automatically resolved by Package Manager) |

All dependent packages are standard Unity packages, so Package Manager resolves them automatically when you add this package.

---

## 1. Install from a Package (Recommended)

This is the easiest and most reliable method. It uses the precompiled Unity package distributed on GitHub Releases.

1. Download the package tarball. The stable link below always points to the latest release:
   - [`jp.co.cri-mw.optpix.ssplayer-unity.tgz`](https://github.com/cri-middleware/SSPlayerForUnity/releases/latest/download/jp.co.cri-mw.optpix.ssplayer-unity.tgz)
   - To pick a specific version instead, download the `.tgz` asset from the [GitHub Releases](https://github.com/cri-middleware/SSPlayerForUnity/releases) page.
2. Open **Window > Package Manager** from the Unity menu.
3. Click the **＋** button in the top left, select **Add package from tarball...**, and choose the downloaded file.

> **Note:** Package Manager's *Add package from URL...* (git URL) cannot install this package. The native plugin binaries are distributed only inside the release tarball, so always use *Add package from tarball...* with the downloaded `.tgz`.

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
