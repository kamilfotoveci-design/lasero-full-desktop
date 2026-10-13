using System.Windows;
using Lasero.App.Controls.Motion;

namespace Lasero.App;

public static class UiAccessibility
{
    // Respect the Windows "Show animations" accessibility preference globally, and the in-app
    // "Omezit animace" setting (EditorPreferences.ReduceMotion) on top of it.
    // Keep this list explicit so new motion tiers cannot accidentally bypass reduced motion.
    public static void ApplyMotionPreferences(bool reduceMotion = false)
    {
        LaseroMotion.ForceReducedMotion = reduceMotion;

        if (SystemParameters.ClientAreaAnimation && !reduceMotion)
        {
            RestoreThemeDurations();
            return;
        }

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

    // Removing the application-level override uncovers the theme's own duration again. A no-op at
    // start-up, when nothing was overridden yet.
    private static void RestoreThemeDurations()
    {
        foreach (var key in new[]
        {
            "Motion.VeryFast", "Motion.Fast", "Motion.Base", "Motion.Spatial", "Motion.Panel", "Motion.Hover",
            "Motion.Press", "Motion.Release", "Motion.Toggle", "Motion.Popup", "Motion.Dialog",
        })
            Application.Current.Resources.Remove(key);
    }
}
