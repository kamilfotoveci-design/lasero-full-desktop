using System.Windows;
using Lasero.App.ViewModels;

namespace Lasero.App;

public partial class OffsetPathWindow : Window
{
    public OffsetPathViewModel ViewModel { get; }

    public OffsetPathWindow(OffsetPathViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
        Closed += (_, _) => ViewModel.Dispose();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnApplyClick(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.HasResult) return;
        DialogResult = true;
        Close();
    }
}
