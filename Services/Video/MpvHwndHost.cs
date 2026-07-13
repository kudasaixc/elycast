using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Elysium_Cast_IPTV.Services.Video;

/// <summary>
/// Hosts a bare Win32 child window inside the WPF visual tree. Its HWND is handed
/// to libmpv via the "wid" option so mpv renders into it natively (vo=gpu-next /
/// D3D11). HwndHost takes care of positioning/sizing the child window to match the
/// element bounds. This is the airspace-bearing but rock-solid embedding path.
/// </summary>
public sealed class MpvHwndHost : HwndHost
{
    private const string WindowClass = "ElyCast.VideoSurface.v1";
    private const int WS_CHILD = 0x40000000;
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_CLIPCHILDREN = 0x02000000;
    private const int WS_CLIPSIBLINGS = 0x04000000;
    private const uint WM_PAINT = 0x000F;
    private const uint WM_ERASEBKGND = 0x0014;
    private const uint WM_MOUSEMOVE = 0x0200;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_RBUTTONDOWN = 0x0204;
    private const uint WM_MBUTTONDOWN = 0x0207;
    private const uint WM_MOUSEWHEEL = 0x020A;
    private const uint WM_MOUSELEAVE = 0x02A3;
    private const uint WM_PRINTCLIENT = 0x0318;
    private const uint TME_LEAVE = 0x00000002;
    private const int ErrorClassAlreadyExists = 1410;
    private static readonly object ClassSync = new();
    private static readonly WindowProc HostWindowProc = SurfaceWndProc;
    private static readonly ConcurrentDictionary<IntPtr, WeakReference<MpvHwndHost>> Hosts = new();
    private static bool _classRegistered;
    private bool _trackingMouse;

    public IntPtr Hwnd { get; private set; }

    /// <summary>Raised on the UI thread once the child HWND exists.</summary>
    public event Action<IntPtr>? HandleReady;

    /// <summary>
    /// Raised when the pointer moves or clicks over the native video surface.
    /// WPF cannot see those messages because HwndHost is an airspace boundary.
    /// </summary>
    public event Action? PointerActivity;
    public event Action? PointerLeft;
    // Raised on a left-click landing on the native surface. When AudioCore+ presents
    // over the WPF overlay, the swapchain HWND wins the z-order and swallows the click,
    // so the overlay's click-to-pause never fires; this relays it back.
    public event Action? PointerClicked;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        EnsureWindowClass();

        // A stock STATIC control repaints its background through GDI whenever
        // focus/z-order changes invalidate it. That paint races DXGI Present and
        // appears as repeated black flashes. This class owns no background brush
        // and explicitly validates paint requests: only mpv/DXGI may draw here.
        Hwnd = CreateWindowEx(
            0, WindowClass, null,
            WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN | WS_CLIPSIBLINGS,
            0, 0, 1, 1,
            hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        if (Hwnd == IntPtr.Zero)
            throw new InvalidOperationException("CreateWindowEx failed (code " + Marshal.GetLastWin32Error() + ").");

        Hosts[Hwnd] = new WeakReference<MpvHwndHost>(this);
        HandleReady?.Invoke(Hwnd);
        return new HandleRef(this, Hwnd);
    }

    private static void EnsureWindowClass()
    {
        lock (ClassSync)
        {
            if (_classRegistered) return;
            var instance = GetModuleHandle(null);
            var windowClass = new WindowClassEx
            {
                Size = (uint)Marshal.SizeOf<WindowClassEx>(),
                Instance = instance,
                WindowProc = HostWindowProc,
                ClassName = WindowClass,
                // Deliberately null: DefWindowProc must never erase the DXGI
                // backbuffer with a GDI class background brush.
                Background = IntPtr.Zero
            };
            if (RegisterClassEx(ref windowClass) == 0 &&
                Marshal.GetLastWin32Error() != ErrorClassAlreadyExists)
                throw new InvalidOperationException("RegisterClassEx failed (code " +
                                                    Marshal.GetLastWin32Error() + ").");
            _classRegistered = true;
        }
    }

    private static IntPtr SurfaceWndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (Hosts.TryGetValue(hwnd, out var weakHost) && weakHost.TryGetTarget(out var host))
            host.ForwardPointerMessage(hwnd, message);

        switch (message)
        {
            case WM_ERASEBKGND:
                return new IntPtr(1); // background already supplied by DXGI
            case WM_PAINT:
                ValidateRect(hwnd, IntPtr.Zero);
                return IntPtr.Zero;
            case WM_PRINTCLIENT:
                return IntPtr.Zero;   // never ask GDI to snapshot the surface
            default:
                return DefWindowProc(hwnd, message, wParam, lParam);
        }
    }

    private void ForwardPointerMessage(IntPtr hwnd, uint message)
    {
        switch (message)
        {
            case WM_MOUSEMOVE:
                if (!_trackingMouse)
                {
                    var tracking = new TrackMouseEventData
                    {
                        Size = (uint)Marshal.SizeOf<TrackMouseEventData>(),
                        Flags = TME_LEAVE,
                        TrackWindow = hwnd
                    };
                    _trackingMouse = TrackMouseEvent(ref tracking);
                }
                PointerActivity?.Invoke();
                break;
            case WM_LBUTTONDOWN:
            case WM_RBUTTONDOWN:
            case WM_MBUTTONDOWN:
            case WM_MOUSEWHEEL:
                PointerActivity?.Invoke();
                break;
            case WM_LBUTTONUP:
                PointerActivity?.Invoke();
                PointerClicked?.Invoke();
                break;
            case WM_MOUSELEAVE:
                _trackingMouse = false;
                PointerLeft?.Invoke();
                break;
        }
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        Hosts.TryRemove(hwnd.Handle, out _);
        _trackingMouse = false;
        if (hwnd.Handle != IntPtr.Zero) DestroyWindow(hwnd.Handle);
        Hwnd = IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(
        int dwExStyle, string lpClassName, string? lpWindowName, int dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClassEx
    {
        public uint Size;
        public uint Style;
        [MarshalAs(UnmanagedType.FunctionPtr)] public WindowProc WindowProc;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? MenuName;
        public string ClassName;
        public IntPtr SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrackMouseEventData
    {
        public uint Size;
        public uint Flags;
        public IntPtr TrackWindow;
        public uint HoverTime;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WindowClassEx windowClass);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ValidateRect(IntPtr hwnd, IntPtr rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TrackMouseEvent(ref TrackMouseEventData tracking);
}
