using System.IO;
using System.Diagnostics;
using System.Text;

namespace Winnas.Console.Infrastructure;

/// <summary>
/// Small helper for the handful of command-line tools the console still needs
/// (schtasks). Output is read to the end instead of streamed — these calls are
/// short and always bounded by a timeout.
/// </summary>
public static class ProcessRunner
{
    /// <summary>
    /// schtasks prints in the OEM/ANSI code page. .NET only knows 936 when the
    /// optional code-pages provider is registered, so fall back to UTF-8 rather
    /// than throwing — a partially readable message beats a crash.
    /// </summary>
    public static Encoding Oem { get; } = ResolveOem();

    private static Encoding ResolveOem()
    {
        try { return Encoding.GetEncoding(936); }
        catch { return Encoding.UTF8; }
    }

    public static (int Code, string Out, string Err) Run(string file, string args,
        Encoding? encoding = null, int timeoutMs = 15000)
    {
        try
        {
            var psi = new ProcessStartInfo(file, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = encoding ?? Oem,
                StandardErrorEncoding = encoding ?? Oem,
            };

            using var p = Process.Start(psi);
            if (p is null) return (-1, "", "无法启动 " + file);

            var so = p.StandardOutput.ReadToEnd();
            var se = p.StandardError.ReadToEnd();
            var exited = p.WaitForExit(timeoutMs);
            return exited ? (p.ExitCode, so, se) : (-1, so, "超时未退出");
        }
        catch (Exception ex) { return (-1, "", ex.Message); }
    }
}
