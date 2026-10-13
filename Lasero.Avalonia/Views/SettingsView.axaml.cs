using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Lasero.Avalonia.Views;

public sealed partial class SettingsView : UserControl
{
    public SettingsView() => AvaloniaXamlLoader.Load(this);
}
