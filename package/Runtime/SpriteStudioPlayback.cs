namespace SpriteStudio.Runtime
{
    /// <summary>
    /// Which way the playhead is configured to walk. This is the *heading*, not
    /// where the playhead happens to be going on a given frame — under
    /// <see cref="SpriteStudioPlaybackStyle.PingPong"/> the return leg runs
    /// backward while this still reads <see cref="Forward"/>. Read
    /// <c>IsPlayingForward</c> for the actual direction of travel.
    /// </summary>
    /// <remarks>
    /// A pair of enums rather than two booleans, so the heading and the style can
    /// be set atomically in one call.
    /// The values are the wire encoding the C-API expects, which is 0/1 and not
    /// the runtime's own <c>+1 / -1</c> discriminants.
    /// </remarks>
    public enum SpriteStudioPlaybackDirection
    {
        Forward = 0,
        Backward = 1,
    }

    /// <summary>What the playhead does when it reaches the far end of the section.</summary>
    public enum SpriteStudioPlaybackStyle
    {
        /// <summary>Jump back to the start, or stop, per the loop count.</summary>
        Normal = 0,

        /// <summary>Turn around and walk back, so one loop is a full round trip.</summary>
        PingPong = 1,
    }

    /// <summary>
    /// How a track lays its animation over the tracks below it. The values are the C-API's
    /// <c>SS_RUNTIME_TRACK_BLEND_*</c>.
    /// </summary>
    public enum SpriteStudioTrackBlend
    {
        /// <summary>Lerp toward the track's pose by its alpha.</summary>
        Mix = 0,

        /// <summary>Add the track's offset from the setup pose.</summary>
        Additive = 1,
    }

    /// <summary>
    /// Where an animation laid on a track starts. The values are the C-API's
    /// <c>SS_RUNTIME_TRACK_SYNC_*</c>.
    /// </summary>
    public enum SpriteStudioTrackSync
    {
        /// <summary>From the head of its section.</summary>
        Free = 0,

        /// <summary>
        /// At the phase track 0's animation stands at (the share of its section played), matched once
        /// when the animation is laid.
        /// </summary>
        Start = 1,
    }

    /// <summary>
    /// The four corner colors of a gradient override, in one value.
    /// </summary>
    /// <remarks>
    /// One argument rather than four, so the color overrides read the same shape
    /// whether they take one color or four. The Brain's
    /// left-top / right-top / left-bottom / right-bottom order is carried by the
    /// field names instead of by position, where swapping two of them silently
    /// mirrored the gradient.
    /// </remarks>
    public readonly struct SpriteStudioCornerColors
    {
        public readonly UnityEngine.Color LeftTop;
        public readonly UnityEngine.Color RightTop;
        public readonly UnityEngine.Color LeftBottom;
        public readonly UnityEngine.Color RightBottom;

        public SpriteStudioCornerColors(
            UnityEngine.Color leftTop,
            UnityEngine.Color rightTop,
            UnityEngine.Color leftBottom,
            UnityEngine.Color rightBottom)
        {
            LeftTop = leftTop;
            RightTop = rightTop;
            LeftBottom = leftBottom;
            RightBottom = rightBottom;
        }
    }
}
