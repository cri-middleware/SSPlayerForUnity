using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using SpriteStudio.Runtime;
using SpriteStudio.UI;

namespace SpriteStudio.Runtime.Tests
{
    /// <summary>
    /// The UI adapter on a real graphic: the state driver, the button and toggle that feed it, and the raycast
    /// filter. Ringo carries no state labels, so the states are given clips naming its animations.
    /// </summary>
    public class SpriteStudioUiTests
    {
        private readonly List<Object> _objects = new List<Object>();
        private GameObject _canvas;
        private EventSystem _eventSystem;

        private static readonly SpriteStudioUiClip[] Clips =
        {
            new SpriteStudioUiClip(SpriteStudioUiState.Normal, false, "wait1", ""),
            new SpriteStudioUiClip(SpriteStudioUiState.Hover, false, "walk", ""),
            new SpriteStudioUiClip(SpriteStudioUiState.Pressed, false, "attack1", ""),
            new SpriteStudioUiClip(SpriteStudioUiState.Disabled, false, "dead", ""),
            new SpriteStudioUiClip(SpriteStudioUiState.Normal, true, "joy", ""),
        };

        [SetUp]
        public void SetUp()
        {
            _canvas = new GameObject("UiCanvas", typeof(Canvas));
            _canvas.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            _objects.Add(_canvas);
            var es = new GameObject("UiEventSystem", typeof(EventSystem));
            _objects.Add(es);
            _eventSystem = es.GetComponent<EventSystem>();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _objects) if (o != null) Object.DestroyImmediate(o);
            _objects.Clear();
        }

        private T Make<T>(string animation = "wait1") where T : Component
        {
            var asset = Resources.Load<SpriteStudioAnimeAsset>("Ringo/Ringo");
            Assert.IsNotNull(asset, "Ringo.ssab is required.");
            var go = new GameObject(typeof(T).Name, typeof(RectTransform));
            go.transform.SetParent(_canvas.transform, false);
            var graphic = go.AddComponent<SpriteStudioGraphic>();
            graphic.AnimeAsset = asset;
            graphic.CurrentAnimation = animation;
            return typeof(T) == typeof(SpriteStudioGraphic) ? (T)(Component)graphic : go.AddComponent<T>();
        }

        private static string Playing(SpriteStudioGraphic graphic) => graphic.InternalPlayer.CurrentAnimationName;

        [Test]
        public void Driver_PlaysTheStatesSection_Looping()
        {
            var graphic = Make<SpriteStudioGraphic>();
            var driver = new SpriteStudioUiStateDriver();

            driver.Show(graphic, SpriteStudioUiState.Hover, false, false, Clips, null);

            Assert.AreEqual("walk", Playing(graphic));
            Assert.IsTrue(graphic.IsPlaying);
            Assert.AreEqual(-1, graphic.LoopCount);
        }

        [Test]
        public void Driver_ShowingTheSameStateAgain_DoesNothing()
        {
            var graphic = Make<SpriteStudioGraphic>();
            var driver = new SpriteStudioUiStateDriver();
            driver.Show(graphic, SpriteStudioUiState.Hover, false, false, Clips, null);
            for (int i = 0; i < 10; i++) graphic.InternalPlayer.Tick(1f / 60f, false);
            float frame = graphic.FrameNo;
            Assume.That(frame, Is.GreaterThan(0f));

            driver.Show(graphic, SpriteStudioUiState.Hover, false, false, Clips, null);

            Assert.AreEqual(frame, graphic.FrameNo, "The section is not restarted");
        }

        [Test]
        public void Driver_ReducedMotion_HoldsTheFirstFrame_Stopped()
        {
            var graphic = Make<SpriteStudioGraphic>();
            var driver = new SpriteStudioUiStateDriver();

            driver.Show(graphic, SpriteStudioUiState.Pressed, false, true, Clips, null);

            Assert.AreEqual("attack1", Playing(graphic));
            Assert.IsFalse(graphic.IsPlaying, "Stopped, not paused, so no event fires");
            Assert.AreEqual(graphic.InternalPlayer.GetAnimationSectionStart(), (int)graphic.FrameNo);
        }

        [Test]
        public void Driver_AnAnimationSomethingElseChose_IsWhereTheStatesAreReadFrom()
        {
            var graphic = Make<SpriteStudioGraphic>();
            var driver = new SpriteStudioUiStateDriver();
            driver.Show(graphic, SpriteStudioUiState.Normal, false, false, null, null);
            Assert.AreEqual("wait1", Playing(graphic), "With no clips, normal is the whole of the graphic's animation");

            Assert.IsTrue(graphic.SetAnimation("run"));
            driver.Show(graphic, SpriteStudioUiState.Normal, false, false, null, null);

            Assert.AreEqual("run", Playing(graphic));
            Assert.IsTrue(graphic.IsPlaying, "Shown again, since what it plays changed under the driver");
        }

        [Test]
        public void Driver_KeepsTheFitOnTheStatesAnimation()
        {
            var graphic = Make<SpriteStudioGraphic>();
            var driver = new SpriteStudioUiStateDriver();
            Assert.IsTrue(graphic.InternalPlayer.TryGetCanvasRect("wait1", out Rect wait));

            driver.Show(graphic, SpriteStudioUiState.Disabled, false, false, Clips, null);
            Assert.AreEqual("dead", Playing(graphic), "dead's canvas is wider than wait1's");
            Assert.AreEqual(wait, graphic.FitBox);

            driver.Release(graphic);
            Assert.IsNull(graphic.FitBox);
        }

