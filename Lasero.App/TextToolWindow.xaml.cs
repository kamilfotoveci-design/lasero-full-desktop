using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Lasero.App;

public partial class TextToolWindow : Window
{
    public string TextValue { get; private set; } = string.Empty;
    public double HeightMm { get; private set; } = 12;
    public VectorTextStyle Style { get; private set; } = VectorTextStyle.Default;

    /// <summary>
    /// Families that can actually produce outlines, sorted for scanning. Symbol fonts are left in —
    /// an operator engraving a dingbat is a legitimate use — but families the system reports without
    /// a usable typeface are not, because they would flatten to nothing and fail at creation time.
    /// </summary>
    public IReadOnlyList<string> FontFamilies { get; } = Fonts.SystemFontFamilies
        .Select(family => family.Source)
        .Where(name => !string.IsNullOrWhiteSpace(name))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
        .ToList();

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

        Loaded += (_, _) =>
        {
            UpdatePreview();
            TextValueInput.Focus();
        };
    }

    private void OnPreviewInputChanged(object sender, RoutedEventArgs e) => UpdatePreview();

    /// <summary>
    /// Shows the chosen family and weight applied to the operator's own text. Picking a font from a
    /// list of names alone is guesswork, and the text becomes uneditable contours the moment it is
    /// placed, so the decision has to be made before committing.
    /// </summary>
    private void UpdatePreview()
    {
        if (PreviewText is null) return;

        var text = TextValueInput.Text.Trim();
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
            height is < 1 or > 200)
        {
            ShowError("Výška písma musí být číslo od 1 do 200 mm.");
            HeightInput.Focus();
            HeightInput.SelectAll();
            return;
        }

        TextValue = text;
        HeightMm = height;
        Style = new VectorTextStyle
        {
            FontFamily = FontFamilyInput.SelectedItem as string ?? "Segoe UI",
            Bold = BoldToggle.IsChecked == true,
            Italic = ItalicToggle.IsChecked == true,
        };
        DialogResult = true;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
