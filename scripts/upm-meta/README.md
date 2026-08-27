# Committed UPM `.meta`

A UPM package is extracted into an immutable folder (`Library/PackageCache/...`), and Unity
ignores any asset there that has no `.meta`. Every file in the shipped tarball therefore needs
one — but CI has no Unity to generate them, and some of the files that go into `package/` are
produced by the release scripts rather than committed, so their `.meta` cannot sit next to them
in the working tree.

This directory holds those `.meta`, with fixed GUIDs so the identity stays stable across
releases. Two groups:

| `.meta` | For | Placed by |
|---|---|---|
| `LICENSE.md.meta`, `LICENSE.ja.md.meta`, `Third Party Notices.md.meta`, `CHANGELOG.md.meta` | Repo-root documents staged into `package/` at pack time, so Package Manager can show the Licenses / Changelog tabs | `pack-release.sh` / `.ps1` |
| `SSRuntimeMethods.g.cs.meta`, `SSConverterMethods.g.cs.meta` | The gitignored csbindgen FFI bindings — without a `.meta` the scripts are dropped and the package does not compile | `download-sdk.sh` / `.ps1` (release), `build-runtime.sh` / `.ps1` (local build) |

`release.yml` asserts the binding `.meta` reached the package before packing.

If a file listed here is renamed, rename its `.meta` too and keep the GUID — changing the GUID
breaks references in projects that already installed the package.
