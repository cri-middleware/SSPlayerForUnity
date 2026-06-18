[**日本語**](./README.ja.md) | [**English**](./README.md)

# SpriteStudioPlayer for Unity

[OPTPiX SpriteStudio 7](https://www.webtech.co.jp/spritestudio/) で作成したアニメーションを [Unity](https://unity.com/) 上で再生するためのハイパフォーマンスなプラグインです。
このプラグインを使用することで、ラスターベースの2Dアニメーションを Unity プロジェクトへ簡単に実装・再生することができます。

## ドキュメント

詳細な使い方は `docs/` フォルダ内のドキュメントを参照してください。読者の目的に合わせて、段階的に情報を整理しています。

- [**ドキュメント (日本語)**](./docs/ja/index.md)

### クイックリンク (日本語)

**セットアップ**
- [インストール](./docs/ja/setup/install.md)
- [ビルドガイド](./docs/ja/setup/build.md)

**ワークフロー**
- [基本的な使い方](./docs/ja/workflow/usage_basic.md)
- [アセットのインポートとエディタ連携](./docs/ja/workflow/usage_asset_pipeline.md)
- [スクリプト制御とイベント](./docs/ja/workflow/usage_scripting.md)

**応用**
- [インポートの自動化](./docs/ja/workflow/import.md)
- [パフォーマンスチューニングと高度な設定](./docs/ja/workflow/tips.md)

## クイックスタート

ご自身の目的に合わせて導入方法を選択してください。本プレイヤーは Unity 6.3 LTS 以降に対応しており、URP と BRP のいずれでも動作します。

### 1. サンプルで動作確認する
1. **取得**: 本リポジトリをクローンまたはダウンロードします。
2. **準備**: `./scripts/setup-download.sh` (Mac/Linux) または `scripts\setup-download.bat` (Windows) を実行してネイティブライブラリをダウンロードします。
3. **開く**: Unity Hub から `examples/UnityTestProject` を開きます。
4. **確認**: `sprs.unity` シーンを開いて再生することで、すぐにアニメーションの動作を確認できます。

### 2. プロジェクトへ導入する
1. **インストール**: Unity の Package Manager を開き、「Add package from URL...」から GitHub Releases のパッケージ URL を入力してインストールします（または直接ダウンロードして追加）。
2. **インポート**: Unity メニューから **Window > SpriteStudio > Importer** を開き、`.sspj` をドラッグ＆ドロップして自動変換します。
3. **再生**: GameObject に `SpriteStudioPlayer` をアタッチし、生成された `SpriteStudioAnimeAsset` をセットして再生します。

カスタマイズを目的とした導入手順（手動ビルド等）の詳細は [インストールガイド](./docs/ja/setup/install.md) を参照してください。

## 概要 (Overview)

本プラグインは、**SpriteStudio と Unity エディタをシームレスに行き来できる強力なアセットパイプライン**を備え、アセットの自動変換・更新が可能です。

データフロー図、主な機能、対応環境などの詳細は **[ドキュメント (日本語)](./docs/ja/index.md)** を参照してください。

## 関連リポジトリ

- [SpriteStudio-SDK](https://github.com/cri-middleware/SpriteStudio-SDK) — `libssruntime` / `libssconverter` を提供する SDK 本体
- [SSPlayerForGodot](https://github.com/SpriteStudio/SSPlayerForGodot) — 同じ Rust コアを用いた Godot Engine 版プレイヤー
