using System;
using System.Collections.Generic;
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
        private unsafe void* _resourceHandle;
        // The buffer this context's resource was parsed from: its key in s_resources, or null when nothing
        // is loaded.
        private byte[] _ssabData;
        private bool _isDisposed = false;

        // One parsed resource per .ssab buffer, shared by every context loaded from that same array. The parse
        // is where every animation's caches are built, and a context only reads its resource, so N players of
        // one asset hold one copy instead of N -- and so do the Instance children that resolve back to their
        // own pack. Keyed by the array itself, not its contents: a reimport hands over a new array and parses
        // afresh. Touched from the main thread only, as LoadSsab and Dispose are.
        private sealed class SharedResource
        {
            public GCHandle Pin;
            public IntPtr Handle;
            public int Users;
        }
        private static readonly Dictionary<byte[], SharedResource> s_resources = new Dictionary<byte[], SharedResource>();

        /// <summary>How many parsed resources are alive; each is shared by every context loaded from its buffer.</summary>
        internal static int SharedResourceCount => s_resources.Count;

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
                    NativeMethods.ss_runtime_set_coordinate_system(_handle.ptr, 0);
                    NativeMethods.ss_runtime_set_render_mode(_handle.ptr, (byte)0);
                }
            }
        }

        ~SpriteStudioNativeContext()
        {
            if (!_isDisposed && (_handle.IsValid || _ssabData != null))
            {
                // Nothing is freed from the finalizer thread: not the native handles (it can crash on the
                // wrong thread + with an unsynchronized Job), and not the pinned buffer either, which other
                // contexts may still be reading. The buffer stays pinned for as long as the process runs.
                Debug.LogError("[SpriteStudioNativeContext] Memory leak detected! Dispose() was not called before the object was garbage collected.");
            }
        }

        public void LoadSsab(byte[] ssabData)
        {
            if (!_handle.IsValid || ssabData == null || ssabData.Length == 0) return;

            unsafe
            {
                // Take the new buffer's resource before letting go of the old one: the runtime borrows the
                // old pin until binding replaces its reference, so the old resource's last user may free the
                // pin only after that. This path is also called on a reload (runtime asset swap), so it
                // assumes the caller (SpriteStudioInternalPlayer.LoadSsab) has already Complete()d in-flight
                // Jobs and destroyed the effect slots made from the old resource.
                var resource = Acquire(ssabData);
                if (resource == IntPtr.Zero)
                {
                    // The pack's schema version is not one this runtime reads. Nothing is bound, so the
                    // runtime keeps the previous resource, which stays held (and pinned) until the next load
                    // or Dispose; SetupSlots sees the null handle and reports the failed load.
                    _resourceHandle = null;
                    return;
                }
                if (!NativeMethods.ss_runtime_bind_resource(_handle.ptr, (void*)resource))
                {
                    // The runtime still holds the previous resource, so that one stays held too.
                    Release(ssabData);
                    _resourceHandle = null;
                    return;
                }
                Release(_ssabData);
                _ssabData = ssabData;
                _resourceHandle = (void*)resource;
            }
        }

        // The resource parsed from ssabData, parsing it on first use; one more user either way. Zero when the
        // runtime cannot read the pack, with nothing registered.
        private static unsafe IntPtr Acquire(byte[] ssabData)
        {
            if (s_resources.TryGetValue(ssabData, out var shared))
            {
                shared.Users++;
                return shared.Handle;
            }

            // Pin the byte array's memory and let the Rust side borrow it (zero-copy). Parse with
            // `ss_resource_create_borrow` and bind that, rather than `ss_runtime_load_ssab_borrow`, which
            // parses on its own and drops its handle: the handle is what shares the parse, and
            // `ss_effect_slot_create` takes it.
            var pin = GCHandle.Alloc(ssabData, GCHandleType.Pinned);
            var handle = (IntPtr)NativeMethods.ss_resource_create_borrow((byte*)pin.AddrOfPinnedObject(), (nuint)ssabData.Length);
            if (handle == IntPtr.Zero)
            {
                pin.Free();
                return IntPtr.Zero;
            }
            s_resources.Add(ssabData, new SharedResource { Pin = pin, Handle = handle, Users = 1 });
            return handle;
        }

        // One user fewer of ssabData's resource. The last one destroys it and unpins the buffer, so it must
        // come after the runtime has let go of the resource (rebound or destroyed).
        private static unsafe void Release(byte[] ssabData)
        {
            if (ssabData == null || !s_resources.TryGetValue(ssabData, out var shared)) return;
            if (--shared.Users > 0) return;
            s_resources.Remove(ssabData);
            NativeMethods.ss_resource_destroy((void*)shared.Handle);
            shared.Pin.Free();
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
                    // Same invariant as LoadSsab: the runtime lets go of the resource before this context
                    // gives up its share of it, which may unpin the buffer. Assumes the caller
                    // (InternalPlayer.Dispose) has already waited for in-flight Jobs to complete.
                    if (_handle.IsValid)
                    {
                        NativeMethods.ss_runtime_destroy(_handle.ptr);
                        _handle.ptr = null;
                    }
                }
            }
            finally
            {
                // Even if the above threw: a share never given up keeps the buffer pinned for good.
                unsafe { _resourceHandle = null; }
                Release(_ssabData);
                _ssabData = null;
            }
        }
    }
}
