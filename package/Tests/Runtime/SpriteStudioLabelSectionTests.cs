using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using SpriteStudio.Runtime;

namespace SpriteStudio.Runtime.Tests
{
    /// <summary>
    /// The label table and the section named by labels, on overall's Signal: its anime_1 runs 0..30 and
    /// carries "Start" on frame 10 and "End" on frame 20. The fixture is converted from the SDK's overall
    /// project by <see cref="SpriteStudioClippingMaskTests"/>; without it the tests are ignored.
    /// </summary>
    public class SpriteStudioLabelSectionTests
    {
        private const string SignalAssetPath = "overall/Signal"; // for Resources.Load
        private const string SignalSsabPath = "../../package/Tests/Runtime/Resources/overall/Signal.ssab";
        private const string Animation = "anime_1";

        private readonly List<Object> _objects = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _objects) if (o != null) Object.DestroyImmediate(o);
            _objects.Clear();
        }

        private SpriteStudioAnimeAsset SignalAsset()
        {
            var loaded = Resources.Load<SpriteStudioAnimeAsset>(SignalAssetPath);
            byte[] bytes = loaded != null && loaded.SsabData != null && loaded.SsabData.Length > 0
                ? loaded.SsabData
                : File.Exists(Path.GetFullPath(SignalSsabPath)) ? File.ReadAllBytes(Path.GetFullPath(SignalSsabPath)) : null;
            if (bytes == null) Assert.Ignore("overall/Signal.ssab is unavailable (converted from the SDK's overall project).");
            var asset = ScriptableObject.CreateInstance<SpriteStudioAnimeAsset>();
            asset.SsabData = bytes;
            _objects.Add(asset);
            return asset;
        }

        private SpriteStudioPlayer MakePlayer(bool setUp = true)
        {
            var go = new GameObject("LabelSectionPlayer");
            _objects.Add(go);
            var player = go.AddComponent<SpriteStudioPlayer>();
            player.AnimeAsset = SignalAsset();
            if (setUp) Assert.IsTrue(player.SetAnimation(Animation));
            return player;
        }

        private static List<string> WarningsWhile(System.Action action)
        {
            var warnings = new List<string>();
            Application.LogCallback keep = (message, _, type) => { if (type == LogType.Warning) warnings.Add(message); };
            Application.logMessageReceived += keep;
            try { action(); }
            finally { Application.logMessageReceived -= keep; }
            return warnings;
        }

        [Test]
        public void LabelTable_IsInFrameOrder_WithTheAnimationsOwnRange()
        {
            var table = MakePlayer().GetLabelTable();

            Assert.IsNotNull(table);
            CollectionAssert.AreEqual(new[] { "Start", "End" }, ToNames(table.Labels));
            CollectionAssert.AreEqual(new[] { 10, 20 }, ToFrames(table.Labels));
            Assert.AreEqual(0, table.StartFrame);
            Assert.AreEqual(30, table.EndFrame);
        }

        [Test]
        public void LabelTable_ByName_ReadsAnAnimationThatIsNotSetUp()
        {
            var player = MakePlayer(setUp: false);

            Assert.IsNull(player.GetLabelTable(), "Nothing is set up, so there is no current animation to read");
            Assert.IsNotNull(player.GetLabelTable(Animation));
            Assert.IsNull(player.GetLabelTable("no_such_animation"));
        }

        [Test]
        public void SectionByLabel_RunsFromTheStartLabelToTheEndLabel_PlusTheOffsets()
        {
            var player = MakePlayer();

            Assert.IsTrue(player.SetAnimationSectionByLabel("Start", "End"));
            Assert.AreEqual(10, player.InternalPlayer.GetAnimationSectionStart());
            Assert.AreEqual(20, player.InternalPlayer.GetAnimationSectionEnd());

            Assert.IsTrue(player.SetAnimationSectionByLabel("Start", "End", 1, -1));
            Assert.AreEqual(11, player.InternalPlayer.GetAnimationSectionStart());
            Assert.AreEqual(19, player.InternalPlayer.GetAnimationSectionEnd());
        }

        [Test]
        public void SectionByLabel_StartAndEnd_AreTheAnimationsOwnEnds_NotTheSections()
        {
            var player = MakePlayer();
            Assert.IsTrue(player.SetAnimationSectionByLabel("Start", "End"));

            Assert.IsTrue(player.SetAnimationSectionByLabel("_start", "_end"));
            Assert.AreEqual(0, player.InternalPlayer.GetAnimationSectionStart());
            Assert.AreEqual(30, player.InternalPlayer.GetAnimationSectionEnd());
        }

        [Test]
        public void SectionByLabel_WithAMissingLabel_IsRefusedOnce_AndChangesNothing()
        {
            var player = MakePlayer();
            Assert.IsTrue(player.SetAnimationSectionByLabel("Start", "End"));

            var warnings = WarningsWhile(() =>
            {
                Assert.IsFalse(player.SetAnimationSectionByLabel("Start", "Ending"));
                Assert.IsFalse(player.SetAnimationSectionByLabel("Start", "Ending"));
            });

            Assert.AreEqual(1, warnings.Count, "The missing label is reported once");
            StringAssert.Contains("'Ending'", warnings[0]);
            StringAssert.DoesNotContain("'Start'", warnings[0], "Only the label that is missing is named");
            Assert.AreEqual(10, player.InternalPlayer.GetAnimationSectionStart(), "The section start stays");
            Assert.AreEqual(20, player.InternalPlayer.GetAnimationSectionEnd(), "The section end stays");
        }

        [Test]
        public void SectionByLabel_EndingBeforeItsStart_IsRefusedOnce_NamingTheLabelsAndTheFrames()
        {
            var player = MakePlayer();
            Assert.IsTrue(player.SetAnimationSectionByLabel("Start", "End"));
            float playhead = player.FrameNo;

            var warnings = WarningsWhile(() =>
            {
                Assert.IsFalse(player.SetAnimationSectionByLabel("End", "Start"));
                Assert.IsFalse(player.SetAnimationSectionByLabel("End", "Start"));
            });

            Assert.AreEqual(1, warnings.Count, "The refusal is reported once");
            StringAssert.Contains("'End'", warnings[0]);
            StringAssert.Contains("'Start'", warnings[0]);
            StringAssert.Contains("(20, 10)", warnings[0], "The frames the labels land on");
            Assert.AreEqual(10, player.InternalPlayer.GetAnimationSectionStart(), "The section start stays");
            Assert.AreEqual(20, player.InternalPlayer.GetAnimationSectionEnd(), "The section end stays");
            Assert.AreEqual(playhead, player.FrameNo, 0.0001f, "The playhead stays");
        }

        [Test]
        public void SectionByLabel_BeforeAnAnimationIsSetUp_ReturnsFalse_AndSaysNothing()
        {
            var player = MakePlayer(setUp: false);

            var warnings = WarningsWhile(() => Assert.IsFalse(player.SetAnimationSectionByLabel("Start", "End")));

            Assert.IsEmpty(warnings);
        }

        [Test]
        public void Graphic_SectionByLabel_ReachesTheRuntime()
        {
            var go = new GameObject("LabelSectionGraphic");
            _objects.Add(go);
            go.AddComponent<Canvas>();
            var graphic = go.AddComponent<SpriteStudioGraphic>();
            graphic.AnimeAsset = SignalAsset();
            Assert.IsTrue(graphic.SetAnimation(Animation));

            Assert.IsTrue(graphic.SetAnimationSectionByLabel("Start", "End"));
            Assert.AreEqual(10, graphic.InternalPlayer.GetAnimationSectionStart());
            Assert.AreEqual(20, graphic.InternalPlayer.GetAnimationSectionEnd());
            Assert.AreEqual(2, graphic.GetLabelTable().Labels.Count);
        }

        [Test]
        public void TryGetFrame_ARealLabelNamedStart_WinsOverTheAnimationsFirstFrame()
        {
            var table = new SpriteStudioLabelTable(
                new[] { new SpriteStudioLabel("b", 8), new SpriteStudioLabel("_start", 3), new SpriteStudioLabel("a", 2) }, 0, 10);

            CollectionAssert.AreEqual(new[] { "a", "_start", "b" }, ToNames(table.Labels));
            Assert.IsTrue(table.TryGetFrame("_start", out int start));
            Assert.AreEqual(3, start);
            Assert.IsTrue(table.TryGetFrame("_end", out int end));
            Assert.AreEqual(10, end);
            Assert.IsFalse(table.TryGetFrame("A", out _), "Names match exactly");
        }

        private static string[] ToNames(IReadOnlyList<SpriteStudioLabel> labels)
        {
            var names = new string[labels.Count];
            for (int i = 0; i < labels.Count; i++) names[i] = labels[i].Name;
            return names;
        }

        private static int[] ToFrames(IReadOnlyList<SpriteStudioLabel> labels)
        {
            var frames = new int[labels.Count];
            for (int i = 0; i < labels.Count; i++) frames[i] = labels[i].Frame;
            return frames;
        }
    }
}
