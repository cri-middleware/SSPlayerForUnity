#if UNITY_EDITOR
using System.Reflection;

namespace SpriteStudio.Runtime.Tests
{
    /// <summary>
    /// The Editor's conversion runner as these tests reach it: through reflection, since the runtime test
    /// assembly cannot reference the Editor one.
    /// </summary>
    internal static class ConversionRunnerProbe
    {
        /// <summary>
        /// Whether the runner has nothing queued or running. A converted file exists from the moment the
        /// converter starts writing it, so a fixture waits for this as well as for the file; without it a
        /// fresh tree could read a pack that was still being written. True when there is no runner to ask.
        /// </summary>
        public static bool IsIdle(System.Type runnerType)
        {
            var idle = runnerType?.GetProperty("IsIdle", BindingFlags.Public | BindingFlags.Static);
            return idle == null || (bool)idle.GetValue(null);
        }
    }
}
#endif
