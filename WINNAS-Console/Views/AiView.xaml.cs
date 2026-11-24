using System.Windows;
using System.Windows.Controls;
using Winnas.Console.ViewModels;

namespace Winnas.Console.Views;

public partial class AiView : UserControl
{
    public AiView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is AiViewModel previous) previous.ScrollRequested -= ScrollToEnd;
        if (e.NewValue is AiViewModel current) current.ScrollRequested += ScrollToEnd;
    }

    private void ScrollToEnd(object? sender, System.EventArgs e) => ChatScroll.ScrollToBottom();
}
