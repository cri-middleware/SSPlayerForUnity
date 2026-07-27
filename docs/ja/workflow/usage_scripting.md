# スクリプト制御とイベント

[← ドキュメント目次へ戻る](../index.md)

Unity の C# スクリプトから `SpriteStudioPlayer` / `SpriteStudioGraphic` を制御する方法を解説します。Unity の設計思想に沿って、シンプルなメソッド呼び出しによる再生制御と、**C# event とインスペクタ配線可能な `UnityEvent` の両方**によるイベント連携を提供しており、ゲームロジックへの組み込みが容易です。

`SpriteStudioPlayer`（ワールド）と `SpriteStudioGraphic`（Canvas）は、**再生制御 API とイベント通知 API を共通**で備えています。本ページのコードはどちらのコンポーネントでもそのまま使えます。

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

`SetAnimation()` はアニメーションのデータをロードし**先頭フレームで待機**します（再生は開始しません）。続けて `Play()` を呼ぶとその場から再生が始まります。`SetAnimation()` は再生設定（ループ/速度/スキップ/方向）を**適用しません**。毎回**先頭フレーム・Forward・Normal（非往復）・ランタイム既定のループ/速度/スキップ**へ戻すため、インスペクタで設定したループ/速度/スキップは起動時に適用されますが、スクリプトから実行時に `SetAnimation()` でアニメを切り替えた場合は、必要に応じて `LoopCount` / `SpeedScale` / `SkipFrames` / `Reverse` / `PingPong` を `SetAnimation()` の後に再設定してください（後述）。

## 再生制御 API

| メンバー | 種別 | 説明 |
|---|---|---|
| `SetAnimation(string animName)` | メソッド | アニメーションを指定しデータをロード（再生は開始しない）。成功で `true` |
| `Play()` | メソッド | セットアップ済みアニメを先頭から再生開始 |
| `Play(float startFrame)` | メソッド | セットアップ済みアニメを指定フレームから再生開始 |
| `Pause()` | メソッド | 一時停止／再開のトグル（現在フレームを保持。`Play()` は先頭へ巻き戻すため再開には使えない） |
| `Stop()` | メソッド | 再生を停止 |
| `IsPlaying` | `bool`（読取） | 再生セッションが有効か（一時停止中でも `true`） |
| `IsPaused` | `bool`（読取） | 一時停止中か |
| `CurrentFrame` | `float` | 現在フレーム。`set` で任意フレームへシーク（小数可） |
| `TotalFrames` | `int`（読取） | 現在のアニメーションの総フレーム数（未ロード時 `0`） |
| `FrameRate` | `int` | 再生フレームレート (fps)。`set` で実行時変更 |
| `Reverse` | `bool` | 逆再生（Backward）か |
| `PingPong` | `bool` | 往復（PingPong）再生か |
| `SetAnimationSection(int start, int end)` | メソッド | 再生範囲をフレームで限定（フレーム指定。ラベル指定は未対応） |

### 例: 一時停止と再開

```csharp
// Pause() は一時停止／再開のトグル（再生中なら一時停止、一時停止中なら再開）
_player.Pause();
```

### 例: シークと区間再生

```csharp
_player.SetAnimation("attack");
_player.Play();
_player.SetAnimationSection(10, 30); // 10〜30 フレームの区間だけ再生
_player.CurrentFrame = 10f;          // 区間先頭へシーク
```

### 例: 逆再生・ピンポン

```csharp
_player.SetAnimation("walk");
_player.Reverse = true;   // 逆再生へ
_player.PingPong = true;  // 往復再生へ
_player.Play();
```

> `SetAnimation()` は毎回 Forward / Normal に戻すため、`Reverse` / `PingPong` は **`SetAnimation()` の後**に設定してください（`Play()` の前後どちらでも可）。

## 再生パラメータのプロパティ

インスペクタ項目に対応するプロパティです。実行時に変更するとランタイムへ即時反映されます。

