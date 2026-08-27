using System.Windows;
using Lasero.App.ViewModels;

namespace Lasero.App;

public partial class BitmapTraceWindow : Window
{
    public BitmapTraceViewModel ViewModel { get; }

    public BitmapTraceWindow(BitmapTraceViewModel viewModel)
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
