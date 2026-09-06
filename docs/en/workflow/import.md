# 🤖 Conversion Features and CI/CD

[← Back to documentation index](../index.md)

This document summarizes the import-time optimizations, batch conversion features, and CI/CD integration related to converting `.sspj` to `.ssab`.

For the basic import procedure, see [Asset Import and Editor Integration](./usage_asset_pipeline.md).

## Configuring the output location

The output location for conversion results is set via **Output Folder** in the SpriteStudio Importer window (default: `Assets/ssab_generated`); any folder inside the project will do. The setting is saved in `EditorUserSettings` (`ProjectSettings/EditorUserSettings.asset`), so it is per project and per person.

## Automatic configuration on import into Unity (textures and materials)

When an optimized binary (`.ssab`) to be used in Unity is added to the project, the internal `AssetPostprocessor` detects it and automatically applies the following asset settings.

This automatic configuration works correctly in both cases: **when the file was converted and output from the SpriteStudio Importer window**, and **when a `.ssab` already converted by an external tool such as `ssconverter-cli` is manually copied (imported) into the Unity project**.

- **Texture import settings**: For textures (image files) in the same folder as the `.ssab`, optimal import settings for 2D animation (uncompressed, Clamp, Mipmap disabled, alpha transparency, etc.) are applied automatically.
- **Automatic shared material generation**: A single `.mat` file corresponding to each texture referenced by the `.ssab` is automatically generated adjacent to the texture.

## Batch conversion of folders

Dropping a **folder** into the drop area of the SpriteStudio Importer window (Unity menu: **Window > SpriteStudio > Importer**) **recursively batch-converts** the `.sspj` files within it **to `.ssab`**. Dropping multiple files at once is also supported.

## Reflecting animation changes (re-conversion)

When you modify an animation on the SpriteStudio side, there are several ways to reload the latest `.sspj` and update (re-convert) the `.ssab` in Unity.

- **Generated asset inspector**: You can run it from the **Reconvert** button in the `SpriteStudioAnimeAsset` inspector.
- **Recent list in the SpriteStudio Importer window**: Clicking a row triggers re-conversion. You can also run it from **Reconvert** in the right-click menu.

## Notes

- **We recommend managing `.sspj` outside the Unity project (e.g., outside the repository).** By dropping it onto the SpriteStudio Importer window, only the conversion result (`.ssab`) is generated in the output location (Output Folder) within the Unity project. This lets you operate without including source data that the game does not need in the Unity project.
- **The display may be unstable immediately after import** (especially with URP). In most cases, restarting the editor resolves it.

---

## For CI/CD: Converting with the SDK CLI

If you want to convert from the command line without launching the Unity editor (e.g., integration into CI/CD or build pipelines, or batch conversion of large numbers of assets), you can use the standalone command-line converter bundled with the SpriteStudio-SDK. It is the same engine, `libssconverter`, that the editor's built-in importer uses behind the scenes.

> [!NOTE]
> The CLI binary is **bundled with the SpriteStudio-SDK release artifacts**. It is not included in this Unity package, so if you want to use the CLI, obtain it separately from the SDK.

1. Get the CLI converter for your platform (Windows / macOS / Linux) from [SpriteStudio-SDK Releases](https://github.com/cri-middleware/SpriteStudio-SDK/releases).
2. Open a terminal (or command prompt) and run it, passing the path to the `.sspj` file as an argument.

**Windows:**
```powershell
.\ssconverter-cli.exe path\to\your.sspj
```

**macOS / Linux:**
```bash
./ssconverter-cli path/to/your.sspj
```

When run, a folder named `<sspj name>/` is created in the same directory as the original `.sspj`, and the `.ssab` (and `.ssqb`) along with image files are output beneath it.
3. Place the **entire `<sspj name>/` folder** under the `Assets/` directory of the Unity project. It is then automatically imported as a `SpriteStudioAnimeAsset`, and the automatic texture and material configuration described above is performed the same way.

For detailed options and specifications, refer to the documentation in the SDK repository.
