# パフォーマンスチューニングと高度な設定

[← ドキュメント目次へ戻る](../index.md)

本ページでは、表示・パフォーマンスを調整するための設定と、カスタムシェーダや Canvas 経路の制約など、一歩進んだトピックを扱います。

## 座標スケール（Pixels Per Unit / PPU）

`SpriteStudioPlayer`（ワールド）の **Pixels Per Unit (PPU)** は、SpriteStudio のピクセル座標を Unity のワールド単位へ換算する係数です。

- 既定値 `1` … ピクセル等倍（SS のピクセル座標がそのままワールド単位）。
- `100` など … Unity のスプライト慣習（1 ユニット = 100 px）に合わせたい場合に設定。

> Canvas (`SpriteStudioGraphic`) は RectTransform 座標系で描画されるため、PPU 設定はありません。

## フレーム進行: Skip Frames（実時間追従）

**Skip Frames** は、フレームの進め方を切り替えます。

| 設定 | 挙動 | 用途 |
|---|---|---|
| ON（既定 / timed） | 経過時間ぶんフレームを進める。負荷でフレーム落ちしても**再生時間が一定**に保たれる | 通常の再生 |
| OFF（fixed） | 毎 update で必ず 1 フレームずつ進める（コマ送り） | 検証・収録など、確実に全フレーム描きたい場合 |

## サブフレーム補間（Sub Frame Enabled）

**Sub Frame Enabled** を有効にすると、フレーム間を補間して滑らかに描画します。低フレームレートのアニメーションを高リフレッシュレートで滑らかに見せたい場合に有効です。

## 再生速度（Speed Scale）

**Speed Scale** は再生速度の倍率です。`2.0` で 2 倍速、`0.5` で半分の速度になります。実行時にプロパティから変更すると即座に反映されます。

## SSAB と SSQB の使い分け

インポートされるバイナリには 2 種類があります。

- **SSAB (Animation Binary)** … 通常のアニメーションデータで、`SpriteStudioAnimeAsset` としてインポートされます。基本的にはこちらを使用します。
- **SSQB (Sequence Binary)** … 複数のアニメーション（例: `歩き` → `走り` → `ジャンプ`）を SpriteStudio 側で「シーケンス」としてタイムライン上に連結したもので、`SpriteStudioSequenceAsset` としてインポートされます。

> [!IMPORTANT]
> **SSQB の現状**: `.ssqb` の**インポート（データとしての認識・参照）には対応**していますが、**シーケンス再生は現時点では未提供**です（今後対応予定）。当面の連続再生は、[スクリプト制御とイベント](./usage_scripting.md) の `OnAnimationFinished` イベントを使って次のアニメーションへ切り替えることで実現してください。

## ゼロアロケーションについて

本プラグインの Tick / メッシュ構築ループは GC アロケーションを発生させません（`NativeArray` の再利用と `Mesh.SetVertexBufferData` への直接書き込み）。

ただし、**イベント通知**は例外です。特に Signal イベントのパラメータ（`Params` 辞書）は通知時にマネージドメモリを確保します。大量・高頻度に Signal を発火するアニメーションでは、モバイル / WebGL でヒッチの原因になり得ます。常時発生するものではなく、Signal の数とバーストに比例します。

## カスタムシェーダ / AddOn（パーツ単位）

SpriteStudio のパーツに設定された AddOn（sepia / hsb / outline / blur / pixelate / noise / wave など）に対応します。

- `SpriteStudioPlayer` の **Shader Catalog** に `SpriteStudioShaderCatalog` を割り当てると、パーツ単位のシェーダ割当が有効になります。
- 組み込み AddOn はパッケージ同梱のカタログにあらかじめ登録されています。空のままにすると、パーツ単位のシェーダ割当は無効になります。
- `MaterialPropertyBlock` は使用せず、`(shaderId, blend)` ごとのマテリアルプールで供給するため、**SRP Batcher を阻害しません**。

> AddOn 経路は URP / Built-in の両方に対応しています。

## マスク / クリッピング

SpriteStudio のマスク / クリッピング（パーツ単位）に、ステンシルバッファを用いて対応します。

- **ワールド経路（`SpriteStudioPlayer`）**: SpriteStudio 内部のマスク機構を用いてステンシルで描画します。URP では SSAO / Deferred / Decal と併用してもマスクが破綻しないよう、使用するステンシルビットを限定しています。
- **Canvas 経路（`SpriteStudioGraphic`）**: SpriteStudio 内部のマスクは抑止し、代わりに uGUI の **Mask** / **RectMask2D** に追従します（親にマスクを置くと、このグラフィックがクリップされます）。

## Canvas (uGUI) 経路の制約

`SpriteStudioGraphic` は CanvasRenderer の「1 レンダラー = 1 メッシュ + 1 マテリアル」という制約に合わせて実装されています。次の点に注意してください。

- **単一アトラス前提**: 複数アトラスを使うアニメーションでは、最初のアトラスのみが描画され、他アトラスのパーツは正しいテクスチャをサンプリングできません（警告がログに出ます）。**1 つのアトラスへまとめる**か、**ワールド経路（`SpriteStudioPlayer`）を使用**してください。
- **パーツの描画モード**: MIX（ミックス） / ADD（加算） はシェーダ内で頂点単位に振り分けます。それ以外のブレンドは MIX に集約されます。
- **TexCoord1 の有効化**: パーツカラー用のデータを TEXCOORD1 で渡すため、対象 Canvas の `additionalShaderChannels` に `TexCoord1` を自動付与します。

複雑なブレンドや複数アトラス、拡張ブレンド全種を正しく使いたい場合は、ワールド経路（`SpriteStudioPlayer`）の利用を推奨します。

## レンダーパイプライン

Built-in (BRP) / Universal (URP) の両方に対応しています。

- URP でインポート直後にマテリアル参照が不整合になり、描画がピンク色になることがあります。多くはエディタの再起動や再インポートで解消します。
- 編集モードでは開始フレームの静止プレビューのみ表示されます。動作確認は Play モードで行ってください。
