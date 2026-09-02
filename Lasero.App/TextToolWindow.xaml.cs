using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Lasero.App.ViewModels;
using Lasero.Core.Scene;

namespace Lasero.App;

public partial class TextToolWindow : Window
{
    public string TextValue { get; private set; } = string.Empty;
    public double HeightMm { get; private set; } = 12;
    public VectorTextStyle TextStyle { get; private set; } = VectorTextStyle.Default;

    /// <summary>
    /// Families that can actually produce outlines, sorted for scanning. Symbol fonts are left in —
    /// an operator engraving a dingbat is a legitimate use — but families the system reports without
    /// a usable typeface are not, because they would flatten to nothing and fail at creation time.
    /// Shared with the inspector's font picker rather than enumerated twice; walking the installed
    /// fonts is slow enough to be felt when it happens on a selection change.
    /// </summary>
    public IReadOnlyList<string> FontFamilies => SceneViewModel.FontFamilies;

    public TextToolWindow(VectorTextStyle? initialStyle = null)
    {
        InitializeComponent();
        DataContext = this;

        var style = initialStyle ?? VectorTextStyle.Default;
        FontFamilyInput.SelectedItem =
            FontFamilies.FirstOrDefault(name => string.Equals(name, style.FontFamily, StringComparison.OrdinalIgnoreCase))
            ?? FontFamilies.FirstOrDefault(name => string.Equals(name, "Segoe UI", StringComparison.OrdinalIgnoreCase))
            ?? FontFamilies.FirstOrDefault();
        BoldToggle.IsChecked = style.Bold;
        ItalicToggle.IsChecked = style.Italic;
        UppercaseToggle.IsChecked = style.Uppercase;
        WeldToggle.IsChecked = style.Weld;

        Loaded += (_, _) =>
        {
            UpdatePreview();
            TextValueInput.Focus();
        };
    }

    private void OnPreviewInputChanged(object sender, RoutedEventArgs e) => UpdatePreview();

    /// <summary>
    /// Shows the chosen family, weight and capitalisation applied to the operator's own text: picking
    /// a font from a list of names alone is guesswork. Welding is deliberately not previewed here —
    /// WPF text rendering fills the overlap either way, so the difference only shows in the contours,
    /// and it stays editable on the object afterwards.
    /// </summary>
    private void UpdatePreview()
    {
        if (PreviewText is null) return;

        var text = TextValueInput.Text.Trim();
        if (UppercaseToggle.IsChecked == true)
            text = text.ToUpper(CultureInfo.CurrentUICulture);
        PreviewText.Text = string.IsNullOrEmpty(text) ? "Náhled písma" : text;
        PreviewText.Opacity = string.IsNullOrEmpty(text) ? 0.45 : 1;

        if (FontFamilyInput.SelectedItem is string family && !string.IsNullOrWhiteSpace(family))
            PreviewText.FontFamily = new FontFamily(family);

        PreviewText.FontWeight = BoldToggle.IsChecked == true ? FontWeights.Bold : FontWeights.Normal;
        PreviewText.FontStyle = ItalicToggle.IsChecked == true ? FontStyles.Italic : FontStyles.Normal;
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
            height < TextSource.MinHeightMm || height > TextSource.MaxHeightMm)
        {
            ShowError($"Výška písma musí být číslo od {TextSource.MinHeightMm:0} do {TextSource.MaxHeightMm:0} mm.");
            HeightInput.Focus();
            HeightInput.SelectAll();
            return;
        }

        TextValue = text;
        HeightMm = height;
        TextStyle = new VectorTextStyle
        {
            FontFamily = FontFamilyInput.SelectedItem as string ?? "Segoe UI",
            Bold = BoldToggle.IsChecked == true,
            Italic = ItalicToggle.IsChecked == true,
            Uppercase = UppercaseToggle.IsChecked == true,
            Weld = WeldToggle.IsChecked == true,
        };
        DialogResult = true;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
