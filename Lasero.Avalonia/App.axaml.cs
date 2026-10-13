using System.Net.Http;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Lasero.Avalonia.Services;
using Lasero.Avalonia.Contracts;
using Lasero.Avalonia.ViewModels;
using Lasero.Core.Grbl;
using Lasero.Core.LaseroApi;
using Lasero.Core.Machines;
using Microsoft.Extensions.DependencyInjection;

namespace Lasero.Avalonia;

public sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    // SessionStore (Lasero.Core.LaseroApi) is DPAPI-based and Windows-only; see AccountViewModel's
    // own remarks for why this is declared explicitly rather than suppressed.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settingsStore = AppSettingsStore.CreateDefault();
            settingsStore.Load();

            var services = new ServiceCollection();
            services.AddSingleton<IMigrationInventory, MigrationInventory>();
            services.AddSingleton<IProjectPicker, AvaloniaProjectPicker>();
            services.AddSingleton<IProjectInspector, ProjectPackageInspector>();
            services.AddSingleton(_ => RecentProjectsStore.CreateDefault());
            services.AddSingleton(_ => JobHistoryStore.CreateDefault());
            services.AddSingleton(settingsStore);
            // Same transport/DI shape as Lasero.App/App.xaml.cs: one physical serial transport plus
            // the safe virtual/simulated one, routed by port name — connecting to the virtual port
            // never opens real hardware. Constructing GrblConnection here does not connect anything;
            // Connect() only runs from ConnectionViewModel.Connect, on an explicit user click.
            services.AddSingleton<IGrblTransport>(_ => new RoutingGrblTransport(new GrblSerialTransport(), new VirtualGrblTransport()));
            services.AddSingleton<IGrblProtocolParser, GrblProtocolParser>();
            services.AddSingleton(sp => new GrblConnection(sp.GetRequiredService<IGrblTransport>(), sp.GetRequiredService<IGrblProtocolParser>()));
            services.AddSingleton<ILaserMachine>(sp => sp.GetRequiredService<GrblConnection>());
            // Device Wizard: its own throwaway factory (independent transports) for scanning, since
            // scanning must probe ports other than the one the live singleton connection might hold.
            services.AddSingleton<ILaserMachineFactory>(_ => new GrblMachineFactory());
            services.AddSingleton(sp => new DeviceScanner(sp.GetRequiredService<ILaserMachineFactory>(), () => GrblConnection.GetAvailablePortNames()));
            // Transient + a Func<> bridge: a fresh DeviceWizardViewModel per run, matching WPF's own
            // "reopening always starts over" design (MS.DI has no automatic Func<T> factory support).
            services.AddTransient<DeviceWizardViewModel>();
            services.AddSingleton<Func<DeviceWizardViewModel>>(sp => sp.GetRequiredService<DeviceWizardViewModel>);
            services.AddSingleton<ConnectionViewModel>();
            services.AddSingleton<MachineStatusViewModel>();
            services.AddSingleton<JogViewModel>();
            services.AddSingleton<ConsoleViewModel>();
            services.AddSingleton<MaterialCatalogViewModel>();
            services.AddSingleton<SettingsViewModel>();
            services.AddSingleton<ProjectBrowserViewModel>();
            services.AddSingleton<EditorViewModel>();
            services.AddSingleton<HomeViewModel>();
            // Login/Account: the same real lasero.net Firebase auth + Netlify entitlement backend
            // Lasero.App calls, via the shared Lasero.Core.LaseroApi clients — not a placeholder.
            services.AddSingleton(_ => new HttpClient { Timeout = TimeSpan.FromSeconds(15) });
            services.AddSingleton(_ => new SessionStore());
            services.AddSingleton<LaseroAuthClient>();
            services.AddSingleton<LaseroAccountClient>();
            services.AddSingleton<AccountViewModel>();
            services.AddSingleton<ShellViewModel>();
            services.AddSingleton(provider => new MainWindow
            {
                DataContext = provider.GetRequiredService<ShellViewModel>(),
            });

            var provider = services.BuildServiceProvider();
            desktop.ShutdownRequested += (_, _) => provider.Dispose();

            // Same gate as Lasero.App/App.xaml.cs: try to resume a stored session first (silently,
            // no window shown yet), and only show LoginWindow if that did not leave the operator
            // signed in. Cancelling sign-in quits the app entirely, matching WPF's ShowDialog() != true.
            //
            // WPF can just `await` this (OnStartup is `async void`, so the Dispatcher keeps pumping
            // while it waits). OnFrameworkInitializationCompleted here returns void and is NOT
            // `async void` — Avalonia's UI-thread SynchronizationContext is already installed by this
            // point, so a plain `.GetAwaiter().GetResult()` deadlocks forever the moment
            // TryResumeSessionAsync actually awaits something (i.e. whenever a stored session file
            // exists and it hits the network to refresh it — the fast "no stored session" path never
            // awaits, so this went unnoticed until a real signed-in session.dat was on disk). Running
            // it via Task.Run first moves the whole async chain onto a thread-pool thread, so its
            // internal awaits never need to resume on the very thread this is blocking.
            var account = provider.GetRequiredService<AccountViewModel>();
            Task.Run(account.TryResumeSessionAsync).GetAwaiter().GetResult();

            if (account.IsSignedIn)
            {
                desktop.MainWindow = provider.GetRequiredService<MainWindow>();
            }
            else
            {
                var login = new LoginWindow(account);
                login.Closed += (_, _) =>
                {
                    if (account.IsSignedIn)
                    {
                        var main = provider.GetRequiredService<MainWindow>();
                        desktop.MainWindow = main;
                        main.Show();
                    }
                    else
                    {
                        desktop.Shutdown();
                    }
                };
                desktop.MainWindow = login;
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
