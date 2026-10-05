using System.Windows;
using System.Windows.Media;

namespace Lasero.App.Controls.Motion;

/// <summary>
/// The one place that decides whether brand motion may animate. Same rule the rest of the app uses
/// (SystemParameters.ClientAreaAnimation plus a hardware-accelerated render tier), plus an explicit
/// switch for an in-app "reduce motion" setting or for tests. When it returns false every control in
/// this folder shows its final frame and runs no clock at all.
/// </summary>
public static class LaseroMotion
{
    /// <summary>Set true to force the final frame everywhere (a future setting, or tests).</summary>
    public static bool ForceReducedMotion { get; set; }

    /// <summary>Play even though Windows animations are off. Only an explicit user choice (a setting) or a QA/demo harness
    /// may set this; the default honours the system accessibility preference.</summary>
    public static bool ForceAnimations { get; set; }

    public static bool AnimationsEnabled =>
        !ForceReducedMotion && (ForceAnimations || (SystemParameters.ClientAreaAnimation && RenderCapability.Tier > 0));
}
