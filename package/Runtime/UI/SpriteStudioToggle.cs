using System.Collections.Generic;
using SpriteStudio.Runtime;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace SpriteStudio.UI
{
    /// <summary>
    /// A <see cref="Toggle"/> that shows its state on the <see cref="SpriteStudioGraphic"/> beside it, as
    /// <see cref="SpriteStudioButton"/> does, with <see cref="Toggle.isOn"/> as the selection: while on, each state
    /// plays the <c>selected_</c> label of its name (<c>selected_normal</c>, <c>selected_hover</c>, …), falling
    /// back through the selected labels before the plain ones, so an on toggle still looks on while hovered.
    /// </summary>
    [AddComponentMenu("SpriteStudio/SpriteStudio Toggle")]
    [RequireComponent(typeof(SpriteStudioGraphic))]
    public class SpriteStudioToggle : Toggle
    {
        [Tooltip("Hold each state's first frame instead of playing it, and play the reduced_motion label in place of normal. Unity cannot read the OS setting, so the game sets this.")]
        [SerializeField] private bool _reducedMotion;
        [Tooltip("What a state plays instead of the label named after it: a whole animation, or a label of one. Selected is the toggle's on side.")]
        [SerializeField] private List<SpriteStudioUiClip> _stateClips = new List<SpriteStudioUiClip>();
        [Tooltip("What a screen reader would say for this toggle. Unity builds no accessibility node for a uGUI control; a game that builds its own AccessibilityHierarchy reads the label here.")]
        [SerializeField] private string _accessibilityLabel = "";

        private readonly SpriteStudioUiStateDriver _driver = new SpriteStudioUiStateDriver();
        private SpriteStudioGraphic _graphic;
        private bool _hovered;
        private bool _focused;
        private SelectionState _transitionState;

        /// <inheritdoc cref="SpriteStudioButton.ReducedMotion"/>
        /// <remarks>The <c>selected_</c> sections are held too, so the toggle still shows that it is on.</remarks>
        public bool ReducedMotion
        {
            get => _reducedMotion;
            set
            {
                _reducedMotion = value;
                Show();
            }
        }

        /// <inheritdoc cref="SpriteStudioButton.StateClips"/>
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

        /// <inheritdoc cref="SpriteStudioButton.AccessibilityLabel"/>
        public string AccessibilityLabel
        {
            get => _accessibilityLabel;
            set => _accessibilityLabel = value ?? "";
        }

        /// <inheritdoc cref="SpriteStudioButton.CurrentState"/>
        public SpriteStudioUiState CurrentState
            => SpriteStudioUiStates.Of(!IsInteractable(), IsPressed() || _transitionState == SelectionState.Pressed,
                                       _hovered, _focused);

        /// <inheritdoc cref="SpriteStudioButton.Graphic"/>
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
            transition = Transition.None;
        }

        protected override void OnValidate()
        {
            base.OnValidate();
            _driver.Invalidate();
        }
#endif

        protected override void OnEnable()
        {
            base.OnEnable();
            onValueChanged.AddListener(OnToggled);
        }

        protected override void OnDisable()
        {
            onValueChanged.RemoveListener(OnToggled);
            _hovered = false;
            _focused = false;
            _driver.Release(Graphic);
            base.OnDisable();
        }

        private void OnToggled(bool isOn) => Show();

        protected override void DoStateTransition(SelectionState state, bool instant)
        {
            _transitionState = state;
            base.DoStateTransition(state, instant);
            Show();
        }

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

        private void Update() => Show();

        private void Show()
        {
            if (!Application.isPlaying || !IsActive()) return;
            _driver.Show(Graphic, CurrentState, isOn, _reducedMotion, _stateClips, this);
        }
    }
}
