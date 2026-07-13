namespace SpriteStudio.Runtime
{
    /// <summary>
    /// Blend operation of a per-part color override. Values mirror the runtime's C-API
    /// (0=Mix 1=Mul 2=Add 3=Sub) and the authored PartColor blend types.
    /// </summary>
    public enum SpriteStudioBlendOp : byte
    {
        Mix = 0,
        Mul = 1,
        Add = 2,
        Sub = 3,
    }

    /// <summary>
    /// Keyframe-vs-override precedence for the attributes that conflict with the animation
    /// (color and cell). Visibility has no priority mode: it is a plain force-hide flag.
    /// Mirrors SS6's KindIgnoreAttribute.
    /// </summary>
    public enum SpriteStudioOverridePriority : byte
    {
        /// <summary>
        /// SS6 NON: the keyframe reclaims the attribute on any frame the animation drives it,
        /// so the override only shows on frames the animation leaves that attribute alone.
        /// Cleared when a new animation is set up.
        /// </summary>
        OverwriteOnNextKeyframe = 0,

        /// <summary>
        /// SS6 NOW_ANIMATION: the override wins for the current animation and is cleared when a
        /// new animation is set up.
        /// </summary>
        HoldUntilNextAnimation = 1,

        /// <summary>
        /// SS6 PERMANENT: the override wins and survives animation changes (re-validated against
        /// the new part count within the same asset).
        /// </summary>
        Permanent = 2,
    }
}
