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

    /// <summary>
    /// Hands an http/https/mailto link to the user's default browser or mail
    /// client. The console itself deliberately does not fetch the human-facing
    /// github.com page: on this network only api.github.com is reachable, while
    /// the browser has its own (often different) path out.
    /// </summary>
    public static OpResult OpenUrl(string url)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(url)) return OpResult.Fail("链接为空");
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) ||
                (u.Scheme != Uri.UriSchemeHttp &&
                 u.Scheme != Uri.UriSchemeHttps &&
                 u.Scheme != Uri.UriSchemeMailto))
                return OpResult.Fail("不是有效的网页或邮件链接：" + url);

            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return OpResult.Good("已打开 " + url);
        }
        catch (Exception ex)
        {
            return OpResult.Fail("无法打开链接：" + OpResult.From(ex).Message);
        }
    }
}
