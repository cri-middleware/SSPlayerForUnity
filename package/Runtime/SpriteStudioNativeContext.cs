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

                // ネイティブハンドル (ss_runtime_destroy 等) はファイナライザスレッドから解放しない
                // (誤ったスレッド + Job 未同期でクラッシュしうる)。ただし pin した GCHandle だけは
                // ファイナライザから安全に Free でき、放置すると managed byte[] を永久に固定し続けて
                // 確実なマネージドリークになるため、ここで必ず解放する。
                if (_ssabPin.IsAllocated)
                {
                    _ssabPin.Free();
                }
            }
        }

        public void LoadSsab(byte[] ssabData)
        {
            if (!_handle.IsValid || ssabData == null || ssabData.Length == 0) return;

            unsafe
            {
                // 解放順序が重要: 旧 _resourceHandle と runtime は「旧 pin のアドレス」を borrow して
                // いる。先に旧 pin を Free すると borrow が dangling になるため、必ず
                //   旧リソース破棄 → 旧 pin Free → 新 pin Alloc → 新リソース作成 → runtime へ load
                // の順で行う。なおこの経路は再ロード (実行時のアセット差し替え) でも呼ばれるので、
                // 呼び出し側 (SpriteStudioInternalPlayer.LoadSsabInternal) で in-flight Job を
                // Complete() 済みであることが前提 (旧バッファを読む Job が走っていないこと)。
                if (_resourceHandle != null)
                {
                    NativeMethods.ss_resource_destroy(_resourceHandle);
                    _resourceHandle = null;
                }

                if (_ssabPin.IsAllocated)
                {
                    _ssabPin.Free();
                }

                // バイト配列のメモリを固定して Rust 側へ Borrow させる（ゼロコピー）
                _ssabPin = GCHandle.Alloc(ssabData, GCHandleType.Pinned);

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

            unsafe
            {
                // LoadSsab と同じ不変条件: pin した buffer を borrow している resource / runtime を
                // 先に破棄し、最後に pin を Free する (借用中に pin を解放して dangling にしない)。
                // in-flight Job の完了待ちは呼び出し側 (InternalPlayer.Dispose) で実施済み前提。
                if (_resourceHandle != null)
                {
                    NativeMethods.ss_resource_destroy(_resourceHandle);
                    _resourceHandle = null;
                }
                if (_handle.IsValid)
                {
                    NativeMethods.ss_runtime_destroy(_handle.ptr);
                    _handle.ptr = null;
                }
            }
            if (_ssabPin.IsAllocated)
            {
                _ssabPin.Free();
            }
        }
    }
}