        [Test]
        public void Driver_AGraphicEnabledAgain_KeepsReadingTheStatesFromTheSameAnimation()
        {
            var graphic = Make<SpriteStudioGraphic>();
            var driver = new SpriteStudioUiStateDriver();
            Assert.IsTrue(graphic.InternalPlayer.TryGetCanvasRect("wait1", out Rect wait));
            driver.Show(graphic, SpriteStudioUiState.Disabled, false, false, Clips, null);

            // The runtime is disposed and rebuilt, set up on dead, the animation last shown.
            graphic.enabled = false;
            graphic.enabled = true;
            Assert.IsTrue(graphic.SetAnimation(graphic.CurrentAnimation));
            driver.Show(graphic, SpriteStudioUiState.Normal, false, false, Clips, null);

            Assert.AreEqual("wait1", Playing(graphic));
            Assert.AreEqual(wait, graphic.FitBox, "Not dead's wider canvas");
        }

        [Test]
        public void Driver_AClipThePackDoesNotHave_IsReportedOnce()
        {
            var graphic = Make<SpriteStudioGraphic>();
            var driver = new SpriteStudioUiStateDriver();
            var clips = new[] { new SpriteStudioUiClip(SpriteStudioUiState.Hover, false, "no_such_animation", "") };
            int warnings = 0;
            Application.LogCallback count = (message, _, type) =>
            {
                if (type == LogType.Warning && message.Contains("no_such_animation")) warnings++;
            };
            Application.logMessageReceived += count;
            try
            {
                driver.Show(graphic, SpriteStudioUiState.Hover, false, false, clips, null);
                driver.Show(graphic, SpriteStudioUiState.Normal, false, false, clips, null);
                driver.Show(graphic, SpriteStudioUiState.Hover, false, false, clips, null);
            }
            finally
            {
                Application.logMessageReceived -= count;
            }

            Assert.AreEqual(1, warnings);
            Assert.AreEqual("wait1", Playing(graphic), "Hover falls back to normal");
        }

        [Test]
        public void Button_ShowsHover_Pressed_AndDisabled()
        {
            var button = Make<SpriteStudioButton>();
            button.StateClips = Clips;
            var graphic = button.Graphic;
            var pointer = new PointerEventData(_eventSystem) { button = PointerEventData.InputButton.Left };

            button.OnPointerEnter(pointer);
            Assert.AreEqual(SpriteStudioUiState.Hover, button.CurrentState);
            Assert.AreEqual("walk", Playing(graphic));

            button.OnPointerDown(pointer);
            Assert.AreEqual(SpriteStudioUiState.Pressed, button.CurrentState);
            Assert.AreEqual("attack1", Playing(graphic));

            button.OnPointerUp(pointer);
            button.interactable = false;
            Assert.AreEqual(SpriteStudioUiState.Disabled, button.CurrentState);
            Assert.AreEqual("dead", Playing(graphic));
        }

        [Test]
        public void Button_HoverWinsOverFocus()
        {
            var button = Make<SpriteStudioButton>();
            button.StateClips = Clips;
            var pointer = new PointerEventData(_eventSystem);

            button.OnSelect(pointer);
            Assert.AreEqual(SpriteStudioUiState.Focused, button.CurrentState);
            button.OnPointerEnter(pointer);
            Assert.AreEqual(SpriteStudioUiState.Hover, button.CurrentState, "Selectable would report Selected here");
        }

        [Test]
        public void Toggle_IsOn_PlaysTheSelectedClip()
        {
            var toggle = Make<SpriteStudioToggle>();
            toggle.StateClips = Clips;
            toggle.isOn = false;

            toggle.isOn = true;

            Assert.AreEqual("joy", Playing(toggle.Graphic));
        }

        [Test]
        public void RaycastFilter_TakesThePointerOnlyOverTheArtwork()
        {
            var graphic = Make<SpriteStudioGraphic>("walk");
            Assert.IsTrue(graphic.SetAnimation("walk"));
            graphic.rectTransform.sizeDelta = new Vector2(800f, 600f);
            graphic.ApplyFit();
            graphic.InternalPlayer.ScheduleMeshUpdate(0f);
            graphic.InternalPlayer.CurrentUpdateJobHandle.Complete();
            var filter = graphic.gameObject.AddComponent<SpriteStudioRaycastFilter>();

            int part = -1;
            SpriteStudioPartQuad quad = default;
            for (int i = 0; i < graphic.GetPartNames().Length && part < 0; i++)
                if (graphic.TryGetPartQuad(i, out quad)) part = i;
            Assert.GreaterOrEqual(part, 0);
            Vector2 onArt = (quad.TopLeft + quad.TopRight + quad.BottomLeft + quad.BottomRight) / 4f;
            Vector2 margin = graphic.rectTransform.rect.min + new Vector2(1f, 1f);
            Assume.That(graphic.HitTestPart(margin), Is.EqualTo(-1), "The rect's corner is transparent");

            // Screen space overlay with no camera: a local point's world position is its screen position.
            Vector2 Screen(Vector2 local) => graphic.rectTransform.TransformPoint(local);
            Assert.IsTrue(filter.IsRaycastLocationValid(Screen(onArt), null));
            Assert.IsFalse(filter.IsRaycastLocationValid(Screen(margin), null));

            filter.Parts.Add("no_such_part");
            Assert.IsFalse(filter.IsRaycastLocationValid(Screen(onArt), null), "Only the parts named take it");

            filter.enabled = false;
            Assert.IsTrue(filter.IsRaycastLocationValid(Screen(margin), null), "A disabled filter narrows nothing");
        }
    }
}