| プロパティ | 型 | 説明 |
|---|---|---|
| `AnimeAsset` | `SpriteStudioAnimeAsset` | 再生対象アセット（差し替え可） |
| `AnimationName` | `string` | アニメーション名 |
| `AutoPlay` | `bool` | 起動時自動再生 |
| `LoopCount` | `int` | ループ回数（`-1` または `0` = 無限 / `n` = n 回） |
| `SpeedScale` | `float` | 再生速度倍率 |
| `SubFrameEnabled` | `bool` | サブフレーム補間 |
| `SkipFrames` | `bool` | 実時間追従（[高度な設定](./tips.md) 参照） |

`SpriteStudioPlayer`（ワールド）のみ: `SortingLayerName` / `SortingOrder` / `PixelsPerUnit`。

## イベント通知

アニメーションの進行に応じて、5 種類のイベントを受け取れます。`C# の event` と、インスペクタで配線できる `UnityEvent` の両方を提供します。

| イベント | C# event | UnityEvent フィールド | ペイロード | 発火タイミング |
|---|---|---|---|---|
| 再生開始 | `OnAnimationStarted` | `OnAnimationStartedEvent` | なし | `Play()` を呼んで再生を開始したとき |
| 再生終了 | `OnAnimationFinished` | `OnAnimationFinishedEvent` | なし | 指定した `LoopCount` をすべて再生し終えたとき。無限ループでは発火せず、`Stop()` でも発火しない |
| ループ | `OnAnimationLooped` | `OnAnimationLoopedEvent` | なし | ループで先頭へ戻るたび。最終周はループせず再生終了になる |
| ユーザーデータ | `OnUserData` | `OnUserDataEvent` | `SpriteStudioUserData` | UserData キーのフレーム到達時 |
| シグナル | `OnSignal` | `OnSignalEvent` | `SpriteStudioSignal` | Signal コマンドのフレーム到達時 |
| オーディオ | `OnAudio` | `OnAudioEvent` | `SpriteStudioAudio` | Audio イベントのフレーム到達時 |

> これらは SpriteStudio 側でアニメーションに設定したキー（UserData / Signal / Audio）に対応します。

### C#からのイベントの登録

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

```csharp
// UserData: 設定されている値だけ HasXxx が true になる
struct SpriteStudioUserData
{
    string PartName; int PartIndex;
    bool HasInt;    int    IntValue;
    bool HasString; string StringValue;
    bool HasRect;   Rect   RectValue;
    bool HasPoint;  Vector2 PointValue;
}

// Signal: パラメータは型付き（boxing なし）。GetParam / TryGetParam で取得（実行時のみ有効）
struct SpriteStudioSignal
{
    string PartName; int PartIndex;
    bool Active; string CommandId;
    int ParamCount;                                    // コマンドパラメータ数
    SpriteStudioSignalParam GetParam(int index);       // 0 <= index < ParamCount
    bool TryGetParam(string id, out SpriteStudioSignalParam p);
}
// パラメータ1件: Type に対応するアクセサで読む（Bool / Index / Integer / Floating / Text）
struct SpriteStudioSignalParam
{
    string Id; SpriteStudioSignalParamType Type;
    bool AsBool; int AsIndex; int AsInteger; float AsFloating; string AsText;
}

// Audio: SoundName で対象音を識別
struct SpriteStudioAudio
{
    string PartName; int PartIndex;
    uint SoundListNameHash; uint SoundNameHash;
    string SoundName; int LoopNum;
}
```

> **Signal パラメータの有効期間について**: `GetParam` / `TryGetParam` が返す値は、プレイヤーが所有して使い回すバッファへのビューです（アロケーションと boxing を避けるための仕組みです）。`SpriteStudioSignal` とそこから読み出した `SpriteStudioSignalParam` が有効なのは、そのプレイヤーの次のイベントポーリングまで（通常は次フレームまで）です。コールバックの外へ持ち越す場合（フィールドへの保存、コルーチンからの参照、キューイングなど）は、**値そのものをコピー**してください（`p.AsInteger` / `p.AsText` など）。`SpriteStudioSignal` を保持したままにすると、後で別の Signal のパラメータをエラーなく返すようになります。

> **Audio イベントについて**: 本プラグインは Audio の発生を**通知**します。実際の音の再生（`AudioSource` での発音）はアプリ側で行ってください。再生に必要な `AudioClip` は `SpriteStudioAnimeAsset.Sounds` に解決済みで保持されています。

