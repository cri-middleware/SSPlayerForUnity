using System.Collections.Generic;
using SpriteStudio.Runtime;
using UnityEngine;

namespace SpriteStudio.UI
{
    /// <summary>
    /// Narrows where the <see cref="SpriteStudioGraphic"/> beside it takes the pointer, from its whole
    /// RectTransform to the outlines of the parts it draws (<see cref="SpriteStudioGraphic.HitTestPart(Vector2)"/>):
    /// every part with an outline, or only the parts in <see cref="Parts"/>. A click on the transparent margin
    /// then reaches whatever is behind. It never widens the rect, and keyboard and gamepad navigation are not
    /// narrowed. uGUI asks a parent's filters too, so a graphic below this one in the hierarchy — a label on
    /// the button — takes the pointer only over the same parts. It answers only when uGUI asks, so it costs
    /// nothing per frame.
    /// </summary>
    [AddComponentMenu("SpriteStudio/SpriteStudio Raycast Filter")]
    [RequireComponent(typeof(SpriteStudioGraphic))]
    public class SpriteStudioRaycastFilter : MonoBehaviour, ICanvasRaycastFilter
    {
        [Tooltip("The parts whose outlines take the pointer. Empty: every part's.")]
        [SerializeField, SpriteStudioPartName] private List<string> _parts = new List<string>();

        private SpriteStudioGraphic _graphic;

        /// <summary>The parts whose outlines take the pointer; empty, every part with an outline.</summary>
        public List<string> Parts => _parts;

        /// <inheritdoc/>
        public bool IsRaycastLocationValid(Vector2 screenPoint, Camera eventCamera)
        {
            // Graphic.Raycast asks every filter on the GameObject, enabled or not.
            if (!isActiveAndEnabled) return true;
            if (_graphic == null) _graphic = GetComponent<SpriteStudioGraphic>();
            if (_graphic == null) return true;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(_graphic.rectTransform, screenPoint, eventCamera, out Vector2 local))
                return false;
            int part = _parts.Count == 0 ? _graphic.HitTestPart(local) : _graphic.HitTestPart(local, _parts);
            return part >= 0;
        }
    }

    internal sealed class SpriteStudioPartNameAttribute : PropertyAttribute
    {
    }
}
