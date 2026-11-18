# 🔘 ボタンと UI 状態

SpriteStudio のアニメーションを uGUI のボタンやトグルにして、いまの状態（ポインタが乗っている、押している、無効、フォーカス、オン・オフ）を見せ、形の不揃いな絵ではポインタを絵の上だけで受けるようにします。このページは Canvas でそれを組む人向けです。グラフィックそのものは [基本的な使い方](./usage_basic.md) にあります。

3 つのコンポーネントは、専用のアセンブリ `SpriteStudio.UI`（名前空間も `SpriteStudio.UI`）にあります。プレイヤーからは呼ばれないので、これらを付けないグラフィックはこれまでどおりに動きます。

| コンポーネント | 付ける先 | すること |
|---|---|---|
| **SpriteStudio Button** | `SpriteStudioGraphic` | 状態ごとにアニメーションの区間を流す uGUI の `Button` |
| **SpriteStudio Toggle** | `SpriteStudioGraphic` | 同じことをし、オンの間は `selected_` の区間を流す uGUI の `Toggle` |
| **SpriteStudio Raycast Filter** | `SpriteStudioGraphic` | ポインタを `RectTransform` 全体ではなく、パーツの輪郭の上だけで受ける |

## ボタンを組む

1. [基本的な使い方](./usage_basic.md) のとおり、Canvas に **SpriteStudio Graphic** を置き、**Anime Asset** と **Animation Name** を設定します。
2. 同じ GameObject に **Add Component > SpriteStudio > SpriteStudio Button**（または **SpriteStudio Toggle**）を追加します。**Transition** は最初から **None** です。状態はアニメーションが見せるので、上から色を付けると二重になります。
3. **On Click ()** は普通の `Button` と同じようにつなぎます（**SpriteStudio Toggle** では **On Value Changed (Boolean)** をつなぎます）。

ゲームの実行中は、ボタンがグラフィックの再生を握ります。アニメーションを選び、区間を設定し、再生と停止をします。グラフィックの再生はボタンに任せてください。ボタンは起動時にグラフィックの **Auto Play** を切ります。エディットモードでは、グラフィックはいつものプレビューを表示します。

## 状態を作る

1 つのアニメーションに、状態と同じ名前のラベルを置きます。`normal`・`hover`・`pressed`・`disabled`・`focused` で、小文字の完全一致です。状態は、そのラベルから、名前を問わず次のラベルの 1 フレーム手前まで（無ければアニメーションの終わりまで）をループ再生します。

| uGUI | 流すラベル | そのラベルが無いとき |
|---|---|---|
| 下のどれでもない | `normal` | アニメーション全体 |
| ポインタが乗っている | `hover` | `normal` |
| ポインタで押してから離すまで（途中でポインタが外れても）、またはキーボードやゲームパッドの **Submit** | `pressed` | `hover`、次に `normal` |
| **Interactable** がオフ（`CanvasGroup` によるものも含む） | `disabled` | `normal` |
| ナビゲーションで選ばれている、またはクリックで選ばれて（**Navigation** が **None** のときは選ばれません）、ほかが選ばれるまで | `focused` | `normal` |

いくつも同時に立ったときは、`disabled`、`pressed`、`hover`、`focused` の順で 1 つを選び、どれも立っていなければ `normal` です。uGUI 自身は Selected を Highlighted より優先しますが、ここでは hover をフォーカスより優先します。状態が変わると、新しい区間の先頭からすぐに流し、流れていた区間は捨てます。

ラベルは、グラフィックの **Animation Name** のアニメーションから読みます。スクリプトから別のアニメーションを選ぶ（`SetAnimation`）と、以後はそのアニメーションから読みます。

### トグル

オンの **SpriteStudio Toggle** は、ラベルの頭に `selected_` を付けて読みます（`selected_normal`、`selected_hover` など）。そのラベルが無ければ、付けないラベルより先にほかの `selected_` のラベルを試すので、オンのトグルはポインタが乗っていてもオンに見えます。

| オンで | 試す順 |
|---|---|
| ポインタが乗っている | `selected_hover`、`selected_normal`、`hover`、`normal` |
| 押している | `selected_pressed`、`pressed`、`selected_hover`、`selected_normal`、`hover`、`normal` |

押下だけは、ほかの `selected_` のラベルより先に `pressed` を引きます。押している間だけの状態で、押したことが伝わったと見せるためです。

## 手持ちのアニメーションを使う

