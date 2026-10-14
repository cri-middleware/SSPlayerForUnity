using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using SpriteStudio.Runtime;
using UnityEngine;
using UnityEngine.TestTools;

namespace SpriteStudio.Runtime.Tests
{
    /// <summary>
    /// What an event handler may do to the player that called it, in the middle of that player's event pass:
    /// set up another animation, swap the pack, or disable its own GameObject.
    ///
    /// Two fixtures from the SDK's overall project:
    ///   - UserData (tests/overall/UserData.ssae) with a second animation added: "anime_2" is a copy of
    ///     "anime_1" whose two USER keys (frames 0 and 10) carry other integers. Every shipped pack with
    ///     events has one animation, and the same-index event of another animation is what a lookup made
    ///     after a switch reads.
    ///   - Signal (tests/overall/Signal.ssae) as shipped: its key at frame 10 holds two commands, which
    ///     arrive as two signals of one event, so a handler runs again after the first one disabled it.
    /// [UnitySetUp] builds them the way AudioEventTests builds Sound:
    ///   1) Resources (a Player build, or an Editor that already has them).
    ///   2) The generated .ssab files under Resources, read directly.
    ///   3) Editor only: write the two packs and a project listing only them into a temp directory, convert
    ///      that, and copy the .ssab files into package/Tests/Runtime/Resources/EventDispatch/.
    /// </summary>
    public class SpriteStudioEventDispatchTests
    {
        private const string FixtureResourceDir = "EventDispatch"; // for Resources.Load
        private const string FixtureResDir = "../../package/Tests/Runtime/Resources/EventDispatch";
        private const string OverallDir = "../../SpriteStudio-SDK/tests/overall";
        private static readonly string[] FixturePacks = { "UserData", "Signal" };

        // The USER integers of the two animations, keyed at frames 0 and 10.
        private const int First0 = 123456789, First10 = 987654321;
        private const int Second0 = 111, Second10 = 222;

        private byte[] _userData;
        private byte[] _signal;
        private readonly List<GameObject> _objects = new List<GameObject>();
        private readonly List<Object> _assets = new List<Object>();

