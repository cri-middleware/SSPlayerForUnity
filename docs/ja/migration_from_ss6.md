# 🔄 SS6Player for Unity からのマイグレーション

[← ドキュメント目次へ戻る](./index.md)

旧プラグイン **[SS6Player for Unity](https://github.com/SpriteStudio/SS6PlayerForUnity)** から本プラグインへ移行する場合は、以下の大きな変更点にご注意ください。本プラグインは内部実装（Rust コア `libssruntime` + Job System）だけでなく、アセットパイプラインと C# API も一新されています。単純な差し替えではなく、**アセットの再インポートとスクリプトの書き換え**が必要になります。

## 1. アセットフォーマットとインポートの変更

- **SS6Player**: `Tools > SpriteStudio6 > Importer` から `.sspj` を取り込み、アニメーションごとに **`.prefab`** と、`Script_SpriteStudio6_DataProject` / `DataAnimation` / `DataCellMap` / `DataEffect` などの **`.asset`（ScriptableObject）** 群を生成していました。インポート時に `SS6PU` / `UNITY_NATIVE` / `UNITY_UI` などの**モード**を選択する必要がありました。
- **本バージョン**: 最適化されたバイナリ形式 **`.ssab`** を内包した単一の **`SpriteStudioAnimeAsset`（ScriptableObject）** を生成します。**プレハブは生成されません**。インポート時のモード選択もありません（ワールド／Canvas の選択は配置時に行います）。
  - `Window > SpriteStudio > Importer` ウィンドウを開き、`.sspj` を**ドラッグ＆ドロップ**するだけで変換されます。既定の出力先は `Assets/ssab_generated` です。
  - 旧バージョンで生成した `.prefab` / `.asset` は本バージョンでは利用できません。**`.sspj` から改めてインポートし直してください。**
  - 詳細は [基本的な使い方](./workflow/usage_basic.md) および [アセットのインポートとエディタ連携](./workflow/usage_asset_pipeline.md) を参照してください。

## 2. コンポーネントと配置の変更

- **SS6Player**: 生成された**プレハブをシーンに配置**して使用しました。プレハブには `Script_SpriteStudio6_Root` が付与され、描画方式に応じて `Script_SpriteStudio6_RootUnityNative`（メッシュ）や `Script_SpriteStudio6_RootUnityUI`（uGUI/Canvas）が併用されていました。クラスはすべてグローバル名前空間にあります。
- **本バージョン**: プレハブではなく、用途に応じて **2 種類のコンポーネント**を使い分けます。名前空間は `SpriteStudio.Runtime` です。

  | 用途 | SS6Player | 本バージョン |
  |---|---|---|
  | ワールド空間（3D/2D） | `Script_SpriteStudio6_Root` + `RootUnityNative` | **`SpriteStudioPlayer`**（MeshRenderer） |
  | Canvas (uGUI) の UI | `Script_SpriteStudio6_Root` + `RootUnityUI` | **`SpriteStudioGraphic`**（CanvasRenderer） |

  - 配置は、生成された `SpriteStudioAnimeAsset` を **Hierarchy へドラッグ＆ドロップ**し、表示されるメニューから `SpriteStudioPlayer` / `SpriteStudioGraphic` を選ぶだけです。手動で Add Component することもできます。
  - どちらのコンポーネントも **共通の再生制御 API とイベント API** を備えており、モードごとにクラスが分かれていた SS6Player と異なり、スクリプトの記述はワールド／Canvas で共通です。

## 3. 再生制御 API の変更

SS6Player の「トラック（`indexTrack`）」を中心とした API から、Unity 標準に沿ったシンプルなメソッド／プロパティ API へ変更されました。

### 3-1. アニメーションの指定は「インデックス」から「名前」へ

- **SS6Player**: `IndexGetAnimation("walk")` でインデックスを取得し、`AnimationPlay(indexTrack, indexAnimation, ...)` に**インデックス**を渡していました。
- **本バージョン**: `SetAnimation("walk")` で**名前**を直接指定してロードし、`Play()` で再生します。

```csharp
using SpriteStudio.Runtime;

[SerializeField] private SpriteStudioPlayer _player;

// SS6Player:
//   int idx = _root.IndexGetAnimation("walk");
//   _root.AnimationPlay(0, idx, 0);          // track 0, 無限ループ
// 本バージョン:
_player.SetAnimation("walk");
_player.LoopCount = -1;                        // 無限ループ
_player.Play();
```

### 3-2. メソッド／プロパティ対応表

| SS6Player | 本バージョン | 備考 |
|---|---|---|
| `IndexGetAnimation(name)` + `AnimationPlay(track, index, …)` | `SetAnimation(name)` + `Play()` | ロードと再生開始を分離 |
| `AnimationPlay(…, frameOffset)` | `Play(float startFrame)` | 開始フレーム指定 |
| `AnimationStop(track)` | `Stop()` | |
| `AnimationPause(track, true/false)` | `Pause()` / `Resume()` | 2つに分離。`Pause()` でフレームを保持、`Resume()` で解除（`Play()` は巻き戻り） |
| `CursorSet/Get(track, cursor)` | `FrameNo`（get/set） | シーク。小数フレーム可 |
| `RateTimeSet/Get(track, rate)` | `SpeedScale` | 再生速度倍率 |
| `AnimationPlay(…, timesPlay)` | `LoopCount` | **後述のループ回数の違いに注意** |
| `KindStylePlay.PINGPONG` | `PlaybackStyle`（enum） | 往復再生 |
| `rateTime` を負値にして逆再生 | `PlaybackDirection`（enum） | **速度と方向を分離**（下記参照） |
| `AnimationPlay(…, framePerSecond)` | `FrameRate` | 再生 fps |
| `labelRangeStart / labelRangeEnd` | `SetAnimationSectionByLabel(string startLabel, string endLabel, int startOffset, int endOffset)` | `frameRangeOffsetStart` / `frameRangeOffsetEnd` はオフセットの引数。フレームで指定するのは `SetAnimationSection(int start, int end)` |
| `StatusIsPlaying` / `StatusGetPlay(track)` | `IsPlaying` | 一時停止中も `true` |
| `StatusGetPause(track)` | `IsPausing` | |
| （なし） | `IsPlayingForward` | 実際の進行方向。`PlaybackStyle.PingPong` の戻りの脚では `false`。音声のゲートに使います |
| `CountGetAnimation()` | `AnimationNames.Length` | |

### 3-3. ループ回数

`timesPlay` の値はそのまま移植できます。

| 意味 | SS6Player `timesPlay` | 本バージョン `LoopCount` |
|---|---|---|
| 無限ループ | `0` | `-1`（推奨）または `0` |
| 1 回だけ再生（ループなし） | `1` | `1` |
| n 回再生 | `n` | `n` |

> 無限ループの正式な値は `-1` で、既定値でもあります。`0` はその別名として受け付けるため、`timesPlay = 0` をそのままコピーしても無限ループになります。「再生しない」を表す値はありません。`Play()` を呼ばなければ再生されません。

### 3-4. 逆再生・速度は分離されました

SS6Player では `rateTime` に負値を渡すことで逆再生していましたが、本バージョンでは**速度（`SpeedScale`）と方向（`PlaybackDirection`）が独立**しています。

```csharp
// SS6Player: 2倍速で逆再生
//   _root.RateTimeSet(0, -2.0f);
// 本バージョン:
_player.SetAnimation("walk");
_player.SpeedScale = 2.0f;   // 速度は正の倍率
_player.PlaybackDirection = SpriteStudioPlaybackDirection.Backward;   // 方向は enum
_player.Play();
```

> `PlaybackDirection` / `PlaybackStyle` はクリップではなく再生そのものを表すため、`SetAnimation()` をまたいでも**保持**されます。切り替えのたびに設定し直す必要はなく、`Play()` の前に一度設定すれば十分です（切り替えで再アームされるのは再生ヘッドとループ回数のみ）。

## 4. イベント／コールバックの変更

- **SS6Player**: `Script_SpriteStudio6_Root` の**デリゲートフィールド**（`FunctionUserData` / `FunctionSignal` / `FunctionPlayEnd`）に関数を代入していました。各デリゲートは `scriptRoot, nameParts, indexParts, …, ref userData, flagWayBack` のように多数の引数を取り、`FunctionPlayEnd` は戻り値 `bool`（`false` で自動破棄）でした。
- **本バージョン**: Unity 標準に沿った **C# `event` と、インスペクタで配線できる `UnityEvent` の両方**を提供します。ペイロードは構造体（`SpriteStudioUserData` など）で受け取ります。

### 4-1. イベント対応表

| SS6Player（デリゲート） | 本バージョン（C# event / UnityEvent） | ペイロード |
|---|---|---|
| `FunctionPlayEnd` | `OnAnimationFinished` / `OnAnimationFinishedEvent` | なし |
| `FunctionUserData` | `OnUserData` / `OnUserDataEvent` | `SpriteStudioUserData` |
| `FunctionSignal` | `OnSignal` / `OnSignalEvent` | `SpriteStudioSignal` |
| （なし・新規） | `OnAnimationStarted` / `OnAnimationLooped` | なし |
| （なし・新規） | `OnAudio` / `OnAudioEvent` | `SpriteStudioAudio` |

### 4-2. 登録方法の変更

```csharp
// SS6Player:
//   _root.FunctionUserData = HandleUserData;   // フィールドに代入
//   _root.FunctionPlayEnd  = HandlePlayEnd;

// 本バージョン: += / -= で登録・解除
void OnEnable()
{
    _player.OnUserData         += HandleUserData;
    _player.OnAnimationFinished += HandleFinished;
}
void OnDisable()
{
    _player.OnUserData         -= HandleUserData;
    _player.OnAnimationFinished -= HandleFinished;
}

void HandleUserData(SpriteStudioUserData data)   // 引数は構造体1つ
{
    if (data.HasString) Debug.Log(data.StringValue);
    if (data.HasInt)    Debug.Log(data.IntValue);
}
void HandleFinished() { /* 戻り値なし。自動破棄の挙動は無くなりました */ }
```

> - `FunctionPlayEnd` にあった「`false` を返すと GameObject を自動破棄」という仕様はありません。破棄が必要な場合は `OnAnimationFinished` 内で明示的に `Destroy()` してください。
> - `UnityEvent` 版（`OnUserDataEvent` など）を使えば、インスペクタ上で他コンポーネントのメソッドへ配線できます。
> - イベントデータ構造体（`SpriteStudioUserData` / `SpriteStudioSignal` / `SpriteStudioAudio`）の詳細は [スクリプト制御とイベント](./workflow/usage_scripting.md#受け取るイベントデータの構造) を参照してください。

## 5. その他の注意点・仕様差分

- **マルチトラック／クロスフェード**: SS6Player の複数トラックによるアニメーションの重ね合わせと `TrackTransition()`（クロスフェード）は、トラックの積み重ねで行います。`SetTrackAnimation()` で `SetAnimation()` が選んだアニメーションの上にアニメーションを重ね、`Crossfade()` でトラックをフェードしながら切り替えます。[モーションブレンド](./workflow/motion_blending.md) を参照してください。
- **エフェクト**: SS6Player の `Script_SpriteStudio6_RootEffect` のような独立コンポーネントは不要です。エフェクトパーツを含むアニメーションは、`SpriteStudioPlayer` / `SpriteStudioGraphic` 内で**インラインに再生**されます。
- **パーツへの GameObject 追従**: SS6Player の `TrackConnectParts()` / `InstanceGet()` などに代わり、`SpriteStudioPlayer` の **Part Trackers**（指定パーツへ任意の `Transform` を追従させる機能）を利用してください。詳細は [スクリプト制御とイベント](./workflow/usage_scripting.md#パーツトラッキング指定パーツへの追従) を参照してください。
- **Timeline**: 引き続き対応しています。`SpriteStudioPlayer` は `ITimeControl` を実装しており、Timeline トラックから再生時刻を制御できます。
- **対応環境**: [インストール](./setup/install.md#対応環境) を参照してください。
- **パーツ単位のカラー / セル / 表示の上書き**: SS6Player の `PartsColorChange()` / `CellChangeParts()` / `HideSet()` に相当する機能は、**パーツオーバーライド（Override Layer API）** として提供しています。キーフレームやアニメーションのブレンドより優先してパーツを上書きできます。詳細は [スクリプト制御とイベント](./workflow/usage_scripting.md#パーツオーバーライドパーツカラー--セル--表示指定) を参照してください。
- **コライダー連携**: SS6Player のコライダー機能に直接相当する API はありません。必要な場合は、指定パーツへ追従させた GameObject（**Part Trackers**）側に任意のコライダーを付ける方法で代替してください（Part Trackers の詳細は上記を参照）。
