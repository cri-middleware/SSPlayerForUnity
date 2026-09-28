# ⚡ パフォーマンスチューニングと高度な設定

[← ドキュメント目次へ戻る](../index.md)

本ページでは、表示・パフォーマンスを調整するための設定と、カスタムシェーダや Canvas 経路の制約など、一歩進んだトピックを扱います。

## 座標スケール（Pixels Per Unit / PPU）

`SpriteStudioPlayer`（ワールド）の **Pixels Per Unit (PPU)** は、SpriteStudio のピクセル座標を Unity のワールド単位へ換算する係数です。設定はスクリプトの `PixelsPerUnit` プロパティで行います（インスペクタには表示されません）。

- 既定値 `1` … ピクセル等倍（SS のピクセル座標がそのままワールド単位）。
- `100` など … Unity のスプライト慣習（1 ユニット = 100 px）に合わせたい場合に設定。

> Canvas (`SpriteStudioGraphic`) は RectTransform 座標系で描画されるため、PPU 設定はありません。

## フレーム進行: Frame Skipping（実時間追従）

インスペクタの **Frame Skipping**（スクリプトからは `FrameSkipEnabled`）は、フレームの進め方を切り替えます。

| 設定 | 挙動 | 用途 |
|---|---|---|
| ON（既定 / timed） | 経過時間ぶんフレームを進める。負荷でフレーム落ちしても**再生時間が一定**に保たれる | 通常の再生 |
| OFF（fixed） | 毎 update で必ず 1 フレームずつ進める（コマ送り） | 検証・収録など、確実に全フレーム描きたい場合 |

## サブフレーム補間（Sub-Frame Interpolation）

インスペクタの **Sub-Frame Interpolation**（スクリプトからは `SubFrameEnabled`）を有効にすると、フレーム間を補間して滑らかに描画します。低フレームレートのアニメーションを高リフレッシュレートで滑らかに見せたい場合に有効です。

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

**イベント通知**は避けられない箇所のみ確保します。Signal のコマンドパラメータは再利用される型付きバッファに展開され（`SpriteStudioSignal.GetParam` / `TryGetParam` で取得）、数値パラメータは一切確保しません。残るのはパーツ名・コマンド ID・文字列パラメータのマネージド文字列生成のみ（不可避）で、Signal の数とバーストに比例します。そのため Signal を大量・高頻度に発火するアニメーションでは、モバイル / WebGL で軽微なヒッチの原因になり得ます。

## カスタムシェーダ / AddOn（パーツ単位）

SpriteStudio のパーツに設定された AddOn（sepia / hsb / outline / blur / pixelate / noise / wave など）に対応します。

- 組み込み AddOn は設定不要で動作します。パッケージには既定カタログ `Runtime/Resources/SpriteStudioAddOnCatalog.asset` が同梱されており、これが自動的に解決します。
- 独自のカスタムシェーダを追加するには、この既定カタログに登録します。アセットを選択し、SpriteStudio エディタでパーツに設定したシェーダ ID と一致する **Id Name** と、AddOn 契約（`SpriteStudioAddOn.hlsl`）に従う **Shader** を持つエントリを追加してください。**Window > SpriteStudio > Create or Update Default AddOn Catalog** メニューはカタログを組み込み AddOn から再構築するため、実行後はカスタムエントリを追加し直してください。
- `MaterialPropertyBlock` は使用せず、`(shaderId, blend)` ごとのマテリアルプールで供給するため、**SRP Batcher を阻害しません**。

> AddOn 経路は URP / Built-in の両方に対応しています。

## マスク / クリッピング

SpriteStudio のマスク / クリッピング（パーツ単位）に、ステンシルバッファを用いて対応します。

- **ワールド経路（`SpriteStudioPlayer`）**: SpriteStudio 内部のマスク機構を用いてステンシルで描画します。URP では SSAO / Deferred / Decal と併用してもマスクが破綻しないよう、使用するステンシルビットを限定しています。
- **Canvas 経路（`SpriteStudioGraphic`）**: SpriteStudio 内部のマスクは抑止し、代わりに uGUI の **Mask** / **RectMask2D** に追従します（親にマスクを置くと、このグラフィックがクリップされます。グラフィック自身に **Mask** を置くと、その子がクリップされます）。

## Canvas (uGUI) 経路の制約

`SpriteStudioGraphic` は CanvasRenderer の「1 レンダラー = 1 メッシュ + 1 マテリアル」という制約に合わせて実装されています。次の点に注意してください。

- **単一アトラス前提**: 複数アトラスを使うアニメーションでは、最初のアトラスのみが描画され、他アトラスのパーツは正しいテクスチャをサンプリングできません（警告がログに出ます）。**1 つのアトラスへまとめる**か、**ワールド経路（`SpriteStudioPlayer`）を使用**してください。
- **パーツの描画モード**: MIX（ミックス） / ADD（加算） はシェーダ内で頂点単位に振り分けます。それ以外のブレンドは MIX に集約されます。
- **TexCoord1 / TexCoord2 の有効化**: パーツカラー用のデータを TEXCOORD1 と TEXCOORD2 で渡すため、対象 Canvas の `additionalShaderChannels` に `TexCoord1` と `TexCoord2` を自動付与します。頂点カラーは uGUI に任せており、`Image` と同じくグラフィックの **Color** を運びます。

複雑なブレンドや複数アトラスを正しく使いたい場合は、ワールド経路（`SpriteStudioPlayer`）の利用を推奨します。ワールド経路は Mul2 を Mul、Screen2 を Screen として描画し、Div2 / Overlay2 は MIX として描画します（[制限事項](../limitations.md) 参照）。

## レンダーパイプライン

Universal (URP) / Built-in (BRP) の両方に対応しています。

- URP でインポート直後にマテリアル参照が不整合になり、描画がピンク色になることがあります。多くはエディタの再起動や再インポートで解消します。
- 編集モードでは開始フレームの静止プレビューのみ表示されます。動作確認は Play モードで行ってください。
