using System.Globalization;
using System.Windows;

namespace Lasero.App;

public partial class TextToolWindow : Window
{
    public string TextValue { get; private set; } = string.Empty;
    public double HeightMm { get; private set; } = 12;

    public TextToolWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => TextValueInput.Focus();
    }

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        var text = TextValueInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            ShowError("Napište text, který chcete vložit do návrhu.");
            TextValueInput.Focus();
            return;
        }

        var rawHeight = HeightInput.Text.Trim().Replace(',', '.');
        if (!double.TryParse(rawHeight, NumberStyles.Float, CultureInfo.InvariantCulture, out var height) ||
            height is < 1 or > 200)
        {
            ShowError("Výška písma musí být číslo od 1 do 200 mm.");
            HeightInput.Focus();
            HeightInput.SelectAll();
            return;
        }

        TextValue = text;
        HeightMm = height;
        DialogResult = true;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
