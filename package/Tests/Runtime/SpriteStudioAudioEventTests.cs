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
        private const string OverallSspjPath = "../../SpriteStudio-SDK/tests/overall/overall.sspj";

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

        /// <summary>
        /// AnimeAsset.TryGetSoundInfo() が、実際に発火した音のハッシュキーで ssab の音メタ
        /// (alias / file_path / 再生長) を引けることを確認する。発火ペイロード (アドレスのみ) に
        /// 乗っていない file_path 等を補う opt-in アクセサの往復検証。未知ハッシュは false。
        /// </summary>
        [Test]
        public void TryGetSoundInfo_ResolvesMetadata_ForFiredSound()
        {
            Assert.IsNotNull(_sound, $"Sound.ssab が必要です: {Path.GetFullPath(SoundResSsabPath)}");

            // 実際に発火した音のハッシュを 1 件採取する (ssab に確実に存在するキー)。
            SpriteStudioAudio captured = default;
            bool got = false;
            using (var player = new SpriteStudioInternalPlayer())
            {
                player.LoadSsab(_sound);
                player.OnAudio += a => { if (!got) { captured = a; got = true; } };
                string anim = player.GetAnimationName(0);
                Assert.IsTrue(player.SetupAnimation(anim), $"先頭アニメ '{anim}' が見つかるはず");
                player.SetLoop(1);
                player.Play();
                int guard = 0;
                while (player.IsPlaying() && guard++ < 10000) player.Tick(1f / 60f, false);
            }
            Assert.IsTrue(got, "音声イベントが 1 件は採取できるはず");

            // 同じ ssab を載せた AnimeAsset から、その音のメタを引けるはず。
            var asset = ScriptableObject.CreateInstance<SpriteStudioAnimeAsset>();
            try
            {
                asset.SsabData = _sound;

                bool ok = asset.TryGetSoundInfo(captured.SoundListNameHash, captured.SoundNameHash, out var info);
                Assert.IsTrue(ok, "発火した音のハッシュは ssab の SoundList に存在するはず");
                Assert.IsFalse(string.IsNullOrEmpty(info.FilePath), "file_path が取得できるはず");
                Assert.IsFalse(string.IsNullOrEmpty(info.Alias), "alias が取得できるはず");
                // 発火ペイロードの SoundName は診断用 (オプション) なので、存在する場合のみ一致を検証。
                if (!string.IsNullOrEmpty(captured.SoundName))
                    Assert.AreEqual(captured.SoundName, info.Alias, "alias は発火ペイロードの SoundName と一致するはず");
                Debug.Log($"[AUDIO] TryGetSoundInfo: alias={info.Alias} file={info.FilePath} ms={info.TimeTotalMs}");

                // 未知ハッシュは false を返し、out は default。
                Assert.IsFalse(asset.TryGetSoundInfo(0xDEADBEEFu, 0xCAFEBABEu, out var none),
                    "未知ハッシュは false を返すはず");
                Assert.IsNull(none.FilePath);
            }
            finally
            {
                Object.DestroyImmediate(asset);
            }
        }
    }
}
