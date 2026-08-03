# 🛠️ ビルドガイド（開発者向け）

[← ドキュメント目次へ戻る](../index.md)

通常の利用ではビルド済みのネイティブライブラリがパッケージに含まれているため、本ページの作業は不要です。**プラグイン自体を開発する場合**や、**特定プラットフォーム向けにネイティブライブラリを再ビルドする場合**にのみ必要です。

ビルドでは、Rust 製の SDK (`libssruntime` / `libssconverter`) をコンパイルし、Unity パッケージ側の `Plugins` フォルダへ配置します。同時に `csbindgen` が生成した C# FFI バインディングもコピーされます。

## 事前準備

ビルドには **Rust ツールチェイン (`cargo`)** が必要です。未導入の場合は [rustup](https://rustup.rs/) からインストールしてください。

クロスコンパイル（別プラットフォーム向けビルド）を行う場合は、対象プラットフォームの SDK / ツールチェイン（Android NDK、Emscripten、Xcode など）が別途必要です。

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

   - `package/Runtime/Plugins/<Platform>/<Arch>/` — `libssruntime`（実行時に使用）
   - `package/Editor/Plugins/<Platform>/<Arch>/` — `libssconverter`（エディタの変換に使用）
   - `package/Runtime/Bindings/SSRuntimeMethods.g.cs` — 生成された FFI バインディング
   - `package/Editor/Bindings/SSConverterMethods.g.cs` — 同上（変換用）

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

例:

```sh
# Windows x86_64 のリリースビルド
.\scripts\build-runtime.ps1 platform=windows arch=x86_64 build=release

# Android arm64 のリリースビルド
./scripts/build-runtime.sh platform=android arch=arm64 build=release

# iOS（実機 + シミュレータを内包する XCFramework を生成）
./scripts/build-runtime.sh platform=ios build=release
```

> **iOS について:** `platform=ios` は実機（arm64）と シミュレータ（arm64 / x86_64）の両スライスを含む `libssruntime.xcframework` を生成し、`package/Runtime/Plugins/iOS/` に配置します。device / simulator は XCFramework 内で分離されるため、シミュレータ専用オプションは不要です（Apple Silicon Mac のシミュレータでも動作します）。

> **補足:** ホスト環境と同じ platform/arch を指定した場合は `cargo build` を直接実行します。それ以外（クロスコンパイル）の場合は SDK 側のリリーススクリプトへ委譲します。

## FFI バインディングのみ再生成する

SDK 側で FFI が追加・変更された際に、バインディングコードだけを再生成したい場合は次を実行します。

```sh
# macOS / Linux
./scripts/generate-runtime-code.sh

# Windows (PowerShell)
.\scripts\generate-runtime-code.ps1
```

> 生成されたファイル（`SSRuntimeMethods.g.cs` / `ssab_generated.cs` など）は手で編集しないでください。必ずスクリプトで再生成します。

## よくある注意点

- **古いネイティブ DLL を Unity が掴み続ける**: Windows 環境では、起動時にロードしたネイティブ DLL をエディタが掴み続けることがあります。ビルドを差し替えても反映されない場合は、**Unity エディタを終了してからビルドし、再起動**してください。
