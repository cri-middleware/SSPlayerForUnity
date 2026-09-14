using System;
using System.Runtime.InteropServices;
using SpriteStudio.SsRuntime;
using UnityEngine;

namespace SpriteStudio.Runtime
{
    /// <summary>
    /// A context dedicated to the hard-to-manage lifecycle (create / destroy) of the Rust-side native pointers.
    /// Separates ownership of the SSAB binary and Handle lifetime management out of SpriteStudioInternalPlayer.
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
                // Set explicitly rather than relying on the defaults (YUp / Direct), so a future change to
                // the SDK defaults does not affect us.
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

                // Do not free native handles (ss_runtime_destroy, etc.) from the finalizer thread (it can
                // crash on the wrong thread + with an unsynchronized Job). Only the pinned GCHandle can be
                // safely Freed from the finalizer, and leaving it would keep the managed byte[] pinned
                // forever, a certain managed leak, so always release it here.
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
                // Release order matters: the old _resourceHandle and runtime borrow "the old pin's
                // address". Freeing the old pin first would leave the borrow dangling, so always do it
                // in the order:
                //   destroy old resource -> Free old pin -> Alloc new pin -> create new resource -> bind it.
                // This path is also called on a reload (runtime asset swap), so it assumes the caller
                // (SpriteStudioInternalPlayer.LoadSsab) has already Complete()d in-flight Jobs
                // (no Job reading the old buffer is still running).
                if (_resourceHandle != null)
                {
                    NativeMethods.ss_resource_destroy(_resourceHandle);
                    _resourceHandle = null;
                }

                if (_ssabPin.IsAllocated)
                {
                    _ssabPin.Free();
                }

                // Pin the byte array's memory and let the Rust side borrow it (zero-copy).
                _ssabPin = GCHandle.Alloc(ssabData, GCHandleType.Pinned);

                byte* ptr = (byte*)_ssabPin.AddrOfPinnedObject();
                // Parse once, then bind that same resource. `ss_runtime_load_ssab_borrow` would parse the
                // buffer a SECOND time -- it creates a resource of its own, binds it and drops its handle --
                // and the parse is where every animation's metadata is built, so on a large pack it is not
                // cheap. We need the handle anyway: `ss_effect_slot_create` takes it, and Dispose releases
                // it. Binding clones the reference into the context, so both stay alive until each is
                // released. A null resource (the pack's schema version is not one this runtime reads) binds
                // nothing and returns false; SetupSlots sees the null handle and reports the failed load.
                _resourceHandle = NativeMethods.ss_resource_create_borrow(ptr, (nuint)ssabData.Length);
                NativeMethods.ss_runtime_bind_resource(_handle.ptr, _resourceHandle);
            }
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            GC.SuppressFinalize(this);

            try
            {
                unsafe
                {
                    // Same invariant as LoadSsab: destroy the resource / runtime that borrow the pinned
                    // buffer first, and Free the pin last (do not free the pin while borrowed, leaving it
                    // dangling). Assumes the caller (InternalPlayer.Dispose) has already waited for
                    // in-flight Jobs to complete.
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
            }
            finally
            {
                // The pin is a managed GCHandle. Always Free it after destroying the borrowers
                // (resource/runtime), and even if the above threw. Leaking it would keep the borrowed
                // managed byte[] pinned forever, a certain leak.
                if (_ssabPin.IsAllocated)
                {
                    _ssabPin.Free();
                }
            }
        }
    }
}
