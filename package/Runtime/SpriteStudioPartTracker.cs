using UnityEngine;

namespace SpriteStudio.Runtime
{
    /// <summary>
    /// Behaviour of a part tracker when the tracked part is <c>hide</c> on the current frame.
    /// (A part that is absent from the animation entirely is always deactivated, regardless of this.)
    /// </summary>
    public enum SpriteStudioPartTrackerHide
    {
        /// <summary>Keep following the part's coordinate frame even while it is hidden (default).</summary>
        FollowAlways,
        /// <summary>Deactivate the target GameObject while the part is hidden.</summary>
        Deactivate,
    }

    /// <summary>
    /// A single part-tracking entry: drives a user-owned <see cref="target"/> Transform so it follows
    /// the named SpriteStudio part each frame. The player never creates or destroys the target
    /// (constraint-style); the user owns its lifecycle and may parent it under the player (exact, scale
    /// included) or place it anywhere (world position/rotation exact; scale approximate — see driveScale).
    /// </summary>
    [System.Serializable]
    public class SpriteStudioPartTracker
    {
        [Tooltip("追従対象のパーツ名 (ssab の PartData.Name)。")]
        public string partName;

        [Tooltip("このパーツに追従させるユーザー所有の Transform。プレーヤー直下でも別階層でも可。")]
        public Transform target;

        [Tooltip("ON: localScale を除いた継承スケールも反映。OFF (既定): 位置と回転のみ追従。")]
        public bool driveScale = false;

        [Tooltip("パーツが hide のフレームでの挙動。FollowAlways=追従継続 / Deactivate=対象を非表示。")]
        public SpriteStudioPartTrackerHide onHide = SpriteStudioPartTrackerHide.FollowAlways;

        // Cached part index resolved from partName. Re-resolved when set to Dirty (e.g. after the
        // animation/asset changes). Not serialized: rebuilt at runtime from partName.
        internal const int ResolveDirty = -2;
        internal const int ResolveNotFound = -1;
        [System.NonSerialized] public int ResolvedIndex = ResolveDirty;
    }
}
