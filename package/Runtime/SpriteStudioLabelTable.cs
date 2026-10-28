using System.Collections.Generic;
using System.Linq;

namespace SpriteStudio.Runtime
{
    /// <summary>A label of one animation: its name and the frame it marks.</summary>
    public readonly struct SpriteStudioLabel
    {
        public readonly string Name;
        public readonly int Frame;

        public SpriteStudioLabel(string name, int frame)
        {
            Name = name;
            Frame = frame;
        }
    }

    /// <summary>
    /// The labels of one animation in frame order, and the frame range it was authored with — what a
    /// section named by labels resolves against.
    /// </summary>
    public sealed class SpriteStudioLabelTable
    {
        private readonly SpriteStudioLabel[] _labels;

        /// <summary>In frame order. The pack keeps them sorted by name hash instead.</summary>
        public IReadOnlyList<SpriteStudioLabel> Labels => _labels;

        /// <summary>The animation's own first frame — never a section's.</summary>
        public int StartFrame { get; }

        /// <summary>The animation's own last frame — never a section's.</summary>
        public int EndFrame { get; }

        public SpriteStudioLabelTable(IEnumerable<SpriteStudioLabel> labels, int startFrame, int endFrame)
        {
            _labels = labels.OrderBy(l => l.Frame).ToArray();
            StartFrame = startFrame;
            EndFrame = endFrame;
        }

        internal static SpriteStudioLabelTable Of(ss.format.AnimationData animation)
        {
            var labels = new List<SpriteStudioLabel>(animation.LabelsLength);
            for (int i = 0; i < animation.LabelsLength; i++)
            {
                var label = animation.Labels(i);
                string name = label?.Name;
                if (name != null) labels.Add(new SpriteStudioLabel(name, label.Value.Time));
            }
            return new SpriteStudioLabelTable(labels, animation.StartFrame, animation.EndFrame);
        }

        /// <summary>
        /// The frame <paramref name="label"/> marks, matched exactly. <c>_start</c> and <c>_end</c> are the
        /// animation's first and last frame, as they are in SpriteStudio, where they are never in the table;
        /// a real label of the same name wins. False when the animation has no such label.
        /// </summary>
        public bool TryGetFrame(string label, out int frame)
        {
            foreach (var l in _labels)
            {
                if (l.Name == label)
                {
                    frame = l.Frame;
                    return true;
                }
            }
            if (label == "_start") { frame = StartFrame; return true; }
            if (label == "_end") { frame = EndFrame; return true; }
            frame = 0;
            return false;
        }
    }
}
