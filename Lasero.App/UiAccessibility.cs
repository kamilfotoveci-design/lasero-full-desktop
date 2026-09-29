using System.Windows;

namespace Lasero.App;

public static class UiAccessibility
{
    // Respect the Windows "Show animations" accessibility preference globally.
    // Keep this list explicit so new motion tiers cannot accidentally bypass reduced motion.
    public static void ApplyMotionPreferences()
    {
        if (SystemParameters.ClientAreaAnimation) return;

        Application.Current.Resources["Motion.VeryFast"] = new Duration(TimeSpan.Zero);
        Application.Current.Resources["Motion.Fast"] = new Duration(TimeSpan.Zero);
        Application.Current.Resources["Motion.Base"] = new Duration(TimeSpan.Zero);
        Application.Current.Resources["Motion.Spatial"] = new Duration(TimeSpan.Zero);
        Application.Current.Resources["Motion.Panel"] = new Duration(TimeSpan.Zero);
    }
}
