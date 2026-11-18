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
        [Tooltip("Name of the part to follow (ssab PartData.Name).")]
        public string partName;

        [Tooltip("User-owned Transform to make follow this part. May be directly under the player or in a separate hierarchy.")]
        public Transform target;

        [Tooltip("ON (default): follow the part's position. A follower that does not follow the position is not following.")]
        public bool drivePosition = true;

        [Tooltip("ON (default): follow the part's rotation. A weapon in a hand turns with the hand.")]
        public bool driveRotation = true;

        [Tooltip("ON: also reflect the inherited scale (excluding localScale). OFF (default): a follower is placed at the part, not sized by it.")]
        public bool driveScale = false;

        [Tooltip("Behavior on frames where the part is hidden. FollowAlways = keep following / Deactivate = hide the target.")]
        public SpriteStudioPartTrackerHide onHide = SpriteStudioPartTrackerHide.FollowAlways;

        // Cached part index resolved from partName. Re-resolved when set to Dirty (e.g. after the
        // animation/asset changes). Not serialized: rebuilt at runtime from partName.
        internal const int ResolveDirty = -2;
        internal const int ResolveNotFound = -1;
        [System.NonSerialized] internal int ResolvedIndex = ResolveDirty;

        // The target this tracker deactivated itself -- for an absent part, or a hidden one under
        // Deactivate -- or null. Only that target is activated again when the condition clears, so one the
        // user deactivated stays inactive. Not serialized: rebuilt at runtime.
        [System.NonSerialized] internal Transform HiddenTarget;
    }
}
