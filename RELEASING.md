# Releasing SSPlayerForUnity

Maintainers only. Contributors do not run any of this — see [CONTRIBUTING.md](./CONTRIBUTING.md).

## Cutting a release

Anyone with the Write role on this repository can cut a release: every step is a branch, a pull request, a tag, a workflow run or a Release, and the signing credentials are organization secrets, so nothing depends on whose machine runs it beyond the checks in step 3 that need Unity. You need `git`, `gh` 2.87 or later (run `gh auth login` once) and Node.js, plus Unity for those checks. The commands are bash; on Windows, run them in Git Bash.

A release is a tag on `main`, pushed first and built second. The version is written in one place, `version` in `package/package.json` (semver, `7.0.0-beta.1` for a prerelease): `scripts/release-version.sh` prints it, the tag is `v` followed by it, and the release build refuses a tag that disagrees, since the tarball is named after it. The SDK it ships is pinned on its own, in `scripts/SDK_VERSION.txt`. Branches follow git-flow: `develop` integrates, `main` holds released commits only, and `release/X.Y` lives for one release — cut from `develop` for a new `X.Y`, or from the line's latest tag for a patch, and deleted once the release is out.

```bash
VERSION=7.0.1                                       # what is being released
BRANCH=release/$(echo "$VERSION" | cut -d. -f1,2)   # release/7.0
```

### 1. Branch

A new `X.Y` is cut from `develop`. A patch release is cut from the line's latest tag, whether or not an earlier `release/X.Y` is still there, and its fixes are committed or cherry-picked onto it.

```bash
git fetch origin
git switch -c "$BRANCH" origin/develop           # a new X.Y
git switch -C "$BRANCH" v7.0.0                   # a patch release: the line's latest tag
```

### 2. SDK, version and changelog

In one commit. Pin the SpriteStudio-SDK release this one ships, in both places it is read from: `scripts/SDK_VERSION.txt`, the tag `download-sdk` fetches the native libraries and FFI bindings by, and the `SpriteStudio-SDK` submodule. When the SDK's `.fbs` schemas changed, regenerate the C# bindings (`flatc` required); when its platforms changed, the native plugins need new `.meta` first — see below. Then set the version, and date the version's heading in `CHANGELOG.md` as `## [X.Y.Z] - YYYY-MM-DD`, pointing its link line at the tag; on a patch release, add both. The changelog ships inside the package, as Package Manager's Changelog tab.

```bash
SDK_TAG=v7.0.0                                   # the SDK release this ships
printf '%s\n' "$SDK_TAG" > scripts/SDK_VERSION.txt
git -C SpriteStudio-SDK fetch --tags origin && git -C SpriteStudio-SDK checkout "$SDK_TAG"
scripts/generate-fbs-code.sh                     # only when the SDK's schema changed
(cd package && npm version "$VERSION" --no-git-tag-version --allow-same-version)
scripts/release-version.sh                       # must print $VERSION
# date the heading in CHANGELOG.md and point its link line at the tag, then:
git add scripts/SDK_VERSION.txt SpriteStudio-SDK package/Runtime/Format package/package.json CHANGELOG.md
git commit -m "chore(release): v$VERSION"
```

### 3. Pull request, QA build and checks in Unity

Open the pull request into `main`; its CI checks, among other things, that every native plugin path has a committed `.meta`. Then dispatch the release workflow from the branch. With `upload_release` left false it fetches the pinned SDK, packs and signs the tarball and checks it, keeps the result on the run (`ssplayer-unity-release-dist`), and creates no Release. Fix anything on the branch and repeat until it is green.

```bash
git push -u origin "$BRANCH"
gh pr create -B main --title "Release v$VERSION" --body ""
RUN=$(gh workflow run release.yml --ref "$BRANCH" | sed 's|.*/||')
gh run watch "${RUN:?}" --exit-status
gh run download "${RUN:?}" -n ssplayer-unity-release-dist -D qa
```

A missing signing secret does not fail the run: it packs the tarball unsigned, says so in the log, and passes, and Unity 6.3 then warns whoever installs it. In this run and in step 5, this must print nothing:

