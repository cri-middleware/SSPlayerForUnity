# 実装計画: オーディオイベントによる音声再生

ステータス: **実装済み（実験版）・実機ワンショット確認待ち**
対象: SSPlayerForUnity / SpriteStudio Audio イベント（`ADIO`）
最終更新: 2026-06-29

> 配置メモ: 本書はパッケージ配布物ではなく作業用の実装計画書。`docs/` 配下は
> GitHub 公開ドキュメントに直結するため、計画書はリポジトリ直下の `_` 始まり
> ファイル（`_MEMO.txt` と同じ慣習）として置く。

---

## 0. 設計の方針転換（2026-06-29）

当初は「Player と疎結合な別コンポーネント `SpriteStudioAudioPlayer` を貼り、virtual
override でカスタムする」案だった。しかし**デザイナ/初心者が操作するには敷居が高い**
（個体ごとに別コンポーネントを貼る・カスタムは継承クラスが要る）と判断し、方針を転換した。

ポイントは **ユーザー層が 2 ついて要求が真逆**であること:

- **デザイナ/初心者** — コードもコンポーネント追加もしたくない。「置いたら鳴る」が理想。
- **上級者/ミドルウェア連携（CRI ADX 等）** — 完全に差し替えたい。

`ADIO` は「やることが自明」（= この音を鳴らす）なので、デザイナ向けには**イベント関数の
登録ではなく自動再生（ゼロコンフィグ）**が正解。上級者向けには**毎オブジェクトの継承
ではなく、プロジェクトに 1 か所の差し替え口**を用意すれば両立する。

新方針:

1. **内蔵・既定 ON** — Player/Graphic 自身が音を鳴らす。別コンポーネント不要。置けば鳴る。
2. **差し替え口は 1 か所** — `SpriteStudioAudioBackend`（ScriptableObject）。個体フィールド
   または**プロジェクト全体の `Global`** で差し替え。null なら内蔵 AudioClip 再生。

---

## 1. 目的とスコープ

`ADIO` キーを契機に Unity 上で効果音/BGM を再生する。

### 確定方針

- **デザイナはゼロコンフィグ**: Player/Graphic に音付き AnimeAsset を割り当てれば、
  別コンポーネント・コード無しでワンショット再生される（`Play Audio` 既定 ON）。
- **上級者は 1 か所差し替え**: `SpriteStudioAudioBackend` を継承し、`Global` か個体
  フィールドに割り当てれば全/個別の再生を自前バックエンドへ流せる。
- 音量は内蔵再生用に `Audio Volume` フィールドで供給（パン/ミキサー等はバックエンド領分）。
- MVP は **ワンショットのみ**（`loop_num` は無視、ペイロードには素通しで残る）。

### スコープ外（後続フェーズ）

- ループ音（`loop_num != 0`）の専用 AudioSource 管理とアニメ停止連動（§5 LoopNum 規約参照）
- BGM をアニメの停止/ループ境界に同期して止める仕組み
- ssab スキーマ / SDK への音量・パン等のフィールド追加
- AudioClip インポートのオプト化（Importer トグル）＋警告抑制
  （ADX 前提プロジェクトの警告氾濫対策。importer 改修として独立着手）

---

## 2. 現状（既に配線済みの土台）

| 要素 | 場所 | 状態 |
|---|---|---|
| イベント発火（PollEvents） | `package/Runtime/SpriteStudioInternalPlayer.cs:694-713` | ✅ |
| ペイロード構造体 `SpriteStudioAudio` | `package/Runtime/SpriteStudioEvents.cs` | ✅ |
| `Player`/`Graphic` の `OnAudio` / Inspector `OnAudioEvent` / `InvokeAudio` | Player.cs / Graphic.cs | ✅ |
| AudioClip のインポート・保持 (`AnimeAsset.Sounds`) | `package/Editor/SpriteStudioAnimeAssetBuilder.cs:319-353` | ✅ |
| ハッシュ→AudioClip 解決 `ResolveAudioClip()` | `package/Runtime/SpriteStudioAnimeAsset.cs:459-474` | ✅ |

### 2.1 ペイロードに乗っている情報と、乗っていない情報

イベントで渡る `SpriteStudioAudio` は **`PartAttributeAudio`** 由来で、ssab の
**`SoundList` / `SoundFile`**（ssse 相当のメタ）とは別物。

