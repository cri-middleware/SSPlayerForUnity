using SpriteStudio.Runtime;
using SpriteStudio.UI;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
/// UI integration demo
///
/// A SpriteStudioButton and a SpriteStudioToggle made of Ringo, each with a SpriteStudioRaycastFilter so only
/// the artwork takes the pointer, over a background button that counts the clicks that get through. The panel
/// switches the button's interactable, reduced motion on both, and the outline overlay; the row at the bottom
/// fits Ringo into three rects whose width the slider sets.
///
/// API used:
///   SpriteStudioButton / SpriteStudioToggle: CurrentState, ReducedMotion, StateClips (set in the Inspector)
///   SpriteStudioRaycastFilter: Parts
///   SpriteStudioGraphic: Fit / Align, HitTestPart, GetPartNames
/// </summary>
public class UiDemo : MonoBehaviour
{
    [SerializeField] private SpriteStudioButton _button = null;
    [SerializeField] private SpriteStudioToggle _toggle = null;
    [SerializeField] private Button _background = null;
    [SerializeField] private Toggle _interactable = null;
    [SerializeField] private Toggle _reducedMotion = null;
    [SerializeField] private Toggle _showOutlines = null;
    [SerializeField] private Slider _fitWidth = null;
    [SerializeField] private RectTransform[] _fitBoxes = null;
    [SerializeField] private PartOutlineOverlay[] _overlays = null;
    [SerializeField] private Text _status = null;

    private int _buttonClicks;
    private int _backgroundClicks;

    private void Start()
    {
        _button.onClick.AddListener(() => _buttonClicks++);
        _background.onClick.AddListener(() => _backgroundClicks++);
        _interactable.onValueChanged.AddListener(on => _button.interactable = on);
        _reducedMotion.onValueChanged.AddListener(on =>
        {
            _button.ReducedMotion = on;
            _toggle.ReducedMotion = on;
        });
        _showOutlines.onValueChanged.AddListener(on =>
        {
            foreach (var o in _overlays) o.enabled = on;
        });
        _fitWidth.onValueChanged.AddListener(width =>
        {
            foreach (var box in _fitBoxes) box.sizeDelta = new Vector2(width, box.sizeDelta.y);
        });
        foreach (var o in _overlays)
        {
            var filter = o.GetComponentInParent<SpriteStudioRaycastFilter>();
            if (filter != null) o.HitParts = filter.Parts;
        }
    }

    private void Update()
    {
        string buttonPart = PartUnderPointer(_button.Graphic, _overlays.Length > 0 ? _overlays[0] : null);
        string togglePart = PartUnderPointer(_toggle.Graphic, _overlays.Length > 1 ? _overlays[1] : null);
        _status.text =
            $"Button: {_button.CurrentState}, clicked {_buttonClicks}\n" +
            $"Toggle: {(_toggle.isOn ? "on" : "off")}, {_toggle.CurrentState}\n" +
            $"Clicks that reached the background: {_backgroundClicks}\n" +
            $"Part under the pointer: {buttonPart ?? togglePart ?? "-"}";
    }

    private static string PartUnderPointer(SpriteStudioGraphic graphic, PartOutlineOverlay overlay)
    {
        string name = null;
        int part = -1;
        if (Pointer.current != null
            && RectTransformUtility.ScreenPointToLocalPointInRectangle(graphic.rectTransform, Pointer.current.position.ReadValue(), null, out Vector2 local))
        {
            part = graphic.HitTestPart(local);
            if (part >= 0) name = graphic.GetPartNames()[part];
        }
        if (overlay != null) overlay.Highlighted = part;
        return name;
    }
}
