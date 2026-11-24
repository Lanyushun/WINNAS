using System.Windows;
using System.Windows.Controls;
using Winnas.Console.ViewModels;

namespace Winnas.Console.Views;

public partial class ConnectionsView : UserControl
{
    public ConnectionsView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// PasswordBox cannot be data-bound by design, so the secret is handed to
    /// the view model directly and never touches the WPF property system.
    /// </summary>
    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not PasswordBox box) return;
        if (DataContext is ConnectionsViewModel vm) vm.SetPendingPassword(box.SecurePassword);
    }
}
