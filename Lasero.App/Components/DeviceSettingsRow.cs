using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Lasero.App.Components;

/// <summary>
/// One row of a settings group: the label (and an optional one-line hint) on the left, a compact
/// value control on the right. The template lives with the page that uses it
/// (<c>Views/DeviceSettingsView.xaml</c>); this class only carries the label, the hint, and the
/// accessible name.
///
/// The value control is the row's Content. When it has no accessible name of its own it takes the
/// row's label, so a switch or a numeric field is announced as "Šířka (mm)" and not as an anonymous
/// "toggle button" or "edit".
/// </summary>
public sealed class DeviceSettingsRow : HeaderedContentControl
{
    public static readonly DependencyProperty HintProperty = DependencyProperty.Register(
        nameof(Hint), typeof(string), typeof(DeviceSettingsRow), new PropertyMetadata(null));

    static DeviceSettingsRow()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(DeviceSettingsRow), new FrameworkPropertyMetadata(typeof(DeviceSettingsRow)));
    }

    /// <summary>Secondary line under the label: what the value does or what its limits are.</summary>
    public string? Hint
    {
        get => (string?)GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);
        NameTheValueControl();
    }

    protected override void OnHeaderChanged(object oldHeader, object newHeader)
    {
        base.OnHeaderChanged(oldHeader, newHeader);
        NameTheValueControl();
    }

    private void NameTheValueControl()
    {
        if (Content is not DependencyObject control || Header is not string label || label.Length == 0) return;
        if (string.IsNullOrEmpty(AutomationProperties.GetName(control)))
            AutomationProperties.SetName(control, label);
    }
}
