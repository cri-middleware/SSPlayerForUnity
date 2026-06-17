# インストール

[← ドキュメント目次へ戻る](../index.md)

本ページでは、SpriteStudioPlayer for Unity をプロジェクトへ導入する方法を説明します。ご自身の目的に合わせて、3つの導入方法から選んでください。

## 対応環境

| 項目 | 内容 |
|---|---|
| Unity バージョン | **Unity 6.3 LTS (6000.3)** 以降 |
| レンダーパイプライン | Built-in (BRP) / Universal (URP) |
| 依存パッケージ | Burst / Mathematics / Collections / Timeline（Package Manager が自動解決） |

依存パッケージはすべて Unity 標準パッケージのため、本パッケージを追加すると Package Manager が自動的に解決します。

---

## 1. パッケージからのインストール（推奨）

最も簡単で確実な方法です。GitHub Releases で配布されているコンパイル済みの Unity パッケージを利用します。

### 方法 A: Package Manager から URL で追加する
1. Unity メニューから **Window > Package Manager** を開きます。
2. 左上の **＋** ボタンから **Add package from URL...** （または git URL）を選択します。
3. リリースページに記載されている `.tgz` またはパッケージ用 URL を入力して **Add** を押します。

   *(※ 実際の URL は [GitHub Releases](https://github.com/SpriteStudio/SSPlayerForUnity/releases) の最新版を参照してください)*

### 方法 B: ダウンロードしてローカルから追加する
1. [GitHub Releases](https://github.com/SpriteStudio/SSPlayerForUnity/releases) から最新の成果物（`.tgz` など）を直接ダウンロードします。
2. Package Manager の **＋** ボタンから **Add package from tarball...** を選び、ダウンロードしたファイルを選択します。

---

## 2. ローカルソースからのインストール（カスタマイズ用）

Unity 側の C# スクリプトを改造したり、パッケージの中身を直接触りたい場合の方法です。Rust 環境の構築は不要です。

1. リポジトリを clone またはダウンロードします。
   ```sh
   git clone https://github.com/SpriteStudio/SSPlayerForUnity.git
   ```
2. ネイティブライブラリ（`libssruntime`）をダウンロード・配置するために、`scripts/` フォルダ内にあるセットアップスクリプトを実行します。
   - **Windows**: `scripts\setup-download.bat` （または `scripts\setup-download.ps1`）
   - **Mac/Linux**: `./scripts/setup-download.sh`
3. 準備が整った `package/` フォルダを、ご自身の Unity プロジェクトの `Packages/` 配下にコピーするか、Package Manager の **Add package from disk...** から `package.json` を指定して追加します。

---

## 3. ソースコードからのフルビルド（プラグイン開発者向け）

Rust コア (`libssruntime`) 自体の開発やデバッグを行う場合の手順です。
セットアップスクリプトは使用せず、ローカルの Cargo 環境を使用してソースコードからネイティブライブラリを直接ビルドします。

1. サブモジュールを含めてリポジトリを clone します。
   ```sh
   git clone --recursive https://github.com/SpriteStudio/SSPlayerForUnity.git
   ```
2. `scripts/build-runtime.sh`（Windows の場合は `scripts\build-runtime.ps1` など）を実行して、Cargo で Rust コアをビルドし、プラグインフォルダへ配置します。
3. 詳細は **[ビルドガイド](./build.md)** を参照してください。

---

## インストールの確認

インストールに成功すると、次が利用可能になります。

- **メニュー**: **Window > SpriteStudio > Importer**（インポート用ウィンドウ）
- **コンポーネント**: `SpriteStudio Player` / `SpriteStudio Graphic`（Add Component から追加可能）

## 次のステップ

インストールが完了したら、以下のいずれかへ進んでください。

- まだ `.ssab`（`SpriteStudioAnimeAsset`）を持っていない場合 → [アセットのインポートとエディタ連携](../workflow/usage_asset_pipeline.md) で SpriteStudio プロジェクト（`.sspj`）から変換・生成します。
- すでにインポート済みのアセットがある場合 → [基本的な使い方](../workflow/usage_basic.md) でシーンへの配置と再生を行います。
