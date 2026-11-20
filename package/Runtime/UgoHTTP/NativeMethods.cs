using System;
using System.Runtime.InteropServices;

namespace UgoHttp.Native
{
    internal static unsafe class NativeMethods
    {
#if (UNITY_IOS || UNITY_SWITCH) && !UNITY_EDITOR
        const string Library = "__Internal";
#else
        const string Library = "ugo";
#endif
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern long ugo_monotonic_us();
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ugo_shutdown_all();
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ugo_client_create(in ugo_client_config config, out IntPtr client);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ugo_client_shutdown(UgoHandle client);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void ugo_client_destroy(IntPtr client);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ugo_submit(UgoHandle client, in ugo_request request, out ulong id);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ugo_abort(UgoHandle client, ulong id);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ugo_release(UgoHandle client, ulong id);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ugo_get_result(UgoHandle client, ulong id, out ugo_result result);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ugo_wait_events(UgoHandle client, UgoEvent* events, uint capacity, uint timeoutMs);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ugo_ws_open(UgoHandle client, in ugo_ws_request request, out ulong id);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ugo_ws_send(UgoHandle client, ulong id, uint type, byte* data, uint len);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ugo_ws_close(UgoHandle client, ulong id, uint code, byte* reason, uint reasonLen);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ugo_ws_release(UgoHandle client, ulong id, ulong throughSeq);
    }
}
