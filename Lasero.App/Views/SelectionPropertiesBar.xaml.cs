using System.Windows.Controls;
using System.Windows.Input;

namespace Lasero.App.Views;

public partial class SelectionPropertiesBar : UserControl
{
    public SelectionPropertiesBar()
    {
        InitializeComponent();
    }

    // Every value field on this bar defers its binding to LostFocus, so a half-typed number never
    // reaches the scene. Enter commits it, Esc reverts it and a click selects it all: that is the
    // app-wide behaviour of InteractionBehaviors, not something this bar has to carry itself.
}
