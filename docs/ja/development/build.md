# 🛠️ ビルドガイド

[← ドキュメント目次へ戻る](../index.md)

通常の利用ではビルド済みのネイティブライブラリがパッケージに含まれているため、本ページの作業は不要です。**プラグイン自体を開発する場合**や、**特定プラットフォーム向けにネイティブライブラリを再ビルドする場合**にのみ必要です。C# だけを変えるならこのページの作業は要りません。[変更したプレイヤーを使う](#変更したプレイヤーを使う)を参照してください。プレイヤーの構成とその理由は[プレイヤーの構成](architecture.md)にあります。

ビルドでは、Rust 製の SDK (`libssruntime` / `libssconverter`) をコンパイルし、Unity パッケージ側の `Plugins` フォルダへ配置します。同時に `csbindgen` が生成した C# FFI バインディングもコピーされます。

## 事前準備

ビルドには **Rust ツールチェイン (`cargo`)** が必要です。未導入の場合は [rustup](https://rustup.rs/) からインストールしてください。

`build-runtime.sh` と `deploy-examples.sh` は **zsh** で動きます。macOS には入っていますが、Linux では先にインストールしてください。Windows では `.ps1` を使います。これには Windows PowerShell 5.1 ではなく **PowerShell 7**（`pwsh`）が必要です。

クロスコンパイル（別プラットフォーム向けビルド）を行う場合は、対象プラットフォームのツールチェインが別途必要です。SDK のリリーススクリプトが挙げる Rust のターゲット（各スクリプトの冒頭に `rustup target add` が書いてあります）に加えて、macOS と iOS には Xcode、Android には Android NDK と `cargo-ndk`、web には `rust-src` 付きの nightly ツールチェイン、Windows には MSVC のビルドツールが要ります。

## 手順

1. サブモジュールを含めてリポジトリを取得し、`SpriteStudio-SDK` を用意します。

   ```sh
   # 新規 clone する場合
   git clone --recursive https://github.com/cri-middleware/SSPlayerForUnity.git

   # すでに clone 済みの場合
   git submodule update --init --recursive
   ```

2. ビルドスクリプトを実行します。

   ```sh
   # macOS / Linux
   ./scripts/build-runtime.sh

   # Windows (PowerShell)
   .\scripts\build-runtime.ps1
   ```

3. `libssruntime` と `libssconverter` がコンパイルされ、次の場所へ自動配置されます。

   - `package/Runtime/Plugins/<Platform>/<Arch>/` — `libssruntime`（実行時に使用）。macOS・iOS・WebGL には `<Arch>` の階層がありません
   - `package/Editor/Plugins/<Platform>/[<Arch>/]` — `libssconverter`（エディタの変換に使用）。macOS・Windows・Linux 向けだけがビルドされます
   - `package/Runtime/Bindings/SSRuntimeMethods.g.cs` — 生成された FFI バインディング
   - `package/Editor/Bindings/SSConverterMethods.g.cs` — 同上（変換用）

   Rust の依存のライセンスファイル（`THIRD-PARTY-LICENSES.ssruntime.md` / `.ssconverter.md`）がライブラリの横に置かれるのは、SDK の `generate-licenses` スクリプトがそれらを `SpriteStudio-SDK/target/licenses/` に書き出した後だけです。`download-sdk` は、リリース済みのライブラリと一緒にこれらを置きます。

## ビルドオプション

引数を指定すると、対象プラットフォーム・アーキテクチャ・ビルドモードを切り替えられます（指定しない場合はホスト環境向けの debug ビルド）。

```sh
./scripts/build-runtime.sh platform=<platform> arch=<arch> build=<mode>
```

| オプション | 値 | 説明 |
|---|---|---|
| `platform` | `windows` / `macos` / `linux` / `android` / `ios` / `web` | 対象プラットフォーム |
| `arch` | `x86_64` / `arm64` / `universal` など | 対象アーキテクチャ |
| `build` | `debug` / `release` | ビルドモード（配布時は `release`） |
| `hostbuild` | `auto` / `false` | `false` でホスト向けのビルドも SDK のリリーススクリプトを通す（既定 `auto`） |

例:

```sh
# Windows x86_64 のリリースビルド
.\scripts\build-runtime.ps1 platform=windows arch=x86_64 build=release

# Android arm64 のリリースビルド
./scripts/build-runtime.sh platform=android arch=arm64 build=release

# iOS（実機 + シミュレータを内包する XCFramework を生成）
./scripts/build-runtime.sh platform=ios build=release
```

> **iOS について:** `platform=ios` は実機（arm64）とシミュレータ（arm64 / x86_64）の両スライスを含む `libssruntime.xcframework` を生成し、`package/Runtime/Plugins/iOS/` に配置します。device / simulator は XCFramework 内で分離されるため、シミュレータ専用オプションは不要です（Apple Silicon Mac のシミュレータでも動作します）。

> **補足:** ホスト環境と同じ platform/arch の debug ビルドは `cargo build` を直接実行します（Linux では SDK の `release-linux.sh`）。それ以外（release ビルドやクロスコンパイル）は SDK 側のリリーススクリプトへ委譲します。`hostbuild=false` を指定すると、ホスト向けのビルドもリリーススクリプトを通します。

## FlatBuffers アクセサを再生成する

SDK 側で `.fbs` スキーマが変更された際は、`package/Runtime/Format/` 以下の C# アクセサを再生成します。`flatc` に PATH が通っている必要があります。`flatc` は、同梱している FlatBuffers の C# ランタイムと同じリリース（`scripts/download-flatbuffers.sh` の `VERSION`）にしてください。生成コードがそのランタイムの版名つきの確認メソッドを呼ぶので、別の版ではコンパイルできません。

```sh
# macOS / Linux
./scripts/generate-fbs-code.sh

# Windows (PowerShell)
.\scripts\generate-fbs-code.ps1
```

> 生成されたファイル（`ssab_generated.cs` / `framedata_generated.cs` など）は手で編集しないでください。必ずスクリプトで再生成します。

> csbindgen による FFI バインディング（`SSRuntimeMethods.g.cs` / `SSConverterMethods.g.cs`）は**このスクリプトの出力ではありません**。SDK 側のビルドが生成し、`build-runtime` / `download-sdk` がパッケージへ配置します。

## よくある注意点

- **古いネイティブライブラリを Unity が掴み続ける**: エディタは一度読み込んだネイティブプラグインを読み直しません。ビルドを差し替えても反映されない場合は、**Unity エディタを終了してからビルドし、再起動**してください。

## 変更したプレイヤーを使う

ネイティブライブラリが入っているのはリリースのパッケージだけで、git の URL からパッケージを入れられないのはそのためです（`Plugins/` と `Bindings/` は git にありません）。チェックアウトでは、それをスクリプトで取ってきます。

**C# だけを変えるなら**、Rust は要りません。

1. repo を clone します。
2. `scripts/download-sdk.sh`（Windows は `.ps1`）を実行します。`scripts/SDK_VERSION.txt` の SDK リリースのネイティブライブラリとそのバインディングが `package/` に入ります。
3. 自分のプロジェクトを `package/` に向けます。Package Manager の **＋** → **Install package from disk...** で `package/package.json` を選ぶか、`examples/UnityTestProject` のように `Packages/manifest.json` に `file:` の依存を書きます（`"jp.co.cri-mw.spritestudio.ssplayer-unity": "file:../../../package"`）。

これでプロジェクトはチェックアウトをそのまま使うので、C# の変更は次のコンパイルで反映されます。**ネイティブのコアを変えるなら**このページ冒頭の手順です。`build-runtime` が `download-sdk` の置いたものを置き換えます。読み込むにはエディタの再起動が要ります。

## 実際のホストで確かめる

`scripts/run-tests.sh` は、`examples/UnityTestProject` で、EditMode・PlayMode・スタンドアロンのプレイヤーのビルドをヘッドレスで回します。動くのは macOS と Windows で、必要なのは、そのプロジェクトが指定する版のエディタ（Unity Hub の既定の場所にインストールし、ライセンスを有効化したもの）と、配置済みのネイティブライブラリとバインディング（`download-sdk` か `build-runtime`）です。先にエディタを閉じてください。開いている間は batchmode がプロジェクトのロックを取れません。テスト用のデータが無いときは、スイートの前に SDK のサブモジュールから変換するので、サブモジュールを取得しておく必要があります。

- **プロジェクトを直接開くと、このマシンに無いネイティブプラグインの `.meta` が消えます。** エラーではなく削除なので、`git status` でしか分かりません。`run-tests.sh` は自分で戻しますが、自分でプロジェクトを開いたあとは `git checkout -- package/Runtime/Plugins package/Editor/Plugins` で戻してください。
- **エディタは一度読み込んだネイティブプラグインを読み直しません。** `build-runtime` や `download-sdk` のあとは再起動してください。しないと、直したのに直っていないように見えます。
- **ネイティブプラグインの読み込みは、Play Mode ではなくスタンドアロンのプレイヤーで確かめます。** エディタはプレイヤーよりプラグインを緩く解決します。macOS ではランタイムは `ssruntime.bundle` という名前のフォルダでなければならず、`libssruntime.bundle` はエディタでは読めてもプレイヤーで `DllNotFoundException: ssruntime` になります。それを捕まえるのがプレイヤーのスイートです。
- macOS 27 では Unity 6000.3.16f1 が起動しません（batchmode は何もせずに exit 0 で終わります）。6000.3.25f1 は起動します。

### Unity の表記の引き方

ガイドでは、エディタのメニューを Unity の表示どおりに書きます。文字列は `Unity.app/Contents/Resources/Scripting/Managed/UnityEngine/UnityEditor.CoreModule.dll` に UTF-16 で入っているので、UTF-16 のバイト列で探してください（Package Manager の **Install package from disk...**・**Install package from tarball...**・**Install package from git URL...** はそこにあります）。uGUI の **GameObject > UI** メニューは、プロジェクトの `Library/PackageCache/com.unity.ugui@*/` の `Editor/UGUI/UI/MenuOptions.cs` です。日本語のページでも、エディタの英語の表記を使います。
