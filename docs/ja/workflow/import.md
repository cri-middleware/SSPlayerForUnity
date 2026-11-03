# 🤖 各種コンバート機能と CI/CD

[← ドキュメント目次へ戻る](../index.md)

`.sspj` から `.ssab` へのコンバートに関する、インポート時の最適化処理や一括変換機能、および CI/CD への組み込みについてまとめます。

基本的なインポート手順は [アセットのインポートとエディタ連携](./usage_asset_pipeline.md) を参照してください。

## 出力先の設定

変換結果の出力先は SS Importer ウィンドウの **Output Folder** で設定します（既定 `Assets/ssab_generated`）。`Assets/` 配下であれば任意のフォルダを指定できます。フォルダは Project ウィンドウからフィールドへドラッグするか、丸いボタンで選びます。**Reset** で `Assets/ssab_generated` に戻ります。それ以外（ファイル、`Assets/` の外のフォルダ）を指定すると理由を出して拒否し、次のインポートが実際に使うフォルダへ表示を戻します。設定は `EditorUserSettings`（`UserSettings/EditorUserSettings.asset`）に保存されるため、プロジェクトごと・作業者ごとの設定になります。

## Unity へのインポート時の自動設定（テクスチャ・マテリアル）

Unity 上で扱う最適化バイナリ（`.ssab`）がプロジェクト内に追加される際、内部の `AssetPostprocessor` がそれを検知して以下のアセット設定を自動で行います。

この自動設定は、**SS Importer ウィンドウからコンバートして出力された場合**でも、**外部の `ssconverter-cli` などで変換済みの `.ssab` を手動で Unity プロジェクト内にコピー（インポート）した場合**のどちらでも、問題なく適切に処理されます。

- **テクスチャのインポート設定**: `.ssab` と同フォルダにあるテクスチャ（画像ファイル）に対して、2D アニメーション向けの最適なインポート設定（無圧縮・Clamp・Mipmap 無効・アルファ透過など）が自動適用されます。
- **共有マテリアルの自動生成**: `.ssab` が参照するテクスチャに対応する `.mat` ファイルが、テクスチャに隣接する位置に 1 つだけ自動生成されます。

## フォルダの一括変換

SS Importer ウィンドウ（Unity メニュー: **Window > SpriteStudio > Importer**）のドロップ領域に**フォルダ**をドロップすると、その中の `.sspj` を**再帰的に `.ssab` へ一括変換**します。複数ファイルの同時ドロップにも対応しています。

## アニメーション変更の反映（再コンバート）

SpriteStudio 側でアニメーションを修正した際、最新の `.sspj` を再度読み込んで Unity 上の `.ssab` を更新する（再コンバートする）方法は複数あります。

- **インスペクタ**: `SpriteStudioAnimeAsset` のインスペクタ、または `SpriteStudioPlayer` / `SpriteStudioGraphic` のインスペクタで **Anime Asset** の下にある **Reconvert** ボタンから実行できます。書き先はそのアセットがあるフォルダなので、フォルダを移動・リネームした後でも正しい場所に書き戻されます。
- **プロジェクトブラウザの右クリック**: `.ssab` を選んで **SpriteStudio > Reconvert** を実行すると、選択した分だけ同じことを行います。
- **SS Importer ウィンドウの Recent リスト**: 行をクリックすると再コンバートが走ります。右クリックメニューの **Reconvert** からも実行できます。こちらは `.sspj` を起点にするため、書き先は新規ドロップと同じく **Output Folder** です。

## 注意点

- **`.sspj` は Unity プロジェクト外（リポジトリ外など）で管理することを推奨します**。SS Importer ウィンドウへドロップすれば、Unity プロジェクト内の出力先（Output Folder）に変換結果（`.ssab`）のみが生成されます。ゲームに不要な元データを Unity プロジェクトに含めない運用が可能です。

---

## CI/CD 向け: SDK の CLI で変換する

Unity エディタを起動せずにコマンドラインから変換したい場合（CI/CD やビルドパイプラインへの組み込み、大量アセットの一括変換など）は、SpriteStudio-SDK に同梱されている独立したコマンドラインコンバータを使用できます。エディタ内蔵のインポータが裏で利用している `libssconverter` と同じエンジンです。

1. [SpriteStudio-SDK の Releases](https://github.com/cri-middleware/SpriteStudio-SDK/releases) から、該当プラットフォーム（Windows / macOS / Linux）向けの CLI コンバータを取得します。
2. ターミナル（またはコマンドプロンプト）を開き、`.sspj` ファイルへのパスを引数に渡して実行します。

    **Windows:**

    ```powershell
    .\ssconverter-cli.exe path\to\your.sspj
    ```

    **macOS / Linux:**

    ```bash
    ./ssconverter-cli path/to/your.sspj
    ```

    実行すると、元の `.sspj` と同じディレクトリに `<sspj名>/` というフォルダが作成され、その配下に `.ssab`（および `.ssqb`）と画像ファイルが出力されます。

    `-e` / `--embedded` は付けずに実行してください。本プレイヤーが読むのは既定の出力で、画像が `.ssab` の隣に別ファイルで並びます。

3. 出力された **`<sspj名>/` フォルダごと**、Unity プロジェクトの `Assets/` 配下へ配置します。すると `SpriteStudioAnimeAsset` として自動的にインポートされ、上記のテクスチャやマテリアルの自動設定も同様に行われます。

詳細なオプションや仕様は、SDK リポジトリの [`ssconverter-cli` の README](https://github.com/cri-middleware/SpriteStudio-SDK/blob/HEAD/cli/README.ja.md) を参照してください。
