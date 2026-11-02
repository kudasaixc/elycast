using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Elysium_Cast_IPTV.Infrastructure;

/// <summary>user32 interop used by the player shell (monitor bounds, cursor, window styles).</summary>
internal static class NativeMethods
{
    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TOOLWINDOW = 0x00000080L;
    public const long WS_EX_NOACTIVATE = 0x08000000L;
    public const int WM_MOUSEACTIVATE = 0x0021;
    public const int MA_NOACTIVATE = 3;
    public const int WM_GETMINMAXINFO = 0x0024;
    public const int WM_WINDOWPOSCHANGED = 0x0047;
    public const int WM_ENTERSIZEMOVE = 0x0231;
    public const int WM_EXITSIZEMOVE = 0x0232;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo mi);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int RegisterWindowMessage(string lpString);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out PointInt point);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr newValue);

    [StructLayout(LayoutKind.Sequential)]
    private struct RectInt { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int cbSize; public RectInt rcMonitor; public RectInt rcWork; public uint dwFlags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointInt { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public PointInt ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize;
    }

    /// <summary>Cursor position in screen pixels, or null when Windows refuses it.</summary>
    public static Point? CursorScreenPosition() =>
        GetCursorPos(out var cursor) ? new Point(cursor.X, cursor.Y) : null;

    /// <summary>True when the cursor lies inside <paramref name="element"/>'s laid-out bounds.</summary>
    public static bool IsCursorOver(FrameworkElement element)
    {
        if (CursorScreenPosition() is not { } cursor) return false;
        try
        {
            var p = element.PointFromScreen(cursor);
            return p.X >= 0 && p.Y >= 0 && p.X < element.ActualWidth && p.Y < element.ActualHeight;
        }
        catch (InvalidOperationException)
        {
            // The element is not connected to a presentation source yet.
            return false;
        }
    }

    /// <summary>Full bounds of the monitor holding <paramref name="window"/>, in DIPs.</summary>
    public static Rect? MonitorBoundsDip(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var mi = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref mi)) return null;
        var m = HwndSource.FromHwnd(hwnd)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var tl = m.Transform(new Point(mi.rcMonitor.Left, mi.rcMonitor.Top));
        var br = m.Transform(new Point(mi.rcMonitor.Right, mi.rcMonitor.Bottom));
        return new Rect(tl.X, tl.Y, br.X - tl.X, br.Y - tl.Y);
    }

    /// <summary>
    /// WindowStyle=None makes a maximized window overflow the screen by the
    /// resize border, pushing the bottom OSD controls under the taskbar.
    /// Rewrites WM_GETMINMAXINFO so the maximized bounds equal the work area.
    /// </summary>
    public static bool TryConstrainMaximizedBounds(IntPtr hwnd, IntPtr lParam)
    {
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return false;

        var mi = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref mi)) return false;

        var mmi = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        mmi.ptMaxPosition.X = mi.rcWork.Left - mi.rcMonitor.Left;
        mmi.ptMaxPosition.Y = mi.rcWork.Top - mi.rcMonitor.Top;
        mmi.ptMaxSize.X = mi.rcWork.Right - mi.rcWork.Left;
        mmi.ptMaxSize.Y = mi.rcWork.Bottom - mi.rcWork.Top;
        Marshal.StructureToPtr(mmi, lParam, false);
        return true;
    }
}
