using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Lasero.App.ViewModels;
using Lasero.Core.Scene;

namespace Lasero.App;

/// <summary>Read-only before/after preview. Cancel leaves the document and Undo stack untouched.</summary>
public sealed class BooleanPreviewWindow : Window
{
    private const double PreviewWidth = 360;
    private const double PreviewHeight = 310;

    public BooleanPreviewWindow(VectorOperationPreview preview)
    {
        Title = $"{preview.Title} · náhled";
        Width = 850;
        Height = 540;
        MinWidth = 720;
        MinHeight = 470;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = false;
        Background = ThemeBrush("Brush.Background", Brushes.White);

        var root = new Grid { Margin = new Thickness(22) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var intro = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        intro.Children.Add(new TextBlock
        {
            Text = preview.Title, FontSize = 22, FontWeight = FontWeights.SemiBold,
            Foreground = ThemeBrush("Brush.TextPrimary", Brushes.Black),
        });
        intro.Children.Add(new TextBlock
        {
            Text = preview.Sources.Count == 2
                ? $"A = spodní tvar, B = horní tvar · Výsledná vrstva: {preview.ResultLayer}"
                : $"Vstupů: {preview.Sources.Count} · Výsledná vrstva: {preview.ResultLayer}",
            Margin = new Thickness(0, 7, 0, 0), Foreground = ThemeBrush("Brush.TextSecondary", Brushes.DimGray),
            TextWrapping = TextWrapping.Wrap,
        });
        if (preview.ApproximatesCurves)
            intro.Children.Add(new TextBlock
            {
                Text = "Bézierovy křivky se pro tuto operaci převádějí na obrysy s tolerancí 0,05 mm. Výsledek bude tvořen úsečkami.",
                Margin = new Thickness(0, 8, 0, 0), Foreground = ThemeBrush("Brush.Danger", Brushes.DarkRed),
                TextWrapping = TextWrapping.Wrap,
            });
        if (preview.LayerNotice is { } layerNotice)
            intro.Children.Add(new TextBlock
            {
                Text = layerNotice, Margin = new Thickness(0, 8, 0, 0),
                Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap,
            });
        if (preview.GeometryNotice is { } notice)
            intro.Children.Add(new TextBlock
            {
                Text = notice, Margin = new Thickness(0, 8, 0, 0),
                Foreground = ThemeBrush("Brush.Warning", Brushes.DarkGoldenrod), TextWrapping = TextWrapping.Wrap,
            });
        root.Children.Add(intro);

        var panes = new Grid();
        panes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        panes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        panes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var bounds = BoundsOf(preview.Sources.Concat(preview.Results));
        var before = CreatePane("Před", preview.Sources, bounds);
        var after = CreatePane("Po", preview.Results, bounds);
        Grid.SetColumn(after, 2);
        panes.Children.Add(before);
        panes.Children.Add(after);
        Grid.SetRow(panes, 1);
        root.Children.Add(panes);

        var footer = new DockPanel { Margin = new Thickness(0, 18, 0, 0), LastChildFill = false };
        var apply = new Button
        {
            Content = preview.ApproximatesCurves ? "Použít aproximaci" : "Použít výsledek",
            MinWidth = 148, Height = 38, Padding = new Thickness(12, 0, 12, 0),
            IsDefault = true,
        };
        apply.Click += (_, _) => DialogResult = true;
        DockPanel.SetDock(apply, Dock.Right);
        footer.Children.Add(apply);
        var cancel = new Button
        {
            Content = "Zrušit", MinWidth = 95, Height = 38,
            Margin = new Thickness(0, 0, 10, 0), IsCancel = true,
        };
        cancel.Click += (_, _) => DialogResult = false;
        DockPanel.SetDock(cancel, Dock.Right);
        footer.Children.Add(cancel);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        Content = root;
    }

    /// <summary>The app theme's brush, so the preview follows the same palette as every other window;
    /// the fallback only matters where no application resources are loaded.</summary>
    private static Brush ThemeBrush(string key, Brush fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? fallback;

    private static Border CreatePane(string heading, IReadOnlyList<SceneObject> objects,
        (double MinX, double MinY, double MaxX, double MaxY) bounds)
    {
        var panel = new DockPanel();
        var label = new TextBlock
        {
            Text = heading == "Před" && objects.Count == 2
                ? "Před · A modrá, B oranžová" : heading,
            FontWeight = FontWeights.SemiBold, Foreground = ThemeBrush("Brush.TextPrimary", Brushes.Black),
            Margin = new Thickness(12, 10, 12, 8),
        };
        DockPanel.SetDock(label, Dock.Top);
        panel.Children.Add(label);
        var canvas = new Canvas
        {
            Width = PreviewWidth, Height = PreviewHeight,
            Background = ThemeBrush("Brush.Canvas.WorkArea", Brushes.White), ClipToBounds = true,
        };
        var width = Math.Max(bounds.MaxX - bounds.MinX, 0.001);
        var height = Math.Max(bounds.MaxY - bounds.MinY, 0.001);
        var scale = Math.Min((PreviewWidth - 36) / width, (PreviewHeight - 36) / height);
        var left = (PreviewWidth - width * scale) / 2;
        var top = (PreviewHeight - height * scale) / 2;
        Point Map(double x, double y) => new(left + (x - bounds.MinX) * scale,
            top + (bounds.MaxY - y) * scale);

        var colors = new[]
        {
            Color.FromRgb(36, 91, 164), Color.FromRgb(183, 84, 36),
            Color.FromRgb(108, 65, 150), Color.FromRgb(36, 123, 99),
        };
        for (var index = 0; index < objects.Count; index++)
        {
            var geometry = new PathGeometry { FillRule = FillRule.EvenOdd };
            var shapes = objects[index].GetWorldShapes();
            foreach (var shape in shapes)
            {
                if (shape.Points.Count < 2) continue;
                var figure = new PathFigure { StartPoint = Map(shape.Points[0].X, shape.Points[0].Y),
                    IsClosed = shape.IsClosed };
                foreach (var point in shape.Points.Skip(1))
                    figure.Segments.Add(new LineSegment(Map(point.X, point.Y), true));
                geometry.Figures.Add(figure);
            }
            var color = colors[index % colors.Length];
            canvas.Children.Add(new System.Windows.Shapes.Path
            {
                Data = geometry,
                Fill = shapes.Any(shape => shape.IsClosed)
                    ? new SolidColorBrush(Color.FromArgb(55, color.R, color.G, color.B))
                    : Brushes.Transparent,
                Stroke = new SolidColorBrush(color), StrokeThickness = 1.5,
                IsHitTestVisible = false,
            });
        }
        panel.Children.Add(new Viewbox { Stretch = Stretch.Uniform, Child = canvas });
        return new Border
        {
            Child = panel, BorderBrush = ThemeBrush("Brush.PanelBorder", new SolidColorBrush(Color.FromRgb(205, 209, 214))),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7),
        };
    }

    private static (double MinX, double MinY, double MaxX, double MaxY) BoundsOf(
        IEnumerable<SceneObject> objects)
    {
        var points = objects.SelectMany(item => item.GetWorldShapes())
            .SelectMany(shape => shape.Points).ToList();
        return points.Count == 0 ? (0, 0, 1, 1) :
            (points.Min(point => point.X), points.Min(point => point.Y),
                points.Max(point => point.X), points.Max(point => point.Y));
    }
}
