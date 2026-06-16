# インストール

[← ドキュメント目次へ戻る](../index.md)

本ページでは、SpriteStudioPlayer for Unity をプロジェクトへ導入する方法を説明します。

## 対応環境

| 項目 | 内容 |
|---|---|
| Unity バージョン | **Unity 6.3 LTS (6000.3)** 以降 |
| レンダーパイプライン | Built-in (BRP) / Universal (URP) |
| 依存パッケージ | Burst / Mathematics / Collections / Timeline（Package Manager が自動解決） |

依存パッケージはすべて Unity 標準パッケージのため、本パッケージを追加すると Package Manager が自動的に解決します。サードパーティ製パッケージは不要です。

## インストール方法

次のいずれかの方法で導入できます。

### 方法 1: Git URL から追加（推奨）

1. Unity メニューから **Window > Package Manager** を開きます。
2. 左上の **＋** ボタンから **Add package from git URL...** を選択します。
3. 次の URL を入力して **Add** を押します。

   ```
   https://github.com/SpriteStudio/SSPlayerForUnity.git?path=/package
   ```

   > **重要:** パッケージはリポジトリ直下ではなく `package/` 配下にあるため、`?path=/package` の指定が必須です。これを省略するとインストールに失敗します。

4. 特定のブランチやタグに固定したい場合は、末尾に `#<ref>` を付与します。

   ```
   # 安定版 (main ブランチ) を使う場合
   https://github.com/SpriteStudio/SSPlayerForUnity.git?path=/package#main
   ```

### 方法 2: ローカルから追加

リポジトリを手元に clone してから導入する方法です。プラグイン自体に手を入れる場合や、オフライン環境で使う場合に向いています。

1. リポジトリを clone します。

   ```sh
   git clone https://github.com/SpriteStudio/SSPlayerForUnity.git
   ```

2. 次のいずれかで導入します。
   - **Package Manager から**: **Add package from disk...** を選び、`package/package.json` を指定します。
   - **直接配置**: `package/` フォルダを Unity プロジェクトの `Packages/` 配下へコピーします。

> **補足:** ローカル導入の場合、リポジトリにはあらかじめビルド済みのネイティブライブラリが含まれています。自分でランタイムをビルドし直したい場合は [ビルドガイド](./build.md) を参照してください。

## インストールの確認

インストールに成功すると、次が利用可能になります。

- **メニュー**: **Window > SpriteStudio > Importer**（インポート用ウィンドウ）
- **コンポーネント**: `SpriteStudio Player` / `SpriteStudio Graphic`（Add Component から追加可能）

## 次のステップ

インストールが完了したら、以下のいずれかへ進んでください。

- まだ `.ssab`（`SpriteStudioAnimeAsset`）を持っていない場合 → [アセットのインポートとエディタ連携](../workflow/usage_asset_pipeline.md) で SpriteStudio プロジェクト（`.sspj`）から変換・生成します。
- すでにインポート済みのアセットがある場合 → [基本的な使い方](../workflow/usage_basic.md) でシーンへの配置と再生を行います。

## トラブルシューティング

- **`?path=/package` を付けずに Git URL を追加してしまった**: パッケージが見つからずエラーになります。`?path=/package` を付け直してください。
- **インポート直後に表示が不安定**: アセットのインポート直後（特に URP）に描画が乱れることがあります。多くはエディタの再起動で解消します。
- **ビルドからネイティブライブラリが除外される**: プラグインのプラットフォーム設定を確認してください。詳細は [ビルドガイド](./build.md) を参照してください。
