using System.Reactive.Concurrency;
using ReactiveUI.Builder;

namespace Lasero.Avalonia.Tests;

/// <summary>
/// RxAppBuilder.CreateReactiveUIBuilder().WithCoreServices().BuildApp() mutates process-global
/// ReactiveUI state; calling it once per test (as every test class in this project used to) is only
/// safe when tests run one at a time. Consolidating to a single call, guarded by Lazy&lt;T&gt;'s own
/// thread-safety, removes that specific source of cross-class interference.
/// CanExecute assertions need a deterministic scheduler because this headless test process has no
/// Avalonia UI loop. The production app supplies its dispatcher scheduler; tests use immediate
/// scheduling so the latest CanExecute state is observable as soon as the machine signal is raised.
/// </summary>
internal static class TestApp
{
    private static readonly Lazy<bool> Initialized = new(() =>
    {
        RxAppBuilder.CreateReactiveUIBuilder()
            .WithMainThreadScheduler(ImmediateScheduler.Instance)
            .WithCoreServices()
            .BuildApp();
        return true;
    });

    public static void EnsureInitialized() => _ = Initialized.Value;
}
