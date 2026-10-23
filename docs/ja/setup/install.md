# 📥 インストール

[← ドキュメント目次へ戻る](../index.md)

本ページでは、SpriteStudio Player for Unity をプロジェクトへ導入する方法を説明します。リリースのパッケージを npm または tarball から導入します。

## 対応環境

| 項目 | 内容 |
|---|---|
| Unity バージョン | **Unity 6.3 LTS (6000.3)** 以降 |
| レンダーパイプライン | Universal (URP) / Built-in (BRP) |
| 依存パッケージ | Burst / Mathematics / Collections / Timeline / uGUI（Package Manager が自動解決） |
| 対応プラットフォーム | Windows / macOS / Linux / Android / iOS / WebGL |

依存パッケージはすべて Unity 標準パッケージのため、本パッケージを追加すると Package Manager が自動的に解決します。

プロジェクト作成時のテンプレートは **2D / 3D のどちらでも構いません**。本パッケージが要求するのはレンダーパイプラインが URP または Built-in であることで、テンプレートの種別ではありません。URP では **Universal Renderer** でも、2D テンプレートが設定する **2D Renderer** でも同じように描画されます。どちらでもライティングは受けず、Sprite-Unlit-Default を使うスプライトと同じく Light 2D の影響を受けません。

> **導入時に再起動を求められた場合:** エディタの起動中にプロジェクトの Burst が別バージョンへ変わると、Unity が **Burst Package Update Detected** ダイアログを表示します。本パッケージ固有のエラーではありません。**Restart Now**（Burst のバージョンによっては **OK** を選んでから手動で）エディタを再起動してください。再起動するまでは Burst コンパイルが停止したままになり、再生が大幅に遅くなります。

---

## 1. リリースのパッケージからのインストール（推奨）

最も簡単で確実な方法です。各リリースが公開するコンパイル済みのパッケージを使います。npm と GitHub Releases の tarball の 2 か所にあり、中身は同じです。npm なら Package Manager が名前で取得でき、tarball なら設定が要りません。

### npm から

パッケージは npm に [`jp.co.cri-mw.spritestudio.ssplayer-unity`](https://www.npmjs.com/package/jp.co.cri-mw.spritestudio.ssplayer-unity) として公開しています。プロジェクトごとに一度、npm を `jp.co.cri-mw` scope の registry として登録してから、名前でインストールします。

1. **Edit > Project Settings > Package Manager** を開き、**Scoped Registries** に次の項目を追加して保存します。
   - **Name**: `npmjs`（任意の名前で構いません）
   - **URL**: `https://registry.npmjs.org`
   - **Scope(s)**: `jp.co.cri-mw`
2. **Window > Package Manager** を開き、左上の **＋** ボタンから **Install package by name...** を選びます。
3. 名前に `jp.co.cri-mw.spritestudio.ssplayer-unity`、バージョンにインストールするバージョンを入力し、**Install** を押します。[npm のバージョン一覧](https://www.npmjs.com/package/jp.co.cri-mw.spritestudio.ssplayer-unity?activeTab=versions)は [GitHub Releases](https://github.com/cri-middleware/SSPlayerForUnity/releases) と同じです。

同じ 2 つの設定は、`Packages/manifest.json` に直接書いても構いません。既にある項目の隣に足し、`X.Y.Z` をインストールするバージョンに置き換えます。

```json
{
  "scopedRegistries": [
    {
      "name": "npmjs",
      "url": "https://registry.npmjs.org",
      "scopes": ["jp.co.cri-mw"]
    }
  ],
  "dependencies": {
    "jp.co.cri-mw.spritestudio.ssplayer-unity": "X.Y.Z"
  }
}
```

### tarball から

1. パッケージの tarball をダウンロードします。下記の恒久リンクは常に最新リリースを指します。
   - [`jp.co.cri-mw.spritestudio.ssplayer-unity.tgz`](https://github.com/cri-middleware/SSPlayerForUnity/releases/latest/download/jp.co.cri-mw.spritestudio.ssplayer-unity.tgz)
   - 特定バージョンを選びたい場合は、[GitHub Releases](https://github.com/cri-middleware/SSPlayerForUnity/releases) ページから `.tgz` アセットをダウンロードしてください。
2. Unity メニューから **Window > Package Manager** を開きます。
3. 左上の **＋** ボタンから **Install package from tarball...** を選び、ダウンロードしたファイルを選択します。

> **ダウンロードの検証（任意）:** 各リリースには `SHA256SUMS` も添付されています。本パッケージはネイティブバイナリを含むため、必要に応じてダウンロードした tarball が改変されていないか確認できます。
>
> ```sh
> # SHA256SUMS と .tgz を同じフォルダに置いて実行
> sha256sum -c SHA256SUMS      # macOS: shasum -a 256 -c SHA256SUMS
> ```
>
> バージョン付き tarball と安定エイリアスの両方が記載されているため、ダウンロードした側が `OK`、もう一方は `No such file or directory` と表示されます。

> **注意:** Package Manager の *Install package from git URL...*ではこのパッケージを導入できません。ネイティブプラグインのバイナリはリリースのパッケージにのみ同梱されるため、npm から、または *Install package from tarball...* で追加してください。

---

パッケージそのもの（C# スクリプトやその下のネイティブコア）を改造する場合は、[ビルドガイド](./build.md) を参照してください。

---

## インストールの確認

インストールに成功すると、次が利用可能になります。

- **メニュー**: **Window > SpriteStudio > Importer**（SS Importer ウィンドウ）
- **コンポーネント**: `SpriteStudio Player` / `SpriteStudio Graphic`（Add Component から追加可能）

## 次のステップ

インストールが完了したら、以下のいずれかへ進んでください。

- まだ `.ssab`（`SpriteStudioAnimeAsset`）を持っていない場合 → [アセットのインポートとエディタ連携](../workflow/usage_asset_pipeline.md) で SpriteStudio プロジェクト（`.sspj`）から変換・生成します。
- すでにインポート済みのアセットがある場合 → [基本的な使い方](../workflow/usage_basic.md) でシーンへの配置と再生を行います。
