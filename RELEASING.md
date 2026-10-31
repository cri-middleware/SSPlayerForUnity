# Releasing SSPlayerForUnity

Maintainers only. Contributors do not run any of this — see [CONTRIBUTING.md](./CONTRIBUTING.md).

## Cutting a release

Anyone with the Write role on this repository can cut a release: every step is a branch, a pull request, a tag, a workflow run or a Release, and the signing credentials are GitHub Actions secrets, so nothing depends on whose machine runs it beyond the checks in step 3 that need Unity. You need `git`, `gh` 2.87 or later (run `gh auth login` once) and Node.js, plus Unity for those checks and, for step 6, an npm account with 2FA that can publish the package. The repository needs GitHub Pages enabled with GitHub Actions as its source, and a `v*` tag rule on its `github-pages` environment, or publishing a Release deploys nothing; both are settings a repository admin makes. The commands are bash: in zsh, run `setopt interactive_comments` first, so that the `#` comments are not taken as arguments; on Windows, use Git Bash. Steps 1, 4 and 8 switch branches, so run them in a checkout of their own — a clone, or one made with `git worktree add` — not in one where other work is going on.

A release is a tag on `main`, pushed first and built second. The version is written in one place, `version` in `package/package.json` (semver, `7.0.0-beta.1` for a prerelease): `scripts/release-version.sh` prints it, the tag is `v` followed by it, and the release build refuses a tag that disagrees, since the tarball is named after it. The SDK it ships is pinned on its own, in `scripts/SDK_VERSION.txt`. Branches follow git-flow: `develop` integrates, `main` holds released commits only, and `release/X.Y` lives for one release — cut from `develop` when the release ships `develop`'s work, or from the line's latest tag when it ships only fixes on top of it, and deleted once the release is out.

Unity 6.3's Package Manager checks every package for a signature, so a release's tarball is signed, and only `release.yml` can sign it: the credentials, `UNITY_ORGANIZATION_ID` and the UPM service account's key, exist only as this repository's GitHub Actions secrets. A release is therefore made on GitHub Actions and on no one's machine. A local build is unsigned, and serves to debug the release build — see the last section.

Every block runs as written in a new shell: it works out what it needs from the checkout, git and GitHub. Two values are set by hand: the version being released, written `<version>` in steps 1 and 2 — fill it in, as in `7.0.1` — and the SpriteStudio-SDK release it ships, written `<sdk-tag>` in step 2, as in `v7.0.0`. Left as they are, the line stops with a syntax error. A value that would not stop a command by itself when empty is read as `${VAR:?}`: an empty `RUN` would let the signing check in step 3 print nothing and pass.

An agent can run every block, but five things are a person's: sorting the scan's summary in step 3; the checks in Unity in step 3, which no CI can run; the merge in step 4, which puts the release on `main`; step 6, which puts the version on npm for good; and step 7, which publishes the Release for good.

### 1. Branch

A release that ships `develop`'s work is cut from `develop`: a new `X.Y`, or `7.0.0` after `7.0.0-beta.1`. A patch release ships only fixes: it is cut from the line's latest tag, and its fixes are committed or cherry-picked onto it. Either way, an earlier `release/X.Y` that is still there is replaced. Run the block that fits.

For `develop`'s work:

```bash
VERSION=<version>
BRANCH=release/$(echo "${VERSION:?}" | cut -d. -f1,2)
git fetch origin
git switch -C "$BRANCH" origin/develop
```

For a patch:

```bash
VERSION=<version>
BRANCH=release/$(echo "${VERSION:?}" | cut -d. -f1,2)
git fetch origin --tags
BASE=$(git -c versionsort.suffix=- tag --list "v${BRANCH#release/}.*" --sort=-v:refname | head -n 1)   # the line's latest tag
git switch -C "$BRANCH" "${BASE:?}"
```

### 2. SDK, version and changelog

In one commit. Pin the SpriteStudio-SDK release this one ships, in both places it is read from: `scripts/SDK_VERSION.txt`, the tag `download-sdk` fetches the native libraries and FFI bindings by, and the `SpriteStudio-SDK` submodule. When the SDK's `.fbs` schemas changed, regenerate the C# bindings with `scripts/generate-fbs-code.sh` (`flatc` required); the block's last command lists those changes. When its platforms changed, the native plugins need new `.meta` first — see below. `<version>` is the one from step 1.

```bash
VERSION=<version>
SDK_TAG=<sdk-tag>
OLD_SDK=$(cat scripts/SDK_VERSION.txt)
git submodule update --init SpriteStudio-SDK
git -C SpriteStudio-SDK fetch --tags origin && git -C SpriteStudio-SDK checkout "${SDK_TAG:?}"
printf '%s\n' "${SDK_TAG:?}" >| scripts/SDK_VERSION.txt
(cd package && npm version "${VERSION:?}" --no-git-tag-version --allow-same-version)
scripts/release-version.sh                       # must print the version
git -C SpriteStudio-SDK diff --stat "${OLD_SDK:?}" "$SDK_TAG" -- libs/ssruntime/fbs libs/ssab/fbs   # any output: run scripts/generate-fbs-code.sh
```

Then date the version's heading in `CHANGELOG.md` as `## [X.Y.Z] - YYYY-MM-DD` and point its link line at the tag, adding both if the version has none yet. The changelog ships inside the package, as Package Manager's Changelog tab. Commit:

```bash
git add scripts/SDK_VERSION.txt SpriteStudio-SDK package/Runtime/Format package/package.json CHANGELOG.md
git commit -m "chore(release): v$(scripts/release-version.sh)"
```

### 3. Pull request, QA build and checks in Unity

Open the pull request into `main`; its CI checks, among other things, that every native plugin path has a committed `.meta`:

