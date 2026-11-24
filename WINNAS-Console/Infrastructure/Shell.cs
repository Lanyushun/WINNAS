using System.Diagnostics;
using System.IO;
using Winnas.Console.Models;

namespace Winnas.Console.Infrastructure;

/// <summary>Thin wrapper over Explorer so failures surface as a message, not a crash.</summary>
public static class Shell
{
    public static OpResult OpenFolder(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return OpResult.Fail("路径为空");

            // Opening a directory that was never created is a silent no-op in
            // Explorer, which reads as a broken button. Create it first.
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            return OpResult.Good("已打开 " + path);
        }
        catch (Exception ex)
        {
            return OpResult.Fail("无法打开目录：" + OpResult.From(ex).Message);
        }
    }

    public static OpResult OpenFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return OpResult.Fail("文件不存在：" + path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return OpResult.Good("已打开 " + path);
        }
        catch (Exception ex)
        {
            return OpResult.Fail("无法打开文件：" + OpResult.From(ex).Message);
        }
    }
}
