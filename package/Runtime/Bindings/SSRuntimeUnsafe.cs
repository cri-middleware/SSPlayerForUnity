using System;
using Unity.Collections.LowLevel.Unsafe;
using SpriteStudio.SsRuntime;

namespace SpriteStudio.Runtime
{
    /// <summary>
    /// The native runtime handle held by SpriteStudioNativeContext. <see cref="IsValid"/> is false once it is released.
    /// </summary>
    public unsafe struct SsPlayerHandle
    {
        [NativeDisableUnsafePtrRestriction]
        public void* ptr;

        public bool IsValid => ptr != null;
    }
}
