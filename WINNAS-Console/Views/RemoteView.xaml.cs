using System.Windows.Controls;
using System.Windows.Input;
using Winnas.Console.ViewModels;

namespace Winnas.Console.Views;

public partial class RemoteView : UserControl
{
    public RemoteView() => InitializeComponent();

    /// <summary>
    /// The startup-type dropdown is a menu of actions rather than a value: pick
    /// an entry and it is applied immediately. Resetting the selection means
    /// picking the same entry twice still fires.
    /// </summary>
    private void OnStartModePicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ComboBoxItem { Tag: string mode } item) return;

        if (DataContext is RemoteViewModel vm &&
            vm.SetStartModeCommand.CanExecute(mode))
            vm.SetStartModeCommand.Execute(mode);

        if (ItemsControl.ItemsControlFromItemContainer(item) is ComboBox combo)
            combo.SelectedIndex = -1;
    }
}
