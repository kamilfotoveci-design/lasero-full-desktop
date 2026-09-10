using System.IO;
using System.Net.Http;
using System.Windows;
using Lasero.App.ViewModels;
using Lasero.Core.Grbl;
using Lasero.Core.Jobs;
using Lasero.Core.LaseroApi;
using Lasero.Core.Machines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace Lasero.App;

public partial class App : Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            var errorId = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
            Log.Fatal(args.Exception, "Unhandled UI-thread exception {ErrorId}", errorId);
            StopMachineAfterFatalError();
            MessageBox.Show(
                $"Došlo k neočekávané chybě. Aplikace bude bezpečně ukončena.\n\nKód chyby: {errorId}",
                "Lasero Desktop — neočekávaná chyba",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
            Shutdown(-1);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log.Fatal(args.ExceptionObject as Exception, "Unhandled non-UI-thread exception");
            StopMachineAfterFatalError();
        };

        var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lasero", "logs");
        Directory.CreateDirectory(logDir);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(Path.Combine(logDir, "lasero-.log"), rollingInterval: RollingInterval.Day)
            .CreateLogger();

        var settingsStore = AppSettingsStore.CreateDefault();
        var settings = settingsStore.Load();

        _host = Host.CreateDefaultBuilder()
            .UseSerilog()
            .ConfigureServices((_, services) =>
            {
                services.AddSingleton(settingsStore);
                services.AddSingleton<IGrblTransport>(_ => new RoutingGrblTransport(
                    new GrblSerialTransport(),
                    new VirtualGrblTransport()));
                services.AddSingleton<IGrblProtocolParser, GrblProtocolParser>();
                services.AddSingleton(sp => new GrblConnection(sp.GetRequiredService<IGrblTransport>(), sp.GetRequiredService<IGrblProtocolParser>()));
                services.AddSingleton<ILaserMachine>(sp => sp.GetRequiredService<GrblConnection>());
                services.AddSingleton<ILaserMachineFactory>(_ => new GrblMachineFactory());
                services.AddSingleton(sp => new DeviceScanner(
                    sp.GetRequiredService<ILaserMachineFactory>(),
                    () => GrblConnection.GetAvailablePortNames()));
                services.AddTransient<DeviceWizardViewModel>();
                services.AddSingleton<Func<DeviceWizardViewModel>>(sp => sp.GetRequiredService<DeviceWizardViewModel>);
                services.AddSingleton<ConnectionViewModel>();
                services.AddSingleton<MachineStatusViewModel>();
                services.AddSingleton<JogViewModel>();
                services.AddSingleton<ConsoleViewModel>();
                services.AddSingleton<SceneViewModel>();
                services.AddSingleton<GCodeViewModel>();
                services.AddSingleton(_ => new HttpClient { Timeout = TimeSpan.FromSeconds(15) });
                services.AddSingleton<LaseroAuthClient>();
                services.AddSingleton<LaseroAccountClient>();
                services.AddSingleton<LaseroChatClient>();
                services.AddSingleton<MaterialSyncClient>();
                services.AddSingleton<SessionStore>();
                services.AddSingleton<DeviceIdStore>();
                services.AddSingleton<DeviceActivationClient>();
                services.AddSingleton(_ => ProjectRecoveryStore.CreateDefault());
                services.AddSingleton(_ => RecentProjectsStore.CreateDefault());
                services.AddSingleton(_ => JobHistoryStore.CreateDefault());
                services.AddSingleton(_ => MaterialPresetStore.CreateDefault());
                services.AddSingleton(_ => ChatStore.CreateDefault());
                services.AddSingleton<MaterialsViewModel>();
                services.AddSingleton<AccountViewModel>();
                services.AddSingleton<ChatViewModel>();
                services.AddSingleton<KamilAssistantViewModel>();
                services.AddSingleton<HomeViewModel>();
                services.AddSingleton<MainViewModel>();
                services.AddTransient<MainWindow>();
            })
            .Build();

        UiAccessibility.ApplyMotionPreferences();
        _host.Start();
        var viewModel = _host.Services.GetRequiredService<MainViewModel>();
        await viewModel.Account.TryResumeSessionAsync();

        if (!viewModel.Account.IsSignedIn)
        {
            var login = new LoginWindow(viewModel.Account);
            if (login.ShowDialog() != true)
            {
                await _host.StopAsync();
                _host.Dispose();
                Shutdown();
                return;
            }
        }

        // No explicit reload call here: MainViewModel and its sub-viewmodels were already
        // constructed (and their AccountViewModel.PropertyChanged subscriptions wired) above, before
        // TryResumeSessionAsync/SignIn ever changed Account.UserId — so Chat/Materials/Home's
        // account-scoped caches have already reloaded for the now-current account automatically.
        var window = new MainWindow(viewModel);
        MainWindow = window;
        window.Show();

        var projectPath = e.Args.FirstOrDefault(argument =>
            string.Equals(Path.GetExtension(argument), ".lasero", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(projectPath))
            viewModel.OpenProjectFromShell(projectPath);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        var jobState = _host?.Services.GetService<GCodeViewModel>()?.JobState;
        if (jobState is JobRunState.Running or JobRunState.Paused or JobRunState.Framing)
            StopMachineAfterFatalError();
        _host?.Services.GetService<ILaserMachine>()?.Dispose();
        _host?.StopAsync().GetAwaiter().GetResult();
        _host?.Dispose();
        Log.CloseAndFlush();
        base.OnExit(e);
    }

    private void StopMachineAfterFatalError()
    {
        try
        {
            var machine = _host?.Services.GetService<ILaserMachine>();
            machine?.FeedHold();
            machine?.SoftReset();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Emergency machine stop failed during application shutdown");
        }
    }
}
