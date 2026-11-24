using System.Windows;

namespace Winnas.Console.Infrastructure;

/// <summary>
/// Confirmation prompts. Centralised so view models stay free of direct
/// MessageBox calls and the UI thread is handled in one place.
/// </summary>
public static class DialogService
{
    public static bool Confirm(string message, string title = "请确认")
    {
        var result = MessageBoxResult.No;
        Application.Current.Dispatcher.Invoke(() =>
        {
            result = MessageBox.Show(Application.Current.MainWindow, message, title,
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        });
        return result == MessageBoxResult.Yes;
    }

    public static void Info(string message, string title = "WINNAS")
    {
        Application.Current.Dispatcher.Invoke(() =>
            MessageBox.Show(Application.Current.MainWindow, message, title,
                MessageBoxButton.OK, MessageBoxImage.Information));
    }

    public static void Warn(string message, string title = "WINNAS")
    {
        Application.Current.Dispatcher.Invoke(() =>
            MessageBox.Show(Application.Current.MainWindow, message, title,
                MessageBoxButton.OK, MessageBoxImage.Warning));
    }

    /// <summary>Pick local files to upload. Returns empty when cancelled.</summary>
    public static string[] PickFiles()
    {
        string[] picked = Array.Empty<string>();
        Application.Current.Dispatcher.Invoke(() =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择要上传的文件",
                Multiselect = true,
                CheckFileExists = true,
            };
            picked = dlg.ShowDialog() == true ? dlg.FileNames : Array.Empty<string>();
        });
        return picked;
    }

    /// <summary>Save a generated file (exported backup script, report, CSV).</summary>
    public static bool SaveText(string suggestedName, string content, string filter = "文本文件|*.txt|所有文件|*.*")
    {
        var saved = false;
        Application.Current.Dispatcher.Invoke(() =>
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "导出",
                FileName = suggestedName,
                Filter = filter,
                DefaultExt = ".txt",
            };
            if (dlg.ShowDialog() != true) return;
            System.IO.File.WriteAllText(dlg.FileName, content, System.Text.Encoding.UTF8);
            saved = true;
        });
        return saved;
    }

    /// <summary>Pick a local folder to download into. Returns null when cancelled.</summary>
    public static string? PickFolder(string startPath)
    {
        string? chosen = null;
        Application.Current.Dispatcher.Invoke(() =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "选择下载保存位置",
                Multiselect = false,
            };
            if (System.IO.Directory.Exists(startPath)) dlg.InitialDirectory = startPath;
            chosen = dlg.ShowDialog() == true ? dlg.FolderName : null;
        });
        return chosen;
    }
}
