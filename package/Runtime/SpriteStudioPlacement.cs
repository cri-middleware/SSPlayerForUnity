using UnityEngine;

namespace SpriteStudio.Runtime
{
    /// <summary>
    /// How an animation's canvas is scaled into a rect. <see cref="None"/> is no fit at all: scale 1, the
    /// animation's origin at the rect's centre.
    /// </summary>
    public enum SpriteStudioFit
    {
        Contain = 0,
        Cover = 1,
        None = 2,
    }

    /// <summary>Which point of an animation's canvas lands on the matching point of a rect.</summary>
    public enum SpriteStudioAlign
    {
        Center = 0,
        Top = 1,
        Bottom = 2,
        Left = 3,
        Right = 4,
        TopLeft = 5,
        TopRight = 6,
        BottomLeft = 7,
        BottomRight = 8,
    }

    /// <summary>
    /// Fit and align: where an animation's box lands in a rect. Pure arithmetic over the two, in a space with
    /// Y up — the animation's own and a RectTransform's.
    /// </summary>
    internal static class SpriteStudioPlacement
    {
        /// <summary>
        /// The authored canvas as a box in player-local units, Y up. The pivot is normalised from the canvas
        /// centre, so a character standing on its origin — pivot (0, -0.5) — gets a box wholly above it.
        /// </summary>
        internal static Rect CanvasRect(float width, float height, float pivotX, float pivotY)
            => new Rect(-pivotX * width - width / 2f, -pivotY * height - height / 2f, width, height);

        /// <summary>
        /// Puts <paramref name="box"/> into <paramref name="view"/>: the draw maps
        /// <c>p -> origin + scale * p</c>. An absent or empty box, or an empty view, leaves nothing to divide
        /// by, so the result is the unfitted placement <see cref="SpriteStudioFit.None"/> gives.
        /// </summary>
        internal static void Resolve(Rect? box, Rect view, SpriteStudioFit fit, SpriteStudioAlign align,
                                     out Vector2 origin, out float scale)
        {
            origin = view.center;
            scale = 1f;
            if (fit == SpriteStudioFit.None || !box.HasValue) return;
            Rect b = box.Value;
            if (!(b.width > 0f) || !(b.height > 0f) || !(view.width > 0f) || !(view.height > 0f)) return;

            float sx = view.width / b.width;
            float sy = view.height / b.height;
            scale = fit == SpriteStudioFit.Cover ? Mathf.Max(sx, sy) : Mathf.Min(sx, sy);
            origin = new Vector2(SolveAxis(view.xMin, view.width, scale, b.xMin, b.width, SideX(align)),
                                 SolveAxis(view.yMin, view.height, scale, b.yMin, b.height, SideY(align)));
        }

        // -1 the low end of an axis, 0 its centre, 1 its high end. Y is up, so the top is the high end.
        private static int SideX(SpriteStudioAlign align)
        {
            switch (align)
            {
                case SpriteStudioAlign.Left: case SpriteStudioAlign.TopLeft: case SpriteStudioAlign.BottomLeft: return -1;
                case SpriteStudioAlign.Right: case SpriteStudioAlign.TopRight: case SpriteStudioAlign.BottomRight: return 1;
                default: return 0;
            }
        }

        private static int SideY(SpriteStudioAlign align)
        {
            switch (align)
            {
                case SpriteStudioAlign.Bottom: case SpriteStudioAlign.BottomLeft: case SpriteStudioAlign.BottomRight: return -1;
                case SpriteStudioAlign.Top: case SpriteStudioAlign.TopLeft: case SpriteStudioAlign.TopRight: return 1;
                default: return 0;
            }
        }

        private static float SolveAxis(float viewMin, float viewSize, float s, float lo, float size, int side)
        {
            if (side == 0) return viewMin + viewSize / 2f - s * (lo + size / 2f);
            return side < 0 ? viewMin - s * lo : viewMin + viewSize - s * (lo + size);
        }
    }
}
