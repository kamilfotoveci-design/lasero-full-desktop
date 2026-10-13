using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Lasero.Avalonia.Views;

public sealed partial class ProjectsView : UserControl
{
    public ProjectsView() => AvaloniaXamlLoader.Load(this);
}
