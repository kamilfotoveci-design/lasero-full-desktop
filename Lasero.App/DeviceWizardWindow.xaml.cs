using System.Windows;
using Lasero.App.ViewModels;

namespace Lasero.App;

public partial class DeviceWizardWindow : Window
{
    private readonly DeviceWizardViewModel _viewModel;

    public DeviceWizardWindow(DeviceWizardViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OnWorkAreaEdited(object sender, RoutedEventArgs e) => _viewModel.NotifyWorkAreaEdited();

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
