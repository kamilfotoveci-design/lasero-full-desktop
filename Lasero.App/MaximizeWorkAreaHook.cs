using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Lasero.App;

/// <summary>
/// Keeps a borderless (<c>WindowStyle="None"</c> + <see cref="System.Windows.Shell.WindowChrome"/>)
/// window inside the monitor's work area when it is maximized.
///
/// Windows maximizes such a window to the monitor bounds inflated by the resize border, on the
/// assumption that a normal frame will cover the overhang. There is no frame here, so the last few
/// pixels of the window — the persistent job strip with Frame and Start — ended up off-screen behind
/// the taskbar. Handling WM_GETMINMAXINFO lets us state the maximized size ourselves.
///
/// The size comes from the monitor the window is actually on (MonitorFromWindow), not from
/// SystemParameters.WorkArea, so a second monitor with a different resolution or a taskbar on a
/// different edge is handled too.
/// </summary>
internal static class MaximizeWorkAreaHook
{
    private const int WmGetMinMaxInfo = 0x0024;
    private const int MonitorDefaultToNearest = 0x00000002;

    public static void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (PresentationSource.FromVisual(window) is HwndSource existing)
        {
            existing.AddHook(WndProc);
            return;
        }

        window.SourceInitialized += (_, _) =>
        {
            if (PresentationSource.FromVisual(window) is HwndSource source)
                source.AddHook(WndProc);
        };
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmGetMinMaxInfo) return IntPtr.Zero;

        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return IntPtr.Zero;

        var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return IntPtr.Zero;

        var minMax = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        var work = info.rcWork;
        var screen = info.rcMonitor;

        // Position and size are expressed relative to the monitor's own origin, not the desktop.
        minMax.ptMaxPosition.x = work.left - screen.left;
        minMax.ptMaxPosition.y = work.top - screen.top;
        minMax.ptMaxSize.x = work.right - work.left;
        minMax.ptMaxSize.y = work.bottom - work.top;
        minMax.ptMaxTrackSize.x = minMax.ptMaxSize.x;
        minMax.ptMaxTrackSize.y = minMax.ptMaxSize.y;

        Marshal.StructureToPtr(minMax, lParam, fDeleteOld: true);
        handled = true;
        return IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public Point32 ptReserved;
        public Point32 ptMaxSize;
        public Point32 ptMaxPosition;
        public Point32 ptMinTrackSize;
        public Point32 ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public Rect32 rcMonitor;
        public Rect32 rcWork;
        public int dwFlags;
    }
}
