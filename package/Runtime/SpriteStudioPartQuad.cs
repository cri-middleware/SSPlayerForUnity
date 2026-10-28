using UnityEngine;

namespace SpriteStudio.Runtime
{
    /// <summary>
    /// A part's outline as drawn: four corners in the space the player draws in. Four points rather than a
    /// box, because a rotated, skewed or vertex-deformed part is not axis-aligned. The corners are the part's
    /// own — <see cref="TopLeft"/> is where the cell's top left is drawn — so on a rotated or mirrored part
    /// they are wherever the pose put them.
    /// </summary>
    public readonly struct SpriteStudioPartQuad
    {
        public readonly Vector2 TopLeft;
        public readonly Vector2 TopRight;
        public readonly Vector2 BottomLeft;
        public readonly Vector2 BottomRight;

        public SpriteStudioPartQuad(Vector2 topLeft, Vector2 topRight, Vector2 bottomLeft, Vector2 bottomRight)
        {
            TopLeft = topLeft;
            TopRight = topRight;
            BottomLeft = bottomLeft;
            BottomRight = bottomRight;
        }

        /// <summary>
        /// Whether <paramref name="point"/> lies inside the outline TopLeft → TopRight → BottomRight →
        /// BottomLeft, by the even-odd rule, so a deform that folds the quad over itself still answers.
        /// </summary>
        public bool Contains(Vector2 point)
        {
            bool inside = false;
            if (Crosses(TopLeft, BottomLeft, point)) inside = !inside;
            if (Crosses(TopRight, TopLeft, point)) inside = !inside;
            if (Crosses(BottomRight, TopRight, point)) inside = !inside;
            if (Crosses(BottomLeft, BottomRight, point)) inside = !inside;
            return inside;
        }

        private static bool Crosses(Vector2 a, Vector2 b, Vector2 p)
            => (a.y > p.y) != (b.y > p.y) && p.x < (b.x - a.x) * (p.y - a.y) / (b.y - a.y) + a.x;
    }
}
