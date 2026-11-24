using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Winnas.Console.ViewModels;

namespace Winnas.Console.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// A Button only opens its ContextMenu on right-click, which nobody would
    /// discover on a gear icon. Open it under the button on left-click instead.
    /// </summary>
    private void OnSettingsMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;

        // A ContextMenu lives outside the visual tree, so it never inherits the
        // DataContext of the window. Handing it over here makes every item in
        // the menu bind plainly to the MainViewModel.
        menu.DataContext = DataContext;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.HorizontalOffset = -110;
        menu.IsOpen = true;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            await vm.StartupAsync();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.Shutdown();
    }
}
