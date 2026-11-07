using System.Collections;
using SpriteStudio.Runtime;
using UnityEngine;

/// <summary>
/// Override Layer API demo
///
/// Put this on the same GameObject as a SpriteStudioPlayer, press Play, and the demo cycles through
/// the three per-part runtime overrides while the animation keeps playing. Each override wins over
/// the keyframed animation underneath. The current step is drawn in the top-left corner.
///
/// API used (all on SpriteStudioPlayer / SpriteStudioGraphic):
///   FindPartIndex(partName)
///   SetPartColorOverride(part, color, blendOp = Mix, priority = HoldUntilNextAnimation)
///   SetPartVisibilityOverride(part, hide, cascade = false)
///   SetPartCellOverride(part, cellmapName, cellName, priority = HoldUntilNextAnimation)
///   ClearAllPartOverrides()
/// Cell names can be discovered from the player: GetCellmapNames() / GetCellNames(cellmap).
/// </summary>
public class OverrideDemo : MonoBehaviour
{
    [Tooltip("SpriteStudioPlayer object that you wish to control from outside.")]
    [SerializeField] private SpriteStudioPlayer _player = null;

    [Tooltip("Part the demo overrides. 'apple' is Ringo's body.")]
    [SerializeField] private string _partName = "apple";

    [Tooltip("Second part, hidden on its own to show a non-cascading force-hide.")]
    [SerializeField] private string _secondPartName = "heta";

    [Tooltip("Cell the cell-override step swaps in (must exist in the first cellmap).")]
    [SerializeField] private string _cellName = "effect3";

    private string _status = "";
    private bool isBooted = false;

    void Start()
    {
        BootupCoroutine();
    }

    void Update()
    {
        if (!isBooted)
        {
            BootupCoroutine();
        }
    }

    void OnGUI()
    {
        var style = new GUIStyle(GUI.skin.label)
        {
            fontSize = 22,
            normal = { textColor = Color.white },
        };

        // Cheap outline so the text stays readable on any background.
        var shadow = new GUIStyle(style) { normal = { textColor = Color.black } };
        GUI.Label(new Rect(18, 18, 900, 40), _status, shadow);
        GUI.Label(new Rect(16, 16, 900, 40), _status, style);
    }

    private void BootupCoroutine()
    {
        if (_player == null)
        {
            _player = GetComponent<SpriteStudioPlayer>();
        }
        if (_player == null)
        {
            return;
        }

        // Make sure the animation is playing: an override is easiest to read while the animation
        // underneath keeps advancing (it also shows that the override wins over the keyframes).
        _player.Play();

        isBooted = true;
        StartCoroutine(RunDemo());
    }

    private IEnumerator RunDemo()
    {
        // Discover what the loaded asset offers (handy as a reference in the Console).
        var cellmaps = _player.GetCellmapNames();
        if (cellmaps.Length == 0)
        {
            _status = "No cellmap in the loaded asset — nothing to demo.";
            Debug.LogWarning($"[Override Demo] {_status}");
            yield break;
        }
        string cellmap = cellmaps[0]; // "Ringo"

        // The same part is overridden over and over, so resolve its index once and reuse it
        // (indices are stable within an asset, and addressing by index skips the name lookup).
        int partIndex = _player.FindPartIndex(_partName);
        if (partIndex < 0)
        {
            _status = $"Part '{_partName}' is not in this animation.";
            Debug.LogWarning($"[Override Demo] {_status}");
            yield break;
        }

        while (true)
        {
            _player.ClearAllPartOverrides();
            yield return Step("Ringo Override Demo — no overrides", 1.5f);

            // 1) Color — tint the body (Normal parts only). The default priority
            //    (HoldUntilNextAnimation) keeps it through animation loops.
            _player.SetPartColorOverride(partIndex, new Color(0f, 0.2f, 1f, 0.75f), SpriteStudioBlendOp.Mix);
            yield return Step("1) Color override:  body -> blue", 2f);

            // 2) Visibility — force-hide. Cascade would take the descendants with it; here the
            //    second part is hidden on its own instead, to show the non-cascading form.
            _player.ClearAllPartOverrides();
            _player.SetPartVisibilityOverride(partIndex, hide: true, cascade: false);
            _player.SetPartVisibilityOverride(_secondPartName, hide: true, cascade: false);
            yield return Step($"2) Visibility override:  hide '{_partName}' and '{_secondPartName}'", 2f);

            // 3) Cell — swap the body's sprite to a different cell in the same cellmap.
            _player.ClearAllPartOverrides();
            _player.SetPartCellOverride(partIndex, cellmap, _cellName);
            _player.SetPartVisibilityOverride(_secondPartName, hide: true, cascade: false);
            yield return Step($"3) Cell override:  body sprite -> '{_cellName}' cell", 2f);

            _player.ClearAllPartOverrides();
            yield return Step("4) No override (restored)", 2f);
        }
    }

    private IEnumerator Step(string text, float seconds)
    {
        _status = text;
        yield return new WaitForSeconds(seconds);
    }
}