- **乗っている = アドレス情報のみ**: `SoundListNameHash` / `SoundNameHash` /
  `SoundName`(alias・診断用) / `LoopNum` + PartName/PartIndex。
- **乗っていない = ssab `SoundFile` 側にだけある実体メタ**: `file_path`（例
  `"sound/foo.acb"`）/ `file_path_hash` / `time_total`（再生長 ms）。

→ AudioClip を鳴らすだけなら `ResolveAudioClip` で足りるが、file_path 等で自前
バックエンドを叩くには `TryGetSoundInfo`（§4.3）で引く。

---

## 3. クラス設計（内蔵 + 差し替え口）

```
SpriteStudioInternalPlayer ──OnAudio──▶ Player/Graphic.InvokeAudio
                                              │
                              ┌───────────────┴───────────────┐
                              ▼                                ▼
                   OnAudioEvent (UnityEvent)        SpriteStudioAudioController.Play()
                   (従来どおり手配線も可)                     │
                                       ┌──────────────────────┼──────────────────────┐
                                       ▼                      ▼                      ▼
                              個体 Audio Backend      Global Backend          内蔵 AudioClip
                              (任意・最優先)          (任意・次点)            PlayOneShot (既定)
```

### 3.1 Player / Graphic に追加したフィールド

```csharp
[Header("Audio")]
[SerializeField] private bool _playAudio = true;                  // 既定 ON: 置けば鳴る
[Range(0f,1f)] [SerializeField] private float _audioVolume = 1f;  // 内蔵再生の音量
[SerializeField] private SpriteStudioAudioBackend _audioBackend;  // 個体差し替え (任意)

private SpriteStudioAudioController _audioController;             // 非直列化・遅延生成
```

`InvokeAudio` を経由して内蔵再生にぶら下げる（購読管理を増やさない）:

```csharp
private void InvokeAudio(SpriteStudioAudio data)
{
    OnAudioEvent?.Invoke(data);
    if (_playAudio && Application.isPlaying)
    {
        if (_audioController == null) _audioController = new SpriteStudioAudioController(gameObject);
        _audioController.Play(in data, _animeAsset, _audioBackend, _audioVolume);
    }
}
```

### 3.2 `SpriteStudioAudioController`（内蔵再生ヘルパ・素の C# クラス）

`package/Runtime/SpriteStudioAudioController.cs`。Player/Graphic 共通。MonoBehaviour ではない。

解決順 = **個体バックエンド > グローバルバックエンド > 内蔵**。AudioSource は内蔵経路で
初めて必要になった時に owner へ自動付与（音を使わない個体には付けない）。

```csharp
public void Play(in SpriteStudioAudio ev, SpriteStudioAnimeAsset asset,
                 SpriteStudioAudioBackend backend, float volume)
{
    var be = backend != null ? backend : SpriteStudioAudioBackend.Global;
    if (be != null) { be.PlayAudio(in ev, asset, _owner); return; }

    var clip = asset != null ? asset.ResolveAudioClip(ev.SoundListNameHash, ev.SoundNameHash) : null;
    if (clip == null) return;                 // 非対応/未解決は安全に無音
    EnsureAudioSource();
    _audioSource.PlayOneShot(clip, volume);   // MVP: ワンショット
}
```

### 3.3 `SpriteStudioAudioBackend`（差し替え口・ScriptableObject）

`package/Runtime/SpriteStudioAudioBackend.cs`。

```csharp
public abstract class SpriteStudioAudioBackend : ScriptableObject
{
    public static SpriteStudioAudioBackend Global { get; set; }   // プロジェクト全体の既定
    public abstract void PlayAudio(in SpriteStudioAudio ev,
                                   SpriteStudioAnimeAsset asset, GameObject source);
}
```

---

## 4. 使い方

### 4.1 デザイナ（ゼロコンフィグ）

1. `SpriteStudioPlayer`（または `SpriteStudioGraphic`）に音付き AnimeAsset を割り当てる。
2. Play。該当フレームで自動再生される。**別コンポーネントもコードも不要。**
3. 鳴らしたくない個体は `Play Audio` を OFF。音量は `Audio Volume`。

### 4.2 上級者: プロジェクト全体を自前バックエンドへ（CRI ADX 等）

