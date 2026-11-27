# 🛠️ Build Guide

[← Back to documentation index](../index.md)

For normal use, the package already includes prebuilt native libraries, so this page is not needed. It is for **developing the plugin itself** from a checkout of the repository. How the player is put together, and why, is on [How the Player Is Put Together](architecture.md).

The native libraries `libssruntime` / `libssconverter` and the C# FFI bindings that `csbindgen` generates for them go into the package here:

- `package/Runtime/Plugins/<Platform>/<Arch>/` — `libssruntime` (used at runtime); macOS, iOS and WebGL have no `<Arch>` level
- `package/Editor/Plugins/<Platform>/[<Arch>/]` — `libssconverter` (used for conversion in the editor); desktop platforms only
- `package/Runtime/Bindings/SSRuntimeMethods.g.cs` — generated FFI bindings
- `package/Editor/Bindings/SSConverterMethods.g.cs` — same as above (for conversion)

[`download-sdk`](#installing-the-released-native-libraries) installs the released ones and needs no Rust. [`build-runtime`](#building-the-native-libraries-from-source) compiles them from the SDK submodule instead, to change the native core.

## Installing the Released Native Libraries

`download-sdk` installs the SpriteStudio-SDK release pinned in `scripts/SDK_VERSION.txt`: every platform's libraries, their bindings, and the license files. It needs neither Rust nor the submodule, and skips the download when that version is already in place.

```sh
# macOS / Linux
./scripts/download-sdk.sh

# Windows (PowerShell)
.\scripts\download-sdk.ps1
```

Changing only the C# needs nothing more: see [Using a Changed Player](#using-a-changed-player).

## Building the Native Libraries from Source

`build-runtime` compiles `libssruntime` / `libssconverter` from the SDK submodule for the platform you name (the host's by default), and replaces what `download-sdk` placed for it.

### Prerequisites

The build requires the **Rust toolchain (`cargo`)**. If it is not installed, install it from [rustup](https://rustup.rs/).

`build-runtime.sh` and `deploy-examples.sh` run under **zsh**: macOS has it, on Linux install it first. On Windows use the `.ps1` twins, which need **PowerShell 7** (`pwsh`), not Windows PowerShell 5.1.

If you cross-compile (build for a different platform), you also need that platform's toolchain: the Rust targets its SDK release script names (each script's header lists its `rustup target add` commands), plus Xcode for macOS and iOS, the Android NDK and `cargo-ndk` for Android, the nightly toolchain with `rust-src` for web, and the MSVC build tools for Windows.

### Steps

1. Fetch the repository including submodules to prepare `SpriteStudio-SDK`.

   ```sh
   # For a fresh clone
   git clone --recursive https://github.com/cri-middleware/SSPlayerForUnity.git

   # If you have already cloned
   git submodule update --init --recursive
   ```

2. Run the build script.

   ```sh
   # macOS / Linux
   ./scripts/build-runtime.sh

   # Windows (PowerShell)
   .\scripts\build-runtime.ps1
   ```

3. The libraries and bindings land in the places listed at the top of this page.

   The Rust dependencies' license files (`THIRD-PARTY-LICENSES.ssruntime.md` / `.ssconverter.md`) land beside the libraries only after the SDK's `generate-licenses` script has written them to `SpriteStudio-SDK/target/licenses/`; `download-sdk` places them with the released libraries.

### Build Options

By passing arguments, you can switch the target platform, architecture, and build mode (if not specified, a debug build for the host environment is produced).

```sh
./scripts/build-runtime.sh platform=<platform> arch=<arch> build=<mode>
```

| Option | Value | Description |
|---|---|---|
| `platform` | `windows` / `macos` / `linux` / `android` / `ios` / `web` | Target platform |
| `arch` | `x86_64` / `arm64` / `universal`, etc. | Target architecture |
| `build` | `debug` / `release` | Build mode (`release` for distribution) |
| `hostbuild` | `auto` / `false` | `false` sends a host build through the SDK's release script too (default `auto`) |

Examples:

```sh
# Release build for Windows x86_64
.\scripts\build-runtime.ps1 platform=windows arch=x86_64 build=release

# Release build for Android arm64
./scripts/build-runtime.sh platform=android arch=arm64 build=release

# iOS (generates an XCFramework containing both device and simulator)
./scripts/build-runtime.sh platform=ios build=release
```

> **About iOS:** `platform=ios` generates a `libssruntime.xcframework` containing both the device (arm64) and simulator (arm64 / x86_64) slices, and places it in `package/Runtime/Plugins/iOS/`. Since device / simulator are separated within the XCFramework, no simulator-only option is needed (it also works on the simulator on Apple Silicon Macs).

> **Note:** A debug build for the host's own platform/arch runs `cargo build` directly (on Linux, the SDK's `release-linux.sh`). Anything else — a release build, or cross-compilation — is delegated to the release script on the SDK side, and `hostbuild=false` sends a host build there as well.

## Regenerating the FlatBuffers Accessors

When the `.fbs` schemas change on the SDK side, regenerate the C# accessors under `package/Runtime/Format/`. This requires `flatc` on your PATH. The `flatc` must be the release the vendored FlatBuffers C# runtime comes from (`VERSION` in `scripts/download-flatbuffers.sh`): the generated code calls a version-named check in that runtime and does not compile against another.

```sh
# macOS / Linux
./scripts/generate-fbs-code.sh

# Windows (PowerShell)
.\scripts\generate-fbs-code.ps1
```

> Do not manually edit the generated files (`ssab_generated.cs`, `framedata_generated.cs`, etc.). Always regenerate them with the script.

> The csbindgen FFI bindings (`SSRuntimeMethods.g.cs`, `SSConverterMethods.g.cs`) are **not** this script's output. The SDK's own build emits them, and `build-runtime` / `download-sdk` place them into the package.

## Common Pitfalls

- **Unity keeps holding an old native library**: the editor does not reload a native plugin it has loaded. If replacing the build does not take effect, **quit the Unity editor, build, and then restart it**.

## Using a Changed Player

The released package is the only one with native libraries in it, which is why a git URL cannot
install the package: `Plugins/` and `Bindings/` are not in git. A checkout gets them from a script.

**Changing only the C#** needs no Rust:

1. Clone the repository.
2. [Install the released native libraries](#installing-the-released-native-libraries) with
   `scripts/download-sdk.sh` (`.ps1` on Windows).
3. Point your project at `package/`: in Package Manager, **＋** → **Install package from disk...**
   and choose `package/package.json`, or add a `file:` dependency to `Packages/manifest.json`, as
   `examples/UnityTestProject` does with `"jp.co.cri-mw.spritestudio.ssplayer-unity": "file:../../../package"`.

The project then uses your checkout as it stands, so a change to the C# shows on the next
compile. **Changing the native core** is
[Building the Native Libraries from Source](#building-the-native-libraries-from-source), and the
Editor must be restarted to load what it builds.

## Checking Against the Real Host

`scripts/run-tests.sh` runs EditMode, PlayMode and a standalone player build headless, in
`examples/UnityTestProject`. It needs macOS or Windows; the Editor version that project names,
installed through Unity Hub in its default location or the install location set in its preferences,
with an activated licence; and the native libraries and bindings already in place (`download-sdk` or
`build-runtime`). Close the Editor first: batchmode cannot take the project lock while it is open.
When the test fixtures are missing it converts them from the SDK submodule before the suites start,
so the submodule must be checked out.

- **Opening the project directly deletes the `.meta` of every native plugin this machine lacks**
  — not an error, a deletion, which only `git status` shows. `run-tests.sh` puts them back; after
  opening the project yourself, run `git checkout -- package/Runtime/Plugins package/Editor/Plugins`.
- **The Editor does not reload a native plugin it has loaded.** After `build-runtime` or a new
  `download-sdk`, restart it, or a fix looks like it did nothing.
- **Check native-plugin loading in the standalone player, not Play Mode.** The Editor resolves
  plugins more loosely than a player: on macOS the runtime must be a folder named
  `ssruntime.bundle`, and a `libssruntime.bundle` loads in the Editor and fails in the player with
  `DllNotFoundException: ssruntime`. The player suite is what catches it.
- On macOS 27, Unity 6000.3.16f1 does not start: batchmode exits 0 having done nothing.
  6000.3.25f1 does.

### Where Unity's labels come from

The guides name the Editor's menus as Unity shows them. Its strings are UTF-16 inside
`Unity.app/Contents/Resources/Scripting/Managed/UnityEngine/UnityEditor.CoreModule.dll`, so search
for UTF-16 bytes (the Package Manager's **Install package from disk...**,
**Install package from tarball...** and **Install package from git URL...** are there). uGUI's
**GameObject > UI** menu is `Editor/UGUI/UI/MenuOptions.cs` in the project's
`Library/PackageCache/com.unity.ugui@*/`.
The Japanese pages keep the Editor's English labels.
