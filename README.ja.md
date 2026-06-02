[**日本語**](./README.ja.md) | [**English**](./README.md)

# SpriteStudioPlayer for Unity

> **注意:** この `develop` ブランチは現在開発中です。API やワークフローは予告なく変更される場合があります。

[OPTPiX SpriteStudio 7](https://www.webtech.co.jp/spritestudio/) で作成されたアニメーションを Unity 上で再生するための、超高パフォーマンスプラグインです。
Unity の Job System (Burst Compiler) とゼロアロケーションメッシュ構築、そして高度に最適化された Rust コア (`libssruntime`) を組み合わせることで、極限のパフォーマンスを実現します。

## 主な特徴
- **ゼロアロケーションパイプライン**: Burst Job から `Mesh.SetVertexBufferData` へ直接頂点データを書き込むゼロコピー描画。
- **Rust コア**: 公式の `SpriteStudio7-SDK` (Rust) と `csbindgen` によるオーバーヘッドのない FFI バインディング。
- **エディタ統合**: `.sspj` ファイルを直接 Unity へドラッグ＆ドロップ可能。バックグラウンドで `libssconverter` により `.ssab` へ自動変換。
- **uGUI & 空間描画**: ワールド空間用の `SpriteStudioPlayer` と、Canvas UI 用の `SpriteStudioGraphic` を提供。

## クイックスタート
1. `package/` フォルダを Unity プロジェクトの `Packages/` に配置するか、Package Manager からローカルインストールします。
2. Unity メニューから **SpriteStudio > Importer** を開き、出力先(Output Folder)を設定します。
3. Unity エディタ上に `.sspj` をドラッグ＆ドロップします。自動的に FlatBuffers (`.ssab`) に変換され、`SpriteStudioAnimeAsset` (ScriptableObject) としてインポートされます。
4. GameObject に `SpriteStudioPlayer` を（または Canvas 下に `SpriteStudioGraphic` を）アタッチします。
5. インポートされたアセットをセットして再生します！

## ランタイムのビルド（開発者向け）
プラグイン自体の開発を行う場合は、Rust SDK をコンパイルして Unity 側にデプロイする必要があります。
1. サブモジュールを含めて clone し、`SpriteStudio7-SDK` を取得します。
2. `./scripts/build-runtime.sh` (Mac/Linux) または `.\scripts\build-runtime.ps1` (Windows) を実行します。
3. `libssruntime` と `libssconverter` がコンパイルされ、Unity の `package/Runtime/Plugins` 等へ自動配置されます。
