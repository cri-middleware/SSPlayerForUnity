[**日本語**](./README.ja.md) | [**English**](./README.md)

# SpriteStudioPlayer for Unity

> **注意:** 本 `develop` ブランチは現在開発中のバージョンです。本ブランチで扱う API・ワークフローは予告なく変更される可能性があり、いかなる保証もサポートも提供しません（リクエストやバグ報告への返信もできません）。安定版は [main ブランチ](https://github.com/SpriteStudio/SSPlayerForUnity/tree/main) または [Releases](https://github.com/SpriteStudio/SSPlayerForUnity/releases) から取得してください。

[OPTPiX SpriteStudio 7](https://www.webtech.co.jp/spritestudio/) で作成したアニメーションを [Unity](https://unity.com/) 上で再生するための、超高パフォーマンスなプラグインです。
Unity の Job System (Burst Compiler) によるゼロアロケーションメッシュ構築と、高度に最適化された Rust コア (`libssruntime`) を組み合わせることで、極限のパフォーマンスを実現します。

## ドキュメント

詳細な使い方は `docs/` フォルダ内のドキュメントを参照してください。

- [**ドキュメント (日本語)**](./docs/ja/index.md)

### クイックリンク (日本語)
- [インストール](./docs/ja/setup/install.md)
- [基本的な使い方](./docs/ja/workflow/usage_basic.md)
- [アセットのインポートとエディタ連携](./docs/ja/workflow/usage_asset_pipeline.md)
- [スクリプト制御とイベント](./docs/ja/workflow/usage_scripting.md)
- [インポートの自動化](./docs/ja/workflow/import.md)
- [パフォーマンスチューニングと高度な設定](./docs/ja/workflow/tips.md)
- [ビルドガイド](./docs/ja/setup/build.md)

## クイックスタート

本プレイヤーは Unity 6.3 LTS 以降に対応しており、ユニバーサルレンダーパイプライン (URP) と Built-in レンダーパイプライン (BRP) のいずれの環境でも動作します。

1. 本パッケージを Unity プロジェクトへインストールします（Git URL / ローカルのいずれか。詳細は [インストールガイド](./docs/ja/setup/install.md)）。
   ```
   https://github.com/SpriteStudio/SSPlayerForUnity.git?path=/package
   ```
2. Unity メニューから **Window > SpriteStudio > Importer** を開き、`.sspj` をドラッグ＆ドロップして `.ssab` (FlatBuffers) へ変換します。
3. GameObject に `SpriteStudioPlayer` を（または Canvas 下に `SpriteStudioGraphic` を）アタッチし、生成された `SpriteStudioAnimeAsset` をセットして再生します。

詳細は [基本的な使い方](./docs/ja/workflow/usage_basic.md) を参照してください。

## 概要 (Overview)

本プラグインは、**SpriteStudio と Unity エディタをシームレスに行き来できるアセットパイプライン**を備えています。`.sspj` をエディタに取り込むと自動的に `.ssab` (FlatBuffers) へ変換され、`SpriteStudioAnimeAsset` (ScriptableObject) としてインポートされます。

データフロー、主な機能、対応バージョンなどの詳細は **[ドキュメント (日本語)](./docs/ja/index.md)** を参照してください。

## 関連リポジトリ

- [SpriteStudio7-SDK](https://github.com/SpriteStudio/SpriteStudio7-SDK) — `libssruntime` / `libssconverter` を提供する SDK 本体
- [SSPlayerForGodot](https://github.com/SpriteStudio/SSPlayerForGodot) — 同じ Rust コアを用いた Godot Engine 版プレイヤー