```bash
BRANCH=$(git branch --show-current | grep '^release/')
git push -u origin "${BRANCH:?}"
gh pr create -B main -H "${BRANCH:?}" --title "Release v$(scripts/release-version.sh)" --body ""
```

Then dispatch the release workflow from the branch. A run from a branch fetches the pinned SDK, packs and signs the tarball and checks it, keeps the result on the run (`ssplayer-unity-release-dist`), and creates no Release; the block downloads it into `qa/<run-id>/`. Fix anything on the branch and run this block again, until the run, the pull request's CI and the checks in Unity below all pass.

```bash
BRANCH=$(git branch --show-current | grep '^release/')
git push origin "${BRANCH:?}" &&
  RUN=$(gh workflow run release.yml --ref "${BRANCH:?}" | sed 's|.*/||') &&
  gh run watch "${RUN:?}" --exit-status &&
  gh run download "${RUN:?}" -n ssplayer-unity-release-dist -D "qa/$RUN" &&
  ! gh run view "${RUN:?}" --log | grep -E 'not set|Skipping' | grep -v 'echo '   # must print nothing
```

A missing signing secret does not fail the run: it packs the tarball unsigned, says so in the log, and passes, and Unity 6.3 then warns whoever installs it. The block's last command looks for that, here and in step 5, and must print nothing. If it prints anything, no change on the branch fixes it: the signing secrets, `UNITY_ORGANIZATION_ID` and the UPM service account's key, are not reaching this repository. An admin of the repository or the organization provides them, and on GitHub Free organization secrets cannot reach a private repository. Dispatch again once they do; in step 5, delete the draft first.

**A person runs these two checks**, on the branch, since no CI has Unity: the test suite (`scripts/run-tests.sh`, as [CONTRIBUTING.md](./CONTRIBUTING.md) describes), and installing the QA tarball from `qa/<run-id>/` into a project through **Window > Package Manager > + > Install package from tarball...**.

Last, scan the branch for code that carries someone else's copyright or license. `provenance.yml` runs ScanCode, which finds copyright and license notices, and keeps its report and a summary on the run (`provenance`); the block downloads them into `qa/<run-id>/` and prints the summary. **A person sorts what the summary lists.** A notice of the earlier SpriteStudio Players and SDK marks our own code. A notice of a library this repository bundles must be listed in `THIRD_PARTY_NOTICES.md`. Anything else is rewritten, unless its license, read at its source, is compatible with BSD-3-Clause and the code keeps the notice that license asks for. After any change on the branch, run the block again.

```bash
BRANCH=$(git branch --show-current | grep '^release/')
git push origin "${BRANCH:?}" &&
  RUN=$(gh workflow run provenance.yml --ref "${BRANCH:?}" | sed 's|.*/||') &&
  gh run watch "${RUN:?}" --exit-status &&
  gh run download "${RUN:?}" -n provenance -D "qa/$RUN" &&
  cat "qa/$RUN/summary.md"
```

### 4. Merge and tag

Merge with a merge commit, never a squash or a rebase, so that `main` keeps the commits the QA build built rather than copies of them. Before merging, check that a QA run built the branch's head green, and that merging it changes nothing in its tree. If it would, `main` holds changes the branch does not, and the tag would go on a tree no QA run built: merge `main` into the branch and go back to step 3.

```bash
BRANCH=$(git branch --show-current | grep '^release/')
git fetch origin
SHA=$(git rev-parse --verify -q "origin/${BRANCH:?}")
gh run list -w release.yml -c "${SHA:?}" -s success -L 1                                             # must list a run
git diff --stat "origin/${BRANCH:?}" "$(git merge-tree --write-tree origin/main "origin/$BRANCH")"   # must print nothing
```

**A person runs the merge**, which puts the release on `main`: choose **Create a merge commit** on the pull request's page, or run

```bash
BRANCH=$(git branch --show-current | grep '^release/')
gh pr merge "${BRANCH:?}" --merge
```

Then tag `main`. The tag goes on the merge commit only when its tree is the branch's, which `git diff` confirms by printing nothing:

```bash
git switch main && git pull --ff-only &&
  git diff --stat --exit-code HEAD^2 HEAD &&
  TAG=v$(scripts/release-version.sh) &&
  git tag -a "$TAG" -m "$TAG" && git push origin "$TAG"
```

### 5. Build the Release

