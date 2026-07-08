[**日本語**](./README.ja.md) | [**English**](./README.md)

# SpriteStudioPlayer for Unity

**Unity のゲームに、プロフェッショナルな2Dアニメーションを。直感的な使いやすさと、極限のパフォーマンスを両立するプラグイン。**

**[OPTPiX SpriteStudio 7](https://www.webtech.co.jp/spritestudio/)** のアニメーション (`.ssab`) を [Unity](https://unity.com/) 上で再生するためのハイパフォーマンスなプラグインです。Unity の強力な機能と、専用アニメーションツールの表現力を組み合わせることで、ラスターベースの2Dアニメーションを Unity プロジェクトへ簡単に実装・再生することができます。

## ✨ SpriteStudio を Unity で使うメリット

- **Unity 標準ツールを超える、2D アニメーション専用エディタ**
  SpriteStudio 7 は、ボーン・メッシュ変形・パーティクルエフェクトをアニメーター向けに備えた、skeletal／メッシュ変形ベースの 2D アニメーション専用エディタです。Unity 標準のアニメーション機能では手間がかかる／現実的でない表現を実現でき、さらに動きは Unity の外で作り込んで `.ssab` として書き出すため、アニメーターはエンジンに触れず自分のツールだけで反復作業を進められます。
- **キャラクター・エフェクト・シーン全体を、一つのエディタで**
  パーティクルエフェクトはキャラクターと同じタイムライン上で扱える標準の部品で、別途エフェクトシステムを組み合わせる必要がありません。さらにキャラクター単体にとどまらず、背景・エフェクト・UI を組み合わせたカットシーンや画面全体の演出を丸ごと構築し、Unity 上で 1 つのアニメーションとして再生できます。
- **手持ちの PNG 素材を、そのまま Unity ネイティブのコンポーネントで**
  テクスチャは Unity 標準の画像パイプラインで読み込むため、お持ちのラスター（PNG）素材をベクターへ作り直すことなくそのまま使えます。`SpriteStudioPlayer`（ワールド空間）や `SpriteStudioGraphic`（uGUI Canvas）は通常のコンポーネントとしてシーンに配置でき、C# や Timeline から制御できます。
- **SpriteStudio ⇄ Unity のシームレスなアセットパイプライン**
  `.sspj` を Unity にドロップするだけで、最適化バイナリ `.ssab` とすぐに使える `SpriteStudioAnimeAsset` が自動生成され、SpriteStudio で編集し直せば Unity 側のアセットも即座に更新されます。アニメーションはシーンから分離したアセットとして管理されるため、シーンに埋め込むアニメーションで起きがちな Git コンフリクトも回避できます。
- **省メモリ・ハイパフォーマンスなネイティブランタイム**
  アセットは FlatBuffers として保持されるため、実行時のパース負荷はゼロで、オンメモリから即座に再生できます。Rust 製コアの SIMD 最適化に、Unity の Job System (Burst)・ゼロアロケーションなメッシュ更新を組み合わせることで CPU・メモリ負荷を低く抑え、モバイルや大量キャラクターの描画でも滑らかに動作します。さらにサブフレーム補間により、高リフレッシュレートのモニターでも滑らかに再生されます。

## 📚 ドキュメント

詳細な使い方は `docs/` フォルダ内のドキュメントを参照してください。読者の目的に合わせて、段階的に情報を整理しています。

- [**ドキュメント (日本語)**](./docs/ja/index.md)
- [**Documentation (English)**](./docs/en/index.md)

### クイックリンク
- [インストール](./docs/ja/setup/install.md)
- [基本的な使い方](./docs/ja/workflow/usage_basic.md)
- [アセットのインポートとエディタ連携](./docs/ja/workflow/usage_asset_pipeline.md)
- [スクリプト制御とイベント](./docs/ja/workflow/usage_scripting.md)
- [インポートの自動化](./docs/ja/workflow/import.md)
- [パフォーマンスチューニングと高度な設定](./docs/ja/workflow/tips.md)
- [ビルドガイド](./docs/ja/setup/build.md)

## 🚀 クイックスタート

ご自身の目的に合わせて導入方法を選択してください。本プレイヤーは Unity 6.3 LTS 以降に対応しており、URP と BRP のいずれでも動作します。

### 1. サンプルで動作確認する
1. **取得**: 本リポジトリをクローンまたはダウンロードします。
2. **準備**: `./scripts/download-sdk.sh` (Mac/Linux) または `.\scripts\download-sdk.ps1` (Windows) を実行してネイティブライブラリをダウンロードします。
3. **開く**: Unity Hub から `examples/UnityTestProject` を開きます。
4. **確認**: `sprs.unity` シーンを開いて再生することで、すぐにアニメーションの動作を確認できます。

### 2. プロジェクトへ導入する
1. **インストール**: GitHub Releases からパッケージの tarball（恒久リンク・常に最新リリース）— [`jp.co.cri-mw.optpix.ssplayer-unity.tgz`](https://github.com/cri-middleware/SSPlayerForUnity/releases/latest/download/jp.co.cri-mw.optpix.ssplayer-unity.tgz) — をダウンロードし、Unity の Package Manager で **Add package from tarball...** を選んで指定します。*(「Add package from URL...」/ git URL では導入できません。ネイティブバイナリは tarball のみに同梱されるためです。)*
2. **インポート**: Unity メニューから **Window > SpriteStudio > Importer** を開き、`.sspj` をドラッグ＆ドロップして自動変換します。
3. **再生**: GameObject に `SpriteStudioPlayer` をアタッチし、生成された `SpriteStudioAnimeAsset` をセットして再生します。

カスタマイズを目的とした導入手順（手動ビルド等）の詳細は [インストールガイド](./docs/ja/setup/install.md) を参照してください。

## 💡 概要 (Overview)

本プラグインは、**SpriteStudio と Unity エディタをシームレスに行き来できる強力なアセットパイプライン**を備え、アセットの自動変換・更新が可能です。

データフロー図、主な機能、対応環境などの詳細は **[ドキュメント (日本語)](./docs/ja/index.md)** を参照してください。

## 🎬 サンプル

[examples フォルダ](./examples/) にサンプルプロジェクトがあります。先に `setup-download`（クイックスタート参照）を実行してアセットを取得し、Unity Hub から開いてください。

- [UnityTestProject](./examples/UnityTestProject) — `sprs.unity` シーンを開くと、キャラクター・全パーツ種・パーティクルエフェクトの再生を確認できます。

## 🔗 関連リポジトリ

- [SpriteStudio-SDK](https://github.com/cri-middleware/SpriteStudio-SDK) — `libssruntime` / `libssconverter` を提供する SDK 本体

## 📄 ライセンス (License)

本プロジェクトは BSD-3-Clause ライセンスのもとで公開されています。詳細は [LICENSE.md](./LICENSE.md) をご参照ください。

同梱されている `SpriteStudio-SDK` や、依存するサードパーティライブラリ（FlatBuffers など）のライセンスについては、[THIRD_PARTY_NOTICES.md](./THIRD_PARTY_NOTICES.md) をご参照ください。
