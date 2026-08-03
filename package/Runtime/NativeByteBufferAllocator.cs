#if ENABLE_SPAN_T && UNSAFE_BYTEBUFFER
using System;

namespace SpriteStudio.Runtime
{
    /// <summary>
    /// Read-only allocator that mounts a native (unmanaged) buffer received from ssruntime
    /// (FFI) onto a FlatBuffers ByteBuffer without copying. Since Span&lt;byte&gt; can point at
    /// a raw pointer as well as a managed array, this is the only zero-copy path.
    ///
    /// Usage notes:
    ///  - The buffer is a borrowed pointer owned by the native side (Rust). It is invalidated
    ///    on the next step / destroy, so read it synchronously right after Reset.
    ///  - Reusing a single instance via Reset yields zero GC (the ByteBuffer can be reused too).
    ///  - Writing (GrowFront) is not supported. Read-only.
    /// </summary>
    internal sealed unsafe class NativeByteBufferAllocator : Google.FlatBuffers.ByteBufferAllocator
    {
        private byte* _ptr;

        public NativeByteBufferAllocator()
        {
            _ptr = null;
            Length = 0;
        }

        /// <summary>Point at a new native buffer (neither allocates nor copies).</summary>
        public void Reset(byte* ptr, int length)
        {
            _ptr = ptr;
            Length = length;
        }

        public override Span<byte> Span => new Span<byte>(_ptr, Length);
        public override ReadOnlySpan<byte> ReadOnlySpan => new ReadOnlySpan<byte>(_ptr, Length);

        // The FlatBuffers read path (GetRootAs + scalar/struct accessors) uses only Span/ReadOnlySpan.
        // Memory<byte> would require a MemoryManager to safely represent native memory and is never
        // called in this use case, so it is not supported.
        public override Memory<byte> Memory =>
            throw new NotSupportedException("NativeByteBufferAllocator does not support Memory<byte>.");
        public override ReadOnlyMemory<byte> ReadOnlyMemory =>
            throw new NotSupportedException("NativeByteBufferAllocator does not support ReadOnlyMemory<byte>.");

        // The native buffer is read-only. GrowFront is never called on the FlatBuffers read path.
        public override void GrowFront(int newSize) =>
            throw new NotSupportedException("NativeByteBufferAllocator is read-only and cannot grow.");
    }
}
#endif