        [UnitySetUp]
        public IEnumerator Setup()
        {
            _userData = LoadFixture("UserData");
            _signal = LoadFixture("Signal");
            if (_userData != null && _signal != null) yield break;

#if UNITY_EDITOR
            string overall = Path.GetFullPath(OverallDir);
            Assert.IsTrue(Directory.Exists(overall), $"SDK test packs not found: {overall}");

            string tmpDir = Path.Combine(Application.temporaryCachePath, "ss_event_dispatch_fixture");
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
            string srcDir = Path.Combine(tmpDir, "src");
            string outDir = Path.Combine(tmpDir, "out");
            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(outDir);

            string sspj = Path.Combine(srcDir, "eventdispatch.sspj");
            File.WriteAllText(sspj, ListOnlyFixturePacks(File.ReadAllText(Path.Combine(overall, "overall.sspj"))));
            File.WriteAllText(Path.Combine(srcDir, "UserData.ssae"), AddSecondAnimation(File.ReadAllText(Path.Combine(overall, "UserData.ssae"))));
            foreach (var file in new[] { "Signal.ssae", "common.ssce", "common.png", "box_00_00.ssce", "box_00_00.png" })
                File.Copy(Path.Combine(overall, file), Path.Combine(srcDir, file));

            var runnerType = System.Type.GetType("SpriteStudio.Editor.SpriteStudioConversionRunner, SpriteStudio.Editor");
            var enqueue = runnerType?.GetMethod("Enqueue", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            enqueue?.Invoke(null, new object[] { sspj, outDir });

            var poll = runnerType?.GetMethod("Poll", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            float timeout = 30f;
            while (!AllConverted(outDir) && timeout > 0f)
            {
                poll?.Invoke(null, null);
                yield return null;
                timeout -= Time.deltaTime;
            }

            if (AllConverted(outDir))
            {
                // Into Resources for the Player build; this session reads the bytes directly.
                string resDir = Path.GetFullPath(FixtureResDir);
                Directory.CreateDirectory(resDir);
                foreach (var pack in FixturePacks)
                    File.Copy(Path.Combine(outDir, pack + ".ssab"), Path.Combine(resDir, pack + ".ssab"), overwrite: true);
                var assetDatabaseType = System.Type.GetType("UnityEditor.AssetDatabase, UnityEditor");
                assetDatabaseType?.GetMethod("Refresh", new System.Type[0])?.Invoke(null, null);
                _userData = LoadFixture("UserData");
                _signal = LoadFixture("Signal");
            }
#endif
            yield return null;
        }

        private static byte[] LoadFixture(string pack)
        {
            var asset = Resources.Load<SpriteStudioAnimeAsset>(FixtureResourceDir + "/" + pack);
            if (asset != null && asset.SsabData != null && asset.SsabData.Length > 0) return asset.SsabData;
            string path = Path.GetFullPath(Path.Combine(FixtureResDir, pack + ".ssab"));
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _objects) if (go != null) Object.DestroyImmediate(go);
            _objects.Clear();
            foreach (var a in _assets) if (a != null) Object.DestroyImmediate(a);
            _assets.Clear();
        }

#if UNITY_EDITOR
        private static bool AllConverted(string outDir)
        {
            foreach (var pack in FixturePacks)
                if (!File.Exists(Path.Combine(outDir, pack + ".ssab"))) return false;
            return true;
        }

        // overall.sspj with every list cut down to the two packs and the cellmaps they draw from.
        private static string ListOnlyFixturePacks(string sspj)
        {
            sspj = ReplaceElement(sspj, "cellmapNames", "<cellmapNames><value>common.ssce</value><value>box_00_00.ssce</value></cellmapNames>");
            sspj = ReplaceElement(sspj, "animepackNames", "<animepackNames><value>UserData.ssae</value><value>Signal.ssae</value></animepackNames>");
            foreach (var tag in new[] { "charmapNames", "effectFileNames", "sequencepackNames", "audiopackNames", "ExternalTextures", "expandAnimation" })
                sspj = ReplaceElement(sspj, tag, "");
            return sspj;
        }

        private static string ReplaceElement(string xml, string tag, string replacement)
        {
            var element = new Regex($@"<{tag}>.*?</{tag}>|<{tag} />", RegexOptions.Singleline);
            Assert.IsTrue(element.IsMatch(xml), $"overall.sspj has no <{tag}>");
            return element.Replace(xml, replacement, 1);
        }

        // The pack with a copy of anime_1 after it, renamed anime_2 and carrying the other integers.
        private static string AddSecondAnimation(string ssae)
        {
            var first = Regex.Match(ssae, @"<anime>\s*<name>anime_1</name>.*?</anime>", RegexOptions.Singleline);
            Assert.IsTrue(first.Success, "UserData.ssae has no anime_1");
            Assert.IsTrue(first.Value.Contains(First0.ToString()) && first.Value.Contains(First10.ToString()),
                "anime_1's USER keys are not the ones this test expects");
            string second = first.Value
                .Replace("<name>anime_1</name>", "<name>anime_2</name>")
                .Replace(First0.ToString(), Second0.ToString())
                .Replace(First10.ToString(), Second10.ToString());
            return ssae.Insert(first.Index + first.Length, "\n\t\t" + second);
        }
#endif

        private void RequireFixtures()
        {
            Assert.IsNotNull(_userData, $"The UserData fixture is required (generated from the SDK's UserData.ssae): {Path.GetFullPath(FixtureResDir)}");
            Assert.IsNotNull(_signal, $"The Signal fixture is required (generated from the SDK's Signal.ssae): {Path.GetFullPath(FixtureResDir)}");
        }

        // One update long enough to pass both keys of anime_1 and finish it, so the finish and the events
        // come out of the same pass.
        private const float WholeAnimation = 1f;

        [Test]
        public void EventsOfTheFinishingUpdate_KeepTheirAnimation_WhenTheFinishHandlerSetsUpAnother()
        {
            RequireFixtures();
            using (var player = new SpriteStudioInternalPlayer())
            {
                player.LoadSsab(_userData);
                Assert.IsTrue(player.SetupAnimation("anime_1"));
                player.SetLoop(1);
                player.Play();

                bool switched = false, setUp = false;
                var afterSwitch = new List<int>();
                player.OnAnimationFinished += () => { switched = true; setUp = player.SetupAnimation("anime_2"); };
                player.OnUserData += u => { if (switched) afterSwitch.Add(u.IntValue); };

                player.Tick(WholeAnimation, false);

                Assert.IsTrue(switched, "anime_1 should finish within the update");
                Assert.IsTrue(setUp, "anime_2 should be found");
                // The update that finished anime_1 also passed its keys, and they fire after the finish.
                CollectionAssert.AreEqual(new[] { First0, First10 }, afterSwitch,
                    "The events anime_1 passed should be read from anime_1, not from the animation the handler set up");
            }
        }

        [Test]
        public void TheNextAnimationsEvents_ComeFromIt_AfterASwitchInTheFinishHandler()
        {
            RequireFixtures();
            using (var player = new SpriteStudioInternalPlayer())
            {
                player.LoadSsab(_userData);
                Assert.IsTrue(player.SetupAnimation("anime_1"));
                player.SetLoop(1);
                player.Play();

                bool switched = false;
                player.OnAnimationFinished += () =>
                {
                    if (switched) return;
                    switched = true;
                    player.SetupAnimation("anime_2");
                    player.SetLoop(1);
                    player.Play();
                };
                player.Tick(WholeAnimation, false);
                Assert.IsTrue(switched);

                var values = new List<int>();
                player.OnUserData += u => values.Add(u.IntValue);
                player.Tick(WholeAnimation, false);

                CollectionAssert.AreEqual(new[] { Second0, Second10 }, values, "anime_2 plays its own keys");
            }
        }

        [Test]
        public void APlayerDisposedByItsHandler_FiresNothingMore()
        {
            RequireFixtures();
            var player = new SpriteStudioInternalPlayer();
            player.LoadSsab(_userData);
            Assert.IsTrue(player.SetupAnimation("anime_1"));
            player.SetLoop(1);
            player.Play();

            var values = new List<int>();
            player.OnUserData += u => { values.Add(u.IntValue); player.Dispose(); };

            Assert.DoesNotThrow(() => player.Tick(WholeAnimation, false));
            CollectionAssert.AreEqual(new[] { First0 }, values, "The pass ends with the handler that disposed the player");
            player.Dispose();
        }

        [Test]
        public void APackSwappedByItsHandler_EndsThePass()
        {
            RequireFixtures();
            using (var player = new SpriteStudioInternalPlayer())
            {
                player.LoadSsab(_userData);
                Assert.IsTrue(player.SetupAnimation("anime_1"));
                player.SetLoop(1);
                player.Play();

                var values = new List<int>();
                player.OnUserData += u =>
                {
                    values.Add(u.IntValue);
                    if (values.Count == 1) player.LoadSsab(_userData);
                };

                Assert.DoesNotThrow(() => player.Tick(WholeAnimation, false));
                // The second event was decoded against the pack the handler replaced.
                CollectionAssert.AreEqual(new[] { First0 }, values);
            }
        }

        [UnityTest]
        public IEnumerator AHandlerThatDisablesItsOwnPlayer_LeavesTheOtherPlayersRunning()
        {
            if (!Application.isPlaying) Assert.Ignore("The update manager drives players in Play mode only.");
            RequireFixtures();
            var asset = ScriptableObject.CreateInstance<SpriteStudioAnimeAsset>();
            asset.SsabData = _signal;
            _assets.Add(asset);

            // Registered first, so it is prepared before the other one in the update manager's pass.
            var selfDisabling = MakePlayer("SelfDisabling", asset);
            var other = MakePlayer("Other", asset);

            int fired = 0;
            selfDisabling.OnSignal += _ =>
            {
                fired++;
                selfDisabling.gameObject.SetActive(false);
            };
            int otherFired = 0;
            other.OnSignal += _ => otherFired++;

            // Frame 10 holds two commands: the first disables the player, and the second would then be
            // dispatched by a disposed one. An exception escaping the pass fails the test as a logged
            // exception, and stops the other player for that frame.
            float waited = 0f;
            for (int frame = 0; fired == 0 && waited < 5f && frame < 10000; frame++)
            {
                yield return null;
                waited += Time.deltaTime;
            }
            yield return null;

            Assert.AreEqual(1, fired, "The disabled player fires nothing after its handler disabled it");
            Assert.IsFalse(selfDisabling.gameObject.activeSelf);
            Assert.AreEqual(2, otherFired, "The other player still fires both commands of that frame");
            Assert.Greater(other.FrameNo, 10f, "The other player still advances");
        }

        private SpriteStudioPlayer MakePlayer(string name, SpriteStudioAnimeAsset asset)
        {
            var go = new GameObject(name);
            _objects.Add(go);
            var player = go.AddComponent<SpriteStudioPlayer>();
            player.AnimeAsset = asset;
            player.LoopCount = -1;
            Assert.IsTrue(player.SetAnimation("anime_1"));
            player.Play();
            return player;
        }
    }
}