```bash
gh run view "${RUN:?}" --log | grep -E 'not set|Skipping' | grep -v 'echo '
```

No CI has Unity, so two checks are yours, on the branch: the test suite (`scripts/run-tests.sh`, as [CONTRIBUTING.md](./CONTRIBUTING.md) describes), and installing the QA tarball into a project through **Window > Package Manager > + > Install package from tarball...**.

### 4. Merge and tag

Merge with a merge commit, never a squash or a rebase, so that `main` keeps the commits the QA build built rather than copies of them. Tag `main` only when its tree is the one the QA build built. It differs only when `main` held commits the branch did not; merge `main` into the branch and go back to step 3.

```bash
gh pr merge "$BRANCH" --merge
git switch main && git pull --ff-only
git diff --stat HEAD^2 HEAD                           # must print nothing
TAG=v$(scripts/release-version.sh) && echo "$TAG"
git tag -a "$TAG" -m "$TAG" && git push origin "$TAG"
```

### 5. Build the Release

Dispatch from the tag. The run builds again and creates a **draft** Release named `SpriteStudio Player for Unity <tag>`, carrying the tarballs, `SHA256SUMS` and generated notes; nothing is public yet. `upload_release=true` from anything but a `v*` tag fails the run rather than skipping the Release quietly. Check the signing as in step 3.

```bash
RUN=$(gh workflow run release.yml --ref "${TAG:?}" -f upload_release=true | sed 's|.*/||')
gh run watch "${RUN:?}" --exit-status
```

### 6. Publish

