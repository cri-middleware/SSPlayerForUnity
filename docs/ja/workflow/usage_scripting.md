# 📝 スクリプト制御とイベント

[← ドキュメント目次へ戻る](../index.md)

Unity の C# スクリプトから `SpriteStudioPlayer` / `SpriteStudioGraphic` を制御する方法を解説します。Unity の設計思想に沿って、シンプルなメソッド呼び出しによる再生制御と、**C# event とインスペクタ配線可能な `UnityEvent` の両方**によるイベント連携を提供しており、ゲームロジックへの組み込みが容易です。

`SpriteStudioPlayer`（ワールド）と `SpriteStudioGraphic`（Canvas）は、**再生制御 API とイベント通知 API を共通**で備えています。本ページのコードは、`SpriteStudioPlayer` 専用と記した箇所（Flip X・Offset・描画順、パーツトラッキング、Timeline）を除き、どちらのコンポーネントでもそのまま使えます。

名前空間は `SpriteStudio.Runtime` です。

```csharp
using SpriteStudio.Runtime;
```

## 再生の開始

```csharp
[SerializeField] private SpriteStudioPlayer _player;

// アニメーションを指定してデータをロード（再生はまだ開始しない）
_player.SetAnimation("walk");
// 再生を開始
_player.Play();
```

`SetAnimation()` はアニメーションのデータをロードし**先頭フレームで待機**します（再生は開始しません）。続けて `Play()` を呼ぶとその場から再生が始まります。すでにセットアップ済みのアニメーションを指定した場合は何も起こりません。再生ヘッド・再生状態・再生区間・ループ回数はそのままなので、ステートマシンから毎フレーム `SetAnimation(state.clip)` を呼んでも問題ありません。最初からやり直すには `Play()` を呼びます。

設定した再生パラメータは切り替えを跨いで保持されます。`SpeedScale` / `FrameSkipEnabled` / フレームレート上書き / `PlaybackDirection`・`PlaybackStyle` はいずれもクリップではなく再生そのものを記述するものなのでランタイムが保持し、`LoopCount` は遷移ごとの指定なのでコンポーネントが再適用します。リセットされるのは再生ヘッド（先頭フレームで待機）と再生区間で、区間のフレーム番号は別のアニメーションに対して意味を持たないためです。

## 再生制御 API

| メンバー | 種別 | 説明 |
|---|---|---|
| `SetAnimation(string animName)` | メソッド | アニメーションを指定しデータをロード（再生は開始しない）。成功で `true` |
| `Play()` | メソッド | セットアップ済みアニメを区間の先頭（`PlaybackDirection` が `Backward` のときは末尾）から再生開始 |
| `Play(float startFrame)` | メソッド | セットアップ済みアニメを指定フレームから再生開始 |
| `Pause()` | メソッド | 現在フレームを保持して再生を止める。冪等で、再生していないときは無視される |
| `Resume()` | メソッド | `Pause()` を解除し、同じフレームから再開する。冪等。`Play()` は先頭へ巻き戻すため再開には使えない |
| `Stop()` | メソッド | 再生を停止。再生ヘッドは **その場に留まり**、巻き戻りません（停止したフレームを描画し続けます）。先頭に戻すのは `Play()` です |
| `StopAllSounds()` | メソッド | 組み込み再生が鳴らしたすべての音を止める。ほかに音を止めるものはありません（イベント通知の注記を参照） |
| `IsPlaying` | `bool`（読取） | 再生セッションが有効か（一時停止中でも `true`） |
| `IsPausing` | `bool`（読取） | 一時停止中か |
| `IsFinished` | `bool`（読取） | 設定したループを全て再生し終えたか。パルスではなく**状態**で、完了時に latch され次の `Play()` / `SetAnimation()` まで保持されます。そのため `Play(); yield return new WaitUntil(() => player.IsFinished);` は今開始した再生を待てます。`LoopCount` が無限のときは立たず、**`Stop()` でも立ちません** —— 命令された停止は完了ではないからで、そこが `!IsPlaying` との違いです |
| `IsPlayingForward` | `bool`（読取） | 再生ヘッドが**実際に**進んでいる向き。`PlaybackDirection` は設定した向きに過ぎず、`PlaybackStyle.PingPong` の戻りの脚では `PlaybackDirection` が `Forward` のままでもこれは `false` になります。**音声はこれでゲートしてください**（SpriteStudio に逆再生音声は無いため、後ろ向きの脚で跨いだキーは鳴らすべきものではありません） |
| `FrameNo` | `float` | 現在フレーム。`set` で任意フレームへシーク（小数可） |
| `TotalFrames` | `int`（読取） | 再生中の区間のフレーム数（両端を含む）。`SetAnimationSection` で区間を設定していればその区間（未ロード時 `0`） |
| `FrameRate` | `int` | 再生フレームレート (fps)。`set` で実行時変更。`0` 以下は値ではなく**上書きの解除**として扱われ、クリップに設定されたフレームレートへ戻ります。読み出しは常に実効値なので、`0` を書いて読み返すとクリップ本来の fps（例: `60`）が返ります |
| `PlaybackDirection` | `enum` | 設定した進行方向（`Forward` / `Backward`） |
| `PlaybackStyle` | `enum` | 終端での挙動（`Normal` / `PingPong`） |
| `SetAnimationSection(int start, int end)` | メソッド | 再生範囲をフレームで限定（両端を含む）。範囲外の値（`-1` など）を渡した側はアニメーションの端になる。両端がアニメーションの範囲内で終わりが始まりより前のときは `false` を返し、区間も再生ヘッドも変えない。そのときの警告は同じ組み合わせにつき最初の 1 回だけ |
| `SetAnimationSectionByLabel(string startLabel, string endLabel, int startOffset = 0, int endOffset = 0)` | メソッド | 再生範囲を今のアニメーションのラベルで指定する `SetAnimationSection`。`startLabel` のフレームに `startOffset` を足した位置から、`endLabel` のフレームに `endOffset` を足した位置までで、インスタンスパーツの開始・終了ラベルと同じ規則。`_start` と `_end` はアニメーション自身の最初と最後のフレーム。アニメーションにそのラベルが無いとき、または求めたフレームが `SetAnimationSection` の受け付けない区間になったときは `false` を返し、区間も再生ヘッドも変えない。そのときの警告は最初の 1 回だけ |
| `GetLabelTable(string animationName = null)` | メソッド | アニメーション（省略時は今のアニメーション）のラベルをフレーム順に並べたものと、アニメーション自身の最初と最後のフレーム（`StartFrame` / `EndFrame`）。アセットにそのアニメーションが無いとき、名前を省略してまだ何もセットアップしていないときは `null`。`Labels` の各要素は `Name` と `Frame` を持ち、`TryGetFrame(label, out frame)` で 1 つを引けます。呼ぶたびにアセットから読み出す |

