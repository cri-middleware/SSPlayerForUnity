# 🎚️ モーションブレンド

1 つのプレーヤで、複数のアニメーションを同時に再生できます。歩きに足を引きずる動きを重ねる、トラッキングの値で首を振らせる、待機から攻撃へ切り替えるときにカットではなくなめらかに移る、といったことができます。アニメーションは **トラック** に積み、ランタイムがそれを 1 つの姿勢にまとめます。`.ssab` は変える必要がなく、どのアニメーションもプレーヤに割り当てたアセットから取ります。

このページのメソッドは、`SpriteStudioPlayer` と `SpriteStudioGraphic` の両方に、同じ名前・同じ挙動で用意されています。

## トラックの積み重ね

track 0 は、`SetAnimation()` で選んだアニメーションです。その上のトラックは番号順に、下のトラックが作った姿勢の上へ重なります。

```
結果 = セットアップポーズ（アニメーションの .ssae が定義する素の姿勢）
track 0  →  重ねる   （主のアニメーション: SetAnimation）
track 1  →  重ねる   （上に重ねるアニメーション）
track 2  →  重ねる   （さらにもう 1 つ）
```

調整の値は 2 つあり、**どちらを使うかが大事** です。

| | 役割 | 単位 |
|---|---|---|
| **alpha**（`SetTrackAlpha`） | そのトラックを、下の結果にどれだけ重ねるか。**強さの調整はこちら** | トラックごと |
| **weight**（`SetSourceWeight`） | **同じトラック** に載せた複数のアニメーションの比率 | ソースごと |

ソースの weight だけでは強さは決まりません。あるアトリビュートにキーを打っているアニメーションが 1 つだけのときは、比べる相手がいないので、weight がどれだけ小さくてもそのアニメーションの値になります。`weight` は 1 つのトラックでアニメーション同士を混ぜるときだけに使い、それ以外は `alpha` を使ってください。

トラックは、自分のアニメーションがキーを打ったアトリビュートにだけ効きます。頭だけにキーを打ったアニメーションを重ねても、マスクを設定しなくても脚には影響しません。つまり **重ねたアニメーションが効く範囲は、作画した範囲そのもの** です。

track 0 は **構造** を受け持ちます。セル・パーツカラー・シェーダ・反転・非表示・描画優先度・テクスチャはすべて track 0 の主アニメーションから来ます。既定のイベントの発火も track 0 からです。重ねたトラックが混ぜるのは、連続的なアトリビュート（位置・回転・スケール・不透明度など）です。

## 重ねる

```csharp
using SpriteStudio.Runtime;

[SerializeField] SpriteStudioPlayer _player;

void Start()
{
    _player.SetAnimation("idle");               // track 0
    _player.SetTrackAnimation(1, "look_around");
    _player.SetTrackAlpha(1, 0.4f);             // 40% 重ねる
    _player.Play();
}
```

インスペクタの **Animation Name** には track 0 のアニメーションを設定してください。プレーヤ自身の開始処理がそれを選ぶので、その処理が `Start()` より後に別の名前で走ると、track 0 が切り替わり、重ねたトラックは畳まれます。

アニメーションを載せる呼び出し（`SetTrackAnimation`・`Crossfade`・`AddTrackSource`）は、まだ無いトラックを作ります（track 3 に載せると track 1・2 も空のまま作られます）。`SetTrackAlpha`・`SetTrackBlend`・`FadeTrack` はトラックを作らず、無いトラックには `false` を返すので、先にアニメーションを載せてください。`alpha` は 0〜1 に制限しません。1 を超えると外挿になります。

### 加算のトラック

`SpriteStudioTrackBlend.Additive` のトラックは、自分の姿勢へ近づける代わりに **セットアップポーズからの差分** を足すため、下のアニメーションが残り続けます。値で動かす軸に向いています。軸のアニメーションは中央のフレームがセットアップポーズになるように作り、フレームを直接動かします。

```csharp
_player.SetTrackAnimation(1, "lean");
_player.SetTrackBlend(1, SpriteStudioTrackBlend.Additive);
_player.SetTrackSpeed(1, 0f);                   // 時計ではなく手で動かす

void Update()
{
    _player.SetTrackFrame(1, 30f + _yaw * 30f);
}
```

## クロスフェードで切り替える

