# Contributing to SSPlayerForUnity

> [!IMPORTANT]
> We are not accepting pull requests until a Contributor License Agreement (CLA) is in place. Issues remain open for bug reports and requests.

Thank you for your interest in contributing to SSPlayerForUnity! We welcome all kinds of contributions, including bug reports, feature requests, documentation improvements, and code contributions.

## Table of Contents
- [Code of Conduct](#code-of-conduct)
- [How Can I Contribute?](#how-can-i-contribute)
- [Development Environment](#development-environment)
- [Coding Standards](#coding-standards)

## Code of Conduct
Please refer to [CODE_OF_CONDUCT.md](./CODE_OF_CONDUCT.md).

## How Can I Contribute?

### Technical Constraints & Guiding Principles
This player is a thin C# "Body" driving the SpriteStudio-SDK Rust "Brain" (`libssruntime`) over csbindgen-generated FFI. To keep it fast and predictable, please follow these principles:

- **Unity idioms first:** match the C# conventions and the existing style of the file you touch.
- **Standard packages only:** Burst, Mathematics, Collections, and Timeline. Please do not introduce third-party dependencies.
- **Zero allocation in the hot path:** the tick / mesh-update loop must not allocate GC memory. Reuse `NativeArray`, and upload meshes with `SetVertexBufferData`.
- **FFI safety:** native handles (`SsPlayerHandle`) are plain pointers owned by `IDisposable` classes (`SpriteStudioNativeContext`, `SpriteStudioInternalPlayer`). Always `JobHandle.Complete()` before freeing native resources - a running Burst job will segfault otherwise.
- **Respect the layer split:** `SpriteStudioInternalPlayer` owns the native lifecycle, FFI, and `IDisposable`; all per-frame geometry (frame-data decode, effect / instance / mask batching, Burst scheduling, mesh upload) lives in `SpriteStudioGeometryBuilder`, its collaborators, and `SpriteStudioMeshJob.cs`. Add rendering features there rather than in the player component, and preserve the struct layouts Burst and the FFI depend on.
- **SDK separation:** the core animation logic lives in the `SpriteStudio-SDK` submodule (Rust `libssruntime` / `libssconverter`). Changes to the core or the algorithms belong in the SDK repository; Unity-specific rendering belongs in `package/`.
- **Generated code is never hand-edited:** regenerate the FFI bindings and FlatBuffers accessors with the provided scripts and commit the result.

### Reporting Bugs
If you find a bug, please open a GitHub Issue and include:
- A clear, descriptive title.
- Steps to reproduce.
- Your Unity version, render pipeline (URP / BRP), and target platform.
- Expected vs. actual behavior.

*Note: If you need to send proprietary `.sspj` projects to reproduce the bug, please do not attach them to public issues. See [SUPPORT.md](./SUPPORT.md) for how to contact us privately.*

*Note: If the same problem reproduces on other SpriteStudio players, it most likely belongs to the shared core - please report it in [SpriteStudio-SDK](https://github.com/cri-middleware/SpriteStudio-SDK/issues) instead.*

### Pull Requests
1. Fork the repository and create your branch from `develop`.
2. Keep the tests in `package/Tests/` green, and add coverage when you change core logic or the geometry builder.
3. Submit a Pull Request with a clear description of the changes. Use [Conventional Commits](https://www.conventionalcommits.org/) (e.g. `feat(runtime): …`).

## Development Environment

### Prerequisites
- Unity, matching the `unity` field in [`package/package.json`](./package/package.json).
- A Rust toolchain (`rustup`) - only to build `libssruntime` / `libssconverter` from source, or to run `deploy-examples`, which builds `ssconverter-cli`.
- The `SpriteStudio-SDK` submodule (`git submodule update --init --recursive`), for `build-runtime`, `deploy-examples` and the test fixtures, which are converted from its sources. `download-sdk` does not need it.

### Setup
```bash
./scripts/download-sdk.sh          # fetch prebuilt native libraries
```
The FlatBuffers C# runtime under `package/Runtime/FlatBuffers/` is committed; `scripts/download-flatbuffers.sh` re-vendors it, and is run only to change its version. How the package is put together, and how to use your checkout in a project of your own, are in [docs/en/development/internals.md](./docs/en/development/internals.md).

### Build from source (optional)
```bash
./scripts/build-runtime.sh   # builds the native libraries and regenerates the FFI bindings
```

### Tests
```bash
./scripts/run-tests.sh       # headless; do not run while the Unity Editor has the project open
```
With the Editor open, use the Unity Test Runner instead. Windows users: every script has a `.ps1` counterpart, except the maintainer tools `scaffold-plugin-placeholders.sh`, `scan-snippets.sh`, `npm-dist-tag.sh` and `release-channel.sh`.

### Example projects
```bash
./scripts/deploy-examples.sh   # converts the SDK's Ringo into examples/SampleProject and examples/Override_Ringo
```
No converted asset is committed, so this is what makes either project show anything. Run it **before** the project is opened in Unity: open it first and Unity deletes the committed `.meta` that carry the scenes' references. If you opened it first, `git checkout -- examples/<project>/Assets` puts them back. It builds `ssconverter-cli` from the submodule, so it needs Rust; without it, open the project, drag `SpriteStudio-SDK/tests/Ringo/Ringo.sspj` onto the SS Importer window (**Window > SpriteStudio > Importer**), and re-assign the imported asset on the player in the scene. `examples/UnityTestProject` is the blank scratch project: the package linked, and nothing else.

### Documentation
Pages live as both `docs/en/<path>` and `docs/ja/<path>`, each locale with its own `assets/`; there is no fallback locale, so a page missing from one side is a nav entry pointing at nothing. Asset paths are source-relative (`../assets/…`) **including inside raw `<video>` / `<img>` HTML**, which Zensical rewrites the same way it rewrites Markdown links. If you edit a page, build both locales before you finish. CI builds them too, both `--strict`, on a pull request that touches the site's sources, on the release PR and weekly.

```bash
./scripts/prepare-docs.sh          # once: .venv + the pins in docs/requirements.txt
./scripts/build-docs.sh            # English then Japanese, both --strict
./scripts/build-pages.sh serve=yes # the published tree, both locales -> http://localhost:8000/
```

Both locales, English first: it clears `site/`, which contains `site/ja`, so an English-only build leaves no Japanese site at all. `mkdocs.base.yml` sets `strict: true`; `zensical serve` validates nothing, `--strict` or not, and serves one locale at a time, so `build-pages.sh serve=yes` is the only way to watch the language selector resolve. `pages.yml`'s build job **is** that script. `.ps1` twins on Windows, `key=value` options and `--help` on both.

## Coding Standards

- Follow the existing C# style of the file you edit (naming, `#nullable` usage where present, member ordering).
- Keep the tick loop allocation-free, and prefer Burst-compatible constructs in job code.
- Document public API surface that users are expected to call; the user-facing docs live in `docs/en/` and `docs/ja/` - update **both** when behavior changes.

---
Contributions to this project are considered to be made under the terms of the project's [LICENSE.md](./LICENSE.md).

---

# SSPlayerForUnity への貢献

> [!IMPORTANT]
> コントリビューターライセンス契約（CLA）の準備が整うまで、プルリクエストは受け付けていません。バグ報告や要望は引き続き Issue で受け付けています。

SSPlayerForUnity への貢献にご関心をお寄せいただきありがとうございます。不具合報告、機能要望、ドキュメントの改善、コードの貢献など、あらゆる形の貢献を歓迎します。

## 目次
- [行動規範](#行動規範)
- [貢献の方法](#貢献の方法)
- [開発環境](#開発環境)
- [コーディング規約](#コーディング規約)

## 行動規範
[CODE_OF_CONDUCT.md](./CODE_OF_CONDUCT.md) を参照してください。

## 貢献の方法

### 技術的な制約と方針
本 Player は、csbindgen が生成する FFI を介して SpriteStudio-SDK の Rust 製「Brain」（`libssruntime`）を駆動する、薄い C# 製の「Body」です。速度と予測可能性を保つため、以下の方針に従ってください。

- **まず Unity の作法に従う:** 編集するファイルの C# の規約と既存スタイルに合わせてください。
- **標準パッケージのみ:** Burst / Mathematics / Collections / Timeline。サードパーティ依存の追加はご遠慮ください。
- **ホットパスでのアロケーション禁止:** tick／メッシュ更新ループで GC アロケーションを発生させないでください。`NativeArray` を再利用し、メッシュ更新は `SetVertexBufferData` で行います。
- **FFI の安全性:** ネイティブハンドル（`SsPlayerHandle`）はただのポインタで、`IDisposable` なクラス（`SpriteStudioNativeContext`、`SpriteStudioInternalPlayer`）が所有します。ネイティブリソースの解放前には必ず `JobHandle.Complete()` を呼んでください。実行中の Burst ジョブがあるとクラッシュします。
- **レイヤー分離の遵守:** `SpriteStudioInternalPlayer` がネイティブのライフサイクル・FFI・`IDisposable` を持ち、フレームごとのジオメトリ処理（フレームデータのデコード、エフェクト／インスタンス／マスクのバッチング、Burst のスケジューリング、メッシュのアップロード）は `SpriteStudioGeometryBuilder` とその協調クラス、`SpriteStudioMeshJob.cs` にあります。描画機能の追加は Player コンポーネントではなくそちらへ。Burst と FFI が依存する構造体レイアウトは維持してください。
- **SDK との分離:** アニメーションのコアロジックは `SpriteStudio-SDK` サブモジュール（Rust の `libssruntime` / `libssconverter`）にあります。コアやアルゴリズムの変更は SDK リポジトリへ、Unity 固有の描画は `package/` へ。
- **生成コードは手編集しない:** FFI バインディングと FlatBuffers アクセサーは、付属スクリプトで再生成してコミットしてください。

### 不具合の報告
不具合を見つけた場合は、GitHub Issue に以下を含めてご投稿ください。
- 明確で説明的なタイトル
- 再現手順
- Unity のバージョン、レンダーパイプライン（URP / BRP）、対象プラットフォーム
- 期待する動作と実際の動作

*注: 再現のために非公開の `.sspj` プロジェクトを送付する必要がある場合、公開 Issue には添付しないでください。非公開の連絡方法は [SUPPORT.md](./SUPPORT.md) を参照してください。*

*注: 他の SpriteStudio Player でも同じ問題が再現する場合、共通コア側の問題である可能性が高いため、[SpriteStudio-SDK](https://github.com/cri-middleware/SpriteStudio-SDK/issues) へご報告ください。*

### プルリクエスト
1. リポジトリを fork し、`develop` からブランチを作成してください。
2. `package/Tests/` のテストをグリーンに保ち、コアロジックやジオメトリビルダーを変更した場合はテストを追加してください。
3. 変更内容を明確に記載した PR を作成してください。コミットは [Conventional Commits](https://www.conventionalcommits.org/)（例: `feat(runtime): …`）に従ってください。

## 開発環境

### 前提
- Unity（バージョンは [`package/package.json`](./package/package.json) の `unity` フィールドに準拠）
- Rust ツールチェーン（`rustup`）— `libssruntime` / `libssconverter` をソースからビルドする場合と、`ssconverter-cli` をビルドする `deploy-examples` を実行する場合のみ
- `SpriteStudio-SDK` サブモジュール（`git submodule update --init --recursive`）。`build-runtime`・`deploy-examples` と、そのソースから変換するテスト用のデータに要ります。`download-sdk` には要りません。

### セットアップ
```bash
./scripts/download-sdk.sh          # ビルド済みネイティブライブラリの取得
```
`package/Runtime/FlatBuffers/` の FlatBuffers の C# ランタイムはコミット済みです。`scripts/download-flatbuffers.sh` はそれを取り込み直すもので、版を変えるときだけ実行します。パッケージの構成と、チェックアウトを自分のプロジェクトで使う方法は [docs/ja/development/internals.md](./docs/ja/development/internals.md) にあります。

### ソースからのビルド（任意）
```bash
./scripts/build-runtime.sh   # ネイティブライブラリのビルドと FFI バインディングの再生成
```

### テスト
```bash
./scripts/run-tests.sh       # ヘッドレス実行。Unity エディターでプロジェクトを開いたままでは実行しないでください
```
エディターを開いている場合は Unity Test Runner を使用してください。Windows では各スクリプトの `.ps1` 版を使います（メンテナー用の `scaffold-plugin-placeholders.sh`・`scan-snippets.sh`・`npm-dist-tag.sh`・`release-channel.sh` は `.sh` のみです）。

### サンプルプロジェクト
```bash
./scripts/deploy-examples.sh   # SDK の Ringo を examples/SampleProject と examples/Override_Ringo へ変換
```
変換済みアセットは 1 つもコミットされていないため、これを実行しないとどちらのプロジェクトも何も表示しません。**Unity でプロジェクトを開く前に**実行してください。先に開くと、シーンの参照を持つコミット済みの `.meta` を Unity が削除します。先に開いてしまったときは `git checkout -- examples/<project>/Assets` で戻せます。submodule から `ssconverter-cli` をビルドするため Rust が必要です。Rust が無い場合は、プロジェクトを開いてから `SpriteStudio-SDK/tests/Ringo/Ringo.sspj` を SS Importer ウィンドウ（**Window > SpriteStudio > Importer**）にドラッグし、シーン上の Player に取り込んだアセットを割り当て直してください。`examples/UnityTestProject` は、パッケージをリンクしただけの空のスクラッチ用プロジェクトです。

### ドキュメント
ページは `docs/en/<path>` と `docs/ja/<path>` の両方に存在し、アセットもロケールごとに `assets/` を持ちます。フォールバックロケールは無いため、片方に欠けたページは行き先の無い nav エントリになります。アセットのパスはソース相対（`../assets/…`）で、**生の `<video>` / `<img>` HTML の中でも同じ**です（Zensical は Markdown リンクと同じ規則で書き換えます）。ページを編集したら、仕上げる前に両ロケールをビルドしてください。CI も、サイトのソースに触れるプルリクエスト、リリースのプルリクエスト、週次の実行で、両ロケールを `--strict` でビルドします。

```bash
./scripts/prepare-docs.sh          # 一度だけ: .venv と docs/requirements.txt のピン
./scripts/build-docs.sh            # 英語 → 日本語の順に、どちらも --strict
./scripts/build-pages.sh serve=yes # 公開ツリー、両ロケール -> http://localhost:8000/
```

必ず両ロケールを、英語を先にビルドします。英語ビルドは `site/`（その中に `site/ja` がある）を消すため、英語だけビルドすると日本語サイトは残らず消えます。`mkdocs.base.yml` が `strict: true` を設定します。`zensical serve` は `--strict` を付けても何も検証せず、1 ロケールずつしか配信しないため、言語セレクタの動作を見られるのは `build-pages.sh serve=yes` だけです。`pages.yml` のビルドジョブはそのスクリプトです。Windows は `.ps1` 版を使い、どちらも `key=value` と `--help` を取ります。

## コーディング規約

- 編集するファイルの既存の C# スタイル（命名、`#nullable` の扱い、メンバー順）に従ってください。
- tick ループはアロケーションフリーに保ち、ジョブ内では Burst 互換の記述を優先してください。
- 利用者が呼び出す公開 API にはドキュメントを付けてください。利用者向けドキュメントは `docs/en/` と `docs/ja/` にあり、挙動を変更したときは **両方** を更新してください。

---
本プロジェクトへの貢献は、プロジェクトの [LICENSE.md](./LICENSE.md) の条件の下で行われたものとみなされます。
