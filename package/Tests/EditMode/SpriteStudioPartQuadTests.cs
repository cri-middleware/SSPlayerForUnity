using NUnit.Framework;
using UnityEngine;
using SpriteStudio.Runtime;

namespace SpriteStudio.Runtime.EditModeTests
{
    public class SpriteStudioPartQuadTests
    {
        [Test]
        public void Contains_ARotatedQuad()
        {
            // A 2x2 square turned 45 degrees about the origin.
            float r = Mathf.Sqrt(2f);
            var quad = new SpriteStudioPartQuad(new Vector2(0f, r), new Vector2(r, 0f), new Vector2(-r, 0f), new Vector2(0f, -r));

            Assert.IsTrue(quad.Contains(Vector2.zero));
            Assert.IsTrue(quad.Contains(new Vector2(0.6f, 0.6f)));
            Assert.IsFalse(quad.Contains(new Vector2(0.9f, 0.9f)), "Inside the axis-aligned box, outside the quad");
            Assert.IsFalse(quad.Contains(new Vector2(5f, 0f)));
        }

        [Test]
        public void Contains_AFoldedQuad_ByTheEvenOddRule()
        {
            // TopRight and BottomRight swapped: the outline crosses itself at the centre.
            var quad = new SpriteStudioPartQuad(new Vector2(-1f, 1f), new Vector2(1f, -1f), new Vector2(-1f, -1f), new Vector2(1f, 1f));

            Assert.IsTrue(quad.Contains(new Vector2(0.5f, 0f)));
            Assert.IsTrue(quad.Contains(new Vector2(-0.5f, 0f)));
            Assert.IsFalse(quad.Contains(new Vector2(0f, 0.5f)), "Between the two triangles");
        }
    }
}
