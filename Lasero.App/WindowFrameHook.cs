using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Lasero.App;

/// <summary>
/// Rounds a borderless window's corners and draws a hairline border around it, through DWM.
///
/// A <c>WindowStyle="None"</c> window opts out of the native frame, and with it out of the rounding
/// and the 1px edge that Windows 11 gives every other window. On a light desktop that leaves a white
/// app with square corners bleeding into whatever is behind it, and no way to see where the window
/// ends.
///
/// Asking DWM for it rather than drawing it ourselves matters: the rounding is applied to the window
/// region, so it clips correctly, casts the system shadow and does not affect layout or hit-testing
/// the way a rounded <c>Border</c> inside the window would. Windows squares the corners off itself
/// while the window is maximized, which is the behaviour every other app has.
///
/// Both attributes are Windows 11 (build 22000) and later. On Windows 10 the calls fail and are
/// ignored — the window simply looks the way it did before.
/// </summary>
internal static class WindowFrameHook
{
    private const int WmSysKeyDown = 0x0104;
    private const int VkSpace = 0x20;
    private const long AltContextMask = 0x20000000;
    private static readonly ConditionalWeakTable<Window, object> AttachedWindows = new();
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColour = 34;
    private const int DwmwcpRound = 2;

    /// <summary>First Windows 11 build. DWM only understands the corner-preference and border-colour
    /// attributes from here on; older builds fail the call, so nothing is requested there and the
    /// window keeps its square look instead of half-applying a frame.</summary>
    internal const int RoundedCornersMinimumBuild = 22000;

    /// <summary>The corner preference to ask DWM for on a given OS build: round on Windows 11, nothing
    /// (<c>null</c>) on anything older. Pure so it can be tested without a window handle.</summary>
    internal static int? ChooseCornerPreference(int osBuild) =>
        osBuild >= RoundedCornersMinimumBuild ? DwmwcpRound : null;

    public static void Attach(Window window, Brush? borderBrush = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!TryMarkAttached(window)) return;

        if (PresentationSource.FromVisual(window) is HwndSource source)
        {
            AttachNativeCommands(window, source);
            Apply(source.Handle, borderBrush);
            return;
        }

        window.SourceInitialized += OnSourceInitialized;

        void OnSourceInitialized(object? sender, EventArgs args)
        {
            window.SourceInitialized -= OnSourceInitialized;
            if (PresentationSource.FromVisual(window) is HwndSource initialised)
            {
                AttachNativeCommands(window, initialised);
                Apply(initialised.Handle, borderBrush);
            }
        }
    }

    private static bool TryMarkAttached(Window window)
    {
        lock (AttachedWindows)
        {
            if (AttachedWindows.TryGetValue(window, out _)) return false;
            AttachedWindows.Add(window, new object());
            return true;
        }
    }

    private static void AttachNativeCommands(Window window, HwndSource source)
    {
        HwndSourceHook hook = (IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (message != WmSysKeyDown || wParam.ToInt32() != VkSpace || (lParam.ToInt64() & AltContextMask) == 0)
                return IntPtr.Zero;

            var menuOrigin = window.PointToScreen(new Point(8, 8));
            SystemCommands.ShowSystemMenu(window, menuOrigin);
            handled = true;
            return IntPtr.Zero;
        };
        source.AddHook(hook);
        window.Closed += (_, _) => source.RemoveHook(hook);
    }

    private static void Apply(IntPtr handle, Brush? borderBrush)
    {
        if (handle == IntPtr.Zero) return;

        if (ChooseCornerPreference(Environment.OSVersion.Version.Build) is not { } preference) return;
        _ = DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref preference, sizeof(int));

        if (borderBrush is not SolidColorBrush solid) return;
        var colour = ToColourRef(solid.Color);
        _ = DwmSetWindowAttribute(handle, DwmwaBorderColour, ref colour, sizeof(int));
    }

    /// <summary>DWM wants a COLORREF — 0x00BBGGRR — which is the opposite channel order to everything
    /// else in WPF, and the reason a border set from a brush comes out the wrong hue if copied
    /// straight across.</summary>
    private static int ToColourRef(Color colour) => colour.R | (colour.G << 8) | (colour.B << 16);

    [DllImport("dwmapi.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
