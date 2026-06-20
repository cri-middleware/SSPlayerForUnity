# 基本的な使い方

[← ドキュメント目次へ戻る](../index.md)

ここでは、`.sspj` をインポートしてからアニメーションを再生するまでの最短手順を説明します。インポートの詳細は [アセットのインポートとエディタ連携](./usage_asset_pipeline.md)、スクリプトによる制御は [スクリプト制御とイベント](./usage_scripting.md) を参照してください。

## 1. アニメーションをインポートする

1. Unity メニューから **Window > SpriteStudio > Importer** を開きます。
2. **Output Folder**（出力先）を設定します（既定は `Assets/ssab_generated`）。
3. SpriteStudio で作成した `.sspj` ファイルを、ウィンドウのドロップ領域へドラッグ＆ドロップします。

変換が完了すると、出力先に `SpriteStudioAnimeAsset`（`.ssab` を内包した ScriptableObject）が生成されます。

## 2. プレイヤーを設置する

用途に応じて 2 種類のコンポーネントを使い分けます。

| 用途 | コンポーネント | 描画方式 |
|---|---|---|
| 3D / 2D のワールド空間 | `SpriteStudioPlayer` | MeshRenderer |
| Canvas (uGUI) の UI | `SpriteStudioGraphic` | CanvasRenderer |

### 方法 A: アセットをドラッグ＆ドロップして設置する（推奨）

最も手軽な方法です。インポートで生成された `SpriteStudioAnimeAsset`（SpriteStudio のアイコンが付いたアセット）を、プロジェクトウィンドウから **Hierarchy ウィンドウ**（または **Scene ビュー**）へドラッグ＆ドロップします。

ドロップすると設置するコンポーネントを選ぶメニューが表示されるので、用途に応じて選択してください。

- **SpriteStudioPlayer** … ワールド空間用。選んだアセットを割り当てた GameObject が作成されます。
- **SpriteStudioGraphic (Canvas UI)** … Canvas UI 用。

選択すると GameObject が作成され、**SpriteStudio Asset には今ドロップしたアセットが自動的に割り当てられます**。あとは **Animation Name** を設定すれば再生できます。

> [!TIP]
> - **SpriteStudioGraphic (Canvas UI)** を選んだ際にシーンへ `Canvas` が無い場合は、`Canvas`（および `EventSystem`）が自動的に作成され、その下に設置されます。
> - Hierarchy 上で既存の GameObject の上にドロップすると、その子として設置されます。
> - 複数のアセットを同時にドロップした場合は、まとめて設置されます。
> - `SpriteStudioAnimeAsset` のインスペクタにある **「Instantiate in Scene」** ボタンからもワールド用プレイヤーを設置できます。

### 方法 B: コンポーネントを手動で追加する

空の GameObject を用意し、自分でコンポーネントを追加する方法です。

#### ワールド空間に置く場合（`SpriteStudioPlayer`）

1. 空の GameObject を作成します。
2. **Add Component** から **SpriteStudio Player** を追加します（`MeshFilter` / `MeshRenderer` は自動で付与されます）。
3. インスペクタの **SpriteStudio Asset** に、インポートした `SpriteStudioAnimeAsset` を割り当てます。
4. **Animation Name** に再生したいアニメーション名を入力します。

#### Canvas (uGUI) に置く場合（`SpriteStudioGraphic`）

1. **Canvas** 下に空の GameObject を作成します。
2. **Add Component** から **SpriteStudio Graphic** を追加します（`RectTransform` / `CanvasRenderer` は自動で付与されます）。
3. **SpriteStudio Asset** と **Animation Name** を上記と同様に設定します。

## 3. 再生する

**Auto Play** が有効（既定）であれば、再生（Play）モードに入ると **Animation Name** のアニメーションが自動的に再生されます。

スクリプトから明示的に再生する場合は次のようにします。

```csharp
using SpriteStudio.Runtime;
using UnityEngine;

public class Sample : MonoBehaviour
{
    [SerializeField] private SpriteStudioPlayer _player;

    void Start()
    {
        _player.Play("walk"); // アニメーション名を指定して再生
    }
}
```

## インスペクタの主な設定項目

`SpriteStudioPlayer` / `SpriteStudioGraphic` で共通して設定できる項目です。

| 項目 | 既定値 | 説明 |
|---|---|---|
| **SpriteStudio Asset** | （なし） | 再生対象の `SpriteStudioAnimeAsset` |
| **Animation Name** | `""` | 再生するアニメーション名 |
| **Auto Play** | `true` | シーンの実行起動時に自動再生するか |
| **Loop Count** | `-1` | ループ回数（`-1` = 無限 / `1` = 1 回 / `n` = n 回） |
| **Speed Scale** | `1.0` | 再生速度の倍率 |
| **Sub Frame Enabled** | `false` | フレーム間補間（サブフレーム描画）を行うか |
| **Skip Frames** | `true` | 実時間追従。ON = 経過時間ぶん進める / OFF = 毎 update で 1 フレームずつ |

`SpriteStudioPlayer`（ワールド）には、さらに次の項目があります。

| 項目 | 既定値 | 説明 |
|---|---|---|
| **Sorting Layer / Sorting Order** | `Default` / `0` | 描画順（MeshRenderer のソート設定） |
| **Pixels Per Unit (PPU)** | `1` | 1 ワールド単位あたりのピクセル数。Unity のスプライト慣習に合わせるなら `100` などを設定 |
| **Shader Catalog** | （なし） | パーツ単位の AddOn / カスタムシェーダのカタログ（[高度な設定](./tips.md) 参照） |
| **Part Trackers** | （空） | 指定パーツに Transform を追従させる設定（[スクリプト制御とイベント](./usage_scripting.md) 参照） |

## 編集モードでのプレビュー

両コンポーネントは `[ExecuteAlways]` で動作し、シーンに設置すると**開始フレームの静止プレビュー**が表示されます。ただし編集モードでは再生は進みません。アニメーションの動きを確認するには **Play モード**に入ってください。

## マスクパーツへの対応

本プラグインは SpriteStudio の「マスク機能（Mask）」にも対応しています。

- **ワールド（`SpriteStudioPlayer`）**: 特別な設定は不要です。`.ssab` にマスクパーツが含まれていれば、ステンシルバッファを用いて自動的にクリッピング処理が行われます。そのままシーンに配置してお使いいただけます。
- **Canvas（`SpriteStudioGraphic`）**: SpriteStudio 内部のマスクは抑止され、代わりに uGUI の **Mask** / **RectMask2D** に追従します（親にこれらを置くとクリップされます）。

詳細は [パフォーマンスチューニングと高度な設定](./tips.md#マスク--クリッピング) を参照してください。
