using System.Windows;

namespace Lasero.App;

public static class UiAccessibility
{
    // Respect the Windows "Show animations" accessibility preference globally.
    // Motion.Fast and Motion.Base are shared by all interactive controls.
    public static void ApplyMotionPreferences()
    {
        if (SystemParameters.ClientAreaAnimation) return;

        Application.Current.Resources["Motion.Fast"] = new Duration(TimeSpan.Zero);
        Application.Current.Resources["Motion.Base"] = new Duration(TimeSpan.Zero);
    }
}
