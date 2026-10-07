using System.IO;
using Xunit;

namespace Lasero.Tests;

/// <summary>
/// The emergency stop (feed hold + soft reset) belongs to a fatal error or to an exit while a job is active. It must
/// never run on an ordinary exit: a missing brace once made it unconditional.
/// </summary>
public sealed class AppExitSafetyTests
{
    private static string AppSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Lasero.App"))) dir = dir.Parent;
        return File.ReadAllText(Path.Combine(dir!.FullName, "Lasero.App", "App.xaml.cs"));
    }

    [Fact]
    public void OnExitStopsTheMachineOnlyWhileAJobIsActive()
    {
        var source = AppSource();
        var start = source.IndexOf("protected override void OnExit", StringComparison.Ordinal);
        Assert.True(start >= 0, "OnExit not found");
        var end = source.IndexOf("private void StopMachineAfterFatalError", start, StringComparison.Ordinal);
        var body = source[start..end];

        var guard = body.IndexOf("if (jobState is JobRunState.Running or JobRunState.Paused or JobRunState.Framing)", StringComparison.Ordinal);
        var stop = body.IndexOf("StopMachineAfterFatalError();", StringComparison.Ordinal);
        Assert.True(guard >= 0 && stop >= 0, "job guard or stop call missing");

        // The stop call is the guarded statement itself: only whitespace between the condition and the call.
        var between = body[(guard + "if (jobState is JobRunState.Running or JobRunState.Paused or JobRunState.Framing)".Length)..stop];
        Assert.True(string.IsNullOrWhiteSpace(between), "something sits between the job guard and the emergency stop: " + between.Trim());
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(body, "StopMachineAfterFatalError\\(\\)").Count);
    }
}
