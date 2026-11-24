using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Winnas.Console.Infrastructure;
using Winnas.Console.ViewModels;

namespace Winnas.Console.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        RestoreGeometry();
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
        SaveGeometry();
        if (DataContext is MainViewModel vm) vm.Shutdown();
    }

    // ------------------------------------------------------------- geometry

    /// <summary>
    /// Restored in the constructor rather than OnLoaded so the window never
    /// appears at the default position and then jumps. A saved position that
    /// landed on a monitor which has since been disconnected is ignored —
    /// starting off-screen looks like "the app forgot how to open".
    /// </summary>
    private void RestoreGeometry()
    {
        var s = AppState.Settings;
        try
        {
            if (s.WindowWidth is double w && w >= MinWidth) Width = w;
            if (s.WindowHeight is double h && h >= MinHeight) Height = h;

            if (s.WindowLeft is double l && s.WindowTop is double t)
            {
                var vsLeft = SystemParameters.VirtualScreenLeft;
                var vsTop = SystemParameters.VirtualScreenTop;
                var vsRight = vsLeft + SystemParameters.VirtualScreenWidth;
                var vsBottom = vsTop + SystemParameters.VirtualScreenHeight;

                // Keep at least a 120×120 corner visible somewhere.
                if (l + 120 > vsLeft && l < vsRight && t + 120 > vsTop && t < vsBottom)
                {
                    Left = l;
                    Top = t;
                }
            }

            if (s.WindowMaximized) WindowState = WindowState.Maximized;
        }
        catch { /* geometry must never block startup */ }
    }

    private void SaveGeometry()
    {
        var s = AppState.Settings;
        try
        {
            // Maximised: remember that it was maximised but keep the last known
            // normal rectangle, so un-maximising lands where the user had it.
            s.WindowMaximized = WindowState == WindowState.Maximized;
            if (WindowState == WindowState.Normal)
            {
                s.WindowLeft = Left;
                s.WindowTop = Top;
                s.WindowWidth = Width;
                s.WindowHeight = Height;
            }
            AppState.Save();
        }
        catch { /* never block shutdown over a rectangle */ }
    }
}