`Crossfade()` は、トラックがいま再生しているアニメーションから別のアニメーションへフェードします。入ってくるアニメーションは **呼んだ時点で** トラックを引き継ぎます。トラックがそれまで再生していたものは比率を保ったまままとめてフェードアウトし、フェードが終わると解放されます。時間は `Time.deltaTime` と同じく **秒** で指定します。

```csharp
_player.Crossfade(0, "run", 0.3f);
```

| 状況 | 挙動 |
|---|---|
| 何も再生していないトラック | フェードせず、すぐに再生を始める |
| クロスフェード中のトラック | 進行中のフェードをその時点の状態のまま引き取る。ポーズは飛ばない |
| `FadeTrack` のフェード中のトラック | `Crossfade()` がそれを置き換え、alpha はその時点の値で止まる |
| 複数のソースを混ぜているトラック、`CancelTrackFade` で途中で止めたトラック | 載っているものがまとめてフェードアウトする |
| `duration` が 0 以下、またはアセットに無いアニメーション名 | 受け付けず `false` を返す |
| フェードを途中で止めたい | `CancelTrackFade(track)`。alpha と weight はその時点の値で止まる |

`GetTrackFadeProgress(track)` はフェード中に 0〜1、フェードしていないときに **-1** を返します。

`FadeTrack()` はもう 1 つのフェードで、トラックが再生する内容ではなく、トラックがどれだけ効くかを変えます。track 0 を 0 までフェードすると、キャラクターはセットアップポーズに落ち着きます。

```csharp
_player.FadeTrack(1, 0f, 0.2f);                 // 0.2 秒かけて重ねたトラックを消す
```

トラックのフェードは 1 つだけです。クロスフェード中のトラックに `FadeTrack()` を呼ぶと、`CancelTrackFade()` と同じようにクロスフェードを置き換えます。alpha はトラックのもので、別のアニメーションを載せてもリセットされません。重ねたトラックをフェードアウトしたあと、次のアニメーションを載せる前に `SetTrackAlpha(track, 1f)` を呼んでください。

### track 0 のクロスフェードで、何がいつ替わるか

入ってくるアニメーションが track 0 を引き継ぐのは、フェードが終わったときではなく **始まったとき** です。

