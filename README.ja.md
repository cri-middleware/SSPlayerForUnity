[**English**](./README.md) | [**日本語**](./README.ja.md)

# SpriteStudio Player for Unity

**Unity のゲームに、プロフェッショナルな 2D アニメーションを。直感的な使いやすさと、極限のパフォーマンスを両立するプラグイン。**

**[SpriteStudio](https://www.webtech.co.jp/spritestudio/)** のアニメーション (`.ssab`) を [Unity](https://unity.com/) 上で再生するためのハイパフォーマンスなプラグインです。Unity の強力な機能と、専用アニメーションツールの表現力を組み合わせることで、ラスターベースの 2D アニメーションを Unity プロジェクトへ簡単に実装・再生することができます。

## ✨ SpriteStudio を Unity で使うメリット

- **キャラクターが“生きる”。** 呼吸・まばたき、なびく髪や衣装、斬撃・魔法のエフェクトまで——動きは専用エディタ（SpriteStudio）でボーン・メッシュ変形・パーティクルを使ってビジュアルに作り込みます。アーティストは自分のツールで作業し、Unity はその結果を再生するだけです。再生中のアニメーションはトラックに重ねたりクロスフェードしたりでき、歩きから走りへも飛ばずになめらかにつながります。
- **キャラクターだけでなく、エフェクトや UI、シーン全体も。** パーティクルエフェクトはキャラクターと同じタイムライン上で作れるので、別のエフェクトシステムを用意する必要はありません。キャラクター・背景・エフェクト・UI を組み合わせたカットシーンや画面全体の演出も、Unity では 1 つのアニメーションとして再生できます。
- **シーンにも UI にも。** `SpriteStudioPlayer` はワールド空間に、`SpriteStudioGraphic` は uGUI の Canvas に描画します。Canvas では `RectTransform` に収まり、**Mask** / **Rect Mask 2D** に従い、状態ごとに区間を流すボタンやトグルにもなります。どちらも C# から操作でき（実行時のパーツ単位の上書きや、アーティストが置いたユーザーデータ・シグナル・オーディオのキーの通知にも対応）、`SpriteStudioPlayer` は Timeline にも載せられます。
- **SpriteStudio と Unity をシームレスに往復。** `.sspj` を **SS Importer** ウィンドウにドロップすれば、すぐ使えるアセットになります。直したくなったらインスペクタの **Open SSPJ** で SpriteStudio を開いて保存し、**Reconvert** を押すだけ。書き出しの手作業は要りません。アニメーションはシーンの外のアセットに入るので、チーム開発でシーンの Git コンフリクトが起きにくくなります。
- **パース不要の読み込みと SIMD・Burst で、軽く滑らかに。** `.ssab` は FlatBuffers のバイナリをそのまま読むので、読み込み時のパースがありません。毎フレーム、SIMD で最適化した Rust コアと Burst ジョブがゼロコピーでメッシュを組み立て、GC アロケーションも発生しません。モバイルや大量のキャラクターでも滑らかに動きます。**Sub-Frame Interpolation** をオンにすれば、高リフレッシュレートのディスプレイにも追従します。

## 📚 ドキュメント

詳細な使い方は `docs/` フォルダ内のドキュメントにあります。目的に合わせて段階的に整理してあり、データフロー図・主な機能・対応環境もそちらです。

- [**ドキュメントサイト (ホスト版)**](https://cri-middleware.github.io/SSPlayerForUnity/ja/)
- [**ドキュメント (日本語)**](./docs/ja/index.md)
- [**Documentation (English)**](./docs/en/index.md)
- [**AI アシスタント向け**](https://cri-middleware.github.io/SSPlayerForUnity/ja/llms.txt) — このドキュメントを Markdown で提供します。`llms.txt` はページの目次、`llms-full.txt` は全ページを 1 ファイルにまとめたものです。インストールしたパッケージにも、同じページが `Documentation~/` フォルダーで入っています
- [**SpriteStudio Docs（ポータル）**](https://cri-middleware.github.io/SpriteStudio-Docs/ja/) — SDK と全公式 Player の入口。コーディングアシスタントに SpriteStudio を教える[プラグイン](https://cri-middleware.github.io/SpriteStudio-Docs/ja/ai/)もあります

## 🚀 クイックスタート

**ゲームで使う場合**はこのまま下へ。パッケージを入れる → `.sspj` を SS Importer に D&D → コンポーネントを置く。その先（スクリプト制御、イベント、パーツ単位の上書き、Timeline）も同じ道の続きで、[ドキュメント](./docs/ja/index.md)にあります。
**Player 自体や、その下の Rust ランタイムを変更する場合**は [CONTRIBUTING.md](./CONTRIBUTING.md) と[ビルドガイド](./docs/ja/development/build.md)へ。変更したパッケージをプロジェクトで使う方法と Unity での確かめ方もビルドガイドに、層とファイルをまたぐ設計の理由は[プレイヤーの構成](./docs/ja/development/architecture.md)にあります。

本プレイヤーは Unity 6.3 LTS 以降に対応しており、URP と BRP のいずれでも動作します。対応するのは **SpriteStudio 7.6 以上**で作成されたプロジェクトです。

1. **インストール**: 同じパッケージを次のどちらかから入れます（手順は[インストールガイド](./docs/ja/setup/install.md)）。
   - **npm から**: **Edit > Project Settings > Package Manager** で `https://registry.npmjs.org` を `jp.co.cri-mw` の scoped registry として追加します。続いて **Window > Package Manager** の **＋ > Install package by name...** を選び、`jp.co.cri-mw.spritestudio.ssplayer-unity` と、[npm のバージョン一覧](https://www.npmjs.com/package/jp.co.cri-mw.spritestudio.ssplayer-unity?activeTab=versions)にあるバージョンを入力します。
   - **tarball から**: [`jp.co.cri-mw.spritestudio.ssplayer-unity.tgz`](https://github.com/cri-middleware/SSPlayerForUnity/releases/latest/download/jp.co.cri-mw.spritestudio.ssplayer-unity.tgz)（恒久リンク・常に最新リリース）をダウンロードし、**Window > Package Manager** の **＋ > Install package from tarball...** でそのファイルを選びます。

   *(「Install package from git URL...」では導入できません。ネイティブバイナリはリリースのパッケージにのみ同梱されるためです。)*
2. **インポート**: Unity メニューから **Window > SpriteStudio > Importer** を開き、エクスプローラーや Finder から `.sspj` をウィンドウへドラッグ＆ドロップします（ドラッグ中は中ほどの枠が光ります）。ウィンドウの **Output Folder**（既定は `Assets/ssab_generated/<sspj の名前>/`）に変換されます（ウィンドウでの変換は Windows と macOS のエディタで動作します。Linux のエディタでは代わりに `ssconverter-cli` を使ってください。[各種コンバート機能と CI/CD](./docs/ja/workflow/import.md) を参照）。

   ```
   Ringo/                       ← SpriteStudio のプロジェクト（ディスク上のどこでも可）
   ├── Ringo.sspj               ← これを SS Importer のウィンドウへドロップする
   ├── Ringo.png
   └── …

   MyGame/                      ← Unity のプロジェクト
   └── Assets/
       └── ssab_generated/      ← Output Folder（既定）
           └── Ringo/           ← .sspj の名前
               ├── Ringo.ssab   ← SpriteStudioAnimeAsset
               ├── Ringo.png
               ├── Ringo.mat
               └── …
   ```

3. **配置**: `SpriteStudioAnimeAsset`（上の `Ringo.ssab`）を Project ウィンドウから Hierarchy（または Scene ビュー）へドラッグ＆ドロップし、**SpriteStudioPlayer**（ワールド空間）または **SpriteStudioGraphic (Canvas UI)** を選びます。**Animation Name** にはアセットの最初のアニメーションが入っています。別のものにするならリストから選び、再生します。

SpriteStudio のデータが手元に無くても始められます。[公式のサンプルデータ](https://www.webtech.co.jp/help/ja/spritestudio7/download/sample/)がそのまま使えます。キャラクターなら [Ringo](https://www.webtech.co.jp/help/ja/spritestudio7/download/sample/#ringo)、エフェクトを含むものなら [パーティクル](https://www.webtech.co.jp/help/ja/spritestudio7/download/sample/#Perticle_sample) が手頃です。自分のアニメーションを作るには SpriteStudio 本体を使います（[製品サイト](https://www.webtech.co.jp/spritestudio/)）。

**次に読む:** [インストールガイド](./docs/ja/setup/install.md) · [アセットのインポート](./docs/ja/workflow/usage_asset_pipeline.md) · [基本的な使い方](./docs/ja/workflow/usage_basic.md) · [制約と適用範囲](./docs/ja/limitations.md)

## 🔗 関連リポジトリ

- [SpriteStudio Docs](https://cri-middleware.github.io/SpriteStudio-Docs/ja/) — SDK と全公式 Player のドキュメントポータル
- [SpriteStudio-SDK](https://github.com/cri-middleware/SpriteStudio-SDK) — `libssruntime` / `libssconverter` を提供する SDK 本体
- [SSConverterGUI](https://github.com/cri-middleware/SSConverterGUI) — Player なしで `.sspj` を変換できるデスクトップ GUI

## 📄 ライセンス

本プロジェクトは BSD-3-Clause ライセンスのもとで公開されています。詳細は [LICENSE.md](./LICENSE.md) をご参照ください。参考訳は [LICENSE.ja.md](./LICENSE.ja.md) にあります。

同梱されている `SpriteStudio-SDK` や、依存するサードパーティライブラリ（FlatBuffers など）のライセンスについては、[THIRD_PARTY_NOTICES.md](./THIRD_PARTY_NOTICES.md) をご参照ください。
