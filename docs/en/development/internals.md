# 🧩 How the Player Is Put Together

[← Back to documentation index](../index.md)

This page is for whoever works on SpriteStudio Player for Unity, or changes it for a project of
their own. It asks more of you than the rest of the site: it names files and assumes a checkout.
The [Build Guide](../setup/build.md) builds the native libraries;
[CONTRIBUTING](https://github.com/cri-middleware/SSPlayerForUnity/blob/HEAD/CONTRIBUTING.md) has
the constraints, the setup and the tests. This page is the map across them.

What every SpriteStudio Player shares — how playback, motion blending, sound keys and part
tracking behave — is written once, in the SDK's
[Player Porting Guide](https://github.com/cri-middleware/SpriteStudio-SDK/blob/HEAD/libs/ssruntime/docs/README.md).

## 1. The Map

The package is `package/`. A thin C# layer drives the Rust runtime, `libssruntime`, over FFI
bindings that csbindgen generates, builds each frame's geometry in Burst jobs and uploads it to a
`Mesh`. The editor converts `.sspj` with `libssconverter`.

### Playback (`package/Runtime/`)

| File | What it does |
|---|---|
| `SpriteStudioPlayer.cs` | The component for world space, drawn by a `MeshRenderer` |
| `SpriteStudioGraphic.cs` | The component for a uGUI Canvas, fed straight to the `CanvasRenderer` |
| `SpriteStudioPlaybackCore.cs` | The playback logic both components hold and delegate to |
| `SpriteStudioUpdateManager.cs` | Drives every player each frame in two passes, all Schedule then all Apply |
| `SpriteStudioInternalPlayer.cs` | One player's native context, its tick, and the jobs that build its mesh. `IDisposable`. |
| `SpriteStudioNativeContext.cs` | The native handles' create and destroy, and the `.ssab` bytes they read |
| `SpriteStudioGeometryBuilder.cs`, `SpriteStudioBatchBuilder.cs`, `SpriteStudioMaskPlanner.cs`, `SpriteStudioEffectPlanBuffer.cs`, `SpriteStudioNativeBuffers.cs` | A frame's draw data turned into batches, submeshes, mask steps and effect quads |
| `SpriteStudioMeshJob.cs` | The Burst job that writes the vertices |
| `SpriteStudioMeshUploader.cs` | The upload to the `Mesh`; indices are re-sent only when the topology changes |
| `SpriteStudioMaterial.cs`, `SpriteStudioShaderCatalog.cs`, `SpriteStudioAddOnRegistry.cs` | Material variants by texture and blend, and per-part shaders |
| `SpriteStudioAudioController.cs`, `SpriteStudioEvents.cs` | Built-in sound playback, and the events |
| `SpriteStudioPartTracker.cs`, `SpriteStudioPartOverride.cs`, `SpriteStudioTextureOverride.cs` | Following a part, and per-part and texture overrides |
| `SpriteStudioAnimeAsset.cs`, `SpriteStudioSequenceAsset.cs` | The imported assets: the `.ssab` bytes with hard references to their textures, and a `.ssqb` |
| `Timeline/` | The Timeline track and clip |
| `Bindings/`, `Plugins/` | The FFI bindings and the native libraries. Generated or downloaded, not in git — only their `.meta` are. |
| `Format/`, `FlatBuffers/` | The `.ssab` readers (flatc output) and the FlatBuffers C# runtime, both committed |

### The editor (`package/Editor/`)

| File | What it does |
|---|---|
| `SpriteStudioImporterWindow.cs` | **Window > SpriteStudio > Importer** |
| `SpriteStudioImporter.cs`, `SpriteStudioDirectoryScan.cs` | The entry for a batch: collects the `.sspj` under what was dropped, through `libssconverter`'s scan |
| `SpriteStudioConversionRunner.cs` | Runs the conversions: a queue, at most one per CPU, polled from `EditorApplication.update` so the editor keeps drawing |
| `SpriteStudioAssetPostprocessor.cs` | Converts a `.sspj` that lands in `Assets/`, makes each texture's shared material, and sets up the native plugins' import settings |
| `SpriteStudioSsabImporter.cs`, `SpriteStudioSsqbImporter.cs`, `SpriteStudioAnimeAssetBuilder.cs` | The `ScriptedImporter`s that turn a `.ssab` into a `SpriteStudioAnimeAsset` and a `.ssqb` into a `SpriteStudioSequenceAsset` |
| `SpriteStudioOutputFolder.cs`, `SpriteStudioSourceMap.cs`, `SpriteStudioRecentFiles.cs` | Where conversions go, which `.sspj` each `.ssab` came from, and the recent files |
| `SpriteStudioReconvertMenu.cs`, `SpriteStudioDragAndDrop.cs` | Reconvert from the Project window, and dropping a `.ssab` into the Scene or the Hierarchy |
| `SpriteStudioPlayerEditor.cs`, `SpriteStudioGraphicEditor.cs`, `SpriteStudioAnimeAssetEditor.cs`, `SpriteStudioPartTrackerDrawer.cs`, `SpriteStudioAnimationNameField.cs` | The inspectors |

### Around it

| Path | What it is |
|---|---|
| `package/Tests/` | EditMode, PlayMode and player tests; `scripts/run-tests.sh` runs all three |
| `examples/UnityTestProject` | The project the tests run in, with the package linked as `file:../../../package` |
| `examples/SampleProject`, `examples/Override_Ringo` | The samples, filled by `deploy-examples` |

## 2. Using a Changed Player

The released package is the only one with native libraries in it, which is why a git URL cannot
install the package: `Plugins/` and `Bindings/` are not in git. A checkout gets them from a script.

**Changing only the C#** needs no Rust:

1. Clone the repository.
2. Run `scripts/download-sdk.sh` (`.ps1` on Windows). It puts the released native libraries and
   their bindings, for the SDK release in `scripts/SDK_VERSION.txt`, into `package/`.
3. Point your project at `package/`: in Package Manager, **＋** → **Install package from disk...**
   and choose `package/package.json`, or add a `file:` dependency to `Packages/manifest.json`, as
   `examples/UnityTestProject` does with `"jp.co.cri-mw.spritestudio.ssplayer-unity": "file:../../../package"`.

The project then uses your checkout as it stands, so a change to the C# shows on the next
compile. **Changing the native core** is the Build Guide: `build-runtime` replaces what
`download-sdk` placed, and the Editor must be restarted to load it.

## 3. Checking Against Unity

`scripts/run-tests.sh` runs EditMode, PlayMode and a standalone player build headless, in
`examples/UnityTestProject`, with the Editor version that project names. Close the Editor first:
batchmode cannot take the project lock while it is open. It converts the test fixtures from the SDK
submodule before the suites start.

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

## 4. Why It Is Built the Way It Is

Each reason here spans files; the code it points at carries the detail.

**Two components, one core.** `SpriteStudioPlayer` is a `MonoBehaviour` and `SpriteStudioGraphic`
a uGUI `MaskableGraphic`; with different base classes they cannot share playback by inheritance,
so each holds a `SpriteStudioPlaybackCore` and delegates to it. The serialized settings stay on
the components, where Unity serializes them.

**Every player is driven in two passes.** `SpriteStudioUpdateManager` schedules every player's
work first — the runtime's frame data and the mesh jobs, run in parallel — and only then applies
the results to the meshes. Driving each player from its own `Update` would wait on each job in
turn. The manager walks a snapshot of the players, so one created or destroyed during the pass
does not break it.

**The Canvas component bypasses the Canvas rebuild.** `SpriteStudioGraphic` overrides
`OnPopulateMesh` and the vertex and material dirty flags and hands its mesh to the
`CanvasRenderer` itself, so Unity never destroys and reallocates the mesh each frame. In Play Mode
the update manager drives it; in Edit Mode it previews from its own `LateUpdate`.

**Native handles are owned by `IDisposable` classes, and jobs finish first.**
`SpriteStudioNativeContext` and `SpriteStudioInternalPlayer` own the runtime's pointers;
`JobHandle.Complete()` runs before anything is freed, because a Burst job still reading freed
memory crashes the process.

**Conversion never blocks the Editor.** The scan of a dropped folder runs on another thread
(`SpriteStudioDirectoryScan`), and conversions run from a queue capped at the CPU count and polled
from `EditorApplication.update` (`SpriteStudioConversionRunner`), with progress in the status
bar. A `.sspj` can live anywhere on disk; `ProjectSettings/SpriteStudioSourceMap.json` records
which one each `.ssab` came from, which is what Reconvert reads. The output folder and the recent
files are per user (`UserSettings/`).

**An imported `.ssab` holds hard references to its textures.** `SpriteStudioAnimeAsset` keeps the
`.ssab` bytes and references every texture they name, so a build strips nothing an animation
draws and an AssetBundle carries what it needs; the importers resolve the textures through
`SpriteStudioAnimeAssetBuilder`.

**The SDK submodule is the record of which runtime this repository builds.** `Plugins/` and
`Bindings/` are not in git, so the submodule's pointer is the only thing that says which
`libssruntime` a checkout is meant to run. A change that depends on runtime behaviour bumps the
submodule in the same pull request, so a reviewer who builds what the branch records gets what
the pull request claims.
