using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Winnas.Console.Models;
using Winnas.Console.ViewModels;

namespace Winnas.Console.Views;

public partial class BackupView : UserControl
{
    private BackupViewModel? _vm;

    public BackupView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null) _vm.OutputAppended -= ScrollOutput;
        _vm = e.NewValue as BackupViewModel;
        if (_vm is not null) _vm.OutputAppended += ScrollOutput;
    }

    /// <summary>Keep the newest robocopy line in view while the copy runs.</summary>
    private void ScrollOutput()
    {
        if (OutputList.Items.Count == 0) return;
        OutputList.ScrollIntoView(OutputList.Items[^1]);
    }

    /// <summary>
    /// InputBindings on a DataGrid do not inherit the DataContext, so the
    /// double-click to open a log is wired as a real event instead.
    /// </summary>
    private void OnLogDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid || grid.SelectedItem is not LogFileInfo log) return;
        if (DataContext is BackupViewModel vm) vm.ViewLogCommand.Execute(log);
    }
}
