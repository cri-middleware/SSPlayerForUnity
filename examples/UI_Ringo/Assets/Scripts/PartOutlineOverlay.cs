using System.Collections.Generic;
using SpriteStudio.Runtime;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Draws the outline of every part of a SpriteStudioGraphic, as TryGetPartQuad reports it, over the graphic.
/// The parts the button's raycast filter takes the pointer on are green, the part under the pointer yellow,
/// and the rest grey. Put it on a child of the graphic, stretched over it.
/// </summary>
[RequireComponent(typeof(CanvasRenderer))]
public class PartOutlineOverlay : MaskableGraphic
{
    [SerializeField] private SpriteStudioGraphic _target = null;
    [SerializeField] private float _thickness = 2f;

    /// <summary>The parts drawn green; empty, every part.</summary>
    public List<string> HitParts = new List<string>();

    /// <summary>The part drawn yellow, or -1.</summary>
    public int Highlighted { get; set; } = -1;

    protected override void Awake()
    {
        base.Awake();
        raycastTarget = false;
    }

    // The graphic builds its frame early in LateUpdate; the overlay is rebuilt after it, before the canvas draws.
    private void LateUpdate() => SetVerticesDirty();

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();
        if (_target == null) return;
        string[] names = _target.GetPartNames();
        for (int i = 0; i < names.Length; i++)
        {
            if (!_target.TryGetPartQuad(i, out var quad)) continue;
            Color c = i == Highlighted ? new Color(1f, 0.85f, 0f)
                : HitParts.Count == 0 || HitParts.Contains(names[i]) ? new Color(0.1f, 0.8f, 0.3f)
                : new Color(0.5f, 0.5f, 0.5f, 0.6f);
            Vector2 tl = Local(quad.TopLeft), tr = Local(quad.TopRight), bl = Local(quad.BottomLeft), br = Local(quad.BottomRight);
            Edge(vh, tl, tr, c);
            Edge(vh, tr, br, c);
            Edge(vh, br, bl, c);
            Edge(vh, bl, tl, c);
        }
    }

    private Vector2 Local(Vector2 targetLocal)
        => rectTransform.InverseTransformPoint(_target.rectTransform.TransformPoint(targetLocal));

    private void Edge(VertexHelper vh, Vector2 a, Vector2 b, Color c)
    {
        Vector2 d = b - a;
        if (d.sqrMagnitude < 1e-6f) return;
        Vector2 n = new Vector2(-d.y, d.x).normalized * (_thickness * 0.5f);
        int start = vh.currentVertCount;
        vh.AddVert(a - n, c, Vector4.zero);
        vh.AddVert(a + n, c, Vector4.zero);
        vh.AddVert(b + n, c, Vector4.zero);
        vh.AddVert(b - n, c, Vector4.zero);
        vh.AddTriangle(start, start + 1, start + 2);
        vh.AddTriangle(start, start + 2, start + 3);
    }
}
