using System.Runtime.InteropServices;
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
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColour = 34;
    private const int DwmwcpRound = 2;

    public static void Attach(Window window, Brush? borderBrush = null)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (PresentationSource.FromVisual(window) is HwndSource source)
        {
            Apply(source.Handle, borderBrush);
            return;
        }

        window.SourceInitialized += OnSourceInitialized;

        void OnSourceInitialized(object? sender, EventArgs args)
        {
            window.SourceInitialized -= OnSourceInitialized;
            if (PresentationSource.FromVisual(window) is HwndSource initialised)
                Apply(initialised.Handle, borderBrush);
        }
    }

    private static void Apply(IntPtr handle, Brush? borderBrush)
    {
        if (handle == IntPtr.Zero) return;

        var preference = DwmwcpRound;
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
