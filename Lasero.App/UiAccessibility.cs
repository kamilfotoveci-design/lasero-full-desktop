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
        Application.Current.Resources["Motion.Hover"] = new Duration(TimeSpan.Zero);
        Application.Current.Resources["Motion.Press"] = new Duration(TimeSpan.Zero);
        Application.Current.Resources["Motion.Release"] = new Duration(TimeSpan.Zero);
        Application.Current.Resources["Motion.Toggle"] = new Duration(TimeSpan.Zero);
        Application.Current.Resources["Motion.Popup"] = new Duration(TimeSpan.Zero);
        Application.Current.Resources["Motion.Dialog"] = new Duration(TimeSpan.Zero);
    }
}
