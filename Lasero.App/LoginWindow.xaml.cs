using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Lasero.App.ViewModels;

namespace Lasero.App;

public partial class LoginWindow : Window
{
    private readonly AccountViewModel _account;

    public LoginWindow(AccountViewModel account)
    {
        InitializeComponent();
        _account = account;
        DataContext = account;
        _account.PropertyChanged += OnAccountPropertyChanged;
        Loaded += (_, _) => Keyboard.Focus(
            string.IsNullOrWhiteSpace(_account.Email)
                ? EmailInput
                : PasswordInput);
    }

    private void OnAccountPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AccountViewModel.IsSignedIn) && _account.IsSignedIn)
            DialogResult = true;
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e) => _account.Password = PasswordInput.Password;

    private void OnSignInClick(object sender, RoutedEventArgs e)
    {
        if (_account.IsBusy) return;
        if (!_account.ValidateSignInForm())
        {
            Keyboard.Focus(_account.EmailError is not null ? EmailInput : PasswordInput);
            return;
        }

        if (_account.SignInCommand.CanExecute(null))
            _account.SignInCommand.Execute(null);
    }

    protected override void OnClosed(EventArgs e)
    {
        _account.PropertyChanged -= OnAccountPropertyChanged;
        base.OnClosed(e);
    }
}
