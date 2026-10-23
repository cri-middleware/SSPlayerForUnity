# 🧩 How the Player Is Put Together

[← Back to documentation index](../index.md)

For whoever works on SpriteStudio Player for Unity, or changes it for a project of their own. It
describes the parts by what they do; the [Build Guide](build.md) builds the native libraries, uses
a changed package in a project and checks it in Unity, and
[CONTRIBUTING](https://github.com/cri-middleware/SSPlayerForUnity/blob/HEAD/CONTRIBUTING.md) has
the constraints, the setup and the tests.

What every SpriteStudio Player shares — how playback, motion blending, sound keys and part
tracking behave — is written once, in the SDK's
[Player Porting Guide](https://github.com/cri-middleware/SpriteStudio-SDK/blob/HEAD/libs/ssruntime/docs/README.md).

## Layers

The package is `package/`. A thin C# layer drives the Rust runtime, `libssruntime`, over FFI
bindings that csbindgen generates, builds each frame's geometry in Burst jobs and uploads it to a
`Mesh`. The editor converts `.sspj` with `libssconverter`.

**Playback** (`package/Runtime/`):

- Two components: `SpriteStudioPlayer` for world space, drawn by a `MeshRenderer`, and
  `SpriteStudioGraphic` for a uGUI Canvas, fed straight to the `CanvasRenderer`. Both hold one
  shared playback core and hand it the work.
- An update manager drives every player each frame in two passes — every player's work scheduled
  first, then every result applied.
- Under each player is the layer that owns its native handles and its tick, and the geometry stage
  that turns a frame's draw data into batches, submeshes, mask steps and effect quads, writes the
  vertices in a Burst job and uploads them, re-sending indices only when the topology changes.
- Materials are kept per texture and blend; `SpriteStudioShaderCatalog` maps per-part shaders.
  Built-in sound, the events, part following (`SpriteStudioPartTracker`), per-part and texture
  overrides, and a Timeline track sit beside them.
- `SpriteStudioAnimeAsset` and `SpriteStudioSequenceAsset` are the imported assets.
- The FFI bindings and the native libraries are generated or downloaded and are not in git; only
  their `.meta` are. The `.ssab` readers (flatc output) and the FlatBuffers C# runtime are
  committed.

**The editor** (`package/Editor/`): the Importer window (**Window > SpriteStudio > Importer**),
which collects the `.sspj` under what was dropped and queues their conversion; an
`AssetPostprocessor` that converts a `.sspj` landing in `Assets/`, makes each texture's shared
material and sets up the native plugins' import settings; the `ScriptedImporter`s that turn a
`.ssab` into a `SpriteStudioAnimeAsset` and a `.ssqb` into a `SpriteStudioSequenceAsset`;
Reconvert from the Project window; dropping a `.ssab` into the Scene or the Hierarchy; and the
inspectors.

**Around it:** `package/Tests/` holds the EditMode, PlayMode and player tests;
`examples/UnityTestProject` is the project they run in, with the package linked as
`file:../../../package`; `examples/SampleProject` and `examples/Override_Ringo` are the samples,
filled by `deploy-examples`.

## Why It Is Built the Way It Is

Each reason here spans files; the code it concerns carries the detail in its comments.

**Two components, one core.** `SpriteStudioPlayer` is a `MonoBehaviour` and `SpriteStudioGraphic`
a uGUI `MaskableGraphic`; with different base classes they cannot share playback by inheritance, so
each holds the shared playback core and delegates to it. The serialized settings stay on the
components, where Unity serializes them.

**Every player is driven in two passes.** The update manager schedules every player's work first —
the runtime's frame data and the mesh jobs, run in parallel — and only then applies the results to
the meshes. Driving each player from its own `Update` would wait on each job in turn. The manager
walks a snapshot of the players, so one created or destroyed during the pass does not break it.

**The Canvas component bypasses the Canvas rebuild.** `SpriteStudioGraphic` overrides
`OnPopulateMesh` and the vertex and material dirty flags and hands its mesh to the
`CanvasRenderer` itself, so Unity never destroys and reallocates the mesh each frame. In Play Mode
the update manager drives it; in Edit Mode it previews from its own `LateUpdate`.

**Native handles are owned by `IDisposable` classes, and jobs finish first.** The layer under each
player owns the runtime's pointers, and `JobHandle.Complete()` runs before anything is freed,
because a Burst job still reading freed memory crashes the process.

**Conversion never blocks the Editor.** The scan of a dropped folder runs on another thread, and
conversions run from a queue capped at the CPU count and polled from `EditorApplication.update`,
with progress in the status bar. A `.sspj` can live anywhere on disk;
`ProjectSettings/SpriteStudioSourceMap.json` records which one each `.ssab` came from, which is
what Reconvert reads. The output folder and the recent files are per user (`UserSettings/`).

**An imported `.ssab` holds hard references to its textures.** `SpriteStudioAnimeAsset` keeps the
`.ssab` bytes and references every texture they name, so a build strips nothing an animation draws
and an AssetBundle carries what it needs.

**The SDK submodule is the record of which runtime this repository builds.** `Plugins/` and
`Bindings/` are not in git, so the submodule's pointer is the only thing that says which
`libssruntime` a checkout is meant to run. A change that depends on runtime behaviour bumps the
submodule in the same pull request, so a reviewer who builds what the branch records gets what the
pull request claims.
