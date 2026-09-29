using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace Lasero.App.Components;

/// <summary>Shared title bar for secondary windows, with caption actions determined by ResizeMode.</summary>
public partial class WindowTitleBar : UserControl
{
    private Window? _hostWindow;

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(WindowTitleBar), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty IconDataProperty = DependencyProperty.Register(
        nameof(IconData), typeof(Geometry), typeof(WindowTitleBar), new PropertyMetadata(null));

    public static readonly DependencyProperty IsIconFilledProperty = DependencyProperty.Register(
        nameof(IsIconFilled), typeof(bool), typeof(WindowTitleBar), new PropertyMetadata(false));

    public static readonly DependencyProperty IconGridSizeProperty = DependencyProperty.Register(
        nameof(IconGridSize), typeof(double), typeof(WindowTitleBar), new PropertyMetadata(24.0));

    public static readonly DependencyProperty CloseButtonNameProperty = DependencyProperty.Register(
        nameof(CloseButtonName), typeof(string), typeof(WindowTitleBar), new PropertyMetadata("Zavřít okno"));

    public WindowTitleBar()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public Geometry? IconData
    {
        get => (Geometry?)GetValue(IconDataProperty);
        set => SetValue(IconDataProperty, value);
    }

    public bool IsIconFilled
    {
        get => (bool)GetValue(IsIconFilledProperty);
        set => SetValue(IsIconFilledProperty, value);
    }

    public double IconGridSize
    {
        get => (double)GetValue(IconGridSizeProperty);
        set => SetValue(IconGridSizeProperty, value);
    }

    public string CloseButtonName
    {
        get => (string)GetValue(CloseButtonNameProperty);
        set => SetValue(CloseButtonNameProperty, value);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var hostWindow = Window.GetWindow(this);
        if (!ReferenceEquals(_hostWindow, hostWindow))
        {
            if (_hostWindow is not null)
                _hostWindow.StateChanged -= OnHostWindowStateChanged;
            _hostWindow = hostWindow;
            if (_hostWindow is not null)
                _hostWindow.StateChanged += OnHostWindowStateChanged;
        }

        if (_hostWindow is null) return;

        WindowFrameHook.Attach(_hostWindow,
            _hostWindow.TryFindResource("Brush.PanelBorderStrong") as SolidColorBrush);

        MinimizeButton.Visibility = _hostWindow.ResizeMode == ResizeMode.NoResize
            ? Visibility.Collapsed
            : Visibility.Visible;
        MaximizeButton.Visibility = _hostWindow.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateMaximizeButton();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_hostWindow is not null)
            _hostWindow.StateChanged -= OnHostWindowStateChanged;
        _hostWindow = null;
    }

    private void OnHostWindowStateChanged(object? sender, System.EventArgs e) => UpdateMaximizeButton();

    private void UpdateMaximizeButton()
    {
        if (_hostWindow is null) return;

        var isMaximized = _hostWindow.WindowState == WindowState.Maximized;
        MaximizeGlyph.IconData = (Geometry)FindResource(isMaximized ? "Glyph.Restore" : "Glyph.Maximize");
        var actionName = isMaximized ? "Obnovit velikost" : "Maximalizovat";
        MaximizeButton.ToolTip = actionName;
        AutomationProperties.SetName(MaximizeButton, actionName);
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e)
    {
        if (_hostWindow is not null)
            SystemCommands.MinimizeWindow(_hostWindow);
    }

    private void OnMaximizeClick(object sender, RoutedEventArgs e)
    {
        if (_hostWindow is null) return;

        if (_hostWindow.WindowState == WindowState.Maximized)
            SystemCommands.RestoreWindow(_hostWindow);
        else
            SystemCommands.MaximizeWindow(_hostWindow);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => _hostWindow?.Close();
}
