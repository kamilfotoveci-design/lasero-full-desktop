using Lasero.Core.Jobs;

namespace Lasero.App.ViewModels;

/// <summary>
/// Which persistent controls belong on which screen. The single source for the rules written up in
/// docs/screen-controls-matrix.md, so the XAML bindings and the tests read the same decisions.
///
/// The principle: job controls belong to the design workspace. Elsewhere they appear only while a job
/// is actually active, because stopping a running job must be reachable from anywhere. Machine
/// connection state stays global because "is my laser connected" matters on every screen.
/// </summary>
public static class ScreenChrome
{
    /// <summary>Rámovat and Spustit start something, so they exist only where the design is visible.</summary>
    public static bool ShowLaunchControls(AppScreen screen) => screen == AppScreen.Designer;

    /// <summary>The layer colour palette assigns a laser operation to the selected object.</summary>
    public static bool ShowLayerPalette(AppScreen screen) => screen == AppScreen.Designer;

    /// <summary>
    /// Job badge, file name and strip message. Idle and Ready say nothing a non-design screen needs;
    /// every other state (active, finished, cancelled, faulted) is a result the operator must be able
    /// to see wherever they are.
    /// </summary>
    public static bool ShowJobDetails(AppScreen screen, JobRunState state)
        => screen == AppScreen.Designer || state is not (JobRunState.Idle or JobRunState.Ready);

    /// <summary>Pozastavit, Pokračovat and Zastavit (plus the progress bar): only while a job is active,
    /// on every screen. This mirrors the JobState triggers on the button styles.</summary>
    public static bool ShowActiveJobControls(JobRunState state)
        => state is JobRunState.Preparing or JobRunState.Framing or JobRunState.Running or JobRunState.Paused;

    /// <summary>The right-hand zone of the strip (and the divider before it) has content at all.</summary>
    public static bool ShowJobActionZone(AppScreen screen, JobRunState state)
        => ShowLaunchControls(screen) || ShowActiveJobControls(state);

    /// <summary>The title bar's machine settings shortcut. The Device screen carries its own button,
    /// Home and Chat have no use for it.</summary>
    public static bool ShowDeviceSettingsShortcut(AppScreen screen) => screen == AppScreen.Designer;

    /// <summary>The strip's Připojit. Home (device rail) and Zařízení (status card) already carry a
    /// connect call-to-action in the same state, so repeating it in the strip is a duplicate there. Návrh and
    /// Chat have none of their own, so the strip keeps it. The connected/disconnected condition stays on
    /// the button itself.</summary>
    public static bool ShowStripConnect(AppScreen screen) => screen is AppScreen.Designer or AppScreen.Chat;

    /// <summary>
    /// The job badge ("Bez úlohy", "Návrh neodeslán"). On a compact window (under 1200 px) the strip has no room for it
    /// beside the layer palette and the job buttons, and while the job is idle it says less than the next-step
    /// message does, so it is shown only once a job has left Idle and Ready.
    /// </summary>
    public static bool ShowJobBadge(AppScreen screen, JobRunState state, bool compact)
        => ShowJobDetails(screen, state) && (!compact || state is not (JobRunState.Idle or JobRunState.Ready));

    /// <summary>The window width under which the strip drops the idle job badge.</summary>
    public const double CompactWidth = 1200;
}
