using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
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

    private void OnPasswordRevealChanged(object sender, TextChangedEventArgs e) => _account.Password = PasswordRevealInput.Text;

    /// <summary>Swaps which of the two stacked controls is visible/interactive rather than trying to
    /// make one control do both jobs — WPF's PasswordBox has no bindable/revealable plaintext by
    /// design. The hidden side is kept in sync with whichever one the operator just used, so toggling
    /// back never loses a keystroke.</summary>
    private void OnTogglePasswordVisibility(object sender, RoutedEventArgs e)
    {
        var reveal = PasswordVisibilityToggle.IsChecked == true;
        var label = reveal ? "Skrýt heslo" : "Zobrazit heslo";
        PasswordVisibilityToggle.ToolTip = label;
        AutomationProperties.SetName(PasswordVisibilityToggle, label);

        if (reveal)
        {
            PasswordRevealInput.Text = PasswordInput.Password;
            PasswordInput.Visibility = Visibility.Collapsed;
            PasswordRevealInput.Visibility = Visibility.Visible;
            PasswordRevealInput.Focus();
            PasswordRevealInput.CaretIndex = PasswordRevealInput.Text.Length;
        }
        else
        {
            PasswordInput.Password = PasswordRevealInput.Text;
            PasswordRevealInput.Visibility = Visibility.Collapsed;
            PasswordInput.Visibility = Visibility.Visible;
            PasswordInput.Focus();
        }
    }

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
