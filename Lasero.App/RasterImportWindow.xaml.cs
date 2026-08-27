using System.Windows;
using Lasero.App.ViewModels;

namespace Lasero.App;

public partial class RasterImportWindow : Window
{
    public RasterImportViewModel ViewModel { get; }

    public RasterImportWindow(RasterImportViewModel viewModel)
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

    private void OnImportClick(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.TryValidate(out var message))
        {
            ViewModel.StatusMessage = message;
            return;
        }

        DialogResult = true;
        Close();
    }
}