プレイヤーの進行とメッシュの構築は、すべての `Update`・Timeline・Animator が終わった後、**LateUpdate** の早い段階（`DefaultExecutionOrder(-100)`）で行われます。そのため自前の `Update` から呼んだ `SetAnimation`・`Play`・`FrameNo` によるシークは、Animator への変更と同じく同じフレームで描画されます。自前の `LateUpdate` から呼んだものは次のフレームで描画されます。

### 例: 一時停止と再開

```csharp
_player.Pause();    // 現在フレームで保持
_player.Resume();   // そのフレームから再開

// 再生／一時停止ボタン 1 つで扱う場合:
if (_player.IsPausing) _player.Resume();
else if (_player.IsPlaying) _player.Pause();
else _player.Play();   // 停止中 — Play() は区間の先頭から再生し直す
```

### 例: シークと区間再生

```csharp
_player.SetAnimation("attack");
_player.SetAnimationSection(10, 30); // 10〜30 フレームの区間だけ再生。再生ヘッドは 10 フレームへ移る
_player.Play();
_player.FrameNo = 20f;               // 区間内の好きな位置へシーク

// _player.SetAnimationSectionByLabel("loop_start", "loop_end"); // 区間の両端をラベルで指定してもよい
```

### 例: 逆再生・ピンポン

```csharp
_player.SetAnimation("walk");
_player.PlaybackDirection = SpriteStudioPlaybackDirection.Backward;
_player.PlaybackStyle = SpriteStudioPlaybackStyle.PingPong;
_player.Play();
```

> `PlaybackDirection` / `PlaybackStyle` は `SetAnimation()` を跨いで保持されます。インスペクタ側の既定値は無いため、スクリプトで指定するまでは Forward / Normal です。これらはシリアライズされずランタイムが保持するため、コンポーネントを無効化する（または `AnimeAsset` を `null` にする）と破棄され、再び有効にしたコンポーネントは Forward / Normal から始まります。有効になった後で設定し直してください。`FrameRate` の上書きも同じく破棄されます。切り替えでリセットされるのは往復再生の進行中の向きだけで、新しいアニメーションは必ず往路から始まります。方向とスタイルは `SetPlaybackDirection(direction, style)` で 1 回にまとめて設定することもできます。

## 再生パラメータのプロパティ

インスペクタ項目に対応するプロパティです。実行時に変更するとランタイムへ即時反映されます。インスペクタの項目（**Flip X**・**Offset**・**Speed Scale** など）は AnimationClip や Timeline の Animation Track でキーを打つこともでき、`SpriteRenderer` のキーを打った **Flip X** と同じく、評価のたびにプレイヤーへ反映されます。

| プロパティ | 型 | 説明 |
|---|---|---|
| `AnimeAsset` | `SpriteStudioAnimeAsset` | 再生対象アセット（差し替え可） |
| `CurrentAnimation` | `string` | アニメーション名（読み取り専用。切り替えは `SetAnimation()` で行います） |
| `AnimationNames` | `string[]` | アセットに含まれるアニメーションの名前（読み取り専用。アセット未設定のときは空） |
| `AutoPlay` | `bool` | 起動時と再有効化時の自動再生 |
| `LoopCount` | `int` | ループ回数（`-1` または `0` = 無限 / `n` = n 回） |
| `SpeedScale` | `float` | 再生速度倍率。ランタイムの実値を読み返す。`0` と負値は**停止**にクランプされる（逆再生は `PlaybackDirection` の役目）ため、`-1` を設定すると `0` が返る |
| `SubFrameEnabled` | `bool` | サブフレーム補間 |
| `FrameSkipEnabled` | `bool` | 実時間追従（[高度な設定](./tips.md) 参照） |

`SpriteStudioPlayer`（ワールド）のみ: `SortingLayerName` / `SortingOrder` / `PixelsPerUnit`、および以下の 3 つ。

