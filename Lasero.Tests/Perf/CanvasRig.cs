using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lasero.App.Controls;
using Lasero.App.ViewModels;
using Lasero.Core.Scene;

namespace Lasero.Tests.Perf;

/// <summary>
/// An off-screen window that hosts the real SceneCanvas, driven only through code (no synthetic
/// pointer or keyboard input). Private canvas state is reached by reflection, exactly as the existing
/// render tests do, so the same harness runs against the code before and after an optimisation.
/// Must be created and used on the shared WPF test dispatcher (InlineTextEditorRenderTests.Ui).
/// </summary>
public sealed class CanvasRig : IDisposable
{
    public const int Width = 1600;
    public const int Height = 900;
    public const double DefaultScale = 4;

    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public Window Window { get; }
    public SceneCanvas Canvas { get; }
    public SceneViewModel ViewModel { get; }

    public CanvasRig(SceneViewModel? viewModel = null, bool attach = true)
    {
        ViewModel = viewModel ?? new SceneViewModel();
        Canvas = new SceneCanvas { Width = Width, Height = Height, WorkAreaWidthMm = 400, WorkAreaHeightMm = 400 };
        if (attach) Canvas.ViewModel = ViewModel;
        Window = new Window
        {
            Width = Width, Height = Height,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
            ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000, Top = -20000, SizeToContent = SizeToContent.WidthAndHeight,
            Content = Canvas,
        };
        Window.Show();
        Flush();
    }

    public void Attach() => Canvas.ViewModel = ViewModel;

    /// <summary>Lets queued layout/render work run, so one measured frame is not billed for the previous one.</summary>
    /// <summary>Number of Flush calls that gave up after the timeout (dispatcher never went idle).</summary>
    public static int FlushTimeouts;

    public void Flush()
    {
        var done = false;
        try { Dispatcher.CurrentDispatcher.Invoke(() => done = true, DispatcherPriority.ApplicationIdle, CancellationToken.None, TimeSpan.FromSeconds(15)); }
        catch (TimeoutException) { }
        if (!done) { FlushTimeouts++; Perf.Note("Flush timed out: dispatcher did not reach idle within 15 s"); }
    }

    public void Layout() => Canvas.UpdateLayout();

    public T Get<T>(string field) => (T)Canvas.GetType().GetField(field, Private)!.GetValue(Canvas)!;
    public void Set(string field, object? value) => Canvas.GetType().GetField(field, Private)!.SetValue(Canvas, value);

    private readonly Dictionary<string, MethodInfo> _methods = [];

    public object? Call(string method, params object?[] args)
    {
        if (!_methods.TryGetValue(method, out var info))
            _methods[method] = info = Canvas.GetType().GetMethod(method, Private)
                ?? throw new MissingMethodException(Canvas.GetType().Name, method);
        return info.Invoke(Canvas, args);
    }

    public double Scale => Get<double>("_scale");

    /// <summary>Software-rasterises the canvas once (an upper bound on render-thread work: the real
    /// app tessellates on the CPU but rasterises on the GPU).</summary>
    public RenderTargetBitmap Render()
    {
        Canvas.UpdateLayout();
        var bitmap = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(Canvas);
        return bitmap;
    }

    /// <summary>Puts the view at <paramref name="zoomPercent"/> centred on the bed, as the zoom picker does.</summary>
    public void SetZoomCentred(int zoomPercent, double centreXMm = 200, double centreYMm = 200)
    {
        var scale = DefaultScale * zoomPercent / 100.0;
        Set("_autoFit", false);
        Set("_scale", scale);
        const double margin = 20; // SceneCanvas.MarginPx
        Set("_offsetXMm", centreXMm - (Canvas.ActualWidth / 2 - margin) / scale);
        Set("_offsetYMm", centreYMm - (Canvas.ActualHeight / 2 - margin) / scale);
        Call("RepositionAll");
        Layout();
        Flush();
    }

    public void Dispose() => Window.Close();
}
