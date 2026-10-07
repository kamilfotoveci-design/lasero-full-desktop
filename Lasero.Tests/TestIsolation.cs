using System.Runtime.CompilerServices;

namespace Lasero.Tests;

/// <summary>
/// Tests must never write to the owner's real %LOCALAPPDATA%\Lasero (logs, session, settings). Serilog's
/// global logger is silenced for the whole test process before any test runs; stores used by tests are
/// constructed with temp-folder paths.
/// </summary>
internal static class TestIsolation
{
    [ModuleInitializer]
    internal static void SilenceGlobalLogger() => Serilog.Log.Logger = Serilog.Core.Logger.None;
}
