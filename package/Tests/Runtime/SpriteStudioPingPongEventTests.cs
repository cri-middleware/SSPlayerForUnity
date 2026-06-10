using System.IO;
using NUnit.Framework;
using SpriteStudio.Runtime;
using UnityEngine;

namespace SpriteStudio.Runtime.Tests
{
    /// <summary>
    /// ピンポン / 逆再生時のイベント挙動を「観察」するテスト。プレイヤー層 (PlaybackCore.Play) は
    /// SetPlaybackStyle(0,0)=Forward/Normal 固定でピンポンを選べないため、ここでは InternalPlayer を
    /// 直接駆動してピンポンにし、finished / looped / userdata が往路・復路でどう発火するかをログで確認する。
    ///
    /// 期待 (確認したい仮説):
    ///   - finished : 復路で終端 (開始フレーム側) に戻って停止した瞬間に 1 回。往路の終端では出ない。
    ///   - looped   : 1 往復 = 1 パルス (折り返しの数え方は SDK 依存。ここで観察する)。
    ///   - userdata : 理想は往路・復路の両方で拾える。SDK の passed_event が逆走を報告するか要観察。
    /// 観察結果次第で、イベントに往路/復路フラグ (bool) を持たせるか判断する。
    /// </summary>
    public class SpriteStudioPingPongEventTests
    {
        // UserData.ssab has user-data keyframes (unlike Ringo's "walk"), so the ping-pong forward /
        // return legs can actually be observed via OnUserData. Imported in the harness at
        // Assets/ssab_generated/overall/userdata.ssab; read the raw ssab bytes directly.
        private byte[] _userData;
        private string _userDataPath;

        [SetUp]
        public void Setup()
        {
            _userDataPath = Path.Combine(Application.dataPath, "ssab_generated/overall/userdata.ssab");
            _userData = File.Exists(_userDataPath) ? File.ReadAllBytes(_userDataPath) : null;
        }

        [Test]
        public void PingPong_ObserveEventSequence()
        {
            Assert.IsNotNull(_userData, $"userdata.ssab が必要です: {_userDataPath}");
            using (var player = new SpriteStudioInternalPlayer())
            {
                player.LoadSsab(_userData);

                int finished = 0, looped = 0, userData = 0;
                player.OnAnimationFinished += () =>
                {
                    finished++;
                    Debug.Log($"[PP] FINISHED  frame={player.GetFrame():0.0} dir={player.GetPlaybackDirection()}");
                };
                player.OnAnimationLooped += () =>
                {
                    looped++;
                    Debug.Log($"[PP] LOOPED    frame={player.GetFrame():0.0} dir={player.GetPlaybackDirection()}");
                };
                player.OnUserData += d =>
                {
                    userData++;
                    Debug.Log($"[PP] USERDATA  part={d.PartName} reverse={d.IsReverse} frame={player.GetFrame():0.0} dir={player.GetPlaybackDirection()}");
                };

                // userdata.ssab の先頭アニメを使う (名前固定にしない)。
                string anim = player.GetAnimationName(0);
                Assert.IsTrue(player.SetupAnimation(anim), $"先頭アニメ '{anim}' が見つかるはず");

                // setup_animation は再生パラメータ (loop/style/speed) を既定へ戻すので、その後で設定する。
                // direction: 0=Forward / 非0=Backward, style: 0=Normal / 非0=PingPong。
                player.SetPlaybackStyle(0, 1); // Forward 始動・PingPong
                player.SetLoop(2);             // 観察用に有限ループ (無限だと止まらない)
                player.Play();

                // 駆動しながら、フレームの増減でレッグ (往路/復路) の折り返しを検出してログする。
                // GetPlaybackDirection() が設定値 (基準方向) のままでレッグ毎に反転しない可能性があるので、
                // 実効レッグはフレーム差分で判定するのが確実 (= 将来の往路/復路フラグもこれで作れる)。
                float prev = player.GetFrame();
                bool returning = false;
                int guard = 0;
                while (player.IsPlaying() && guard++ < 10000)
                {
                    player.Tick(1f / 60f, false);
                    float now = player.GetFrame();
                    if (now < prev - 0.001f && !returning)
                    {
                        returning = true;
                        Debug.Log($"[PP] TURN forward->return at frame={now:0.0}");
                    }
                    else if (now > prev + 0.001f && returning)
                    {
                        returning = false;
                        Debug.Log($"[PP] TURN return->forward at frame={now:0.0}");
                    }
                    prev = now;
                }

                Debug.Log($"[PP] summary: finished={finished} looped={looped} userData={userData} playing={player.IsPlaying()} guard={guard}");

                // 観察が主目的だが、最低限のサニティ: 有限ピンポンは最終的に停止し、finished が 1 回出るはず。
                Assert.IsFalse(player.IsPlaying(), "有限ループのピンポンは最終的に停止するはず");
                Assert.AreEqual(1, finished, "finished は停止時に 1 度だけのはず (出方が違えば仕様を見直す)");
            }
        }
    }
}
