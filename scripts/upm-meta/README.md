# UPM `.meta` for packed documents

`scripts/pack-release.sh` copies `LICENSE.md`, `THIRD_PARTY_NOTICES.md` (as
`Third Party Notices.md`) and `CHANGELOG.md` from the repository root into `package/` just
before `npm pack`, so the Package Manager UI can show the Licenses / Changelog tabs.

Those copies need a sibling `.meta`: a UPM package is extracted into an immutable folder
(`Library/PackageCache/...`), and Unity ignores any asset there that has no `.meta`. The files
here are those `.meta`, with fixed GUIDs so the identity stays stable across releases.

They live outside `package/` on purpose — the documents themselves only exist inside `package/`
during a pack, so a committed `.meta` next to them would be an orphan in the working tree.
