#if ENABLE_SPAN_T && UNSAFE_BYTEBUFFER
using System;

namespace SpriteStudio.Runtime
{
    /// <summary>
    /// ssruntime (FFI) から受け取ったネイティブ (アンマネージド) バッファを、
    /// コピーせずに FlatBuffers の ByteBuffer へ載せるための読み取り専用アロケータ。
    /// Span&lt;byte&gt; は managed 配列だけでなく生ポインタも指せるため、これが唯一のゼロコピー経路。
    ///
    /// 利用上の注意:
    ///  - バッファはネイティブ側 (Rust) が所有する借用ポインタ。次の step / destroy で無効化されるため、
    ///    Reset 直後に同期的に読み切ること。
    ///  - 1 インスタンスを Reset で使い回せばゼロ GC (ByteBuffer も再利用可)。
    ///  - 書き込み (GrowFront) は非対応。読み取り専用。
    /// </summary>
    internal sealed unsafe class NativeByteBufferAllocator : Google.FlatBuffers.ByteBufferAllocator
    {
        private byte* _ptr;

        public NativeByteBufferAllocator()
        {
            _ptr = null;
            Length = 0;
        }

        /// <summary>新しいネイティブバッファを指す (確保もコピーもしない)。</summary>
        public void Reset(byte* ptr, int length)
        {
            _ptr = ptr;
            Length = length;
        }

        public override Span<byte> Span => new Span<byte>(_ptr, Length);
        public override ReadOnlySpan<byte> ReadOnlySpan => new ReadOnlySpan<byte>(_ptr, Length);

        // FlatBuffers の読み取り経路 (GetRootAs + scalar/struct アクセサ) は Span/ReadOnlySpan のみ使用。
        // Memory<byte> はネイティブメモリを安全に表現するのに MemoryManager が必要で、本用途では呼ばれないため未対応。
        public override Memory<byte> Memory =>
            throw new NotSupportedException("NativeByteBufferAllocator does not support Memory<byte>.");
        public override ReadOnlyMemory<byte> ReadOnlyMemory =>
            throw new NotSupportedException("NativeByteBufferAllocator does not support ReadOnlyMemory<byte>.");

        // ネイティブバッファは読み取り専用。FlatBuffers の読み取りでは GrowFront は呼ばれない。
        public override void GrowFront(int newSize) =>
            throw new NotSupportedException("NativeByteBufferAllocator is read-only and cannot grow.");
    }
}
#endif