```csharp
public class AdxAudioBackend : SpriteStudioAudioBackend
{
    public override void PlayAudio(in SpriteStudioAudio ev, SpriteStudioAnimeAsset asset, GameObject source)
    {
        if (asset != null && asset.TryGetSoundInfo(ev.SoundListNameHash, ev.SoundNameHash, out var info))
            MyAdx.Play(info.FilePath /* "sound/foo.acb" */, info.Alias, source);
    }
}

// 起動時に 1 回だけ。以後すべての Player/Graphic がここを通る。
SpriteStudioAudioBackend.Global = myAdxBackendAsset;
```

個体ごとに変えたい場合は、その個体の `Audio Backend` フィールドに割り当てる（Global より優先）。

### 4.3 メタ解決アクセサ `AnimeAsset.TryGetSoundInfo()`

`ResolveAudioClip` と対の opt-in アクセサ。発火ペイロードに乗らない file_path/alias/長さを
同じハッシュキーで返す。生 FlatBuffers パースを `AnimeAsset` 1 か所に封じ込め、呼び出し側に
`SsabData` を晒さない。SDK 改変ゼロ。

```csharp
public struct SpriteStudioSoundInfo { public string Alias, FilePath; public uint FilePathHash, TimeTotalMs; }
public bool TryGetSoundInfo(uint soundListNameHash, uint soundNameHash, out SpriteStudioSoundInfo info);
```

---

## 5. 確定事項

1. **LoopNum セマンティクス**（SS エディタ仕様）:

   | `loop_num` | 意味 | 再生回数 |
   |---|---|---|
   | `0` | 単発再生 | 1 回 |
   | `N > 0` | N 回ループ | N + 1 回 |
   | `< 0`（負数） | 無限ループ | ∞ |

   MVP はワンショット固定で `loop_num` を無視するが、ペイロードには素通しで残し、後続の
   ループ音フェーズの設計根拠とする。

2. **Player / Graphic 両対応** — どちらも内蔵（同じ `SpriteStudioAudioController`）。

3. **解決失敗時** — 内蔵経路は黙って無音（importer が import 時に既に警告済）。

4. **デコーダ非拘束** — `SpriteStudioAudioBackend` で AudioClip を経由しない再生に丸ごと
   差し替え可能。パッケージは「識別子(alias/file_path/hash)＋発火タイミング」のみ提供し、
   デコード/再生手段を前提にしない（CRI ADX 連携見据え）。

5. **配置** — 計画書はリポジトリ直下 `_IMPLEMENT_Audio_playback.md`。

---

## 6. 実装済みファイル（実験版・2026-06-29）

| ファイル | 内容 |
|---|---|
| `package/Runtime/SpriteStudioEvents.cs` | `SpriteStudioSoundInfo` struct 追加 |
| `package/Runtime/SpriteStudioAnimeAsset.cs` | `TryGetSoundInfo()` ＋ lazy dict |
| `package/Runtime/SpriteStudioAudioBackend.cs`（新規） | 差し替え口（abstract SO + `Global`） |
| `package/Runtime/SpriteStudioAudioController.cs`（新規） | 内蔵再生ヘルパ（解決順ルーティング） |
| `package/Runtime/SpriteStudioPlayer.cs` | Audio フィールド3つ + controller + `InvokeAudio` 配線 |
| `package/Runtime/SpriteStudioGraphic.cs` | 同上 |
| `package/Tests/Runtime/SpriteStudioAudioEventTests.cs` | `TryGetSoundInfo_ResolvesMetadata_ForFiredSound` 追加 |
| `package/Tests/Runtime/SpriteStudioAudioBackendTests.cs`（新規） | ルーティング解決順 3 テスト |

撤去: 旧 `SpriteStudioAudioPlayer`（別コンポーネント）/ `ISpriteStudioAudioSource` interface /
旧 `SpriteStudioAudioPlayerTests`。

### 検証状況

- コンパイル クリーン / EditMode 15/15 / PlayMode 44/44（`examples/UnityTestProject`・Unity 6000.3.16f1・ヘッドレス）。
- **残 = 実機ワンショット確認**（batchmode では音が鳴らせず未カバー）。Sound.ssab を Player に
  割り当てて Play するだけで鳴るはず（ゼロコンフィグ）。