- `CurrentAnimation`（とインスペクタの **Animation Name**）は、呼んだ直後から入ってくるアニメーションの名前になります。`OnAnimationStarted` は発生しません。切り替えは track 0 の再生状態をそのまま引き継ぎます。フェードが残していった側の名前で `SetAnimation()` を呼ぶとそちらへ戻り、入ってくる側の名前なら何も変わらずフェードが続きます。
- セル・非表示など補間できない値は、フェードの最初のフレームから入ってくる側のものになります。`IsPlaying`・`IsFinished`・`JustLooped`・`FrameNo` と `OnAnimationFinished` / `OnAnimationLooped` も入ってくる側の状態を報告します。出ていく側がフェード中に終わっても、キャラクターは終了になりません。
- 入ってくる側は、`SetAnimation()` で切り替えたときと同じく、プレーヤの `SpeedScale`・`FrameRate`・`FrameSkipEnabled`・再生方向・`LoopCount` で再生されます。再生[区間](./usage_scripting.md#例-シークと区間再生)はそのアニメーションの全体です。
- イベントはフェードの開始から入ってくる側が発火し、出ていく側は止まります。下の [イベント](#イベント) を参照してください。

`SetTrackAnimation(0, ...)` も、フェードせずに同じように track 0 を切り替えます。

## 1 つのトラックでアニメーションを混ぜる

比率で混ぜる場合（キャラクターの速さで歩きと走りを混ぜるなど）は、同じトラックに両方を載せて weight を設定します。

```csharp
_player.SetAnimation("walk");
int walk = _player.GetTrackMainSource(0);
int run = _player.AddTrackSource(0, "run", 0f);

void Update()
{
    _player.SetSourceWeight(walk, 1f - _speedRatio);
    _player.SetSourceWeight(run, _speedRatio);
}
```

トラックに載せたアニメーションの 1 本 1 本が **ソース** で、整数のハンドルで指します（無いときは `-1`。`0` は正しいハンドルです）。ハンドルは、そのソースが解放されるまで有効です。解放されるのは、クロスフェードの完了、そのトラックへの `SetTrackAnimation()`（`AddTrackSource()` で足したものを含め、トラックのソースがすべて解放されます）、`ClearTrack()`、別のアニメーションへの `SetAnimation()` のときで、その後は番号が使い回されるため、ソースより長く持ち続けないでください。weight は比率なので、`(0.7, 0.3)` と `(7, 3)` は同じ結果になります。長さの違うソースは位相がそろったままにはなりません。track 0 と歩調を合わせて始めるには、`AddTrackSource()` の前にそのトラックを `SpriteStudioTrackSync.Start` にします（次の節）。そろえるのは載せたときの 1 回だけなので、長さの違う歩きと走りは、その後また少しずつずれます。

## 位相をそろえて始める（`trackSync`）

トラックに載せたアニメーションは、再生区間の先頭から始まります。歩幅の途中にいる歩きの上に走りを載せると、足の運びが食い違います。載せる **前に** トラックを `SpriteStudioTrackSync.Start` にしておくと、次に載せたアニメーションは、track 0 のアニメーションがいる **位相**（再生区間のどこまで進んだかの割合）から始まります。尺の違うアニメーションも割合でそろいます。

```csharp
_player.SetTrackSync(0, SpriteStudioTrackSync.Start);
_player.Crossfade(0, "run", 0.3f);              // 走りは歩きと同じ位相から始まる
```

| | `SpriteStudioTrackSync.Free`（既定） | `SpriteStudioTrackSync.Start` |
|---|---|---|
| 載せたアニメーションの開始位置 | 再生区間の先頭 | track 0 の位相 |
| 向いている使い方 | 一回きりの動き（まばたき、攻撃など） | 周期的な動きどうしを重ねる（歩きと走りなど） |

`SetTrackAnimation`、`Crossfade`（track 0 でも効き、入ってくる側は出ていく側の位相から始まる）、`AddTrackSource` のすべてに効きます。そろえるのは載せたときの 1 回だけで、その後は両方が同じ時計で進むため、尺と速度が同じ間だけそろったままになります。まだ何も載っていないトラックにも設定できます。

## キャラクター全体で 1 つの時計

すべてのトラックはキャラクターの 1 つの時計で動き、再生の操作はその全体に効きます。

| 呼び出し | スタックへの効果 |
|---|---|
| `Play()` | **track 0** を再生区間の先頭へ戻し、時計を動かす。他のトラックは再生位置をそのまま保つ |
| `Pause()` / `Resume()` | すべてのトラックとすべてのフェードを止める／再開する |
| `Stop()` | キャラクター全体をその場で止める。再開できるのは `Play()` だけ |

- **track 0 が終わっても時計は止まりません。** track 0 が最後のループを再生し終えても、重ねたトラックとフェードは進み続けます。`IsPlaying`・`IsPausing`・`IsFinished` と `OnAnimation*` イベントは track 0 の状態を表し、終了は通常どおり報告されます。
- 再生区間は track 0 だけのものです。それ以外の再生設定はトラックごとに持ちます。次の節を参照してください。

## トラックの再生設定

速度・フレームレートの上書き・フレームスキップ・再生方向は、トラックに載ったアニメーションではなく **トラック** のものです。そのトラックに載っているすべてのアニメーション（クロスフェードの両側を含む）と、あとから載せるすべてのアニメーションに効きます。track 0 の設定は、プレーヤの `SpeedScale`・`FrameRate`・`FrameSkipEnabled`・`SetPlaybackDirection()` そのものです（`SetTrackSpeed(0, ...)` と `SpeedScale` は同じ値を書き換えます）。

```csharp
_player.SetTrackSpeed(1, 0.5f);
_player.SetTrackFrameRate(1, 24);
_player.SetTrackPlaybackDirection(1,
    SpriteStudioPlaybackDirection.Forward,
    SpriteStudioPlaybackStyle.PingPong);
```

一方 **ループ回数** はアニメーションのものです。トラックに載せたアニメーションは無限にループするので、回数は載せた後で設定してください。回数を再生し終えたトラックは最後のフレームで止まり、フェードするか空にするまで混ざり続けます。

```csharp
_player.SetTrackAnimation(2, "hit_react");
_player.SetTrackLoopCount(2, 1);                // 1 回再生して最後のフレームで止まる

void Update()
{
    if (_player.IsTrackFinished(2)) _player.ClearTrack(2);
}
```

track 0 のループ回数は `LoopCount` です（`SetTrackLoopCount(0, ...)` も同じ値を書き換えます）。プレーヤは、track 0 が切り替わるたびにこの回数をかけ直します。`TrackJustLooped(track)` は、トラックが先頭へ戻ったフレームで `true` になります。track 0 の `JustLooped` と同じです。どちらもイベントは発生しません。イベントが出るのは track 0 の終了とループ（`OnAnimationFinished`・`OnAnimationLooped`）だけです。

## SetAnimation() でスタックは畳まれる

**別の** アニメーションへの `SetAnimation()` は、スタックを track 0 だけに戻します。重ねたトラック・ソース・トラックの設定（alpha・blend・sync と、track 1 以降の再生設定）はすべてなくなるので、切り替えた後で重ね直してください。いま再生中のアニメーションをもう一度選んでも、何も変わりません。Play モード中にインスペクタで **Animation Name** を書き換えると `SetAnimation()` に続けて `Play()` が呼ばれるので、同じくスタックは畳まれます。

## イベント

既定では **track 0 のアニメーションだけ** がイベント（`OnUserData`・`OnSignal`・`OnAudio`）を発火します。他のソースは、weight がそのソースのイベントのしきい値に届くと発火します。しきい値の初期値は `+Infinity` です。

```csharp
int overlay = _player.GetTrackMainSource(1);
_player.SetSourceEventThreshold(overlay, 1f);   // weight が 1 以上なら発火する
```

しきい値はソースのものです。track 1 以降に `SetTrackAnimation()` や `Crossfade()` で載せたアニメーションは新しいソースで、`+Infinity` からやり直しになるので、アニメーションを替えるたびにしきい値を設定し直してください。

各イベントには、発火したソースのハンドルが `Source`（`SpriteStudioUserData.Source`・`SpriteStudioSignal.Source`・`SpriteStudioAudio.Source`）に付きます。2 つのソースのイベントを区別できます。

track 0 のクロスフェードが始まった時点から、発火するのは入ってくるアニメーションで、出ていく側は止まります。フェードの途中まで出ていく側も発火させたい場合は、トラックに出ていく側のしきい値を設定します。出ていく側は、トラックに占める割合 `1 - progress` がしきい値以上のあいだ発火します。

```csharp
_player.SetTrackOutgoingEventThreshold(0, 0.5f);   // フェードの前半だけ出ていく側も発火する
```

出ていく側のしきい値はトラックの設定なので、一度設定すれば残ります。既定値の `+Infinity` では、フェードの開始と同時に出ていく側は発火しなくなります。**イベントを数えている場合は**、クロスフェードで出ていく側のイベントがすぐに止まる点に注意してください。

## Timeline

Timeline がプレーヤを動かしている間は、track 0 はクリップが決めます。クリップはプレーヤを進めるのではなくシークするため、フェードや重ねたトラックは動かず、SpriteStudio Track のクリップの下では、スクリプトから track 0 を切り替えても、クリップの次の評価で元に戻ります。モーションブレンドは、Timeline が動かしていないプレーヤでスクリプトから使ってください。

## 制約

- **`.ssab` は 1 つです。** 積めるのは、プレーヤに割り当てたアセットのアニメーションだけです。
- **インスタンスパーツとエフェクトパーツはモーションブレンドの対象外です。** これらは 1 つのアニメーションではなく `.ssab` 全体のものなので、track 0 のクロスフェードでは準備し直さず、動いているまま引き継ぎます。切り替えの前後でどちらのキーがそのパーツを動かすかは混ぜられません。両方のアニメーションが同じフレームにキーを持つ場合、出ていく側のキーの設定のまま動き続けることがあります。
- **積むアニメーションでは、同じパーツに同じセルを割り当ててください。** セルの大きさと原点は track 0 から取るため、重ねたトラックのセルが違うとパーツが崩れます。
- 公式プレーヤ共通の仕様は、ランタイムの [アニメーションミキシングのガイド](https://github.com/cri-middleware/SpriteStudio-SDK/blob/HEAD/libs/ssruntime/docs/50_features/10_animation_mixing.ja.md) も参照してください。
