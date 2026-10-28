using System.Collections.Generic;
using SpriteStudio.Runtime;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace SpriteStudio.UI
{
    /// <summary>
    /// A <see cref="Button"/> that shows its state on the <see cref="SpriteStudioGraphic"/> beside it: each of
    /// normal / hover / pressed / disabled / focused plays the section from the label of the same name, looping,
    /// and a state without a label falls back to another (pressed to hover, the rest to normal, normal to the
    /// whole animation). While the game runs it takes the graphic's playback over; in edit mode the graphic
    /// shows its own preview.
    /// </summary>
    [AddComponentMenu("SpriteStudio/SpriteStudio Button")]
    [RequireComponent(typeof(SpriteStudioGraphic))]
    public class SpriteStudioButton : Button
    {
        [Tooltip("Hold each state's first frame instead of playing it, and play the reduced_motion label in place of normal. Unity cannot read the OS setting, so the game sets this.")]
        [SerializeField] private bool _reducedMotion;
        [Tooltip("What a state plays instead of the label named after it: a whole animation, or a label of one.")]
        [SerializeField] private List<SpriteStudioUiClip> _stateClips = new List<SpriteStudioUiClip>();
        [Tooltip("What a screen reader would say for this button. Unity builds no accessibility node for a uGUI control; a game that builds its own AccessibilityHierarchy reads the label here.")]
        [SerializeField] private string _accessibilityLabel = "";

        private readonly SpriteStudioUiStateDriver _driver = new SpriteStudioUiStateDriver();
        private SpriteStudioGraphic _graphic;
        private bool _hovered;
        private bool _focused;
        private SelectionState _transitionState;

        /// <summary>
        /// Hold each state's first frame, stopped, instead of playing it; normal plays the section from the
        /// <c>reduced_motion</c> label instead when the animation has one. Unity has no API for the OS setting, so
        /// the game sets this from its own options.
        /// </summary>
        public bool ReducedMotion
        {
            get => _reducedMotion;
            set
            {
                _reducedMotion = value;
                Show();
            }
        }

        /// <summary>
        /// What a state plays instead of the label named after it. A clip also serves the states that fall back
        /// to it: one for hover is what a pressed button without a label of its own plays. A clip naming an
        /// animation or a label the asset does not have is reported once and ignored.
        /// </summary>
        public IReadOnlyList<SpriteStudioUiClip> StateClips
        {
            get => _stateClips;
            set
            {
                _stateClips = value != null ? new List<SpriteStudioUiClip>(value) : new List<SpriteStudioUiClip>();
                _driver.Invalidate();
                Show();
            }
        }

        /// <summary>
        /// What a screen reader would say for this button; empty, the animation is decoration. Unity builds no
        /// accessibility node for a uGUI control, so a game that builds its own <c>AccessibilityHierarchy</c>
        /// reads it here for this button's node.
        /// </summary>
        public string AccessibilityLabel
        {
            get => _accessibilityLabel;
            set => _accessibilityLabel = value ?? "";
        }

        /// <summary>The state the button shows now.</summary>
        // Pressed is also the brief press a Submit from the keyboard or a gamepad shows, which holds no pointer.
        public SpriteStudioUiState CurrentState
            => SpriteStudioUiStates.Of(!IsInteractable(), IsPressed() || _transitionState == SelectionState.Pressed,
                                       _hovered, _focused);

        /// <summary>The graphic the button plays its states on.</summary>
        public SpriteStudioGraphic Graphic => _graphic != null ? _graphic : (_graphic = GetComponent<SpriteStudioGraphic>());

        protected override void Awake()
        {
            base.Awake();
            // The states play the graphic; its own Auto Play would start the animation over them.
            if (Application.isPlaying) Graphic.AutoPlay = false;
        }

#if UNITY_EDITOR
        protected override void Reset()
        {
            base.Reset();
            // The animation shows the state; a tint on top of it would show it twice.
            transition = Transition.None;
        }

        protected override void OnValidate()
        {
            base.OnValidate();
            _driver.Invalidate();
        }
#endif

        protected override void OnDisable()
        {
            _hovered = false;
            _focused = false;
            _driver.Release(Graphic);
            base.OnDisable();
        }

        protected override void DoStateTransition(SelectionState state, bool instant)
        {
            _transitionState = state;
            base.DoStateTransition(state, instant);
            Show();
        }

        // Selectable reports one state, with Selected over Highlighted; the convention puts hover over focus, so
        // the button keeps the two itself. Set before the base handler, which calls DoStateTransition.
        public override void OnPointerEnter(PointerEventData eventData)
        {
            _hovered = true;
            base.OnPointerEnter(eventData);
        }

        public override void OnPointerExit(PointerEventData eventData)
        {
            _hovered = false;
            base.OnPointerExit(eventData);
        }

        public override void OnSelect(BaseEventData eventData)
        {
            _focused = true;
            base.OnSelect(eventData);
        }

        public override void OnDeselect(BaseEventData eventData)
        {
            _focused = false;
            base.OnDeselect(eventData);
        }

        // The graphic can load a pack, or have its animation changed, with nothing to tell the button; showing
        // the same state again costs a few comparisons.
        private void Update() => Show();

        private void Show()
        {
            if (!Application.isPlaying || !IsActive()) return;
            _driver.Show(Graphic, CurrentState, false, _reducedMotion, _stateClips, this);
        }
    }
}
