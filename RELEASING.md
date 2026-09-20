# Releasing SSPlayerForUnity

Maintainers only. Contributors do not run any of this — see [CONTRIBUTING.md](./CONTRIBUTING.md).

## Building the documentation site

```bash
./scripts/prepare-docs.sh          # once: .venv + the pins in docs/requirements.txt
./scripts/build-docs.sh            # English then Japanese, both --strict
./scripts/build-pages.sh serve=yes # the published tree, both locales -> http://localhost:8000/
```

Both locales, English first: it clears `site/`, which contains `site/ja`, so an English-only build
leaves a stale Japanese site behind and a page you just broke still looks fine. `mkdocs.base.yml`
sets `strict: true`; `zensical serve` validates nothing, `--strict` or not, and serves one locale at
a time, so `build-pages.sh serve=yes` is the only way to watch the language selector resolve.
`pages.yml`'s build job **is** that script with every option spelled out. `.ps1` twins on Windows,
`key=value` options and `--help` on both.

## Cutting a release

A release is a tag, pushed first and built second:

```bash
git tag v7.0.0-beta.1 && git push origin v7.0.0-beta.1
# Actions -> Release -> "Use workflow from" = the tag, upload_release = true
# -> a draft Release on that tag -> review the assets -> Publish from the UI
```

Nothing passes the tag around: `build-release.sh` reads `GITHUB_REF_NAME`, or the tag `HEAD` is
checked out at, and `release.yml` uses the same ref for `tag_name`. Dispatching from a branch is a
package build, not a release — `upload_release=true` from anything but a `v*` tag fails the run
rather than quietly skipping the Release. `scripts/release-version.sh` prints what the tag *should*
be (`version` in `package/package.json`, **not** `SDK_VERSION.txt`), and `build-release.sh` says so
when the two disagree.

Publishing the draft is what emits the `release: published` event that triggers the Pages deploy,
and what makes `releases/latest/download/<name>.tgz` — the install URL in the README and the docs —
resolve. Keep it a full, non-prerelease release, and publish it by hand.

`release.yml`'s job **is** `scripts/build-release.sh` with every option spelled out, and unlike the
SDK's script of the same name this one really does produce the whole package on one machine: it is
C# plus prebuilt native libraries. The way to review a release is to build it and install it.

```bash
./scripts/build-release.sh
# Window > Package Manager > + > Install package from tarball...
```

It checks twice, for different failures. **Before packing:** every plugin directory has native
libraries, each has a committed `.meta`, and both FFI bindings and their `.meta` are there — a UPM
package is extracted into an immutable folder, so Unity ignores any asset with no `.meta`, and a
library whose `.meta` was never committed installs as *nothing*, silently, with no Unity in CI to
generate one. **After packing:** the tarball actually contains them, because `npm pack` applies the
`files` allowlist in `package.json`, a second place a file can vanish. (`LICENSE.md` is deliberately
absent from `files` — npm always includes a `LICENSE*`, so only its `.meta` has to be listed. The
check asserts that rather than trusting anyone to remember.)

`sdk=<download|build>` says where the native libraries come from. `download` is `download-sdk.sh`:
the prebuilt release for `scripts/SDK_VERSION.txt`, every platform, and so the only source a
shippable package can have. `build` is `build-runtime.sh`, compiled from the submodule, and it is
what to use while that SDK release does not exist yet — at the cost of **this host's platform
only**. The checks cannot tell those apart, since they ask that each plugin tree has libraries and
not that it has all of them, so a one-platform tarball passes and the script says so out loud
instead. Use `sdk=build` to get a package to look at; never to cut a release.

<br>

---

# SSPlayerForUnity のリリース手順

維持者向けです。コントリビューターがこの手順を実行することはありません（[CONTRIBUTING.md](./CONTRIBUTING.md) を参照してください）。

## ドキュメントサイトのビルド

```bash
./scripts/prepare-docs.sh          # 一度だけ: .venv と docs/requirements.txt のピン
./scripts/build-docs.sh            # 英語 → 日本語の順に、どちらも --strict
./scripts/build-pages.sh serve=yes # 公開ツリー、両ロケール -> http://localhost:8000/
```