Dispatch from the tag on `main`. The run builds again and creates a **draft** Release named `SpriteStudio Player for Unity <tag>`, carrying the tarballs, `SHA256SUMS` and generated notes; nothing is public yet. A tag that already has a Release, draft or published, keeps it as it is: the run builds, says so in a notice and creates nothing, so delete a draft before dispatching again ([Undoing a release](#undoing-a-release)). With `-f upload_release=false` a run from the tag only builds, its files kept on the run. The block's last command checks the signing as in step 3.

```bash
git fetch origin --tags
TAG=$(git describe --exact-match --tags --match 'v[0-9]*' origin/main)
RUN=$(gh workflow run release.yml --ref "${TAG:?}" -f upload_release=true | sed 's|.*/||') &&
  gh run watch "${RUN:?}" --exit-status &&
  ! gh run view "${RUN:?}" --log | grep -E 'not set|Skipping' | grep -v 'echo '   # must print nothing
```

### 6. Publish to npm

**A person runs this step**: `npm login` and the publish ask for a 2FA code, and a version on npm is permanent — npm never lets its number be used again. The package goes to npm as the very tarball on the draft, which `release.yml` packed and signed: the block prints the signature's path inside it, and publishes nothing when there is none, as in a tarball packed anywhere else. It goes before the Release is published, so that by the time the Release announces the version, a project can install it. Its name has no scope, since a Unity package name cannot carry one; a project reaches it through a scoped registry for `jp.co.cri-mw` at `https://registry.npmjs.org`. `scripts/npm-dist-tag.sh` picks the dist-tag: `latest` for a full release, and for a prerelease while the package's `latest` is not yet a full release; the prerelease's word (`beta`, `rc`) after that. `release.yml` publishes nothing to npm.

```bash
npm login                                         # once
git fetch origin --tags
TAG=$(git describe --exact-match --tags --match 'v[0-9]*' origin/main)
VERSION=${TAG#v}
DIST=$(scripts/npm-dist-tag.sh jp.co.cri-mw.spritestudio.ssplayer-unity "$VERSION")
F="$(mktemp -d)/jp.co.cri-mw.spritestudio.ssplayer-unity-$VERSION.tgz"
gh release download "${TAG:?}" -p "${F##*/}" -D "${F%/*}" &&
  tar -tzf "$F" | grep '\.attestation\.p7m$' &&     # the signature: nothing printed, nothing published
  npm publish "$F" --tag "${DIST:?}"
npm view jp.co.cri-mw.spritestudio.ssplayer-unity dist-tags
```

### 7. Publish the Release

**A person runs this step**: the notes are a judgment, and a published Release is permanent. Open the draft on the [Releases](https://github.com/cri-middleware/SSPlayerForUnity/releases) page and check the attachments: `jp.co.cri-mw.spritestudio.ssplayer-unity-X.Y.Z.tgz`, its alias `jp.co.cri-mw.spritestudio.ssplayer-unity.tgz`, and `SHA256SUMS`. Edit the notes and **Publish release** as the draft is marked: `scripts/release-channel.sh` made it a pre-release when a full release is already tagged, and left it to become Latest otherwise. So `releases/latest/download/jp.co.cri-mw.spritestudio.ssplayer-unity.tgz`, which the README and the docs hand out, stays on the newest full release, or before the first one on the newest prerelease. Only a Release that becomes Latest deploys the site.

Publishing also starts `pages.yml`, which deploys the [documentation site](https://cri-middleware.github.io/SSPlayerForUnity/). Watch the run through, then open the site: a failed run leaves the Release published and the site as it was. Once the cause is fixed, re-run it with `gh run rerun "${RUN:?}"`; a re-run builds the same tag.

```bash
git fetch origin --tags
TAG=$(git describe --exact-match --tags --match 'v[0-9]*' origin/main)
RUN=$(gh run list -w pages.yml -b "${TAG:?}" -L 1 --json databaseId -q '.[0].databaseId')
gh run watch "${RUN:?}" --exit-status             # RUN is empty until the run appears: repeat the last two lines
```

### 8. Merge back

Merge `main` into `develop`. After a patch release this conflicts where `develop` has moved on: keep `develop`'s version and its `Unreleased` heading, with the patch's section below it. The block merges on a detached `origin/develop`, so it runs in a worktree whichever branch the other checkouts hold, and a conflict stops it there: resolve it, `git commit`, and run the block's two `git push` lines. When the next version is decided, set it in `package/package.json` on `develop` and open a `## [X.Y.Z] - Unreleased` heading for it.

```bash
git fetch origin --tags
TAG=$(git describe --exact-match --tags --match 'v[0-9]*' origin/main)
git switch --detach origin/develop &&
  git merge --no-ff -m "Merge branch 'main' into develop" origin/main &&
  git push origin HEAD:develop &&
  git push origin --delete "release/$(echo "${TAG:?}" | sed 's/^v//' | cut -d. -f1,2)"
```

If the button on its pull request already deleted the release branch, the last line only reports that the branch does not exist. Nothing needs it afterwards: the line's next release cuts it again in step 1.

## Patching an earlier line

Fixes go to the latest release (SECURITY.md). When one has to reach an earlier line on request, as 7.0.2 after 7.1.0, `main` and `develop` carry the newer line, so the release differs from the one above in four places, and the blocks below run on the release branch.

1. **Steps 1 to 3, without the pull request.** Step 1's patch block cuts `release/X.Y` from the line's latest tag. Commit or cherry-pick the fix onto it, set the version and date its changelog section as step 2 does, and run step 3 but leave out its `gh pr create`: nothing of this line goes into `main`.
2. **The tag goes on the branch.** Once step 3's runs are green, the first block below tags the head of `release/X.Y`.
3. **Build it, and publish it as an earlier line.** `scripts/release-channel.sh` calls the release `old`: the draft is not a pre-release, its generated notes start from the full release before it in that line, and `pages.yml` deploys nothing for it. Publish it to npm with the third block: `scripts/npm-dist-tag.sh` puts it under `latest-X.Y`, since npm's `latest` is a newer full release, so a bare install keeps getting the newest. Publish the Release with the last block, not from the Releases page, whose default would make it Latest.
4. **No merge back.** Cherry-pick the fix onto `develop` if it still needs it; the last block deletes `release/X.Y`.

```bash
BRANCH=$(git branch --show-current | grep '^release/')
git pull --ff-only origin "${BRANCH:?}" &&
  TAG=v$(scripts/release-version.sh) &&
  git tag -a "$TAG" -m "$TAG" && git push origin "$TAG"
```

```bash
git fetch origin --tags
TAG=$(git describe --exact-match --tags --match 'v[0-9]*' HEAD)
RUN=$(gh workflow run release.yml --ref "${TAG:?}" -f upload_release=true | sed 's|.*/||') &&
  gh run watch "${RUN:?}" --exit-status &&
  ! gh run view "${RUN:?}" --log | grep -E 'not set|Skipping' | grep -v 'echo '   # must print nothing
```

```bash
npm login                                         # once
git fetch origin --tags
TAG=$(git describe --exact-match --tags --match 'v[0-9]*' HEAD)
VERSION=${TAG#v}
DIST=$(scripts/npm-dist-tag.sh jp.co.cri-mw.spritestudio.ssplayer-unity "$VERSION")
F="$(mktemp -d)/jp.co.cri-mw.spritestudio.ssplayer-unity-$VERSION.tgz"
gh release download "${TAG:?}" -p "${F##*/}" -D "${F%/*}" &&
  tar -tzf "$F" | grep '\.attestation\.p7m$' &&     # the signature: nothing printed, nothing published
  npm publish "$F" --tag "${DIST:?}"
npm view jp.co.cri-mw.spritestudio.ssplayer-unity dist-tags
```

```bash
git fetch origin --tags
TAG=$(git describe --exact-match --tags --match 'v[0-9]*' HEAD)
BRANCH=$(git branch --show-current | grep '^release/')
gh release edit "${TAG:?}" --draft=false --latest=false &&
  git push origin --delete "${BRANCH:?}"
```

## Undoing a release

Until step 6 puts the version on npm, a release can be undone. The draft goes first, if the run made one:

```bash
git fetch origin --tags
TAG=$(git describe --exact-match --tags --match 'v[0-9]*' origin/main)
gh release delete "${TAG:?}" --yes
```

A run that failed for a transient reason is then dispatched again from the same tag, with step 5's block. A fault that needs new commits takes the tag down too, on GitHub and here; then fix it on the branch — restore it from its pull request if it was deleted — and go back to step 3.

```bash
git fetch origin --tags
TAG=$(git describe --exact-match --tags --match 'v[0-9]*' origin/main)
git push origin ":refs/tags/${TAG:?}" && git tag -d "${TAG:?}"
```

**Once on npm, a version is permanent**, and so are a published Release's tag and assets — users have the tarball, and the version Package Manager shows names it — so a fix is a new patch version.

## When the SDK's platforms change

A UPM package is extracted into an immutable folder, where Unity ignores any asset without a `.meta`, and no CI has Unity to generate one — so every native plugin's `.meta` is committed, and a native library added, removed or moved by an SDK release needs its `.meta` made by hand before that SDK can ship. The pull request's CI and `build-release.sh` both fail on a plugin path with no committed `.meta`. `scripts/scaffold-plugin-placeholders.sh` makes them, and its header gives the four steps, including the order that keeps Unity from deleting them again.

## Debugging the release build locally

`release.yml`'s job **is** `scripts/build-release.sh`, and unlike the SDK's script of the same name this one builds the whole package on one machine: it is C# plus prebuilt native libraries. **A local build is for debugging the release build, never for shipping**: without the signing credentials, which only GitHub Actions has, it packs the tarball unsigned, and Unity 6.3 warns whoever installs it. It shows what a release will hold and runs the same checks; what ships, on the Release and on npm, is only the tarball `release.yml` signed.

```bash
./scripts/build-release.sh
# Window > Package Manager > + > Install package from tarball...
```

It checks twice, for different failures. **Before packing:** every plugin directory has native libraries, each has a committed `.meta`, and both FFI bindings and their `.meta` are there — a library whose `.meta` was never committed installs as *nothing*, silently. **After packing:** the tarball actually contains them, because `npm pack` applies the `files` allowlist in `package.json`, a second place a file can vanish. (`LICENSE.md` is deliberately absent from `files` — npm always includes a `LICENSE*`, so only its `.meta` has to be listed. The check asserts that rather than trusting anyone to remember.)

`sdk=<download|build>` says where the native libraries come from. `download` is `download-sdk.sh`: the prebuilt release for `scripts/SDK_VERSION.txt`, every platform, and so the only source a shippable package can have. `build` is `build-runtime.sh`, compiled from the submodule, and it is what to use while that SDK release does not exist yet — at the cost of **this host's platform only**. The checks cannot tell those apart, since they ask that each plugin tree has libraries and not that it has all of them, so a one-platform tarball passes and the script says so out loud instead. Use `sdk=build` to get a package to look at; never to cut a release.

<br>

---

# SSPlayerForUnity のリリース手順

維持者向けです。コントリビューターがこの手順を実行することはありません（[CONTRIBUTING.md](./CONTRIBUTING.md) を参照してください）。

## リリースの手順

このリポジトリの Write ロールを持つ人なら誰でもリリースできます。どの手順もブランチ・プルリクエスト・タグ・ワークフローの実行・Release のいずれかで、署名の資格情報は GitHub Actions の Secret なので、Unity が要る手順 3 の確認を除けば、誰の手元で実行しても結果は変わりません。必要なのは `git`、`gh` 2.87 以降（最初に一度 `gh auth login`）、Node.js で、その確認には Unity が、手順 6 にはパッケージを publish できる 2FA 付きの npm アカウントが要ります。リポジトリ側では、GitHub Pages を有効にしてソースを GitHub Actions にし、`github-pages` environment に `v*` のタグのルールを足しておく必要があります。どちらかが欠けると、Release を公開しても何もデプロイされません。どちらもリポジトリの admin が行う設定です。コマンドは bash です。zsh では、`#` のコメントが引数として渡されないよう、先に `setopt interactive_comments` を実行します。Windows では Git Bash を使います。手順 1・4・8 はブランチを切り替えるので、ほかの作業をしていない専用のチェックアウト（クローンか、`git worktree add` で作ったもの）で実行してください。

リリースは `main` 上のタグです。先にタグを push し、ビルドはその後です。バージョンが書かれているのは `package/package.json` の `version` の 1 か所だけで（semver。プレリリースは `7.0.0-beta.1`）、`scripts/release-version.sh` がそれを出力し、タグはその頭に `v` を付けたものです。tarball の名前がこの値で決まるため、食い違うタグはリリースビルドが拒否します。同梱する SDK のバージョンはこれとは別で、`scripts/SDK_VERSION.txt` で固定します。ブランチは git-flow です。`develop` が統合ブランチで、`main` にはリリース済みのコミットだけが入ります。`release/X.Y` は 1 回のリリースの間だけ使います。`develop` の作業を出すリリースなら `develop` から、タグの上に修正だけを載せるリリースならその系統の最新のタグから切り、リリースが済んだら消します。

Unity 6.3 の Package Manager はすべてのパッケージの署名を確かめるので、リリースの tarball は署名します。署名できるのは `release.yml` だけです。資格情報（`UNITY_ORGANIZATION_ID` と UPM のサービスアカウントのキー）は、このリポジトリの GitHub Actions の Secret にしか無いためです。したがってリリースは GitHub Actions で行い、誰の手元でも行いません。ローカルのビルドは署名なしで、リリースビルドのデバッグに使います（最後の節を参照）。

どのブロックも、新しいシェルで書かれたとおりに実行できます。必要なものはチェックアウト・git・GitHub から求めます。手で設定する値は 2 つで、リリースするバージョンが手順 1 と 2 の `<version>`（`7.0.1` のように埋めます）、同梱する SpriteStudio-SDK のリリースが手順 2 の `<sdk-tag>`（`v7.0.0` のように埋めます）です。埋めずに実行すると、その行は構文エラーで止まります。空でもそれだけでは止まらない値は `${VAR:?}` で読みます。`RUN` が空だと、手順 3 の署名の確認が何も出力せずに通ってしまうためです。

どのブロックもエージェントに実行させられますが、5 つのことは人が行います。手順 3 の検査の一覧の仕分け、どの CI でも実行できない手順 3 の Unity での確認、リリースを `main` に入れる手順 4 のマージ、取り消せない npm への公開を行う手順 6、同じく取り消せない Release の公開を行う手順 7 です。

### 1. ブランチ

`develop` の作業を出すリリースは `develop` から切ります。新しい `X.Y` や、`7.0.0-beta.1` の後の `7.0.0` がこれに当たります。パッチリリースは修正だけを出すもので、その系統の最新のタグから切り、修正をそこへコミットするか cherry-pick します。どちらの場合も、以前の `release/X.Y` が残っていれば置き換えます。当てはまる方のブロックを実行します。

`develop` の作業を出すとき:

```bash
VERSION=<version>
BRANCH=release/$(echo "${VERSION:?}" | cut -d. -f1,2)
git fetch origin
git switch -C "$BRANCH" origin/develop
```

パッチのとき:

```bash
VERSION=<version>
BRANCH=release/$(echo "${VERSION:?}" | cut -d. -f1,2)
git fetch origin --tags
BASE=$(git -c versionsort.suffix=- tag --list "v${BRANCH#release/}.*" --sort=-v:refname | head -n 1)   # その系統の最新のタグ
git switch -C "$BRANCH" "${BASE:?}"
```

### 2. SDK、バージョン、CHANGELOG

1 コミットで行います。同梱する SpriteStudio-SDK のリリースを、読まれる 2 か所の両方で固定します。`download-sdk` がネイティブライブラリと FFI バインディングを取得するときに使うタグ `scripts/SDK_VERSION.txt` と、`SpriteStudio-SDK` submodule です。SDK の `.fbs` スキーマが変わったときは、`scripts/generate-fbs-code.sh` で C# のバインディングを生成し直します（`flatc` が必要です）。ブロックの最後のコマンドがその変更を一覧します。SDK のプラットフォーム構成が変わったときは、先にネイティブプラグインの `.meta` を作る必要があります（後述）。`<version>` は手順 1 と同じものです。

```bash
VERSION=<version>
SDK_TAG=<sdk-tag>
OLD_SDK=$(cat scripts/SDK_VERSION.txt)
git submodule update --init SpriteStudio-SDK
git -C SpriteStudio-SDK fetch --tags origin && git -C SpriteStudio-SDK checkout "${SDK_TAG:?}"
printf '%s\n' "${SDK_TAG:?}" >| scripts/SDK_VERSION.txt
(cd package && npm version "${VERSION:?}" --no-git-tag-version --allow-same-version)
scripts/release-version.sh                       # バージョンが出力されること
git -C SpriteStudio-SDK diff --stat "${OLD_SDK:?}" "$SDK_TAG" -- libs/ssruntime/fbs libs/ssab/fbs   # 何か出力されたら scripts/generate-fbs-code.sh を実行する
```

続けて `CHANGELOG.md` のそのバージョンの見出しに日付を入れて `## [X.Y.Z] - YYYY-MM-DD` とし、リンク行をタグへ向けます。そのバージョンの見出しとリンク行がまだ無ければ足します。CHANGELOG はパッケージに同梱され、Package Manager の Changelog タブにそのまま表示されます。そのあとコミットします。

```bash
git add scripts/SDK_VERSION.txt SpriteStudio-SDK package/Runtime/Format package/package.json CHANGELOG.md
git commit -m "chore(release): v$(scripts/release-version.sh)"
```

### 3. プルリクエスト、QA ビルド、Unity での確認

`main` へのプルリクエストを作ります。その CI は、すべてのネイティブプラグインのパスにコミット済みの `.meta` があるかなどを検査します。

```bash
BRANCH=$(git branch --show-current | grep '^release/')
git push -u origin "${BRANCH:?}"
gh pr create -B main -H "${BRANCH:?}" --title "Release v$(scripts/release-version.sh)" --body ""
```

続けて、ブランチからリリースワークフローを実行します。ブランチからの実行では、固定した SDK を取得し、tarball を作って署名・検査し、結果を run に残し（`ssplayer-unity-release-dist`）、Release は作りません。ブロックはそれを `qa/<run-id>/` にダウンロードします。問題があればブランチで直し、このブロックをもう一度実行します。run、プルリクエストの CI、下の Unity での確認がすべて通るまで繰り返します。

```bash
BRANCH=$(git branch --show-current | grep '^release/')
git push origin "${BRANCH:?}" &&
  RUN=$(gh workflow run release.yml --ref "${BRANCH:?}" | sed 's|.*/||') &&
  gh run watch "${RUN:?}" --exit-status &&
  gh run download "${RUN:?}" -n ssplayer-unity-release-dist -D "qa/$RUN" &&
  ! gh run view "${RUN:?}" --log | grep -E 'not set|Skipping' | grep -v 'echo '   # 何も出力されないこと
```

署名の Secret が欠けていても、この run は失敗しません。tarball を署名なしで作り、その旨をログに出して成功します。署名の無い tarball は、Unity 6.3 がインストールする人に警告します。ブロックの最後のコマンドはそれを探すもので、ここでも手順 5 でも何も出力されてはいけません。何か出力されたら、ブランチをどう変えても直りません。署名用の Secret（`UNITY_ORGANIZATION_ID` と UPM のサービスアカウントのキー）がこのリポジトリに届いていません。届けるのはリポジトリか組織の admin で、GitHub Free では組織の Secret は private のリポジトリに届けられません。届くようになったら実行し直してください。手順 5 では先に下書きを消します。

**この 2 つの確認は人が行います。** どの CI にも Unity が無いためです。ブランチ上で、テストスイート（[CONTRIBUTING.md](./CONTRIBUTING.md) にある `scripts/run-tests.sh`）を実行し、`qa/<run-id>/` の tarball を **Window > Package Manager > + > Install package from tarball...** からプロジェクトに入れます。

最後に、他者の著作権やライセンスの付いたコードが無いかを、ブランチで検査します。`provenance.yml` は ScanCode で著作権表示とライセンスの記述を見つけ、報告と一覧を run に残します（`provenance`）。ブロックはそれを `qa/<run-id>/` にダウンロードし、一覧を表示します。**一覧は人が仕分けます。** 以前の SpriteStudio の Player・SDK の表示が付いたコードは、自社のコードです。このリポジトリが同梱するライブラリの表示は、`THIRD_PARTY_NOTICES.md` に載っていなければなりません。それ以外は書き直します。残せるのは、出所で確かめたライセンスが BSD-3-Clause と両立し、そのライセンスが求める表示をコードに残すときだけです。ブランチを変えたら、このブロックをもう一度実行します。

```bash
BRANCH=$(git branch --show-current | grep '^release/')
git push origin "${BRANCH:?}" &&
  RUN=$(gh workflow run provenance.yml --ref "${BRANCH:?}" | sed 's|.*/||') &&
  gh run watch "${RUN:?}" --exit-status &&
  gh run download "${RUN:?}" -n provenance -D "qa/$RUN" &&
  cat "qa/$RUN/summary.md"
```

### 4. マージとタグ

マージはマージコミットで行い、squash や rebase は使いません。QA ビルドしたコミットの写しではなく、そのものを `main` に残すためです。マージの前に、ブランチの先頭を QA の run が緑でビルドしていること、マージしてもブランチのツリーが変わらないことを確かめます。変わるなら `main` にブランチの持たない変更があり、どの QA の run もビルドしていないツリーにタグが付きます。そのときは `main` をブランチへマージして手順 3 に戻ります。

```bash
BRANCH=$(git branch --show-current | grep '^release/')
git fetch origin
SHA=$(git rev-parse --verify -q "origin/${BRANCH:?}")
gh run list -w release.yml -c "${SHA:?}" -s success -L 1                                             # run が 1 つ表示されること
git diff --stat "origin/${BRANCH:?}" "$(git merge-tree --write-tree origin/main "origin/$BRANCH")"   # 何も出力されないこと
```

**マージは人が行います。** リリースが `main` に入るためです。プルリクエストのページで **Create a merge commit** を選ぶか、次を実行します。

```bash
BRANCH=$(git branch --show-current | grep '^release/')
gh pr merge "${BRANCH:?}" --merge
```

続けて `main` にタグを打ちます。タグはマージコミットのツリーがブランチと同じときにだけ打たれます。`git diff` が何も出力しないことがその確認です。

```bash
git switch main && git pull --ff-only &&
  git diff --stat --exit-code HEAD^2 HEAD &&
  TAG=v$(scripts/release-version.sh) &&
  git tag -a "$TAG" -m "$TAG" && git push origin "$TAG"
```

### 5. Release のビルド

`main` 上のタグからワークフローを実行します。もう一度ビルドし、`SpriteStudio Player for Unity <タグ>` という名前の**下書き**の Release を、tarball と `SHA256SUMS`、自動生成のリリースノート付きで作ります。まだ何も公開されていません。Release がすでにあるタグでは、下書きでも公開済みでも、その Release に触れません。run はビルドして notice を出すだけなので、もう一度実行するときは先に下書きを消します（[リリースの取り消し](#リリースの取り消し)）。`-f upload_release=false` を付けると、タグからの実行でもビルドだけを行い、成果物を run に残します。ブロックの最後のコマンドで、手順 3 と同じように署名を確認します。

```bash
git fetch origin --tags
TAG=$(git describe --exact-match --tags --match 'v[0-9]*' origin/main)
RUN=$(gh workflow run release.yml --ref "${TAG:?}" -f upload_release=true | sed 's|.*/||') &&
  gh run watch "${RUN:?}" --exit-status &&
  ! gh run view "${RUN:?}" --log | grep -E 'not set|Skipping' | grep -v 'echo '   # 何も出力されないこと
```

### 6. npm への公開

**この手順は人が行います。** `npm login` と publish のたびに 2FA のコードを求められ、npm に出したバージョンは取り消せないためです（同じ番号は二度と使えません）。npm には、`release.yml` が作って署名した、下書きに添付されている tarball そのものを出します。ブロックは tarball の中の署名のパスを出力し、署名が無ければ何も publish しません。ほかの場所で作った tarball には署名がありません。Release を公開するより先に行い、Release がバージョンを告知する時点でプロジェクトからインストールできるようにします。パッケージ名に scope はありません（Unity のパッケージ名は scope を持てません）。プロジェクトは `https://registry.npmjs.org` を `jp.co.cri-mw` の scoped registry として登録して使います。dist-tag は `scripts/npm-dist-tag.sh` が決めます。正式版は `latest` です。プレリリースも、パッケージの `latest` がまだ正式版でなければ `latest`、正式版になった後はその語（`beta`・`rc`）です。`release.yml` は npm に何も出しません。

```bash
npm login                                         # 最初に一度
git fetch origin --tags
TAG=$(git describe --exact-match --tags --match 'v[0-9]*' origin/main)
VERSION=${TAG#v}
DIST=$(scripts/npm-dist-tag.sh jp.co.cri-mw.spritestudio.ssplayer-unity "$VERSION")
F="$(mktemp -d)/jp.co.cri-mw.spritestudio.ssplayer-unity-$VERSION.tgz"
gh release download "${TAG:?}" -p "${F##*/}" -D "${F%/*}" &&
  tar -tzf "$F" | grep '\.attestation\.p7m$' &&     # 署名。何も出力されなければ publish しない
  npm publish "$F" --tag "${DIST:?}"
npm view jp.co.cri-mw.spritestudio.ssplayer-unity dist-tags
```

### 7. Release の公開

**この手順は人が行います。** リリースノートの中身は判断が要り、公開した Release は取り消せないためです。[Releases](https://github.com/cri-middleware/SSPlayerForUnity/releases) ページで下書きを開き、添付を確かめます。`jp.co.cri-mw.spritestudio.ssplayer-unity-X.Y.Z.tgz` とそのエイリアス `jp.co.cri-mw.spritestudio.ssplayer-unity.tgz`、`SHA256SUMS` です。リリースノートを編集し、下書きの印のまま **Publish release** を押します。`scripts/release-channel.sh` が、正式版のタグがすでにあれば pre-release にし、なければ Latest になるようにしてあります。そのため、README と docs が案内する `releases/latest/download/jp.co.cri-mw.spritestudio.ssplayer-unity.tgz` は最新の正式版を指し続けます（最初の正式版より前は最新のプレリリース）。サイトをデプロイするのは Latest になる Release だけです。

公開すると `pages.yml` が動き、[ドキュメントサイト](https://cri-middleware.github.io/SSPlayerForUnity/) をデプロイします。run を最後まで見届けてから、サイトを開いて確かめます。run が失敗しても Release は公開されたままで、サイトは元のまま残ります。原因を直したら `gh run rerun "${RUN:?}"` で再実行します。再実行は同じタグをビルドします。

```bash
git fetch origin --tags
TAG=$(git describe --exact-match --tags --match 'v[0-9]*' origin/main)
RUN=$(gh run list -w pages.yml -b "${TAG:?}" -L 1 --json databaseId -q '.[0].databaseId')
gh run watch "${RUN:?}" --exit-status             # run が出るまで RUN は空。最後の 2 行を繰り返す
```

### 8. develop へ戻す

`main` を `develop` へマージします。パッチリリースの後は、`develop` が先へ進んだ箇所で衝突します。`develop` のバージョンと `Unreleased` の見出しを残し、パッチの節をその下に置いてください。ブロックは切り離した `origin/develop` の上でマージするので、ほかのチェックアウトがどのブランチにあっても worktree で実行できます。衝突したらそこで止まります。解決して `git commit` し、ブロックの 2 つの `git push` を実行してください。次のバージョンが決まったら、`develop` の `package/package.json` を設定し、`## [X.Y.Z] - Unreleased` の見出しを立てます。

```bash
git fetch origin --tags
TAG=$(git describe --exact-match --tags --match 'v[0-9]*' origin/main)
git switch --detach origin/develop &&
  git merge --no-ff -m "Merge branch 'main' into develop" origin/main &&
  git push origin HEAD:develop &&
  git push origin --delete "release/$(echo "${TAG:?}" | sed 's/^v//' | cut -d. -f1,2)"
```

プルリクエストのページのボタンでリリースブランチを消してあれば、最後の行は「ブランチが無い」と出るだけです。同じ系統の次のリリースは手順 1 で切り直すので、この後ブランチが要ることはありません。

## 古い系統へのパッチ

修正は最新のリリースにだけ出します（SECURITY.md）。依頼を受けて古い系統を直すとき（7.1.0 の後の 7.0.2 など）は、`main` と `develop` が新しい系統を持っているので、上の手順と 4 か所が違います。下のブロックはリリースブランチで実行します。

1. **手順 1〜3 を、プルリクエストなしで。** 手順 1 のパッチのブロックで、その系統の最新のタグから `release/X.Y` を切ります。修正をコミットするか cherry-pick し、手順 2 のとおりにバージョンを設定して CHANGELOG の節に日付を入れ、手順 3 を `gh pr create` を除いて実行します。この系統のものは `main` に入れません。
2. **タグはブランチに打つ。** 手順 3 の run が通ったら、最初のブロックで `release/X.Y` の先頭にタグを打ちます。
3. **ビルドして、古い系統として公開する。** `scripts/release-channel.sh` がこのリリースを `old` と判定します。下書きは pre-release にならず、自動生成のノートはその系統の 1 つ前の正式版から比べ、`pages.yml` は何もデプロイしません。npm へは 3 つめのブロックで公開します。npm の `latest` がより新しい正式版なので、`scripts/npm-dist-tag.sh` は `latest-X.Y` を選び、素のインストールでは最新の版が入り続けます。公開は Releases ページではなく最後のブロックで行います。ページの既定のままだと Latest になるためです。
4. **develop へは戻さない。** `develop` にも要る修正なら cherry-pick します。最後のブロックが `release/X.Y` を消します。

```bash
BRANCH=$(git branch --show-current | grep '^release/')
git pull --ff-only origin "${BRANCH:?}" &&
  TAG=v$(scripts/release-version.sh) &&
  git tag -a "$TAG" -m "$TAG" && git push origin "$TAG"
```

```bash
git fetch origin --tags
TAG=$(git describe --exact-match --tags --match 'v[0-9]*' HEAD)
RUN=$(gh workflow run release.yml --ref "${TAG:?}" -f upload_release=true | sed 's|.*/||') &&
  gh run watch "${RUN:?}" --exit-status &&
  ! gh run view "${RUN:?}" --log | grep -E 'not set|Skipping' | grep -v 'echo '   # 何も出力されないこと
```

```bash
npm login                                         # 最初に一度
git fetch origin --tags
TAG=$(git describe --exact-match --tags --match 'v[0-9]*' HEAD)
VERSION=${TAG#v}
DIST=$(scripts/npm-dist-tag.sh jp.co.cri-mw.spritestudio.ssplayer-unity "$VERSION")
F="$(mktemp -d)/jp.co.cri-mw.spritestudio.ssplayer-unity-$VERSION.tgz"
gh release download "${TAG:?}" -p "${F##*/}" -D "${F%/*}" &&
  tar -tzf "$F" | grep '\.attestation\.p7m$' &&     # 署名。何も出力されなければ publish しない
  npm publish "$F" --tag "${DIST:?}"
npm view jp.co.cri-mw.spritestudio.ssplayer-unity dist-tags
```

```bash
git fetch origin --tags
TAG=$(git describe --exact-match --tags --match 'v[0-9]*' HEAD)
BRANCH=$(git branch --show-current | grep '^release/')
gh release edit "${TAG:?}" --draft=false --latest=false &&
  git push origin --delete "${BRANCH:?}"
```

## リリースの取り消し

手順 6 でバージョンを npm に出すまでは取り消せます。run が下書きを作っていれば、先にそれを消します。

```bash
git fetch origin --tags
TAG=$(git describe --exact-match --tags --match 'v[0-9]*' origin/main)
gh release delete "${TAG:?}" --yes
```

一時的な理由で失敗した run は、そのあと手順 5 のブロックで同じタグからもう一度実行します。新しいコミットが要る不具合なら、タグも GitHub と手元の両方から取り下げ、ブランチで直して（消していたらプルリクエストのページから復元して）手順 3 に戻ります。

```bash
git fetch origin --tags
TAG=$(git describe --exact-match --tags --match 'v[0-9]*' origin/main)
git push origin ":refs/tags/${TAG:?}" && git tag -d "${TAG:?}"
```

**npm に出したバージョンは恒久的です。** 公開した Release のタグとアセットも同じです。利用者はその tarball を持っていて、Package Manager が表示するバージョンがそれを名指ししているので、修正は新しいパッチバージョンで出します。

## SDK のプラットフォーム構成が変わったとき

UPM パッケージは変更不可のフォルダに展開され、Unity は `.meta` の無いアセットを無視します。`.meta` を生成できる Unity はどの CI にも無いので、ネイティブプラグインの `.meta` はすべてコミットしてあり、SDK のリリースでネイティブライブラリが増えたり、消えたり、場所が変わったりしたら、その SDK を出す前に `.meta` を手で作る必要があります。コミット済みの `.meta` が無いプラグインのパスがあると、プルリクエストの CI と `build-release.sh` の両方が失敗します。`.meta` を作るのは `scripts/scaffold-plugin-placeholders.sh` で、その冒頭のコメントに、Unity にまた消されないための順序も含めた 4 つの手順があります。

## リリースビルドのローカルでのデバッグ

`release.yml` のジョブは `scripts/build-release.sh` です。同名の SDK のスクリプトとは違い、こちらは 1 台でパッケージ全体を作れます（中身は C# とビルド済みネイティブライブラリだけだからです）。**ローカルのビルドはリリースビルドのデバッグ用で、出荷には使えません。** 署名の資格情報は GitHub Actions にしか無いので、tarball は署名なしで作られ、インストールすると Unity 6.3 が警告します。リリースが何を持つかを見て、同じ検査を回すためのものです。Release にも npm にも出すのは、`release.yml` が署名した tarball だけです。

```bash
./scripts/build-release.sh
# Window > Package Manager > + > Install package from tarball...
```

このスクリプトは、別々の失敗に対して 2 回検査します。**パッケージ前**: 各 plugin ディレクトリにネイティブライブラリがあり、それぞれにコミット済みの `.meta` があり、FFI バインディングとその `.meta` も揃っていること。`.meta` をコミットし忘れたライブラリは、何も言わずに**存在しないもの**としてインストールされます。**パッケージ後**: tarball に実際に入っていること。`npm pack` は `package.json` の `files` 許可リストを適用するため、1 つめの検査では見えない 2 つめの消失ポイントになります。（`LICENSE.md` を `files` に入れていないのは意図的です。npm は `LICENSE*` を常に含めるので、列挙が要るのは `.meta` だけです。覚えているかどうかに任せず、検査がそう表明します。）

`sdk=<download|build>` はネイティブライブラリの入手元です。`download` は `download-sdk.sh` で、`scripts/SDK_VERSION.txt` のビルド済みリリースを全プラットフォーム分取得します。出荷可能なパッケージが持てる唯一の入手元です。`build` は `build-runtime.sh` で、submodule の Rust からコンパイルします。そのバージョンの SDK リリースがまだ存在しないときに使うもので、代償は**このホストのプラットフォームだけ**になることです。検査はこの 2 つを区別できません（各 plugin ツリーにライブラリが「ある」ことを見るだけで、「すべてある」ことは見ないため）。1 プラットフォームだけの tarball も通るので、代わりにスクリプトが明示的に警告します。`sdk=build` は中身を見るためのもので、リリースを切るために使ってはいけません。
