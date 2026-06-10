using System.IO;
using NUnit.Framework;
using SpriteStudio.Runtime;
using UnityEngine;

namespace SpriteStudio.Runtime.Tests
{
    /// <summary>
    /// Observe that OnAudio fires while an animation with audio (ssaudio) events plays. Audio events
    /// flow through the same passed-event path as UserData / Signal, so this mainly confirms the C#
    /// dispatch side -- OnAudio is the only event kind whose firing had not yet been verified.
    /// Uses the SDK's Sound fixture (tests/overall/Sound.ssae) converted into the harness at
    /// Assets/ssab_generated/overall/Sound.ssab; the raw ssab bytes are read directly.
    /// </summary>
    public class SpriteStudioAudioEventTests
    {
        private byte[] _sound;
        private string _soundPath;

        [SetUp]
        public void Setup()
        {
            _soundPath = Path.Combine(Application.dataPath, "ssab_generated/overall/Sound.ssab");
            _sound = File.Exists(_soundPath) ? File.ReadAllBytes(_soundPath) : null;
        }

        [Test]
        public void OnAudio_Fires_WhilePlayingAudioAnimation()
        {
            Assert.IsNotNull(_sound, $"Sound.ssab が必要です: {_soundPath}");
            using (var player = new SpriteStudioInternalPlayer())
            {
                player.LoadSsab(_sound);

                int audio = 0;
                player.OnAudio += a =>
                {
                    audio++;
                    Debug.Log($"[AUDIO] sound={a.SoundName} part={a.PartName} loop={a.LoopNum} frame={player.GetFrame():0.0}");
                };

                // 先頭アニメを使う (名前固定にしない)。
                string anim = player.GetAnimationName(0);
                Assert.IsTrue(player.SetupAnimation(anim), $"先頭アニメ '{anim}' が見つかるはず");
                player.SetLoop(1); // 1 回だけ再生して全フレームを通す
                player.Play();

                int guard = 0;
                while (player.IsPlaying() && guard++ < 10000) player.Tick(1f / 60f, false);

                Debug.Log($"[AUDIO] summary: audio={audio} animation={anim} guard={guard}");
                Assert.Greater(audio, 0, "音声イベント付きアニメの 1 回再生で OnAudio が発火するはず");
            }
        }
    }
}