必ず両ロケールを、英語を先に。英語ビルドは `site/`（その中に `site/ja` がある）を消すため、英語だけ
建てると古い日本語サイトが残り、壊したページが無傷に見えます。`mkdocs.base.yml` が `strict: true` を
設定します。`zensical serve` は `--strict` を付けても何も検証せず、1 ロケールずつしか配信しないため、
言語セレクタの動作を見られるのは `build-pages.sh serve=yes` だけです。`pages.yml` のビルドジョブは
そのスクリプト（オプションはすべて明示）です。Windows は `.ps1` 双子、どちらも `key=value` と
`--help` を取ります。

## リリースの手順

リリースはタグです。先にタグを push し、ビルドはその後です。

```bash
git tag v7.0.0-beta.1 && git push origin v7.0.0-beta.1
# Actions -> Release -> "Use workflow from" にタグを指定し、upload_release = true
# -> そのタグの下書き Release -> asset を確認 -> UI から公開
```

タグは受け渡されません。`build-release.sh` が `GITHUB_REF_NAME`（ローカルでは `HEAD` のタグ）を読み、
`release.yml` も同じ ref を `tag_name` に使います。ブランチからのディスパッチはパッケージビルドで
あってリリースではありません。`v*` タグ以外からの `upload_release=true` は、Release を黙って飛ばさずに
実行を失敗させます。`scripts/release-version.sh` が本来のタグ名（`package/package.json` の `version`。
**`SDK_VERSION.txt` ではありません**）を表示し、食い違えば `build-release.sh` が指摘します。

下書きの公開が `release: published` イベントを発行し、Pages のデプロイを起動します。README と docs の
インストール URL である `releases/latest/download/<name>.tgz` が解決するのもこれによってです。
pre-release にせず、通常のリリースとして手作業で公開してください。

`release.yml` のジョブは `scripts/build-release.sh`（オプションはすべて明示）です。同名の SDK の
スクリプトとは違い、こちらは 1 台で製品そのものを作れます（中身は C# とビルド済みネイティブライブラリ
だけだからです）。リリースを確認する方法は、実際に作って tarball からインストールすることです。

```bash
./scripts/build-release.sh
# Window > Package Manager > + > Install package from tarball...
```

このスクリプトは、別々の失敗に対して 2 回検査します。**パッケージ前**: 各 plugin ディレクトリに
ネイティブライブラリがあり、それぞれにコミット済みの `.meta` があり、FFI バインディングとその `.meta`
も揃っていること。UPM パッケージは変更不可のフォルダに展開されるため、`.meta` の無いアセットを Unity は
無視します。`.meta` をコミットし忘れたライブラリは、何も言わずに**存在しないもの**としてインストール
されます（CI には `.meta` を生成する Unity がありません）。**パッケージ後**: tarball に実際に入っている
こと。`npm pack` は `package.json` の `files` 許可リストを適用するため、1 つめの検査では見えない
2 つめの消失ポイントになります。（`LICENSE.md` を `files` に入れていないのは意図的です。npm は
`LICENSE*` を常に含めるので、列挙が要るのは `.meta` だけです。覚えているかどうかに任せず、検査が
そう表明します。）

`sdk=<download|build>` はネイティブライブラリの入手元です。`download` は `download-sdk.sh` で、
`scripts/SDK_VERSION.txt` のビルド済みリリースを全プラットフォーム分取得します。出荷可能な
パッケージが持てる唯一の入手元です。`build` は `build-runtime.sh` で、submodule の Rust から
コンパイルします。そのバージョンの SDK リリースがまだ存在しないときに使うもので、代償は
**このホストのプラットフォームだけ**になることです。検査はこの 2 つを区別できません（各 plugin ツリーに
ライブラリが「ある」ことを見るだけで、「すべてある」ことは見ないため）。1 プラットフォームだけの
tarball も通るので、代わりにスクリプトが明示的に警告します。`sdk=build` は中身を見るためのもので、
リリースを切るために使ってはいけません。