Open the draft on the [Releases](https://github.com/cri-middleware/SSPlayerForUnity/releases) page and check the attachments: `jp.co.cri-mw.spritestudio.ssplayer-unity-X.Y.Z.tgz`, its alias `jp.co.cri-mw.spritestudio.ssplayer-unity.tgz`, and `SHA256SUMS`. Edit the notes and choose **Set as the latest release** — **a beta too, never Set as a pre-release**: the README and the docs hand out `releases/latest/download/jp.co.cri-mw.spritestudio.ssplayer-unity.tgz`, which resolves only to a release that is not a pre-release. Then **Publish release**. Publishing also starts `pages.yml`, which deploys the [documentation site](https://cri-middleware.github.io/SSPlayerForUnity/). Watch the run through, then open the site: a failed run leaves the Release published and the site as it was. Once the cause is fixed, re-run it; a re-run builds the same tag.

```bash
RUN=$(gh run list -w pages.yml -b "${TAG:?}" -L 1 --json databaseId -q '.[0].databaseId')
gh run watch "${RUN:?}" --exit-status             # RUN is empty for the few seconds before the run appears
```

### 7. Merge back

Merge `main` into `develop`. After a patch release this conflicts where `develop` has moved on: keep `develop`'s version and its `Unreleased` heading, with the patch's section below it. When the next version is decided, set it in `package/package.json` on `develop` and open a `## [X.Y.Z] - Unreleased` heading for it.

```bash
git switch develop && git pull --ff-only
git merge --no-ff main && git push origin develop
git push origin --delete "$BRANCH"
```

If the button on its pull request already deleted the release branch, the last line only reports that the branch does not exist. Nothing needs it afterwards: a patch release cuts it again from the tag.

## Undoing a release

Until the Release is published, it can be undone. A run that failed for a transient reason is dispatched again from the same tag once its draft is deleted. A fault that needs new commits takes the tag down too: delete the draft, run `git push origin ":refs/tags/$TAG" && git tag -d "$TAG"`, fix it on the branch — restore it from its pull request if it was deleted — and go back to step 3.

**Once published, the tag and its assets are permanent** — users have the tarball, and the version Package Manager shows names it — so a fix is a new patch version.

## When the SDK's platforms change

A UPM package is extracted into an immutable folder, where Unity ignores any asset without a `.meta`, and no CI has Unity to generate one — so every native plugin's `.meta` is committed, and a native library added, removed or moved by an SDK release needs its `.meta` made by hand before that SDK can ship. The pull request's CI and `build-release.sh` both fail on a plugin path with no committed `.meta`. `scripts/scaffold-plugin-placeholders.sh` makes them, and its header gives the four steps, including the order that keeps Unity from deleting them again.

## Building the package locally

`release.yml`'s job **is** `scripts/build-release.sh`, and unlike the SDK's script of the same name this one really does produce the whole package on one machine: it is C# plus prebuilt native libraries. The way to review a release is to build it and install it.

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

このリポジトリの Write ロールを持つ人なら誰でもリリースできます。どの手順もブランチ・プルリクエスト・タグ・ワークフローの実行・Release のいずれかで、署名の資格情報は組織の Secret なので、Unity が要る手順 3 の確認を除けば、誰の手元で実行しても結果は変わりません。必要なのは `git`、`gh` 2.87 以降（最初に一度 `gh auth login`）、Node.js で、その確認には Unity も要ります。コマンドは bash で書いています。Windows では Git Bash で実行してください。

リリースは `main` 上のタグです。先にタグを push し、ビルドはその後です。バージョンが書かれているのは `package/package.json` の `version` の 1 か所だけで（semver。プレリリースは `7.0.0-beta.1`）、`scripts/release-version.sh` がそれを出力し、タグはその頭に `v` を付けたものです。tarball の名前がこの値で決まるため、食い違うタグはリリースビルドが拒否します。同梱する SDK のバージョンはこれとは別で、`scripts/SDK_VERSION.txt` で固定します。ブランチは git-flow です。`develop` が統合ブランチで、`main` にはリリース済みのコミットだけが入ります。`release/X.Y` は 1 回のリリースの間だけ使います。新しい `X.Y` なら `develop` から、パッチならその系統の最新のタグから切り、リリースが済んだら消します。

```bash
VERSION=7.0.1                                       # リリースするバージョン
BRANCH=release/$(echo "$VERSION" | cut -d. -f1,2)   # release/7.0
```

### 1. ブランチ

新しい `X.Y` は `develop` から切ります。パッチリリースは、以前の `release/X.Y` が残っているかどうかに関係なく、その系統の最新のタグから切り、修正をそこへコミットするか cherry-pick します。

```bash
git fetch origin
git switch -c "$BRANCH" origin/develop           # 新しい X.Y
git switch -C "$BRANCH" v7.0.0                   # パッチリリース: その系統の最新のタグ
```

### 2. SDK、バージョン、CHANGELOG

1 コミットで行います。同梱する SpriteStudio-SDK のリリースを、読まれる 2 か所の両方で固定します。`download-sdk` がネイティブライブラリと FFI バインディングを取得するときに使うタグ `scripts/SDK_VERSION.txt` と、`SpriteStudio-SDK` submodule です。SDK の `.fbs` スキーマが変わったときは C# のバインディングを生成し直します（`flatc` が必要です）。SDK のプラットフォーム構成が変わったときは、先にネイティブプラグインの `.meta` を作る必要があります（後述）。続けてバージョンを設定し、`CHANGELOG.md` のそのバージョンの見出しに日付を入れて `## [X.Y.Z] - YYYY-MM-DD` とし、リンク行をタグへ向けます。パッチリリースでは両方を足します。CHANGELOG はパッケージに同梱され、Package Manager の Changelog タブにそのまま表示されます。

```bash
SDK_TAG=v7.0.0                                   # 同梱する SDK のリリース
printf '%s\n' "$SDK_TAG" > scripts/SDK_VERSION.txt
git -C SpriteStudio-SDK fetch --tags origin && git -C SpriteStudio-SDK checkout "$SDK_TAG"
scripts/generate-fbs-code.sh                     # SDK のスキーマが変わったときだけ
(cd package && npm version "$VERSION" --no-git-tag-version --allow-same-version)
scripts/release-version.sh                       # $VERSION が出力されること
# CHANGELOG.md の見出しに日付を入れ、リンク行をタグへ向けてから:
git add scripts/SDK_VERSION.txt SpriteStudio-SDK package/Runtime/Format package/package.json CHANGELOG.md
git commit -m "chore(release): v$VERSION"
```

### 3. プルリクエスト、QA ビルド、Unity での確認

`main` へのプルリクエストを作ります。その CI は、すべてのネイティブプラグインのパスにコミット済みの `.meta` があるかなどを検査します。続けてブランチからリリースワークフローを実行します。`upload_release` を false のままにすると、固定した SDK を取得し、tarball を作って署名・検査し、結果を run に残し（`ssplayer-unity-release-dist`）、Release は作りません。問題があればブランチで直し、緑になるまで繰り返します。

```bash
git push -u origin "$BRANCH"
gh pr create -B main --title "Release v$VERSION" --body ""
RUN=$(gh workflow run release.yml --ref "$BRANCH" | sed 's|.*/||')
gh run watch "${RUN:?}" --exit-status
gh run download "${RUN:?}" -n ssplayer-unity-release-dist -D qa
```

署名の Secret が欠けていても、この run は失敗しません。tarball を署名なしで作り、その旨をログに出して成功します。署名の無い tarball は、Unity 6.3 がインストールする人に警告します。この run と手順 5 の run で、次のコマンドが何も出力しないことを確認してください。

```bash
gh run view "${RUN:?}" --log | grep -E 'not set|Skipping' | grep -v 'echo '
```

どの CI にも Unity が無いので、2 つの確認はブランチ上で自分で行います。テストスイート（[CONTRIBUTING.md](./CONTRIBUTING.md) にある `scripts/run-tests.sh`）と、QA の tarball を **Window > Package Manager > + > Install package from tarball...** からプロジェクトに入れることです。

### 4. マージとタグ

マージはマージコミットで行い、squash や rebase は使いません。QA ビルドしたコミットの写しではなく、そのものを `main` に残すためです。タグは、`main` のツリーが QA ビルドしたものと同じときにだけ打ちます。違うのは `main` にブランチが持たないコミットがあった場合だけで、そのときは `main` をブランチへマージして手順 3 に戻ります。

```bash
gh pr merge "$BRANCH" --merge
git switch main && git pull --ff-only
git diff --stat HEAD^2 HEAD                           # 何も出力されないこと
TAG=v$(scripts/release-version.sh) && echo "$TAG"
git tag -a "$TAG" -m "$TAG" && git push origin "$TAG"
```

### 5. Release のビルド

タグからワークフローを実行します。もう一度ビルドし、`SpriteStudio Player for Unity <タグ>` という名前の**下書き**の Release を、tarball と `SHA256SUMS`、自動生成のリリースノート付きで作ります。まだ何も公開されていません。`v*` タグ以外からの `upload_release=true` は、Release を黙って飛ばさずに実行を失敗させます。署名は手順 3 と同じように確認します。

```bash
RUN=$(gh workflow run release.yml --ref "${TAG:?}" -f upload_release=true | sed 's|.*/||')
gh run watch "${RUN:?}" --exit-status
```

### 6. 公開

[Releases](https://github.com/cri-middleware/SSPlayerForUnity/releases) ページで下書きを開き、添付を確かめます。`jp.co.cri-mw.spritestudio.ssplayer-unity-X.Y.Z.tgz` とそのエイリアス `jp.co.cri-mw.spritestudio.ssplayer-unity.tgz`、`SHA256SUMS` の 3 つです。リリースノートを編集し、**Set as the latest release** を選びます。**ベータでも Set as a pre-release にはしません。** README と docs が案内する `releases/latest/download/jp.co.cri-mw.spritestudio.ssplayer-unity.tgz` は、pre-release ではない Release にしか解決されないためです。最後に **Publish release** を押します。公開すると `pages.yml` が動き、[ドキュメントサイト](https://cri-middleware.github.io/SSPlayerForUnity/) をデプロイします。run を最後まで見届けてから、サイトを開いて確かめます。run が失敗しても Release は公開されたままで、サイトは元のまま残ります。原因を直したら run を再実行します。再実行は同じタグをビルドします。

```bash
RUN=$(gh run list -w pages.yml -b "${TAG:?}" -L 1 --json databaseId -q '.[0].databaseId')
gh run watch "${RUN:?}" --exit-status             # run が一覧に出るまでの数秒間は RUN が空になる
```

### 7. develop へ戻す

`main` を `develop` へマージします。パッチリリースの後は、`develop` が先へ進んだ箇所で衝突します。`develop` のバージョンと `Unreleased` の見出しを残し、パッチの節をその下に置いてください。次のバージョンが決まったら、`develop` の `package/package.json` を設定し、`## [X.Y.Z] - Unreleased` の見出しを立てます。

```bash
git switch develop && git pull --ff-only
git merge --no-ff main && git push origin develop
git push origin --delete "$BRANCH"
```

プルリクエストのページのボタンでリリースブランチを消してあれば、最後の行は「ブランチが無い」と出るだけです。パッチリリースはタグから切り直すので、この後ブランチが要ることはありません。

## リリースの取り消し

Release を公開するまでは取り消せます。一時的な理由で失敗した run は、下書きを消してから同じタグでもう一度実行します。新しいコミットが要る不具合ならタグも取り下げます。下書きを消し、`git push origin ":refs/tags/$TAG" && git tag -d "$TAG"` を実行し、ブランチで直して（消していたらプルリクエストのページから復元して）手順 3 に戻ります。

**公開した後のタグとアセットは恒久的です。** 利用者はその tarball を持っていて、Package Manager が表示するバージョンがそれを名指ししているので、修正は新しいパッチバージョンで出します。

## SDK のプラットフォーム構成が変わったとき

UPM パッケージは変更不可のフォルダに展開され、Unity は `.meta` の無いアセットを無視します。`.meta` を生成できる Unity はどの CI にも無いので、ネイティブプラグインの `.meta` はすべてコミットしてあり、SDK のリリースでネイティブライブラリが増えたり、消えたり、場所が変わったりしたら、その SDK を出す前に `.meta` を手で作る必要があります。コミット済みの `.meta` が無いプラグインのパスがあると、プルリクエストの CI と `build-release.sh` の両方が失敗します。`.meta` を作るのは `scripts/scaffold-plugin-placeholders.sh` で、その冒頭のコメントに、Unity にまた消されないための順序も含めた 4 つの手順があります。

## パッケージのローカルビルド

`release.yml` のジョブは `scripts/build-release.sh` です。同名の SDK のスクリプトとは違い、こちらは 1 台で製品そのものを作れます（中身は C# とビルド済みネイティブライブラリだけだからです）。リリースを確認する方法は、実際に作って tarball からインストールすることです。

```bash
./scripts/build-release.sh
# Window > Package Manager > + > Install package from tarball...
```

このスクリプトは、別々の失敗に対して 2 回検査します。**パッケージ前**: 各 plugin ディレクトリにネイティブライブラリがあり、それぞれにコミット済みの `.meta` があり、FFI バインディングとその `.meta` も揃っていること。`.meta` をコミットし忘れたライブラリは、何も言わずに**存在しないもの**としてインストールされます。**パッケージ後**: tarball に実際に入っていること。`npm pack` は `package.json` の `files` 許可リストを適用するため、1 つめの検査では見えない 2 つめの消失ポイントになります。（`LICENSE.md` を `files` に入れていないのは意図的です。npm は `LICENSE*` を常に含めるので、列挙が要るのは `.meta` だけです。覚えているかどうかに任せず、検査がそう表明します。）

`sdk=<download|build>` はネイティブライブラリの入手元です。`download` は `download-sdk.sh` で、`scripts/SDK_VERSION.txt` のビルド済みリリースを全プラットフォーム分取得します。出荷可能なパッケージが持てる唯一の入手元です。`build` は `build-runtime.sh` で、submodule の Rust からコンパイルします。そのバージョンの SDK リリースがまだ存在しないときに使うもので、代償は**このホストのプラットフォームだけ**になることです。検査はこの 2 つを区別できません（各 plugin ツリーにライブラリが「ある」ことを見るだけで、「すべてある」ことは見ないため）。1 プラットフォームだけの tarball も通るので、代わりにスクリプトが明示的に警告します。`sdk=build` は中身を見るためのもので、リリースを切るために使ってはいけません。
