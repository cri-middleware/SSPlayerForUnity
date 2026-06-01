[**日本語**](./README.ja.md) | [**English**](./README.md)

# SpriteStudioPlayer for Unity

> **Note:** This `develop` branch is a work-in-progress version. The APIs and workflows in this branch may change without notice.

A high-performance plugin for playing back animations created with [OPTPiX SpriteStudio 7](https://www.webtech.co.jp/spritestudio/) inside Unity.
This plugin leverages Unity's Job System (Burst Compiler) and zero-allocation mesh generation combined with a highly optimized Rust core (`libssruntime`) to achieve extreme performance.

## Key Features
- **Zero-Allocation Pipeline**: Direct vertex writing from Burst Jobs to `Mesh.SetVertexBufferData`.
- **Rust Core**: Uses the official `SpriteStudio7-SDK` (Rust) for zero-cost FFI bindings via `csbindgen`.
- **Editor Integration**: Directly import `.sspj` project files. Converts to flatbuffers (`.ssab`) on the fly via `libssconverter`.
- **uGUI & World Space**: Provides `SpriteStudioPlayer` for 3D/2D scenes and `SpriteStudioGraphic` for Canvas UI.

## Quick Start
1. Place the `package/` folder into your Unity project's `Packages/` directory, or install via Unity Package Manager using the local folder.
2. Drag and drop your `.sspj` file into the Unity Editor. It will automatically be converted to a `SpriteStudioProjectAsset` (ScriptableObject).
3. Attach `SpriteStudioPlayer` to a GameObject, or `SpriteStudioGraphic` to a Canvas UI element.
4. Assign the imported asset and play!

## Building the Runtime
If you are developing the plugin itself, you need to compile the Rust SDK:
1. Clone with submodules to get `SpriteStudio7-SDK`.
2. Run `./scripts/build-runtime.sh` (Mac/Linux) or `.\scripts\build-runtime.ps1` (Windows) to build `libssruntime` and `libssconverter`, and deploy them to `package/Runtime/Plugins` and `package/Editor/Plugins`.
