using System;
using Unity.Collections.LowLevel.Unsafe;
using SpriteStudio.SsRuntime;

namespace SpriteStudio.Runtime
{
    /// <summary>
    /// Wrapper struct for safely handling a native pointer (void*) from within an ECS
    /// (Entities) IComponentData or a Burst Job.
    /// </summary>
    public unsafe struct SsPlayerHandle
    {
        [NativeDisableUnsafePtrRestriction]
        public void* ptr;

        public bool IsValid => ptr != null;
    }
}
