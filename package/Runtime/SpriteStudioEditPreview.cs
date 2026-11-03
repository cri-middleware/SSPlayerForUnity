#if UNITY_EDITOR
using UnityEngine;

namespace SpriteStudio.Runtime
{
    /// <summary>
    /// What the inspector's edit-mode preview drives on a player or a graphic. Nothing here is serialized:
    /// the preview draws frames by seeking, so it never advances the runtime and never fires an event.
    /// </summary>
    internal interface ISpriteStudioEditPreview
    {
        /// <summary>Sets the previewed animation up if needed. False until the asset is loaded.</summary>
        bool TryGetEditPreviewSection(out int start, out int end, out int fps);

        /// <summary>Shows <paramref name="frame"/> of the previewed animation.</summary>
        void ShowEditPreviewFrame(float frame);

        /// <summary>Goes back to the static preview of the start frame.</summary>
        void EndEditPreview();

        float EditPreviewSpeedScale { get; }
        bool EditPreviewSubFrame { get; }

        /// <summary>True while a Timeline drives the frame; the preview leaves it alone.</summary>
        bool EditPreviewBlocked { get; }
    }

    internal static class SpriteStudioEditPreviewClock
    {
        /// <summary>
        /// The frame a looping preview of <c>[start, end]</c> shows <paramref name="elapsed"/> seconds after it
        /// was at <paramref name="fromFrame"/>. The last frame holds for one frame's time, as in playback.
        /// </summary>
        internal static float FrameAt(int start, int end, int fps, float speedScale, float fromFrame,
                                      double elapsed, bool subFrame)
        {
            int length = end - start + 1;
            float frame;
            if (length <= 1 || fps <= 0 || speedScale <= 0f)
            {
                frame = Mathf.Clamp(fromFrame, start, end);
            }
            else
            {
                double offset = (fromFrame - start) + elapsed * fps * speedScale;
                offset %= length;
                if (offset < 0) offset += length;
                frame = start + (float)offset;
            }
            return subFrame ? frame : Mathf.Floor(frame);
        }
    }
}
#endif