### 実装例: アニメーションの連続再生

「攻撃」アニメーションが終了したら、自動的に「待機」アニメーションへ遷移させる実装例です。

```csharp
void OnEnable()
{
    _player.OnAnimationFinished += HandleFinished;
}

void OnDisable()
{
    _player.OnAnimationFinished -= HandleFinished;
}

void HandleFinished()
{
    // 攻撃が終わったら待機状態に戻る
    if (_player.AnimationName == "attack")
    {
        _player.SetAnimation("idle");
        _player.Play();
    }
}
```

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

`SpriteStudioPlayer` の **Part Trackers** リストにエントリを追加して設定します。

| 設定 | 説明 |
|---|---|
| **Part Name** | 追従対象のパーツ名（`.ssab` の PartData 名）。インスペクタではアセットのパーツ名からドロップダウンで選べます |
| **Target** | 追従させる `Transform`。プレイヤー直下でも別階層でも可 |
| **Drive Scale** | ON で継承スケールも反映（パーツ自身の localScale は除外）。OFF（既定）は位置と回転のみで、ターゲットのスケールには一切触れません |
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
| `RegisterPartTracker(partName, target, driveScale, onHide)` | トラッカーを追加し、作成されたエントリを返す（`driveScale` / `onHide` は省略可） |
| `UnregisterPartTracker(target)` | その `Transform` を対象とするエントリをすべて削除し、削除件数を返す |
| `TryGetPartHidden(partName, out bool hidden)` | そのパーツが現在フレームで hide かを取得。パーツが不明なら `false` を返す |
| `TryGetPartLocalMatrix(partName, out Matrix4x4 matrix)` | そのパーツの現在フレームの変換行列（プレイヤーローカル、`1 / PixelsPerUnit` 適用済み）を取得 |

> `Transform` を常時追従させるまでもなく「その瞬間のパーツ位置だけ知りたい」場合（弾の発射位置を取るなど）は、トラッカーを登録せず `TryGetPartLocalMatrix()` を直接呼ぶ方が簡潔です。

### 追従のタイミングと精度

トラッカーの駆動は **LateUpdate** で、そのフレームのメッシュが確定した後・描画の前に行われます。追従先に付けた `Renderer` などは同じフレーム内で更新されます。

姿勢はターゲットの**親空間**でのローカル TRS として書き込まれるため、ターゲットの置き場所によって一致の度合いが変わります。

- プレイヤーの**子**に置いた場合、パーツの変換がそのまま書き込まれます。誤差要因はパーツ階層自体のせん断（Skew）のみです。
- 別階層（プレイヤーの子ではない位置）に置いた場合、**ワールド位置は常に正確**です。ただしターゲットの親やプレイヤーが回転と非一様スケールを同時に持つと、**回転とスケールは近似**になります。

> **別階層のターゲットは 1 フレーム遅れることがあります。** トラッカーは駆動時点のプレイヤーの変換を使って書き込むため、その後にプレイヤーが動いても次のフレームまで追随しません。駆動は LateUpdate の早い段階（`DefaultExecutionOrder(-100)`）で走るので、自前の `LateUpdate` でプレイヤーを動かす構成では特に注意してください。子に置いた場合は階層継承で常に追随します。

`Drive Scale` が ON のときに反映されるのは**継承スケールのみ**で、パーツ自身の localScale は取り除かれます。またワールド用プレイヤーは 2D のため、書き込まれるのは X / Y だけで、ターゲットの Z スケールは保持されます。

### 注意点

- **ターゲットの `SetActive` はプレイヤーが操作します。** 次の 2 つの場合に自動で非アクティブ化され、条件が解消すると自動で再アクティブ化されます。ユーザー側で設定したアクティブ状態は上書きされることがあります。
    - パーツ名が現在のアニメーションに存在しない（この場合は `On Hide` の設定に関係なく常に非アクティブ）
    - パーツが hide のフレームで、かつ `On Hide` が `Deactivate`
