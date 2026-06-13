using System.Collections;
using System.IO;
using NUnit.Framework;
using SpriteStudio.Runtime;
using UnityEngine;
using UnityEngine.TestTools;

namespace SpriteStudio.Runtime.Tests
{
    /// <summary>
    /// Observe that OnAudio fires while an animation with audio (ssaudio) events plays. Audio events
    /// flow through the same passed-event path as UserData / Signal, so this mainly confirms the C#
    /// dispatch side -- OnAudio is the only event kind whose firing had not yet been verified.
    ///
    /// フィクスチャ "Sound" は SDK の overall.sspj 内のパック (tests/overall/Sound.ssae)。.ssab はコミット
    /// されない (生成物) ため、RingoPlaybackTests と同方式で [UnitySetUp] がオンデマンド変換する:
    ///   1) Resources にビルド済み (Player ビルド/キャッシュ済み Editor) ならそれを使う。
    ///   2) Resources 配下に生成済みファイルがあれば直接読む。
    ///   3) Editor のみ: overall.sspj を Assets 外の一時ディレクトリへ変換し、生成された Sound.ssab "だけ" を
    ///      package/Tests/Runtime/Resources/Sound/ へ取り込む (Player ビルドにバンドルさせるため)。
    /// 本テストは SsabData のバイトしか使わない (音声イベントにテクスチャは不要) ため、取り込み時に
    /// テクスチャ解決の警告が出ても SsabData は設定され、再生・イベント検証には影響しない。
    /// </summary>
    public class SpriteStudioAudioEventTests
    {
        private const string SoundResourcePath = "Sound/Sound"; // Resources.Load 用
        private const string SoundResSsabPath = "../../package/Tests/Runtime/Resources/Sound/Sound.ssab";
        private const string OverallSspjPath = "../../SpriteStudio7-SDK/tests/overall/overall.sspj";

        private byte[] _sound;

        [UnitySetUp]
        public IEnumerator Setup()
        {
            // 1) Resources にビルド済みならそれを使う。
            var asset = Resources.Load<SpriteStudioAnimeAsset>(SoundResourcePath);
            if (asset != null && asset.SsabData != null && asset.SsabData.Length > 0)
            {
                _sound = asset.SsabData;
                yield break;
            }

            // 2) Resources 配下に生成済みの .ssab があれば直接読む。
            string resSsab = Path.GetFullPath(SoundResSsabPath);
            if (File.Exists(resSsab))
            {
                _sound = File.ReadAllBytes(resSsab);
                yield break;
            }

#if UNITY_EDITOR
            // 3) Editor: overall.sspj をオンデマンド変換し、生成された Sound.ssab だけを Resources へ取り込む。
            //    変換先は Assets 外の一時ディレクトリ (他 21 パックは Unity に取り込ませない)。Poll は
            //    EditorApplication.update を待たずリフレクションで回す (RingoPlaybackTests と同方式)。
            string sspj = Path.GetFullPath(OverallSspjPath);
            Assert.IsTrue(File.Exists(sspj), $"overall.sspj が見つからない: {sspj}");

            string tmpDir = Path.Combine(Application.temporaryCachePath, "ss_sound_fixture");
            Directory.CreateDirectory(tmpDir);
            string tmpSound = Path.Combine(tmpDir, "Sound.ssab");

            var runnerType = System.Type.GetType("SpriteStudio.Editor.SpriteStudioConversionRunner, SpriteStudio.Editor");
            var enqueue = runnerType?.GetMethod("Enqueue", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            enqueue?.Invoke(null, new object[] { sspj, tmpDir });

            var poll = runnerType?.GetMethod("Poll", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            float timeout = 60f; // overall.sspj は多パックで変換に時間がかかる
            while (!File.Exists(tmpSound) && timeout > 0f)
            {
                poll?.Invoke(null, null);
                yield return null;
                timeout -= Time.deltaTime;
            }

            if (File.Exists(tmpSound))
            {
                // Player ビルド用に Sound.ssab だけを Resources へ取り込む。
                Directory.CreateDirectory(Path.GetDirectoryName(resSsab));
                File.Copy(tmpSound, resSsab, overwrite: true);

                var assetDatabaseType = System.Type.GetType("UnityEditor.AssetDatabase, UnityEditor");
                assetDatabaseType?.GetMethod("Refresh", new System.Type[0])?.Invoke(null, null);

                // 今セッションはバイト直読みで確実に進める (import の完了を待たない)。
                _sound = File.ReadAllBytes(resSsab);
            }
#endif
            yield return null;
        }

        [Test]
        public void OnAudio_Fires_WhilePlayingAudioAnimation()
        {
            Assert.IsNotNull(_sound, $"Sound.ssab が必要です (overall.sspj から生成): {Path.GetFullPath(SoundResSsabPath)}");
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
