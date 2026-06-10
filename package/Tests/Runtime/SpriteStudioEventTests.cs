using System.Collections;
using System.IO;
using NUnit.Framework;
using SpriteStudio.Runtime;
using UnityEngine;
using UnityEngine.TestTools;

namespace SpriteStudio.Runtime.Tests
{
    /// <summary>
    /// SSAB イベントポーリングのライフサイクル (finished / looped) を検証する。既存の Ringo を使い、
    /// 特に OnAnimationFinished が「再生終了の瞬間にちょうど 1 回だけ」発火することを確認する
    /// (is_end_frame_reached の貼り付き / 着地+終端の二重発火に対するエッジ検出の回帰テスト)。
    /// </summary>
    public class SpriteStudioEventTests
    {
        private const string RingoAssetPath = "Ringo/Ringo"; // Resources.Load 用
        private const string RingoSsabPath = "../../package/Tests/Runtime/Resources/Ringo/Ringo.ssab";

        private byte[] _ringo;

        [UnitySetUp]
        public IEnumerator Setup()
        {
            // Ringo はコンバート済みが Resources にある。なければファイルから直接読む。
            var ringoAsset = Resources.Load<SpriteStudioAnimeAsset>(RingoAssetPath);
            if (ringoAsset != null && ringoAsset.SsabData != null && ringoAsset.SsabData.Length > 0)
            {
                _ringo = ringoAsset.SsabData;
            }
            else
            {
                string p = Path.GetFullPath(RingoSsabPath);
                _ringo = File.Exists(p) ? File.ReadAllBytes(p) : null;
            }
            yield return null;
        }

        /// <summary>再生が停止する (= is_playing が false になる) まで Tick で進める。</summary>
        private static void DriveToEnd(SpriteStudioInternalPlayer player, int maxTicks = 5000)
        {
            int guard = 0;
            while (player.IsPlaying() && guard++ < maxTicks) player.Tick(1f / 60f, false);
        }

        [Test]
        public void OnAnimationFinished_FiresExactlyOnce_WhenPlayOnceCompletes()
        {
            Assert.IsNotNull(_ringo, "Ringo.ssab が必要です。");
            using (var player = new SpriteStudioInternalPlayer())
            {
                player.LoadSsab(_ringo);
                int finished = 0;
                player.OnAnimationFinished += () => finished++;

                Assert.IsTrue(player.SetupAnimation("walk"), "walk アニメが見つかるはず");
                player.SetLoop(1);   // 1 回だけ再生
                player.Play();

                DriveToEnd(player);
                Assert.IsFalse(player.IsPlaying(), "1 回再生は完了して停止するはず");

                // 停止後にさらに進めても再発火しないこと (エッジ検出の回帰テスト。検出が無いと
                // is_end_frame_reached が貼り付いて毎フレーム発火し、また 1 回再生でも着地+終端の 2 回発火する)。
                for (int i = 0; i < 180; i++) player.Tick(1f / 60f, false);

                Assert.AreEqual(1, finished, "OnAnimationFinished は完了時に 1 度だけ発火するはず");
            }
        }

        [Test]
        public void OnAnimationFinished_DoesNotFire_DuringInfiniteLoop()
        {
            Assert.IsNotNull(_ringo, "Ringo.ssab が必要です。");
            using (var player = new SpriteStudioInternalPlayer())
            {
                player.LoadSsab(_ringo);
                int finished = 0;
                player.OnAnimationFinished += () => finished++;

                Assert.IsTrue(player.SetupAnimation("walk"));
                player.SetLoop(-1);  // 無限ループ
                player.Play();
                for (int i = 0; i < 600; i++) player.Tick(1f / 60f, false);

                Assert.AreEqual(0, finished, "無限ループでは OnAnimationFinished は発火しないはず");
                Assert.IsTrue(player.IsPlaying(), "無限ループは再生し続けるはず");
            }
        }

        [Test]
        public void OnAnimationLooped_Fires_DuringInfiniteLoop()
        {
            Assert.IsNotNull(_ringo, "Ringo.ssab が必要です。");
            using (var player = new SpriteStudioInternalPlayer())
            {
                player.LoadSsab(_ringo);
                int looped = 0;
                player.OnAnimationLooped += () => looped++;

                Assert.IsTrue(player.SetupAnimation("walk"));
                player.SetLoop(-1);
                player.Play();
                int guard = 0;
                while (looped < 1 && guard++ < 5000) player.Tick(1f / 60f, false);

                Assert.GreaterOrEqual(looped, 1, "ループ境界を跨いだら OnAnimationLooped が発火するはず");
            }
        }

        [Test]
        public void Events_DoNotFire_WhenNeverPlayed()
        {
            Assert.IsNotNull(_ringo, "Ringo.ssab が必要です。");
            using (var player = new SpriteStudioInternalPlayer())
            {
                player.LoadSsab(_ringo);
                int finished = 0, looped = 0;
                player.OnAnimationFinished += () => finished++;
                player.OnAnimationLooped += () => looped++;

                Assert.IsTrue(player.SetupAnimation("walk"));
                // Play() を呼ばない (is_playing=false のまま)。
                for (int i = 0; i < 120; i++) player.Tick(1f / 60f, false);

                Assert.AreEqual(0, finished, "未再生では OnAnimationFinished は発火しないはず");
                Assert.AreEqual(0, looped, "未再生では OnAnimationLooped は発火しないはず");
            }
        }
    }
}