| プロパティ | 型 | 説明 |
|---|---|---|
| `FlipX` | `bool` | 描画を左右反転（`SpriteRenderer.flipX` と同じ考え方） |
| `FlipY` | `bool` | 描画を上下反転 |
| `Offset` | `Vector2` | 描画を GameObject の空間の Unity 単位でずらす（`PixelsPerUnit` の後に適用。Transform のスケールが掛かる） |

3 つとも変えるのはそのプレイヤーが描くものだけで、`Transform` には触れません。つまり GameObject の子オブジェクトやコライダーはその場に留まります。これが要点で、キャラクタを反転させるために
`transform.localScale.x` を負にすると武器の取り付け位置・当たり判定・パーティクルまで一緒に反転してしまいますが、`FlipX` ではそうなりません。

```csharp
// ぶら下がっているものを乱さずに、進行方向を向く
_player.FlipX = _velocity.x < 0f;
```

登録済みのパーツトラッカーは反転後の姿勢に追従するので、パーツに固定したオブジェクトは反転しても外れません。ただし鏡像は回転ではないため、反転そのものが対象に伝わるのは対象の `driveScale` が
ON のとき（負のスケールとして届きます）だけです。位置はどちらの場合も正確ですが、向きには制約があります（[「反転が絡むときの制約」](#反転が絡むときの制約)を参照）。

`SpriteStudioGraphic`（uGUI）にはこれらはありません。大きさと位置は `RectTransform` と **Fit**・**Align** で決まります。

## イベント通知

アニメーションの進行に応じて、6 種類のイベントを受け取れます。`C# の event` と、インスペクタで配線できる `UnityEvent` の両方を提供します。

| イベント | C# event | UnityEvent フィールド | ペイロード | 発火タイミング |
|---|---|---|---|---|
| 再生開始 | `OnAnimationStarted` | `OnAnimationStartedEvent` | なし | `Play()` を呼んで再生を開始したとき |
| 再生終了 | `OnAnimationFinished` | `OnAnimationFinishedEvent` | なし | 指定した `LoopCount` をすべて再生し終えたとき。無限ループでは発火せず、`Stop()` でも発火しない |
| ループ | `OnAnimationLooped` | `OnAnimationLoopedEvent` | なし | ループで先頭へ戻るたび。最終周はループせず再生終了になる |
| ユーザーデータ | `OnUserData` | `OnUserDataEvent` | `SpriteStudioUserData` | UserData キーのフレーム到達時 |
| シグナル | `OnSignal` | `OnSignalEvent` | `SpriteStudioSignal` | Signal コマンドのフレーム到達時 |
| オーディオ | `OnAudio` | `OnAudioEvent` | `SpriteStudioAudio` | Audio イベントのフレーム到達時 |

> これらは SpriteStudio 側でアニメーションに設定したキー（UserData / Signal / Audio）に対応します。

### C# からのイベントの登録

```csharp
void OnEnable()
{
    _player.OnAnimationFinished += HandleFinished;
    _player.OnUserData += HandleUserData;
}

void OnDisable()
{
    _player.OnAnimationFinished -= HandleFinished;
    _player.OnUserData -= HandleUserData;
}

void HandleFinished()
{
    Debug.Log("再生が終了しました");
}

void HandleUserData(SpriteStudioUserData data)
{
    Debug.Log($"UserData: part={data.PartName}");
    if (data.HasInt)    Debug.Log($"  int = {data.IntValue}");
    if (data.HasString) Debug.Log($"  str = {data.StringValue}");
    if (data.HasPoint)  Debug.Log($"  point = {data.PointValue}");
    if (data.HasRect)   Debug.Log($"  rect = {data.RectValue}");
}
```

`UnityEvent`（`OnAnimationFinishedEvent` など）はインスペクタ上で他コンポーネントのメソッドへ配線できます。

### 受け取るイベントデータの構造

どのイベントも `FrameNo` を持ちます。キーが **置かれている** フレームであって、検出されたフレームとは限りません（1 ティックで複数フレームをまたぐことがあり、その間のキーはすべてそのティックで発火します）。`Source` は発火したアニメーションのソースのハンドルで、アニメーションを積んだときにだけ意味を持ちます。[モーションブレンド](./motion_blending.md#イベント) を参照してください。

```csharp
// UserData: 設定されている値だけ HasXxx が true になる
struct SpriteStudioUserData
{
    string PartName; int PartIndex; int FrameNo; int Source;
    bool HasInt;    int    IntValue;
    bool HasString; string StringValue;
    bool HasRect;   Rect   RectValue;
    bool HasPoint;  Vector2 PointValue;
}

// Signal: パラメータは型付き（boxing なし）。GetParam / TryGetParam で取得（実行時のみ有効）
struct SpriteStudioSignal
{
    string PartName; int PartIndex; int FrameNo; int Source;
    bool Active; string CommandId;
    int ParamCount;                                    // コマンドパラメータ数
    SpriteStudioSignalParam GetParam(int index);       // 0 <= index < ParamCount
    bool TryGetParam(string id, out SpriteStudioSignalParam p);
}
// パラメータ 1 件: Type に対応するアクセサで読む（Bool / Index / Integer / Floating / Text）
struct SpriteStudioSignalParam
{
    string Id; SpriteStudioSignalParamType Type;
    bool AsBool; int AsIndex; int AsInteger; float AsFloating; string AsText;
}

// Audio: SoundName で対象音を識別
struct SpriteStudioAudio
{
    string PartName; int PartIndex; int FrameNo; int Source;
    uint SoundListNameHash; uint SoundNameHash;
    string SoundName; int LoopNum;
}
```

> [!IMPORTANT]
> **Signal パラメータの有効期間について**: `GetParam` / `TryGetParam` が返す値は、プレイヤーが所有して使い回すバッファへのビューです（アロケーションと boxing を避けるための仕組みです）。`SpriteStudioSignal` とそこから読み出した `SpriteStudioSignalParam` が有効なのは、そのプレイヤーの次のイベントポーリングまで（通常は次フレームまで）です。コールバックの外へ持ち越す場合（フィールドへの保存、コルーチンからの参照、キューイングなど）は、**値そのものをコピー**してください（`p.AsInteger` / `p.AsText` など）。`SpriteStudioSignal` を保持したままにすると、後で別の Signal のパラメータをエラーなく返すようになります。

> [!NOTE]
> **Audio イベントについて**: `Play Audio` は既定でオンのため、コンポーネント自身がプール済みの `AudioSource` で発音します。`OnAudio` はそれとは独立した**観測用**のチャンネルで、再生を奪わずに音へ反応したい場合や、`Play Audio` をオフにして自前で（たとえば音声ミドルウェアで）鳴らす場合に使います。`AudioClip` は `SpriteStudioAnimeAsset.Sounds` に解決済みで保持されており、イベントが指す音は `SpriteStudioAnimeAsset.ResolveAudioClip(SoundListNameHash, SoundNameHash)` で引けます。
>
> 一度鳴り始めた音は、**それを発火させた再生よりも長く生き残ります**。`Pause()` / `Resume()` / `Stop()`、`SetAnimation()` による切り替え、アニメーションの終了 — いずれも音には触れません。音が鳴っている間に同じキーを再び通過した場合も、前の音を切らずに別の音として重ねて鳴らします。止まるのはコンポーネントが無効化・破棄されたとき、`AnimeAsset` を `null` にしたとき、`StopAllSounds()` を呼んだときだけです。SpriteStudio に無限ループの音はなく（`loop_num` は再生回数）、すべての音はいつか自然に終わります。アニメーションの尺より長く鳴る音があるなら、それはアニメーションデータ側の問題です。
>
> 音が鳴るのは、再生ヘッドが通過したキーです。シーク（`FrameNo` への代入や `SetTrackFrame(0, …)`）では、再生中かどうかにかかわらず、着地したフレームのキーの音は鳴りません（`OnAudio` イベントは発火します）。`Play()` 直後の開始フレームのキーは鳴ります。

### 実装例: アニメーションの連続再生

「攻撃」アニメーションが終了したら、自動的に「待機」アニメーションへ遷移させる実装例です。

`LoopCount` はアニメーションではなくコンポーネントの設定で、次にセットするどのアニメーションにも適用されます。既定の `-1` は終了しないため、1 回きりのアニメーションの前に有限の回数を指定し、続くアニメーションの前に `-1` へ戻してください。

```csharp
void OnEnable()
{
    _player.OnAnimationFinished += HandleFinished;
}

void OnDisable()
{
    _player.OnAnimationFinished -= HandleFinished;
}

void PlayAttack()
{
    _player.LoopCount = 1;      // 有限回にして OnAnimationFinished が発火するようにする
    _player.SetAnimation("attack");
    _player.Play();
}

void HandleFinished()
{
    // 攻撃が終わったら待機状態に戻る
    if (_player.CurrentAnimation == "attack")
    {
        _player.LoopCount = -1; // LoopCount は引き継がれるので、待機はループさせる
        _player.SetAnimation("idle");
        _player.Play();
    }
}
```

> [!NOTE]
> `OnAnimationFinished` は同じティックでまたいだキーより先に発火し、それらのキーはハンドラから戻った後に、終了したアニメーションのものとして発火します。ここで切り替えても最後のキーは失われず、次のアニメーションから読まれることもありません。ハンドラがプレイヤーやその GameObject を無効化するか、`DestroyImmediate` で破棄するか、別の `AnimeAsset`（null を含む）を設定した場合は、そのティックのイベントはそこで終わり、残りは届きません。`Destroy` は破棄をフレームの終わりまで待つため、その後も残りのイベントは届きます。

### 実装例: UserData を使ったイベント発生

SpriteStudio 上で設定した UserData（足音の再生、攻撃判定の発生など）を受け取り、ゲーム側で処理を行う実装例です。

```csharp
void HandleUserData(SpriteStudioUserData data)
{
    // 例: 文字列を判定して処理を分岐
    if (data.HasString && data.StringValue == "play_footstep")
    {
        _audioSource.Play();
    }
    else if (data.HasString && data.StringValue == "attack_hit")
    {
        // 整数値でダメージ量などを渡す例
        if (data.HasInt) SpawnHitbox(data.IntValue);
    }
}
```

## パーツトラッキング（指定パーツへの追従）

指定したパーツに、ユーザーが用意した `Transform`（武器・エフェクト・当たり判定など）を毎フレーム追従させる機能です。プレイヤーは GameObject を生成も破棄もせず、登録された `Transform` を駆動するだけの連動（Constraint）方式で、ターゲットのライフサイクルはユーザーが所有します。

> この機能は `SpriteStudioPlayer`（ワールド）専用です。`SpriteStudioGraphic`（Canvas）にはありません。

> [!NOTE]
> **メッシュ（スキン）パーツには直接追従できません。** ボーンにバインドされたメッシュパーツ（手・顔などの変形パーツ）は、頂点がボーンのスキニングで描かれる一方、パーツ自身の変換はセットアップ時のノード位置（多くはキャラクターのルート付近）に留まります。そのため Part Name にこうしたパーツを指定すると、追従先は見た目の絵ではなくルート付近に吸い寄せられます。
>
> メッシュパーツの位置に追従させたいときは、SpriteStudio 側でその位置に **NULL パーツ（ボーンポイント／空パーツ）** を 1 つ置き、そのパーツ名を追従先に指定してください。ボーン（armature）やジョイントパーツを指定しても同様に追従できます。

`SpriteStudioPlayer` の **Part Trackers** リストにエントリを追加して設定します。

| 設定 | 説明 |
|---|---|
| **Part Name** | 追従対象のパーツ名（SpriteStudio で付けたパーツ名）。インスペクタではアセットのパーツ名からドロップダウンで選べます（メッシュ（スキン）パーツはグレー表示で選べません） |
| **Target** | 追従させる `Transform`。プレイヤー直下でも別階層でも可 |
| **Drive Position** | ON（既定）でパーツの位置に追従します |
| **Drive Rotation** | ON（既定）でパーツの回転に合わせて向きを変えます |
| **Drive Scale** | ON で継承スケールも反映（パーツ自身の localScale は除外）。OFF（既定）ではターゲットのスケールに一切触れません |
| **On Hide** | パーツが hide のフレームでの挙動。`FollowAlways`（追従を継続。既定）/ `Deactivate`（ターゲットを非アクティブ化） |

### スクリプトからの登録

インスペクタを使わず、実行時に登録・解除することもできます。

```csharp
var player = GetComponent<SpriteStudioPlayer>();

// 実行時に登録（既定は位置と回転のみ追従）
player.RegisterPartTracker("hand_R", weapon.transform);

// 継承スケールも反映し、hide のフレームではターゲットを非アクティブにする
player.RegisterPartTracker("effect_root", effect.transform,
    driveScale: true, onHide: SpriteStudioPartTrackerHide.Deactivate);

// 解除（その Transform を対象とするエントリをすべて削除）
player.UnregisterPartTracker(weapon.transform);
```

| メソッド | 説明 |
|---|---|
| `RegisterPartTracker(partName, target, driveScale, onHide, drivePosition, driveRotation)` | トラッカーを追加し、作成されたエントリを返す（末尾 4 つは省略可。`drivePosition` / `driveRotation` は既定 ON、`driveScale` は既定 OFF） |
| `UnregisterPartTracker(target)` | その `Transform` を対象とするエントリをすべて削除し、削除件数を返す |
| `TryGetPartHidden(partName, out bool hidden)` | そのパーツが現在フレームで hide かを取得。パーツが不明なら `false` を返す |
| `TryGetPartTransform(partName, out Matrix4x4 matrix)` | そのパーツの現在フレームの変換行列（プレイヤーローカル、`1 / PixelsPerUnit`・`FlipX` / `FlipY`・`Offset` 適用済み）を取得 |

> `Transform` を常時追従させるまでもなく「その瞬間のパーツ位置だけ知りたい」場合（弾の発射位置を取るなど）は、トラッカーを登録せず `TryGetPartTransform()` を直接呼ぶ方が簡潔です。

### 反転が絡むときの制約

パーツの姿勢が**鏡像**になると、`Transform` では表しきれない部分が出ます。`FlipX` / `FlipY` を使ったときのほか、SpriteStudio 側でパーツやその親に負のスケールが付いている場合にも起こります。**回避策ではなく、`Transform` を使う以上どうしても残る性質**なので、把握したうえで設計してください。

**位置は常に正確です。** 影響を受けるのは向きと、ターゲット自身が鏡像化されるかどうかだけです。

- **`TryGetPartTransform()` の戻り値から `matrix.rotation` / `matrix.lossyScale` を直接読まないでください。** 姿勢が鏡像のとき `Quaternion` はそれを表現できないため、これらは**未定義の値**を返します（精度の問題ではなく無意味な値です）。読む前に `matrix.determinant` の符号を確認してください。位置が欲しいだけなら第 4 列をそのまま使えます。
- **符号は仮定できません。** 鏡像かどうかは「パーツやその親に付いた反転」と「`FlipX` / `FlipY`」の**積**で決まります。反転が 2 つ重なれば打ち消し合って正に戻るため、元から反転しているパーツは、プレイヤーを `FlipX` / `FlipY` で反転すると行列式が**正**になります。「反転していないから正」とも「`FlipX` / `FlipY` したから負」とも言えません。毎回確認してください。
- **`Drive Scale` が OFF（既定）のターゲットには鏡像が届きません。** 鏡像は負のスケールとしてしか表せないためです。結果としてターゲット自身は反転せず、さらに **`FlipX`（左右反転）では向きに 180 度の差が残ります**。`FlipY`（上下反転）では残りません。左右と上下で挙動が違うのは、鏡像を「回転＋負のスケール」に分解する際、符号を Y 軸側に載せる規約を採っているためです。左右の鏡像はこの形に直すのに余分な 180 度回転を必要とし、それが回転側に残ります。
- **`Drive Scale` を ON にすると鏡像は届きますが、負のスケールには副作用があります。** とくに **コライダーは負のスケールを反映しません**（PhysX の制約）。絵は反転しているのに当たり判定だけ元のまま、という状態になり、警告も出ません。パーティクルや 3D メッシュの子では、シミュレーション空間や面の向きにも影響します。

当たり判定を追従させる場合は、**対称な形状にする**（位置だけ正確なら十分なので影響を受けません）か、**反転をゲーム側のコードで扱う**のが確実です。

### 追従のタイミングと精度

トラッカーの駆動は **LateUpdate** で、そのフレームのメッシュが確定した後・描画の前に行われます。追従先に付けた `Renderer` などは同じフレーム内で更新されます。

姿勢はターゲットの**親空間**でのローカル TRS として書き込まれるため、ターゲットの置き場所によって一致の度合いが変わります。

- プレイヤーの**子**に置いた場合、パーツの変換がそのまま書き込まれ、完全に一致します。（Skew は描画される頂点だけを変形させるもので、パーツの座標系には含まれないため、追従先には一切影響しません。）
- 別階層（プレイヤーの子ではない位置）に置いた場合、**ワールド位置は常に正確**です。ただしターゲットの親やプレイヤーが回転と非一様スケールを同時に持つと、**回転とスケールは近似**になります。

> **別階層のターゲットは 1 フレーム遅れることがあります。** トラッカーは駆動時点のプレイヤーの変換を使って書き込むため、その後にプレイヤーが動いても次のフレームまで追随しません。駆動は LateUpdate の早い段階（`DefaultExecutionOrder(-100)`）で走るので、自前の `LateUpdate` でプレイヤーを動かす構成では特に注意してください。子に置いた場合は階層継承で常に追随します。

`Drive Scale` が ON のときに反映されるのは**継承スケールのみ**で、パーツ自身の localScale は取り除かれます。またワールド用プレイヤーは 2D のため、書き込まれるのは X / Y だけで、ターゲットの Z スケールは保持されます。

### 注意点

- **次の 2 つの場合、プレイヤーはターゲットを非アクティブにし**、条件が解消すると再アクティブ化します。再アクティブ化するのは自分で非アクティブにしたターゲットだけで、拾うまで隠しておく武器のようにユーザーが非アクティブにしたターゲットはそのままです。ただし、プレイヤーが既に非アクティブにしているあいだにユーザーが非アクティブにしたターゲットは、条件が解消すると戻ります（ターゲット側には両者を区別する手がかりがありません）。
    - パーツ名がアセットに存在しない（1 つのアセットのアニメーションはパーツを共有します。この場合は `On Hide` の設定に関係なく常に非アクティブ）
    - パーツが hide のフレームで、かつ `On Hide` が `Deactivate`

    トラッカーの `target` を差し替えたとき、または `UnregisterPartTracker` でトラッカーを外したときは、そのトラッカーが非アクティブにしていたターゲットを再アクティブ化します。
- パーツ名は、そのプレイヤー自身が再生しているアセットのパーツから解決されます。**インスタンスパーツの内部（子アニメーション）のパーツは指定できません**（インスタンスパーツ自体は指定できます）。
- 同名のパーツが複数ある場合は、最初に見つかったものが使われます。
- `AnimeAsset` を差し替えるとパーツ名は自動で再解決されるため、トラッカーを登録し直す必要はありません。
- 編集モードでも、開始フレームの静止プレビューが構築されるタイミングでトラッカーが一度反映されます。ただし編集モードではターゲットを動かすだけで、アクティブ状態は変えません。

## パーツの輪郭と当たり判定

`TryGetPartQuad` はパーツを描いている輪郭を、`HitTestPart` は点の下にある一番手前のパーツを返します。どちらも `SpriteStudioPlayer` と `SpriteStudioGraphic` にあります。プレイヤーが最後に組み立てたフレームを読み、新しく組み立てはしないので、返るのは画面に出ているものです。座標はコンポーネントが描く空間で、`SpriteStudioPlayer` は GameObject のローカル空間（Pixels Per Unit・反転・オフセット込み）、`SpriteStudioGraphic` は `RectTransform` のローカル空間です。

| メンバー | 説明 |
|---|---|
| `TryGetPartQuad(string partName, out SpriteStudioPartQuad quad)` | パーツの輪郭を 4 つの角 `TopLeft` / `TopRight` / `BottomLeft` / `BottomRight` で返します。角はセル自身の角で、ポーズが置いた場所にあります。セルのあるパーツは描いた四隅（スキューと頂点変形を含む）、メッシュパーツは頂点を囲む軸平行の矩形、シェイプはサイズをピボットの周りに取った矩形です。シェイプの頂点変形でサイズの外へ出た角は追いません。非表示のパーツ、自分では何も描かないパーツ（ヌル・ボーン・インスタンス・エフェクト）、最初のフレームを組み立てる前は `false`。アルファ 0 で描いているパーツには輪郭があります。名前の代わりにパーツのインデックスも渡せます |
| `HitTestPart(Vector2 point)` | 輪郭が `point` を含むパーツのうち一番手前のもののインデックス。無ければ `-1` |
| `HitTestPart(Vector2 point, IReadOnlyList<string> parts)` | 名前を挙げたパーツの中で同じことをします。アセットに無い名前は飛ばします。パーツのインデックスのリストも渡せます |
| `SpriteStudioPartQuad.Contains(Vector2 point)` | `point` が輪郭の内側にあるか |

### 例: クリックしたパーツを知る

```csharp
using UnityEngine;
using UnityEngine.EventSystems;
using SpriteStudio.Runtime;

public class PartClick : MonoBehaviour, IPointerClickHandler
{
    [SerializeField] private SpriteStudioGraphic _graphic;

    public void OnPointerClick(PointerEventData e)
    {
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                _graphic.rectTransform, e.position, e.pressEventCamera, out Vector2 local)) return;
        int part = _graphic.HitTestPart(local, new[] { "head", "body" });
        if (part >= 0) Debug.Log($"clicked {_graphic.GetPartNames()[part]}");
    }
}
```

`SpriteStudioPlayer` では、先に `transform.InverseTransformPoint` でワールド座標をプレイヤーのローカル空間に変えてください。

## パーツオーバーライド（パーツカラー / セル / 表示指定）

パーツ単位のランタイムオーバーライドは、「このパーツを**今**この色に / このセルに / 非表示に」とスクリプトから指示する機能です。オーバーライドはキーフレームやアニメーションのブレンドよりも優先されるので、アニメーションと取り合いになりません。`SpriteStudioPlayer` / `SpriteStudioGraphic` の両方で使えます。

```csharp
var player = GetComponent<SpriteStudioPlayer>();

// パーツを赤く着色（乗算）。通常（画像）パーツに適用されます。
player.SetPartColorOverride("body", Color.red, SpriteStudioBlendOp.Mul);

// 別のセルを描画させる（セルマップ名は ".ssce" を付けずに指定）。
player.SetPartCellOverride("body", "Ringo", "effect3");

// パーツを強制非表示にする（子孫にもカスケード）。
player.SetPartVisibilityOverride("body", hide: true, cascade: true);

// 解除
player.ClearPartColorOverride("body");
player.ClearAllPartOverrides();
```

| メソッド | 説明 |
|---|---|
| `GetPartNames()` | パーツ名の一覧（パーツ index 順）。イベントの `PartIndex` を人間が読める形に戻す、パーツ一覧 UI を作る、といった用途。名前の無いパーツは `""` で枠を保持するので index と揃ったままです。`Length` が個数 |
| `FindPartIndex(name)` | パーツインデックス。アセットに無ければ `-1` |
| `SetPartColorOverride(part, color, blendOp, priority)` | パーツカラーオーバーライド（単色）。`blendOp` は `SpriteStudioBlendOp` の `Mix`・`Mul`・`Add`・`Sub`。4 隅ごとのグラデーションは `SetPartColorOverrideCorners(part, SpriteStudioCornerColors, blendOp, priority)`（左上・右上・左下・右下） |
| `SetPartCellOverride(part, cellmapName, cellName, priority)` | 別のセルで描画する |
| `SetPartVisibilityOverride(part, hide, cascade)` | 強制非表示（`hide: false` でアニメーションに戻す） |
| `ClearPartColorOverride` / `ClearPartCellOverride` / `ClearPartVisibilityOverride` | 1 パーツの 1 オーバーライドを解除 |
| `ClearAllPartOverrides()` | そのプレイヤーの全オーバーライドを解除 |
| `GetCellmapNames()` / `GetCellNames(cellmap)` | セルオーバーライドに指定できるセルマップ名 / セル名の列挙 |

各メソッドの `part` は**パーツ名**と**パーツインデックス**のどちらでも指定でき、パーツが不明な場合やランタイムが受け付けなかった場合は `false` を返します。

> **パーツインデックスの使い方について**: パーツインデックスは同一アセット（同じ `.ssab`）内では安定しているので、頻繁にオーバーライドするなら `FindPartIndex` で一度パーツ名をパーツインデックスに解決して、そのインデックスを使い回すことを推奨します。

### 優先モード（priority mode）

パーツカラーとセルのオーバーライドはアニメーションと競合するため、`SpriteStudioOverridePriority` を取ります（表示指定にはありません。単なる強制非表示フラグで、アニメーションを設定し直すと必ずクリアされます）。

| 優先モード | 挙動 |
|---|---|
| `OverwriteOnNextKeyframe` | アニメーションデータが当該アトリビュートを更新するまで、オーバーライドが適用される（新しいアニメーションを設定すると解除される） |
| `HoldUntilNextAnimation`（既定） | 現在のアニメーション中は勝ち続け、新しいアニメーションを設定するとオーバーライドが解除される |
| `Permanent` | 同じアニメーションデータ（.ssab）である間、オーバーライドが適用される (アニメーション変更をまたいでも持続) |

### 注意点

- **カラー**は通常パーツ、**セル**は通常パーツとマスクパーツに適用されます。それ以外の種別のパーツでは黙って無視されます（呼び出し自体は `true` を返します）。
- 色はオーサリングされた Part Color と同じ 8bit sRGB 空間として解釈され、アルファはランタイム側で pre-multiply されます。自前で変換せず、オーサリングどおりの色を渡してください。
- セルオーバーライドは設定時に解決されるため、存在しないセルマップ名 / セル名はその場で失敗します（`false` を返します）。
- オーバーライドはランタイムが保持し、そのライフサイクルもランタイムが管理します。`SetAnimation()` で切り替えると `Permanent` 以外はすべて解除されます（track 0 のクロスフェードでは何も解除されません）。そのたびに再適用するのではなく、意図に合った優先モードを選んでください。

## 動的テクスチャ差し替え（セルマップの差し替え / 着せ替え）

セルマップ（外部テクスチャ）単位で、バインドするテクスチャを**実行時にそのプレイヤーだけ**差し替える機能です。キャラクターのカラーバリエーションや衣装・装備の切り替え（アバターの着せ替え）を、衣装ごとに別のアセットを用意することなく実装できます。アセットや他のプレイヤーには影響しません。`SpriteStudioPlayer` / `SpriteStudioGraphic` の両方で使えます。これは上記のパーツオーバーライドとは**別系統**の機能です（下記の使い分けを参照）。

### 実装例: 衣装の着せ替え

```csharp
var player = GetComponent<SpriteStudioPlayer>();

// セルマップ名は SpriteStudio の定義名。差し替え可能な名前は GetReplaceableTextureNames() で取得できます。
// インスペクタの "Cellmap Texture Overrides" には、同じ名前を文字列で入力します。
var sailorUniform = Resources.Load<Texture2D>("SailorUniform"); // 差し替え先（元と同じサイズ・アトラス構成を推奨）
player.SetCellmapTexture("Clothes 1", sailorUniform);

// 元のテクスチャに戻す（null を渡す）。
player.SetCellmapTexture("Clothes 1", null);
```

衣装ごとにアセットを用意する必要がなく、1 つのアニメーションでテクスチャだけを差し替える効率的なアバターシステムを構築できます。

| メソッド | 説明 |
|---|---|
| `SetCellmapTexture(cellmapName, texture)` | 指定セルマップのテクスチャを差し替える。`texture` に `null` を渡すと元に戻す |
| `GetCellmapTexture(cellmapName)` | 現在の**差し替え値**を返す（未差し替えなら `null`。元テクスチャは返しません） |
| `GetReplaceableTextureNames()` | 差し替え可能な名前の一覧（セルマップ名＋外部テクスチャ名）。割り当て済みアセットから読み、未割り当てなら空 |

インスペクタの **"Cellmap Texture Overrides"**（名前とテクスチャの組）に設定した差し替えはシーンに保存され、実行時やアセット差し替え時に名前から自動で再適用されます。

> **`SetCellmapTexture()` と `SetPartCellOverride()` の使い分け**: `SetCellmapTexture()` は**セルマップ（テクスチャ）まるごと**の差し替えで、そのセルマップを使う全パーツにまとめて効きます（着せ替え・カラーバリエーション向き）。`SetPartCellOverride()` は**パーツ 1 つ単位**で、描画するセル（絵柄）そのものを差し替えます（表情差分など）。目的に応じて使い分けてください。

### 注意点

- **プレイヤー個別**です。差し替えは名前で保持され、アセットを差し替えても新しいリソースのハッシュへ解決し直して再適用されます。**インスタンス（子アニメ）には伝搬しません** —— 子は独自のテクスチャを持つ独立したプレイヤーなので、必要なら子側にも設定してください。
- 差し替え先テクスチャは**元と同じサイズ・アトラス構成**を前提とします。UV（セルの矩形）は元セルマップのピクセル空間のままなので、解像度やレイアウトが違うと別の領域をサンプルします。
- 差し替えはそのテクスチャを使う**全描画に効きます（マスクを書くパーツも含む）**。`draw_as_mask` のパーツがそのセルマップを使っている場合、差し替え先のアルファでマスクの形が変わります。
- uGUI（`SpriteStudioGraphic`）は単一アトラスのみ描画するため、先頭アトラス以外に解決される名前を差し替えても表示には反映されません。

## Timeline からの駆動

`SpriteStudioPlayer` は、Timeline から次の 2 つの方法で動かせます（`SpriteStudioGraphic` は動かせません）。

- **Sprite Studio Track**（Timeline の追加メニューの **SpriteStudio.Runtime** の下）— `SpriteStudioTrack` を追加して `SpriteStudioPlayer` をバインドし、クリップを並べます。各クリップの **Animation Name** に、そのクリップで再生するアニメーションを指定します。トラックはブレンドしないため、Timeline 上でクリップを重ねることはできません。クリップはアニメーションを停止した状態で準備するため、クリップの後の空白では、プレイヤーはそのクリップの最後のフレームで止まったままになり、スクリプトから `Play()` を呼ぶまで動きません。
- **Control Track** — `SpriteStudioPlayer` は `ITimeControl` を実装しているため、その GameObject を対象にした Control Track のクリップが、**Animation Name** に設定したアニメーションをシークします。

クリップの時刻はアニメーションの先頭フレームから数えます。プレイヤー自身の **Loop Count**・**Speed Scale**・**Frame Skipping** はクリップを跨いで保持されるため、1 回再生に設定したプレイヤーは、カットシーンの後にスクリプトから再生しても 1 回で止まります。

クリップはプレイヤーを進めるのではなくシークするため、クリップの下ではイベントが発火せず、サウンドも鳴らず、ループも効きません。[制約と適用範囲](../limitations.md#timeline) を参照してください。
