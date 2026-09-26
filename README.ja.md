[**English**](./README.md) | [**日本語**](./README.ja.md)

# SpriteStudio Player for Unity

**Unity のゲームに、プロフェッショナルな2Dアニメーションを。直感的な使いやすさと、極限のパフォーマンスを両立するプラグイン。**

**[SpriteStudio](https://www.webtech.co.jp/spritestudio/)** のアニメーション (`.ssab`) を [Unity](https://unity.com/) 上で再生するためのハイパフォーマンスなプラグインです。Unity の強力な機能と、専用アニメーションツールの表現力を組み合わせることで、ラスターベースの2Dアニメーションを Unity プロジェクトへ簡単に実装・再生することができます。

## ✨ SpriteStudio を Unity で使うメリット

- **Unity 標準ツールを超える、2D アニメーション専用エディタ**
  SpriteStudio は、ボーン・メッシュ変形・パーティクルエフェクトをアニメーター向けに備えた、skeletal／メッシュ変形ベースの 2D アニメーション専用エディタです。Unity 標準のアニメーション機能では手間がかかる／現実的でない表現を実現でき、さらに動きは Unity の外で作り込んで `.ssab` として書き出すため、アニメーターはエンジンに触れず自分のツールだけで反復作業を進められます。
- **キャラクター・エフェクト・シーン全体を、一つのエディタで**パーティクルエフェクトはキャラクターと同じタイムライン上で扱える標準の部品で、別途エフェクトシステムを組み合わせる必要がありません。さらにキャラクター単体にとどまらず、背景・エフェクト・UI を組み合わせたカットシーンや画面全体の演出を丸ごと構築し、Unity 上で 1 つのアニメーションとして再生できます。
- **ワールド空間にも uGUI Canvas にも対応**
  `SpriteStudioPlayer`（MeshRenderer）はワールド空間に、`SpriteStudioGraphic`（CanvasRenderer）は uGUI の Canvas 上に描画できます。同じアニメーションを通常のコンポーネントと同じ感覚でどちらにも配置でき、C# や Timeline から制御できます。Canvas 上では uGUI の **Mask** / **RectMask2D** にも自動追従します。ただし CanvasRenderer の「1 メッシュ・1 マテリアル」の制約により、Canvas 経路は**単一アトラス前提**で、ブレンドは **MIX / ADD** 以外すべて MIX に集約されます（ワールド経路にこの制限はありません）。詳細は[制限事項](./docs/ja/limitations.md)を参照してください。
- **SpriteStudio ⇄ Unity のシームレスなアセットパイプライン**
  `.sspj` を Unity にドロップするだけで、最適化バイナリ `.ssab` とすぐに使える `SpriteStudioAnimeAsset` が自動生成され、SpriteStudio で編集し直せば Unity 側のアセットも即座に更新されます。アニメーションはシーンから分離したアセットとして管理されるため、シーンに埋め込むアニメーションで起きがちな Git コンフリクトも回避できます。
- **省メモリ・ハイパフォーマンスなネイティブランタイム**アセットは FlatBuffers として保持されるため、実行時のパース負荷はゼロで、オンメモリから即座に再生できます。Rust 製コアの SIMD 最適化に、Unity の Job System (Burst)・ゼロアロケーションなメッシュ更新を組み合わせることで CPU・メモリ負荷を低く抑え、モバイルや大量キャラクターの描画でも滑らかに動作します。さらにサブフレーム補間により、高リフレッシュレートのモニターでも滑らかに再生されます。

## 📚 ドキュメント

詳細な使い方は `docs/` フォルダ内のドキュメントにあります。目的に合わせて段階的に整理してあり、データフロー図・主な機能・対応環境もそちらです。

- [**ドキュメントサイト (ホスト版)**](https://cri-middleware.github.io/SSPlayerForUnity/ja/) — 🚧 初回リリース後に公開
- [**ドキュメント (日本語)**](./docs/ja/index.md)
- [**Documentation (English)**](./docs/en/index.md)
- [**SpriteStudio Docs（ポータル）**](https://cri-middleware.github.io/SpriteStudio-Docs/ja/) — SDK と全公式 Player の入口 — 🚧 初回リリース後に公開

### クイックリンク
- [インストール](./docs/ja/setup/install.md)
- [基本的な使い方](./docs/ja/workflow/usage_basic.md)
- [制限事項と対応範囲](./docs/ja/limitations.md)

## 🚀 クイックスタート

**ゲームで使う場合**はこのまま下へ。パッケージを入れる → `.sspj` を Importer に D&D → コンポーネントを置く。その先（スクリプト制御、イベント、パーツ単位の上書き、Timeline）も同じ道の続きで、[ドキュメント](./docs/ja/index.md)にあります。
**Player 自体や、その下の Rust ランタイムを変更する場合**は [CONTRIBUTING.md](./CONTRIBUTING.md) と[ビルドガイド](./docs/ja/setup/build.md)へ。

本プレイヤーは Unity 6.3 LTS 以降に対応しており、URP と BRP のいずれでも動作します。対応するのは **SpriteStudio 7.5 以上**で作成されたプロジェクトです。

### 1. プロジェクトへ導入する

> 🚧 **未リリースです。** 以下の tarball リンクは初回リリースの公開後に有効になります。それまでは下の**サンプルプロジェクト**の手順をご利用ください。こちらはチェックアウトから動きます。

1. **インストール**: GitHub Releases からパッケージの tarball（恒久リンク・常に最新リリース）— [`jp.co.cri-mw.spritestudio.ssplayer-unity.tgz`](https://github.com/cri-middleware/SSPlayerForUnity/releases/latest/download/jp.co.cri-mw.spritestudio.ssplayer-unity.tgz) — をダウンロードし、Unity の Package Manager で **Add package from tarball...** を選んで指定します。*(「Add package from URL...」/ git URL では導入できません。ネイティブバイナリは tarball のみに同梱されるためです。)*
2. **インポート**: Unity メニューから **Window > SpriteStudio > Importer** を開き、`.sspj` をドラッグ＆ドロップして自動変換します。
3. **配置**: 生成された `SpriteStudioAnimeAsset` を Project ウィンドウから Hierarchy（または Scene ビュー）へドラッグ＆ドロップし、`SpriteStudioPlayer`（ワールド空間）または `SpriteStudioGraphic`（uGUI Canvas）を選んで、**Animation Name** を設定して再生します。

### 2. サンプルで動作確認する

> 🚧 こちらは**チェックアウト**で、上のインストール手順の一部ではありません。パッケージ公開までの間、動いているところを見るための入口です。`examples/SampleProject` は SpriteStudio パッケージがリンク済みで、手順 3 で SDK の **Ringo** アニメーションを取り込みます。自分の `.sspj` を用意しなくても動くものを見られます。
1. **取得**: 本リポジトリを `--recurse-submodules` 付きでクローンします（サンプルの元プロジェクトは `SpriteStudio-SDK/tests` にあります）。
2. **準備**: `./scripts/download-sdk.sh` (Mac/Linux) または `scripts\download-sdk.ps1` (Windows) を実行してネイティブライブラリ（`libssruntime`）を取得・配置します。
3. **サンプルアニメーションを用意**: `./scripts/deploy-examples.sh` (Mac/Linux) または `scripts\deploy-examples.ps1` (Windows) を実行します。変換済みのアニメーションはリポジトリに入っていないので、Ringo をプロジェクトに入れるのはこの手順です。**Unity でプロジェクトを開く前に**実行してください。シーンの参照を保持している `.meta` は、対応するアセットが無い状態で Unity に見つかると削除されます。（先に開いてしまったときは `git checkout -- examples/SampleProject/Assets` で戻してから実行してください。）Rust ツールチェインが必要です。用意できない場合は、プロジェクトを開いてから `SpriteStudio-SDK/tests/Ringo/Ringo.sspj` を **Window > SpriteStudio > Importer** にドラッグし、シーン上の Player に取り込んだアセットを割り当て直してください。
4. **開く**: Unity Hub から `examples/SampleProject` を開きます。
5. **試す**: `Assets/Test.unity` を開いて Play を押します。

SDK の残り 5 本（`overall` / `allAttributeV7` / `allPartsV7` / `ParticleEffect` / `Doll`）は同梱していません。サイズが大きく、submodule が原本を持っているためです。必要なときは `SpriteStudio-SDK/tests` から **Window > SpriteStudio > Importer** にドラッグしてください。

カスタマイズを目的とした導入手順（手動ビルド等）の詳細は [インストールガイド](./docs/ja/setup/install.md) を参照してください。

## 🎬 サンプル

[examples フォルダ](./examples/) にサンプルプロジェクトがあります。

- [SampleProject](./examples/SampleProject) — まず開くのはこちらです。SDK の Ringo アニメーションを再生するので、自分の `.sspj` が無くても動きます。Unity で開く前に `download-sdk` → `deploy-examples` の順で実行してください（クイックスタート参照）。`SpriteStudio-SDK/tests/Ringo` が変わったときに実行し直すのも `deploy-examples` です。
- [Override_Ringo](./examples/Override_Ringo) — パーツ単位のオーバーライドを `OverrideDemo.cs` から操作するサンプルです。同じ Ringo で、実行時の色変え・非表示・セル差し替えを試せます。こちらも `deploy-examples` が中身を用意するので、開く前に実行する点は同じです。
- [UnityTestProject](./examples/UnityTestProject) — パッケージがリンクされているだけの空のプロジェクトです。プラグイン開発中のスクラッチ用で、自分の `.sspj` を取り込んでクリーンな環境で試すときに使います。

## 🔗 関連リポジトリ

- [SpriteStudio Docs](https://cri-middleware.github.io/SpriteStudio-Docs/ja/) — SDK と全公式 Player のドキュメントポータル
- [SpriteStudio-SDK](https://github.com/cri-middleware/SpriteStudio-SDK) — `libssruntime` / `libssconverter` を提供する SDK 本体
- [SSConverterGUI](https://github.com/cri-middleware/SSConverterGUI) — Player なしで `.sspj` を変換できるデスクトップ GUI

## 📄 ライセンス (License)

本プロジェクトは BSD-3-Clause ライセンスのもとで公開されています。詳細は [LICENSE.md](./LICENSE.md) をご参照ください。参考訳は [LICENSE.ja.md](./LICENSE.ja.md) にあります。

同梱されている `SpriteStudio-SDK` や、依存するサードパーティライブラリ（FlatBuffers など）のライセンスについては、[THIRD_PARTY_NOTICES.md](./THIRD_PARTY_NOTICES.md) をご参照ください。
