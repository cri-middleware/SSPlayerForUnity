using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using SpriteStudio.Runtime;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace SpriteStudio.Runtime.Tests
{
    /// <summary>
    /// Tier 1 で SpriteStudioPlayer / SpriteStudioGraphic に追加した再生制御 API
    /// (IsPlaying / IsPaused / Pause / Stop / CurrentFrame / TotalFrames /
    ///  FrameRate / Reverse / PingPong / SetAnimationSection) を検証する。
    ///
    /// これらは内部プレイヤー (Rust ランタイム) への薄い委譲なので 2 層で確認する:
    ///   (A) コンポーネント層 — ネイティブ状態が同期的に反映される委譲・ラウンドトリップ・null 安全性。
    ///       [ExecuteAlways] により AddComponent で OnEnable が走り、AnimeAsset 割当 → Play まで同期実行されるため、
    ///       フレーム駆動 (UpdateManager / yield) なしで決定論的に検証できる。
    ///   (B) 内部プレイヤー層 — 手動 Tick による前進挙動 (Pause トグルで停止 / 再トグルで現フレーム継続 /
    ///       逆再生で減少 / 区間設定の反映)。コンポーネントの Pause 等が委譲する先のセマンティクスを担保する。
    ///
    /// フィクスチャは既存の Ringo を流用する。アニメ長に依存する前提は Assume で守る (短アニメ時は inconclusive)。
    /// </summary>
    public class SpriteStudioPlaybackControlTests
    {
        private const string RingoAssetPath = "Ringo/Ringo"; // Resources.Load 用
        private const string RingoSsabPath = "../../package/Tests/Runtime/Resources/Ringo/Ringo.ssab";

        private byte[] _ringo;
        private readonly List<GameObject> _objects = new List<GameObject>();
        private readonly List<Object> _assets = new List<Object>();

        [UnitySetUp]
        public IEnumerator Setup()
        {
            var asset = Resources.Load<SpriteStudioAnimeAsset>(RingoAssetPath);
            if (asset != null && asset.SsabData != null && asset.SsabData.Length > 0)
            {
                _ringo = asset.SsabData;
            }
            else
            {
                string p = Path.GetFullPath(RingoSsabPath);
                _ringo = File.Exists(p) ? File.ReadAllBytes(p) : null;
            }
            yield return null;
        }

        [TearDown]
        public void TearDown()
        {
            // GameObject を破棄 → OnDisable → _core.Dispose() で native リソースも解放される。
            foreach (var go in _objects) if (go != null) Object.DestroyImmediate(go);
            _objects.Clear();
            foreach (var a in _assets) if (a != null) Object.DestroyImmediate(a);
            _assets.Clear();
        }

        // --- helpers ---

        private SpriteStudioAnimeAsset MakeAsset()
        {
            Assert.IsNotNull(_ringo, "Ringo.ssab が必要です。");
            var asset = ScriptableObject.CreateInstance<SpriteStudioAnimeAsset>();
            asset.SsabData = _ringo;
            _assets.Add(asset);
            return asset;
        }

        // Ringo の "walk" を再生中の Player を返す (アセット割当 → Play まで同期実行される)。
        private SpriteStudioPlayer MakePlayingPlayer(string anim = "walk")
        {
            var go = new GameObject("Tier1PlaybackPlayer");
            _objects.Add(go);
            var player = go.AddComponent<SpriteStudioPlayer>();
            player.AnimeAsset = MakeAsset();
            player.SetAnimation(anim);
            player.Play();
            return player;
        }

        // ============================================================
        // (A) コンポーネント層: 同期的な委譲・ラウンドトリップ
        // ============================================================

        [Test]
        public void Play_MarksPlaying_NotPaused()
        {
            var player = MakePlayingPlayer();
            Assert.IsTrue(player.IsPlaying, "Play 後は再生中のはず");
            Assert.IsFalse(player.IsPaused, "Play 直後は一時停止していないはず");
        }

        [Test]
        public void AfterPlay_TotalFrames_And_FrameRate_ArePositive()
        {
            var player = MakePlayingPlayer();
            Assert.Greater(player.TotalFrames, 0, "総フレーム数は正のはず");
            Assert.Greater(player.FrameRate, 0, "フレームレートは正のはず");
        }

        [Test]
        public void Pause_SetsPaused_ButStillPlaying()
        {
            var player = MakePlayingPlayer();
            player.Pause();
            Assert.IsTrue(player.IsPaused, "Pause 後は一時停止中のはず");
            Assert.IsTrue(player.IsPlaying, "一時停止中も再生セッションは有効 (IsPlaying=true) のはず");
        }

        [Test]
        public void Pause_Toggle_ClearsPaused()
        {
            var player = MakePlayingPlayer();
            player.Pause(); // toggle: playing -> paused
            player.Pause(); // toggle: paused -> playing
            Assert.IsFalse(player.IsPaused, "Pause を再度呼ぶ (トグル) と一時停止が解除されるはず");
            Assert.IsTrue(player.IsPlaying);
        }

        [Test]
        public void Stop_ClearsPlaying()
        {
            var player = MakePlayingPlayer();
            player.Stop();
            Assert.IsFalse(player.IsPlaying, "Stop 後は再生中でないはず");
        }

        [Test]
        public void CurrentFrame_Seek_RoundTrips()
        {
            var player = MakePlayingPlayer();
            player.Pause(); // 進行で値が変わらないように一時停止
            float target = Mathf.Min(2f, Mathf.Max(0f, player.TotalFrames - 1));
            player.CurrentFrame = target;
            Assert.AreEqual(target, player.CurrentFrame, 0.001f, "シーク値が取得できるはず");
        }

        [Test]
        public void Reverse_Property_RoundTrips()
        {
            var player = MakePlayingPlayer();
            player.Reverse = true;
            Assert.IsTrue(player.Reverse, "Reverse=true が反映されるはず");
            player.Reverse = false;
            Assert.IsFalse(player.Reverse, "Reverse=false が反映されるはず");
        }

        [Test]
        public void PingPong_Property_RoundTrips()
        {
            var player = MakePlayingPlayer();
            player.PingPong = true;
            Assert.IsTrue(player.PingPong, "PingPong=true が反映されるはず");
            player.PingPong = false;
            Assert.IsFalse(player.PingPong, "PingPong=false が反映されるはず");
        }

        [Test]
        public void ReverseAndPingPong_AreIndependent()
        {
            // Reverse / PingPong の setter は直交軸 (direction / style) をネイティブから読んで温存する。
            // 片方の設定がもう片方を潰さないことを確認する (Tier 1 で実装したカスタムロジックの回帰テスト)。
            var player = MakePlayingPlayer();
            player.Reverse = true;
            player.PingPong = true;
            Assert.IsTrue(player.Reverse, "PingPong 設定で Reverse が消えないはず");
            Assert.IsTrue(player.PingPong, "Reverse 設定で PingPong が消えないはず");

            player.Reverse = false;
            Assert.IsFalse(player.Reverse);
            Assert.IsTrue(player.PingPong, "Reverse 解除で PingPong が消えないはず");
        }

        [Test]
        public void SetAnimationSection_DoesNotThrow()
        {
            var player = MakePlayingPlayer();
            Assert.DoesNotThrow(() => player.SetAnimationSection(1, 3));
        }

        [Test]
        public void FrameRate_Set_DoesNotThrow()
        {
            var player = MakePlayingPlayer();
            Assert.DoesNotThrow(() => player.FrameRate = 30);
        }

        // ============================================================
        // (A') null 安全性: 内部プレイヤー未生成 / 破棄後でも例外を出さず既定値を返す
        // ============================================================

        [Test]
        public void Controls_OnAssetlessPlayer_ReturnDefaults_AndDoNotThrow()
        {
            var go = new GameObject("Tier1Assetless");
            _objects.Add(go);
            var player = go.AddComponent<SpriteStudioPlayer>(); // AnimeAsset 未設定 → アニメ未 setup

            // 未再生/未ロードでも状態系は破綻しない既定を返す。
            Assert.IsFalse(player.IsPlaying);
            Assert.IsFalse(player.IsPaused);
            Assert.AreEqual(0, player.FrameRate);
            Assert.AreEqual(0f, player.CurrentFrame, 0.001f);
            Assert.IsFalse(player.Reverse);
            Assert.IsFalse(player.PingPong);
            // TotalFrames は未ロード時にネイティブ既定 (end_frame=0 → 1) になり得るため値は問わず、
            // getter/setter が例外を出さないことだけ確認する。

            Assert.DoesNotThrow(() =>
            {
                _ = player.TotalFrames;
                player.Pause(); player.Pause(); player.Stop();
                player.CurrentFrame = 5f; player.FrameRate = 30;
                player.Reverse = true; player.PingPong = true;
                player.SetAnimationSection(0, 1);
            });
        }

        [Test]
        public void Controls_AfterDisable_DoNotThrow_AndReturnDefaults()
        {
            var player = MakePlayingPlayer();
            player.gameObject.SetActive(false); // OnDisable → _core.Dispose() → Internal=null

            Assert.IsFalse(player.IsPlaying, "破棄後は再生中でないはず");
            Assert.AreEqual(0, player.TotalFrames);

            Assert.DoesNotThrow(() =>
            {
                player.Pause(); player.Pause(); player.Stop();
                _ = player.CurrentFrame; player.CurrentFrame = 1f;
                _ = player.Reverse; player.Reverse = true;
                player.SetAnimationSection(0, 1);
            });
        }

        // ============================================================
        // (A'') Graphic (uGUI) 版も同じ API を持つことのスモークテスト
        // ============================================================

        [Test]
        public void Graphic_PlayPauseStop_StateRoundTrips()
        {
            var go = new GameObject("Tier1PlaybackGraphic");
            _objects.Add(go);
            go.AddComponent<Canvas>();
            go.AddComponent<CanvasRenderer>();
            var graphic = go.AddComponent<SpriteStudioGraphic>();
            graphic.AnimeAsset = MakeAsset();
            graphic.SetAnimation("walk");
            graphic.Play();

            Assert.IsTrue(graphic.IsPlaying, "Play 後は再生中のはず");
            Assert.Greater(graphic.TotalFrames, 0);

            graphic.Pause();
            Assert.IsTrue(graphic.IsPaused);

            graphic.Stop();
            Assert.IsFalse(graphic.IsPlaying);

            graphic.Reverse = true;
            Assert.IsTrue(graphic.Reverse);
        }

        // ============================================================
        // (B) 内部プレイヤー層: 手動 Tick による前進挙動 (決定論的)
        // ============================================================

        [Test]
        public void Internal_Pause_Halts_And_Resume_ContinuesFromCurrentFrame()
        {
            Assert.IsNotNull(_ringo, "Ringo.ssab が必要です。");
            using (var player = new SpriteStudioInternalPlayer())
            {
                player.LoadSsab(_ringo);
                Assert.IsTrue(player.SetupAnimation("walk"), "walk アニメが見つかるはず");
                player.SetLoop(-1);          // 範囲端での停止/巻き戻しを避け、単調増加で検証する
                player.Play();
                player.SetSkipFrames(false); // 1 Tick = 1 frame (フレーム数で決定論的に進める)

                int total = player.GetTotalFrames();
                int step = Mathf.Max(2, total / 5);
                Assume.That(total, Is.GreaterThan(step * 2), "walk が短すぎる (このテストには step*2 未満で 1 周しない長さが必要)");

                for (int i = 0; i < step; i++) player.Tick(1f / 60f, false);
                float f0 = player.GetFrame();
                Assert.Greater(f0, 0f, "再生でフレームが進むはず");

                // 一時停止中は進まない (ネイティブ pause() は is_pausing のトグル)
                player.Pause();
                for (int i = 0; i < step; i++) player.Tick(1f / 60f, false);
                Assert.AreEqual(f0, player.GetFrame(), 0.5f, "一時停止中はフレームが進まないはず");

                // 再開: play() は先頭へ巻き戻すため使えない。pause() を再トグルすると現在フレームを
                // 保持したまま再開する (コンポーネントの Pause() トグルが委譲する経路と同じ)。
                player.Pause(); // toggle: paused -> playing
                for (int i = 0; i < step; i++) player.Tick(1f / 60f, false);
                Assert.Greater(player.GetFrame(), f0, "再開 (pause トグル) は現フレームから継続するはず (先頭に戻らない)");
            }
        }

        [Test]
        public void Internal_Reverse_DecreasesFrame()
        {
            Assert.IsNotNull(_ringo, "Ringo.ssab が必要です。");
            using (var player = new SpriteStudioInternalPlayer())
            {
                player.LoadSsab(_ringo);
                Assert.IsTrue(player.SetupAnimation("walk"));
                player.SetLoop(-1);
                player.Play();
                player.SetSkipFrames(false);

                int total = player.GetTotalFrames();
                int mid = Mathf.Max(3, total / 2);
                Assume.That(total, Is.GreaterThan(mid), "walk が短すぎる (mid より長い長さが必要)");
                player.SetFrame(mid);

                player.SetPlaybackStyle(1, 0); // 1=Backward, 0=Normal
                float before = player.GetFrame();
                for (int i = 0; i < 2; i++) player.Tick(1f / 60f, false);
                Assert.Less(player.GetFrame(), before, "逆再生中はフレームが減少するはず");
            }
        }

        [Test]
        public void Internal_SetAnimationSection_UpdatesBounds()
        {
            Assert.IsNotNull(_ringo, "Ringo.ssab が必要です。");
            using (var player = new SpriteStudioInternalPlayer())
            {
                player.LoadSsab(_ringo);
                Assert.IsTrue(player.SetupAnimation("walk"));

                int total = player.GetTotalFrames();
                int end = Mathf.Min(3, total - 1);
                Assume.That(end, Is.GreaterThan(1), "walk が短すぎる (区間検証には 4 フレーム以上必要)");

                player.SetAnimationSection(1, end);
                Assert.AreEqual(1, player.GetAnimationSectionStart(), "区間開始が反映されるはず");
                Assert.AreEqual(end, player.GetAnimationSectionEnd(), "区間終了が反映されるはず");
            }
        }
    }
}