- パーツ名は、そのプレイヤー自身が再生しているアニメーションのパーツから解決されます。**インスタンスパーツの内部（子アニメーション）のパーツは指定できません**（インスタンスパーツ自体は指定できます）。
- 同名のパーツが複数ある場合は、最初に見つかったものが使われます。
- `AnimeAsset` を差し替えるとパーツ名は自動で再解決されるため、トラッカーを登録し直す必要はありません。
- 編集モードでも、開始フレームの静止プレビューが構築されるタイミングでトラッカーが一度反映されます。

## パーツオーバーライド（パーツカラー / セル / 表示指定）

パーツ単位のランタイムオーバーライドは、「このパーツを**今**この色に / このセルに / 非表示に」とスクリプトから指示する機能です。オーバーライドはキーフレームやアニメーションのブレンドよりも優先されるので、アニメーションと取り合いになりません。`SpriteStudioPlayer` / `SpriteStudioGraphic` の両方で使えます。

```csharp
var player = GetComponent<SpriteStudioPlayer>();

// パーツを赤く着色（乗算）。通常（画像）パーツに適用されます。
player.SetPartColorOverride("body", SpriteStudioBlendOp.Mul, Color.red);

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
| `FindPartIndex(name)` | パーツインデックス。アセットに無ければ `-1` |
| `SetPartColorOverride(part, blendOp, color, priority)` | パーツカラーオーバーライド（単色。4隅指定のオーバーロードもあり：左上・右上・左下・右下） |
| `SetPartCellOverride(part, cellmapName, cellName, priority)` | 別のセルで描画する |
| `SetPartVisibilityOverride(part, hide, cascade)` | 強制非表示（`hide: false` でアニメーションに戻す） |
| `ClearPartColorOverride` / `ClearPartCellOverride` / `ClearPartVisibilityOverride` | 1パーツの1オーバーライドを解除 |
| `ClearAllPartOverrides()` | そのプレイヤーの全オーバーライドを解除 |
| `GetCellMapNames()` / `GetCellNames(cellmap)` | セルオーバーライドに指定できるセルマップ名 / セル名の列挙 |

各メソッドの `part` は**パーツ名**と**パーツインデックス**のどちらでも指定でき、パーツが不明な場合やランタイムが受け付けなかった場合は `false` を返します。

> **パーツインデックスの使い方について**: パーツインデックスは同一アセット（同じ `.ssab`）内では安定しているので、頻繁にオーバーライドするなら `FindPartIndex` で一度パーツ名をパーツインデックスに解決して、そのインデックスを使い回すことを推奨します。

### 優先モード（priority mode）

パーツカラーとセルのオーバーライドはアニメーションと競合するため、`SpriteStudioOverridePriority` を取ります（表示指定にはありません。単なる強制非表示フラグで、アニメーションを設定し直すと必ずクリアされます）。

| 優先モード | 挙動 |
|---|---|
| `OverwriteOnNextKeyframe` | アニメーションデータが当該アトリビュートを更新するまで、オーバーライドが適用される |
| `HoldUntilNextAnimation`（既定） | 現在のアニメーション中は勝ち続け、新しいアニメーションを設定するとオーバーライドが解除される |
| `Permanent` | 同じアニメーションデータ（.ssab）である間、オーバーライドが適用される (アニメーション変更をまたいでも持続) |

### 注意点

- **カラー**は通常パーツ、**セル**は通常パーツとマスクパーツに適用されます。それ以外の種別のパーツでは黙って無視されます（呼び出し自体は `true` を返します）。
- 色はオーサリングされた Part Color と同じ 8bit sRGB 空間として解釈され、アルファはランタイム側で pre-multiply されます。自前で変換せず、オーサリングどおりの色を渡してください。
- セルオーバーライドは設定時に解決されるため、存在しないセルマップ名 / セル名はその場で失敗します（`false` を返します）。
- オーバーライドはランタイムが保持し、そのライフサイクルもランタイムが管理します。アニメーション変更後に再適用する必要はありません。意図に合った優先モードを選んでください。

## Timeline からの駆動

`SpriteStudioPlayer` は `ITimeControl` を実装しており、Unity の Timeline トラックから再生時刻を制御できます。Timeline 用のトラック / アセットも同梱されています。
