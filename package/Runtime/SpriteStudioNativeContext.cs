using System;
using System.Runtime.InteropServices;
using SpriteStudio.SsRuntime;
using UnityEngine;

namespace SpriteStudio.Runtime
{
    /// <summary>
    /// 管理の難しい Rust 側ネイティブポインタのライフサイクル（生成・破棄）を専任するコンテキスト。
    /// SpriteStudioInternalPlayer から、SSAB バイナリの所有権や Handle の寿命管理を分離します。
    /// </summary>
    public class SpriteStudioNativeContext : IDisposable
    {
        private SsPlayerHandle _handle;
        private GCHandle _ssabPin;
        private unsafe void* _resourceHandle;
        private bool _isDisposed = false;

        public SsPlayerHandle Handle => _handle;
        public unsafe void* ResourceHandle => _resourceHandle;

        public SpriteStudioNativeContext()
        {
            unsafe
            {
                _handle = new SsPlayerHandle { ptr = NativeMethods.ss_runtime_create() };
                // 既定値 (YUp / Direct) に依存せず明示設定し、SDK 既定が将来変わっても巻き込まれないようにする。
                if (_handle.IsValid)
                {
                    NativeMethods.ss_context_set_coordinate_system(_handle.ptr, 0);
                    NativeMethods.ss_runtime_set_render_mode(_handle.ptr, (byte)0);
                }
            }
        }

        ~SpriteStudioNativeContext()
        {
            if (!_isDisposed && (_handle.IsValid || _ssabPin.IsAllocated))
            {
                Debug.LogError("[SpriteStudioNativeContext] Memory leak detected! Dispose() was not called before the object was garbage collected.");
            }
        }

        public void LoadSsab(byte[] ssabData)
        {
            if (!_handle.IsValid || ssabData == null || ssabData.Length == 0) return;

            if (_ssabPin.IsAllocated)
            {
                _ssabPin.Free();
            }

            // バイト配列のメモリを固定して Rust 側へ Borrow させる（ゼロコピー）
            _ssabPin = GCHandle.Alloc(ssabData, GCHandleType.Pinned);

            unsafe
            {
                if (_resourceHandle != null)
                {
                    NativeMethods.ss_resource_destroy(_resourceHandle);
                    _resourceHandle = null;
                }
                byte* ptr = (byte*)_ssabPin.AddrOfPinnedObject();
                _resourceHandle = NativeMethods.ss_resource_create_borrow(ptr, (nuint)ssabData.Length);
                NativeMethods.ss_runtime_load_ssab_borrow(_handle.ptr, ptr, (nuint)ssabData.Length);
            }
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            GC.SuppressFinalize(this);

            if (_handle.IsValid)
            {
                unsafe
                {
                    NativeMethods.ss_runtime_destroy(_handle.ptr);
                    _handle.ptr = null;
                }
            }
            if (_ssabPin.IsAllocated)
            {
                _ssabPin.Free();
            }
            unsafe
            {
                if (_resourceHandle != null)
                {
                    NativeMethods.ss_resource_destroy(_resourceHandle);
                    _resourceHandle = null;
                }
            }
        }
    }
}
