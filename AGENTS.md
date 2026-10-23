# SSPlayerForUnity — AI Agent Guide

## Operating rules

*   Keep changes scoped to the requested task. Unrequested hardening is a suggestion, not a commit; a defect
    you actually hit in the code you touched is not — fix it, or say plainly why you did not.
*   Before editing, read enough surrounding context to understand callers and invariants, the ones below
    included.
*   **Anything that exists twice changes twice** — a page and its translation, a `.sh` and its `.ps1`.
    Missing one half is this family's most common defect.
*   **Never break a line between two Japanese characters**; Chrome renders the break as a space.
*   **Finish with this repository's checks**: the commands in [CONTRIBUTING.md](./CONTRIBUTING.md). With
    the Editor open, ask the user to run the Unity Test Runner; the headless script cannot take the project
    lock. Read what a run says it verified, not its exit code — several checks pass, or skip themselves,
    without a prerequisite.
*   **Do not restate what another file owns**; the table below says which file. A fact no file owns goes
    where its staleness will be noticed.
*   **Never commit inside the `SpriteStudio-SDK` submodule.** A core change is an SDK pull request, then a
    submodule bump here.
*   **Playback and rendering semantics are the SDK's**: its [AGENTS.md](./SpriteStudio-SDK/AGENTS.md) and
    the porting guide in `SpriteStudio-SDK/libs/ssruntime/docs/`. Where the guide is silent,
    SSPlayerForWgpu decides. Do not reimplement either.
*   **Code comments never cite the porting guide** (sections, paths, quotes): it moves, and nothing checks
    the citation. Say what breaks instead.

## What this repo is

A Unity integration for SpriteStudio: a thin C# **Body** drives the Rust **Brain** (`libssruntime`) over
csbindgen FFI, builds each frame's geometry in Burst jobs and uploads it with `SetVertexBufferData`. The
editor converts `.sspj` to `.ssab` with `libssconverter` (`SpriteStudioConversionRunner`), and
`ScriptedImporter`s turn `.ssab` and `.ssqb` into assets.

## Where each answer is written

| Question | File |
|---|---|
| Using it: every API and limitation | the [docs site](./docs/en/) |
| Install, convert, first frame, where to read next | [README.md](./README.md), per `SpriteStudio-SDK/MAINTAINING_PLAYERS.md` |
| Constraints, setup, build, test | [CONTRIBUTING.md](./CONTRIBUTING.md), [Build Guide](./docs/en/setup/build.md) |
| The file map, using a changed package, checking in Unity, reasons that span files | [docs/en/development/internals.md](./docs/en/development/internals.md) |
| Releases, maintainers only | [RELEASING.md](./RELEASING.md) |
| Plans and non-goals | [ROADMAP.md](./ROADMAP.md) |
| What a script takes | its `--help` |

## Invariants

*   **No per-frame allocations in the playback hot path**: the Tick and mesh-update loop allocates no GC
    memory. Reuse `NativeArray`, and update meshes through `SetVertexBufferData`.
*   **`SpriteStudioGraphic` bypasses the canvas rebuild** by overriding `OnPopulateMesh` and the vertex and
    material dirty flags, so Unity never destroys and reallocates its mesh. Keep that path intact.
*   **`SpriteStudioUpdateManager` iterates the snapshotted `_tickCount`, not the live list**; creating or
    destroying a component mid-iteration would otherwise throw `NullReferenceException`.
*   **A `.meta` that is not committed is an asset Unity ignores.** A UPM package installs into an immutable
    folder, so a native library or binding without its `.meta` silently installs as nothing.