**State Clips** は、状態のラベルの代わりに、アニメーション全体（**Animation** だけ）、同じアニメーションの別のラベル（**Label** だけ）、別のアニメーションのラベル（両方）を流します。トグルの `selected_` の状態には **Selected** を付けます。ある状態へ落ちてくる状態は、その状態のクリップも引き継ぎます。`pressed` のラベルもクリップも無ければ、押下は `hover` のクリップを流します。インスペクタでは、**Animation** はグラフィックのアセットのアニメーションから、**Label** はクリップが流すアニメーションのラベルから選べます。

```csharp
using SpriteStudio.UI;

_button.StateClips = new[]
{
    new SpriteStudioUiClip(SpriteStudioUiState.Normal, false, "idle", ""),   // アニメーション全体
    new SpriteStudioUiClip(SpriteStudioUiState.Hover, false, "", "over"),    // 同じアニメーションのラベル
    new SpriteStudioUiClip(SpriteStudioUiState.Pressed, false, "push", "down"), // 別のアニメーションのラベル
};
```

クリップで指定した区間も、次のラベルの 1 フレーム手前までです。アセットに無いアニメーションやラベルを指すクリップは Console に 1 度だけ報告し、その状態はクリップが無いものとして流します。グラフィックの **Fit** は、ラベルを読むアニメーションのキャンバスに合わせたままなので、キャンバスの大きいアニメーションをクリップで流しても絵は縮みません。

## Reduced motion

Unity からは OS の「視差効果を減らす」設定を読めないので、ゲームが自分の設定から **Reduced Motion**（`ReducedMotion`）を立てます。オンの間は、どの状態も区間の先頭フレームで止め（停止）、ユーザーデータ・シグナル・オーディオのイベントは出ません。

アニメーションに `reduced_motion` ラベルがあれば、`normal`（と `normal` へ落ちてくる状態）だけは、代わりにその区間をイベント付きでループ再生します。止まった待機の代わりの、穏やかな待機です。それ以外の状態と `selected_` の区間は止めたままなので、押下・無効・オンの見た目は残ります。

## ポインタを絵の上だけで受ける

Canvas は、グラフィックの `RectTransform` のどこでも、透明な余白も含めてクリックを受けます。グラフィックに **SpriteStudio Raycast Filter** を付けると、パーツの輪郭の上だけで受け、余白のクリックは後ろにあるものへ届きます。

- **Parts** が空: 輪郭を持つすべてのパーツ
- **Parts** に名前を並べる: そのパーツだけ（インスペクタではアセットのパーツ名から選べます）。アセットに無い名前は飛ばします

輪郭は、表示中のフレームの [`TryGetPartQuad`](./usage_scripting.md#パーツの輪郭と当たり判定) です。矩形の外へは広げず、キーボードとゲームパッドのナビゲーションは絞りません。uGUI は親のフィルタにも問い合わせるので、ボタンの下の階層にあるグラフィック（ラベルなど）も、同じパーツの上でだけポインタを受けます。uGUI に問われたときだけ答えるので、毎フレームの負担はありません。

## 読み上げ

既定では、アニメーションは装飾として扱い、何も読み上げません。Unity は uGUI のコントロールにアクセシビリティのノードを作らないので、自分で `AccessibilityHierarchy` を組むゲームは、そのコントロールのノードにボタンやトグルの **Accessibility Label**（`AccessibilityLabel`）を読み込みます。役割と操作はそのノードが持ちます。

## スクリプトから状態の規則を使う

`SpriteStudioUiStates` は、自作のコントロール向けに規則だけを持っています。`Of` は状態を選び、`Chain` は状態が読む先を順に返し（各項目は状態と、`selected_` 側かどうかの組。ラベル名は `LabelOf` で得られます）、`TryGetLabelSection` は `SpriteStudioLabelTable`（グラフィックの `GetLabelTable`）からラベルの区間を読み、`TryResolve` はクリップと reduced motion を含めて状態が流す区間を返します。

## 見本プロジェクト

リポジトリの [`examples/UI_Ringo`](https://github.com/cri-middleware/SSPlayerForUnity/tree/HEAD/examples/UI_Ringo) には、Ringo（SpriteStudio のサンプルキャラクター）で作ったボタンとトグルがあります。Ringo のアニメーションを指すクリップと、それぞれのフィルタが付いています。ほかに、Interactable と Reduced motion を切り替えるパネル、パーツの輪郭の重ね描き、**Fit** を見せる矩形の列があります。動かし方はその README にあり、リポジトリの clone が必要です。
