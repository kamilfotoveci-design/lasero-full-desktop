using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Lasero.App.Controls.Motion;

namespace Lasero.App.Views;

public partial class HomeView : UserControl
{
    public HomeView()
    {
        InitializeComponent();
    }

    private void OnKamilWelcomeLoaded(object sender, RoutedEventArgs e)
    {
        if (!LaseroMotion.AnimationsEnabled || sender is not FrameworkElement welcome) return;

        var duration = (Duration)FindResource("Motion.Fast");
        var ease = (IEasingFunction)FindResource("Ease.Out");
        var offset = new TranslateTransform(0, 7);
        welcome.RenderTransform = offset;
        welcome.Opacity = 1;
        welcome.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, duration)
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.Stop,
        });
        offset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(7, 0, duration)
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.Stop,
        });
    }

    /// <summary>The materials library is a window, not a screen, so it is the window that opens it —
    /// same route the navigation rail's Materiály button takes.</summary>
    private void OnMaterialsClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow mainWindow)
            mainWindow.OpenMaterials();
    }
}
