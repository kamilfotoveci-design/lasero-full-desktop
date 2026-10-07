using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Media;
using Lasero.App.ViewModels;
using Lasero.Core.Grbl;
using Lasero.Core.BackgroundRemoval;
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

    public App()
    {
        // WPF otherwise rounds layout only opportunistically. Enabling both at the Window
        // metadata boundary keeps inherited text, 1px borders and vector icons on the same
        // device-pixel grid at 100–200% PerMonitorV2 scaling, without scaling the UI with a transform.
        FrameworkElement.UseLayoutRoundingProperty.OverrideMetadata(
            typeof(Window), new FrameworkPropertyMetadata(true));
        UIElement.SnapsToDevicePixelsProperty.OverrideMetadata(
            typeof(Window), new FrameworkPropertyMetadata(true));
        TextOptions.TextFormattingModeProperty.OverrideMetadata(
            typeof(Window), new FrameworkPropertyMetadata(TextFormattingMode.Display, FrameworkPropertyMetadataOptions.Inherits));
        TextOptions.TextRenderingModeProperty.OverrideMetadata(
            typeof(Window), new FrameworkPropertyMetadata(TextRenderingMode.ClearType, FrameworkPropertyMetadataOptions.Inherits));
        TextOptions.TextHintingModeProperty.OverrideMetadata(
            typeof(Window), new FrameworkPropertyMetadata(TextHintingMode.Fixed, FrameworkPropertyMetadataOptions.Inherits));

        // The Window style in LaseroTheme sets Inter 15 px, but an implicit style is keyed by exact type and never reaches
        // MainWindow or the other Window subclasses, so every TextBlock without its own style fell back to Segoe UI at
        // 12 px (below the 13 px floor, and not the product typeface). A default on the Window metadata would not help,
        // inheritance only carries values that are set, so each window gets the two values locally as it loads.
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(ApplyTypographyDefaults));

        // WPF's stock focus visual is a dotted black rectangle. Every Lasero control with its own ring sets
        // FocusVisualStyle to null and draws it in its template; anything that does not (tab items, list rows,
        // a focusable host) would otherwise fall back to the dotted rectangle. This makes the fallback the same
        // 2px ring the rest of the product uses.
        try
        {
            FrameworkElement.FocusVisualStyleProperty.OverrideMetadata(
                typeof(System.Windows.Controls.Control), new FrameworkPropertyMetadata(CreateFocusVisualFallback()));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            Log.Warning(ex, "Could not replace the default focus visual");
        }
    }

    /// <summary>Sets Inter 15 px on a window that did not choose its own. Internal for the typography test.</summary>
    internal static void ApplyTypographyDefaults(object sender, RoutedEventArgs e)
    {
        if (sender is not Window window || !ReferenceEquals(e.OriginalSource, window)) return;
        if (DependencyPropertyHelper.GetValueSource(window, System.Windows.Controls.Control.FontSizeProperty).BaseValueSource
            is BaseValueSource.Default or BaseValueSource.Inherited
            && window.TryFindResource("Size.Text.Body") is double size)
            window.FontSize = size;
        if (DependencyPropertyHelper.GetValueSource(window, System.Windows.Controls.Control.FontFamilyProperty).BaseValueSource
            is BaseValueSource.Default or BaseValueSource.Inherited
            && window.TryFindResource("Font.Ui") is FontFamily family)
            window.FontFamily = family;
    }

    private static Style CreateFocusVisualFallback()
    {
        var ring = new FrameworkElementFactory(typeof(System.Windows.Shapes.Rectangle));
        ring.SetValue(FrameworkElement.MarginProperty, new Thickness(-2));
        ring.SetValue(System.Windows.Shapes.Shape.StrokeThicknessProperty, 2.0);
        ring.SetValue(System.Windows.Shapes.Rectangle.RadiusXProperty, 6.0);
        ring.SetValue(System.Windows.Shapes.Rectangle.RadiusYProperty, 6.0);
        ring.SetValue(UIElement.IsHitTestVisibleProperty, false);
        ring.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Brush.FocusRing");

        var style = new Style();
        style.Setters.Add(new Setter(System.Windows.Controls.Control.TemplateProperty,
            new System.Windows.Controls.ControlTemplate { VisualTree = ring }));
        style.Seal();
        return style;
    }

    /// <summary>Longest the start waits for the stored session to be refreshed over the network.</summary>
    internal const int SessionResumeCapMilliseconds = 3000;

    /// <summary>False inside a test runner (or any host that is not the Lasero.App executable).</summary>
    internal static bool IsRealApplicationProcess() =>
        string.Equals(System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name, "Lasero.App", StringComparison.OrdinalIgnoreCase);

    protected override async void OnStartup(StartupEventArgs e)
    {
        // Under a test runner the entry assembly is the runner, not Lasero.App. The tests create an App only to get the
        // theme resources; the real start (log file, session.dat, settings, splash, sign-in, main window) must never run
        // against the developer's own profile.
        if (!IsRealApplicationProcess())
        {
            base.OnStartup(e);
            Lasero.App.Input.InteractionBehaviors.Register();
            return;
        }

        base.OnStartup(e);
        Lasero.App.Input.InteractionBehaviors.Register();

        DispatcherUnhandledException += (_, args) =>
        {
            var errorId = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
            Log.Fatal(args.Exception, "Unhandled UI-thread exception {ErrorId}", errorId);
            StartupSplash.Current?.CloseNow(); // never leave the splash over an error dialog
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
            StartupSplash.Current?.CloseNow(); // never leave the splash over an error dialog
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

        // First thing on screen: the brand splash runs on its own UI thread so the work below cannot stall it.
        var splash = StartupSplash.ShouldShow(settings.Startup.ShowIntroAnimation, e.Args) ? StartupSplash.Start() : null;

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
                services.AddSingleton<ISerialPortEnumerator, WindowsSerialPortEnumerator>();
                services.AddSingleton<IGrblTransportFactory, SerialGrblTransportFactory>();
                services.AddSingleton<IGrblPortScanner, GrblPortScanner>();
                services.AddTransient<DeviceWizardViewModel>();
                services.AddSingleton<Func<DeviceWizardViewModel>>(sp => sp.GetRequiredService<DeviceWizardViewModel>);
                services.AddSingleton<ConnectionViewModel>();
                services.AddSingleton<MachineStatusViewModel>();
                services.AddSingleton<JogViewModel>();
                services.AddSingleton<ConsoleViewModel>();
                services.AddSingleton<SceneViewModel>();
                services.AddSingleton<GCodeViewModel>();
                services.AddSingleton(_ => new HttpClient { Timeout = TimeSpan.FromSeconds(15) });
                // Cloud first (signed-in Lasero proxy, no key in the app), then the on-device model if it
                // is already cached. Nothing here reads or stores a Gemini key.
                services.AddSingleton(_ => BackgroundRemovalConsentStore.CreateDefault());
                services.AddSingleton<IBackgroundRemovalService>(sp => new FallbackBackgroundRemovalService(
                    new GeminiBackgroundRemovalService(
                        new HttpClient { Timeout = GeminiBackgroundRemovalService.DefaultRequestTimeout },
                        cancellationToken => sp.GetRequiredService<AccountViewModel>().GetIdTokenAsync(cancellationToken)),
                    new BackgroundRemovalService()));
                services.AddSingleton<BackgroundRemovalCoordinator>();
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
        // Session resume is a network call; the splash must never wait on it. Cap it (the call itself also has a 6 s
        // limit) and carry on: if it is still running the person is offered sign-in, and a late result is simply ignored by
        // the already open window.
        Log.Information("Startup: resuming session");
        var resume = viewModel.Account.TryResumeSessionAsync();
        _ = resume.ContinueWith(t => { if (t.IsFaulted) Log.Warning(t.Exception, "Startup: session resume failed late"); }, TaskScheduler.Default);
        var resumed = await StartupSplash.CompletedWithin(resume, SessionResumeCapMilliseconds);
        Log.Information("Startup: session resume finished in time={InTime} signedIn={SignedIn}", resumed, viewModel.Account.IsSignedIn);
        if (!resumed) viewModel.Account.StatusMessage = "Přihlášení se nepodařilo obnovit. Přihlaste se prosím znovu.";

        // Let the intro finish (at most ~4.5 s, and only if init was faster than it) before any dialog or window appears.
        if (splash is not null) await splash.WaitForAnimationAsync();

        if (!viewModel.Account.IsSignedIn)
        {
            splash?.FadeOutAndClose();
            splash = null;
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
        Log.Information("Startup: creating main window");
        var window = _host.Services.GetRequiredService<MainWindow>();
        Log.Information("Startup: main window created");
        MainWindow = window;
        StartupSplash.Reveal(window, splash);

        var projectPath = e.Args.FirstOrDefault(argument =>
            string.Equals(Path.GetExtension(argument), ".lasero", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(projectPath))
            viewModel.OpenProjectFromShell(projectPath);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        var jobState = _host?.Services.GetService<GCodeViewModel>()?.JobState;
        StartupSplash.Current?.CloseNow(); // never leave the splash behind on exit
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
