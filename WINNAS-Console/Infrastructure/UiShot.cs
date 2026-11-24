using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Winnas.Console.ViewModels;
using Winnas.Console.Views;

namespace Winnas.Console.Infrastructure;

/// <summary>
/// Renders one page to a PNG without needing the window to be in front:
/// "WINNAS.Console.exe --shot 备份 shot.png".
///
/// It exists because a WPF binding failure is invisible to the compiler and to
/// the self test — the only proof a page renders is a picture of it. Doing the
/// capture inside the process also works on a locked or headless desktop, where
/// an external screen grab would just photograph whatever happens to be on top.
/// </summary>
public static class UiShot
{
    public static async Task<int> RunAsync(string pageTitle, string file, double scrollRatio = 0,
        bool diagnose = false)
    {
        var window = new MainWindow
        {
            Width = 1380,
            Height = 940,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ShowInTaskbar = false,
        };

        window.Show();
        await Task.Delay(1600);   // first paint + startup connect

        if (window.DataContext is MainViewModel vm)
        {
            // The startup connect runs on a worker thread and can take a few
            // seconds on a remote target (LogonUser + first WMI round trip).
            // Capturing before it settles photographs the "connecting" state.
            for (var i = 0; i < 80 && vm.IsConnecting; i++) await Task.Delay(250);

            var target = vm.Pages.FirstOrDefault(p =>
                             string.Equals(p.Title, pageTitle, StringComparison.OrdinalIgnoreCase))
                         ?? vm.Pages.FirstOrDefault(p =>
                             p.Title.Contains(pageTitle, StringComparison.OrdinalIgnoreCase));
            if (target is not null) vm.CurrentPage = target;

            // "--shotdiag" additionally presses 连接诊断, because that panel is
            // behind a command and would otherwise never appear in a capture.
            if (diagnose && target is ConnectionsViewModel conn)
            {
                // The dialog opens on whatever was last used; to exercise the
                // remote path, jump to a saved remote connection first.
                var remote = conn.Profiles.FirstOrDefault(p => !p.IsLocal);
                if (remote is not null) conn.Selected = remote;

                if (conn.DiagnoseCommand.CanExecute(null)) await conn.DiagnoseCommand.ExecuteAsync(null);
            }
        }

        // Pages load asynchronously and the layout keeps settling while the
        // data arrives, so give it a generous fixed window rather than guess.
        // A remote page can spend several seconds in WMI (the 共享 page reads
        // one ACL per share), which a fixed delay photographs as "empty".
        if (window.DataContext is MainViewModel shot && shot.CurrentPage is { } page)
            for (var i = 0; i < 120 && page.IsBusy; i++) await Task.Delay(250);

        await Task.Delay(1200);

        if (scrollRatio > 0)
        {
            var sv = FindScrollViewer(window);
            if (sv is not null)
            {
                sv.ScrollToVerticalOffset(sv.ScrollableHeight * scrollRatio);
                await Task.Delay(900);
            }
        }

        var ok = Save(window, file);
        window.Close();
        return ok ? 0 : 1;
    }

    /// <summary>
    /// The page-level scroller is the one with the most to scroll — picking the
    /// first ScrollViewer found would land inside a DataGrid instead.
    /// </summary>
    private static System.Windows.Controls.ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        var all = new List<System.Windows.Controls.ScrollViewer>();
        Collect(root, all);
        return all.Count == 0 ? null : all.MaxBy(x => x.ScrollableHeight);
    }

    private static void Collect(DependencyObject root, List<System.Windows.Controls.ScrollViewer> sink)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is System.Windows.Controls.ScrollViewer sv) sink.Add(sv);
            Collect(child, sink);
        }
    }

    private static bool Save(Window window, string file)
    {
        var width = (int)Math.Ceiling(window.ActualWidth);
        var height = (int)Math.Ceiling(window.ActualHeight);
        if (width <= 0 || height <= 0) return false;

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        var dir = Path.GetDirectoryName(Path.GetFullPath(file));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var fs = new FileStream(file, FileMode.Create, FileAccess.Write);
        encoder.Save(fs);
        return true;
    }
}
