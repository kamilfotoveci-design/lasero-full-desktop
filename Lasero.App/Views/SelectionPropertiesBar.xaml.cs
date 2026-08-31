using System.Windows.Controls;
using System.Windows.Input;

namespace Lasero.App.Views;

public partial class SelectionPropertiesBar : UserControl
{
    public SelectionPropertiesBar()
    {
        InitializeComponent();
    }

    private void OnValueFieldKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox field) return;

        // Every field on this bar defers its binding to LostFocus, so that a half-typed number never
        // reaches the scene. Editing dimensions in a desktop design tool is keyboard-driven, though,
        // so Enter has to commit immediately and leave the field ready for another precise value.
        field.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        field.SelectAll();
        e.Handled = true;
    }
}
