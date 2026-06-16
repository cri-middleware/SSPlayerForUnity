[**日本語**](./README.ja.md) | [**English**](./README.md)

# SpriteStudioPlayer for Unity

> **注意:** 本 `develop` ブランチは現在開発中のバージョンです。本ブランチで扱う API・ワークフローは予告なく変更される可能性があり、いかなる保証もサポートも提供しません（リクエストやバグ報告への返信もできません）。安定版は [main ブランチ](https://github.com/SpriteStudio/SSPlayerForUnity/tree/main) または [Releases](https://github.com/SpriteStudio/SSPlayerForUnity/releases) から取得してください。

[OPTPiX SpriteStudio 7](https://www.webtech.co.jp/spritestudio/) で作成したアニメーションを Unity 上で再生するための、超高パフォーマンスなプラグインです。
Unity の Job System (Burst Compiler) によるゼロアロケーションメッシュ構築と、高度に最適化された Rust コア (`libssruntime`) を組み合わせることで、極限のパフォーマンスを実現します。

## 主な特徴

- **ゼロアロケーションパイプライン**: Burst Job から `Mesh.SetVertexBufferData` へ直接頂点データを書き込むゼロコピー描画。Tick / メッシュ構築ループで GC アロケーションを発生させません。
- **Rust コア**: 公式の `SpriteStudio7-SDK` (Rust) と `csbindgen` によるオーバーヘッドのない FFI バインディング。
- **エディタ統合**: `.sspj` ファイルを直接 Unity へドラッグ＆ドロップ可能。バックグラウンドで `libssconverter` により `.ssab` (FlatBuffers) へ自動変換します。
- **uGUI & 空間描画**: ワールド空間用の `SpriteStudioPlayer` と、Canvas UI 用の `SpriteStudioGraphic` を提供。Built-in / URP の両レンダーパイプラインに対応します。
- **豊富な再生制御**: 再生 / 一時停止 / 再開 / 停止 / 逆再生 / ピンポン / 区間再生 / フレーム指定をコンポーネント API から操作できます。
- **アニメーションイベント通知**: Finished・Looped に加え、SpriteStudio で設定した UserData・Signal・Audio の各イベントをランタイムで受け取れます。

## クイックスタート

本プレイヤーは Unity 6.3 LTS 以降に対応しており、ユニバーサルレンダーパイプライン (URP) と Built-in レンダーパイプライン (BRP) のいずれの環境でも動作します。

1. 本パッケージを Unity プロジェクトへインストールします。次のいずれかの方法が利用できます。
   - **Git URL から追加**: Package Manager の「Add package from git URL...」に次の URL を入力します。パッケージはリポジトリ直下ではなく `package/` 配下にあるため、`?path=/package` の指定が必要です。
     ```
     https://github.com/SpriteStudio/SSPlayerForUnity.git?path=/package
     ```
     特定のブランチやタグに固定する場合は、末尾に `#<ref>` を付与します（例: `https://github.com/SpriteStudio/SSPlayerForUnity.git?path=/package#main`）。
   - **ローカルから追加**: リポジトリを clone し、Package Manager の「Add package from disk...」で `package/package.json` を指定するか、`package/` フォルダを Unity プロジェクトの `Packages/` 配下へ直接配置します。
2. Unity メニューから **Window > SpriteStudio > Importer** を開き、出力先 (Output Folder) を設定します。
3. Unity エディタ上に `.sspj` をドラッグ＆ドロップします。自動的に FlatBuffers (`.ssab`) へ変換され、`SpriteStudioAnimeAsset` (ScriptableObject) としてインポートされます。
4. GameObject に `SpriteStudioPlayer` を（または Canvas 下に `SpriteStudioGraphic` を）アタッチします。
5. インポートされたアセットをセットして再生します！

## ランタイムのビルド（開発者向け）

プラグイン自体の開発を行う場合は、Rust SDK をコンパイルして Unity 側へデプロイする必要があります。

> **事前準備:** ビルドには Rust ツールチェイン (`cargo`) が必要です。未導入の場合は [rustup](https://rustup.rs/) からインストールしてください。

1. サブモジュールを含めて clone し、`SpriteStudio7-SDK` を取得します（`git clone --recursive`、または clone 済みなら `git submodule update --init --recursive`）。
2. `./scripts/build-runtime.sh` (Mac/Linux) または `.\scripts\build-runtime.ps1` (Windows) を実行します。
3. `libssruntime` と `libssconverter` がコンパイルされ、Unity の `package/Runtime/Plugins` 等へ自動配置されます。

## 関連リポジトリ

- [SpriteStudio7-SDK](https://github.com/SpriteStudio/SpriteStudio7-SDK) — `libssruntime` / `libssconverter` を提供する SDK 本体
- [SSPlayerForGodot](https://github.com/SpriteStudio/SSPlayerForGodot) — 同じ Rust コアを用いた Godot Engine 版プレイヤー
