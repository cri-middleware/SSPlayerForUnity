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

`SetAnimation()` はアニメーションのデータをロードし、再生設定（ループ/速度/スキップ）を適用したうえで**先頭フレームで待機**します（再生は開始しません）。続けて `Play()` を呼ぶとその場から再生が始まります。`SetAnimation()` は毎回**先頭フレーム・Forward・Normal（非往復）**へ戻すため、逆再生や往復再生で始めたい場合は `SetAnimation()` の後にフラグを設定してください（後述）。

## 再生制御 API

| メンバー | 種別 | 説明 |
|---|---|---|
| `SetAnimation(string animName)` | メソッド | アニメーションを指定しデータをロード（再生は開始しない）。成功で `true` |
| `Play()` | メソッド | セットアップ済みアニメを先頭から再生開始 |
| `Play(float startFrame)` | メソッド | セットアップ済みアニメを指定フレームから再生開始 |
| `Pause()` | メソッド | 現在フレームを保持して一時停止（冪等） |
| `Resume()` | メソッド | 一時停止を解除して現在フレームから再開（冪等） |
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
if (_player.IsPlaying && !_player.IsPaused)
    _player.Pause();
else
    _player.Resume();
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
| `LoopCount` | `int` | ループ回数（`-1` = 無限） |
| `SpeedScale` | `float` | 再生速度倍率 |
| `SubFrameEnabled` | `bool` | サブフレーム補間 |
| `SkipFrames` | `bool` | 実時間追従（[高度な設定](./tips.md) 参照） |

`SpriteStudioPlayer`（ワールド）のみ: `SortingLayerName` / `SortingOrder` / `PixelsPerUnit`。

## イベント通知

アニメーションの進行に応じて、5 種類のイベントを受け取れます。`C# の event` と、インスペクタで配線できる `UnityEvent` の両方を提供します。

| イベント | C# event | UnityEvent フィールド | ペイロード | 発火タイミング |
|---|---|---|---|---|
| 再生開始 | `OnAnimationStarted` | `OnAnimationStartedEvent` | なし | `Play()` を呼んで再生を開始したとき |
| 再生終了 | `OnAnimationFinished` | `OnAnimationFinishedEvent` | なし | 再生が終端に達して停止したとき |
| ループ | `OnAnimationLooped` | `OnAnimationLoopedEvent` | なし | ループで先頭へ戻ったとき |
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

// Signal: Params はパラメータが無ければ null（実行時のみ有効・非シリアライズ）
struct SpriteStudioSignal
{
    string PartName; int PartIndex;
    bool Active; string CommandId;
    Dictionary<string, object> Params; // 値は bool / int / float / string のいずれか
}

// Audio: SoundName で対象音を識別
struct SpriteStudioAudio
{
    string PartName; int PartIndex;
    uint SoundListNameHash; uint SoundNameHash;
    string SoundName; int LoopNum;
}
```

> [!NOTE]
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

指定したパーツに、ユーザーが用意した `Transform`（武器・エフェクト・当たり判定など）を毎フレーム追従させる機能です。プレイヤーは GameObject を生成せず、登録された `Transform` を駆動する連動方式です。

`SpriteStudioPlayer` の **Part Trackers** リストにエントリを追加して設定します。

| 設定 | 説明 |
|---|---|
| **Part Name** | 追従対象のパーツ名（`.ssab` の PartData 名） |
| **Target** | 追従させる `Transform`。プレイヤー直下でも別階層でも可 |
| **Drive Scale** | ON で（localScale を除く）継承スケールも反映。OFF（既定）は位置と回転のみ |
| **On Hide** | パーツが hide のフレームでの挙動。`FollowAlways`（追従継続）/ `Deactivate`（対象を非表示） |

- ターゲットをプレイヤーの**子**に置くとローカル変換が完全一致します。別階層に置いた場合はワールド位置・回転が一致し、スケールは近似になります。
- アニメーションに存在しないパーツ名を指定した場合、ターゲットは自動的に非アクティブ化されます。

## Timeline からの駆動

`SpriteStudioPlayer` は `ITimeControl` を実装しており、Unity の Timeline トラックから再生時刻を制御できます。Timeline 用のトラック / アセットも同梱されています。
