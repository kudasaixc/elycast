using System.Runtime.InteropServices;

namespace Elysium_Cast_IPTV.Services.Video;

/// <summary>
/// Thin, verified-correct P/Invoke layer over libmpv for property and command
/// access. We deliberately bypass LibMPVSharp's higher-level accessors here: that
/// beta binding has marshalling bugs (e.g. a 1-byte buffer for the 4-byte
/// MPV_FORMAT_FLAG) that corrupt the process heap. Everything below matches the
/// libmpv C ABI exactly (cdecl, UTF-8 strings, correct buffer sizes).
/// </summary>
internal static class MpvInterop
{
    private const int MPV_FORMAT_INT64 = 4;
    private const int MPV_FORMAT_DOUBLE = 5;

    public static int SetString(IntPtr ctx, string name, string value)
        => ctx == IntPtr.Zero ? -1 : mpv_set_property_string(ctx, name, value);

    public static string GetString(IntPtr ctx, string name)
    {
        if (ctx == IntPtr.Zero) return "";
        var ptr = mpv_get_property_string(ctx, name);
        if (ptr == IntPtr.Zero) return "";
        try { return Marshal.PtrToStringUTF8(ptr) ?? ""; }
        finally { mpv_free(ptr); }
    }

    public static long GetLong(IntPtr ctx, string name)
    {
        if (ctx == IntPtr.Zero) return 0;
        return mpv_get_property(ctx, name, MPV_FORMAT_INT64, out long value) >= 0 ? value : 0;
    }

    public static double GetDouble(IntPtr ctx, string name)
    {
        if (ctx == IntPtr.Zero) return 0;
        return mpv_get_property_double(ctx, name, MPV_FORMAT_DOUBLE, out double value) >= 0 ? value : 0;
    }

    public static int Command(IntPtr ctx, params string[] args)
    {
        if (ctx == IntPtr.Zero) return -1;

        // Build a NULL-terminated array of UTF-8 string pointers (const char**).
        var ptrs = new IntPtr[args.Length + 1];
        try
        {
            for (var i = 0; i < args.Length; i++)
                ptrs[i] = Utf8ToHGlobal(args[i]);
            ptrs[args.Length] = IntPtr.Zero;

            var handle = GCHandle.Alloc(ptrs, GCHandleType.Pinned);
            try { return mpv_command(ctx, handle.AddrOfPinnedObject()); }
            finally { handle.Free(); }
        }
        finally
        {
            for (var i = 0; i < args.Length; i++)
                if (ptrs[i] != IntPtr.Zero) Marshal.FreeHGlobal(ptrs[i]);
        }
    }

    private static IntPtr Utf8ToHGlobal(string s)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(s + '\0');
        var ptr = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, ptr, bytes.Length);
        return ptr;
    }

    [DllImport("libmpv-2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int mpv_set_property_string(
        IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string data);

    [DllImport("libmpv-2", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr mpv_get_property_string(
        IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport("libmpv-2", CallingConvention = CallingConvention.Cdecl)]
    private static extern void mpv_free(IntPtr data);

    [DllImport("libmpv-2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int mpv_get_property(
        IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format, out long data);

    [DllImport("libmpv-2", CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_get_property")]
    private static extern int mpv_get_property_double(
        IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format, out double data);

    [DllImport("libmpv-2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int mpv_command(IntPtr ctx, IntPtr args);
}
