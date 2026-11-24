using System.IO;
using System.Text;

namespace Winnas.Console.Infrastructure;

/// <summary>
/// Follows a robocopy /UNILOG file while the copy is running and hands every
/// new line to the UI. Polling the file (instead of reading stdout) is what
/// makes local and remote runs behave identically, and it sidesteps the OEM
/// code page entirely because the log is UTF-16.
/// </summary>
public sealed class LogTailer : IDisposable
{
    private readonly string _path;
    private readonly Action<string>? _onLine;
    private readonly CancellationTokenSource _cts = new();
    private Task? _task;
    private long _pos;
    private bool _bomSeen;

    public LogTailer(string path, Action<string>? onLine)
    {
        _path = path;
        _onLine = onLine;
    }

    public void Start()
    {
        _task = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try { Read(); }
                catch { /* the file appears only once robocopy starts writing */ }
                try { await Task.Delay(700, _cts.Token); }
                catch (OperationCanceledException) { break; }
            }
        });
    }

    public void Stop()
    {
        try { _cts.Cancel(); } catch { /* already stopped */ }
        try { _task?.Wait(1000); } catch { /* ignore */ }
    }

    /// <summary>Last read before the caller reports completion.</summary>
    public void Flush()
    {
        try { Read(); } catch { /* ignore */ }
    }

    private void Read()
    {
        if (!File.Exists(_path)) return;

        using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var length = fs.Length;
        if (length <= _pos) return;

        // UTF-16 keeps every character at an even offset, so a byte position
        // that landed mid-character would corrupt the rest of the line.
        var start = _pos;
        if (start % 2 != 0) start--;

        fs.Seek(start, SeekOrigin.Begin);
        var count = (int)(length - start);
        var buffer = new byte[count];
        var read = fs.Read(buffer, 0, count);
        _pos = length;

        var text = Encoding.Unicode.GetString(buffer, 0, read);
        if (!_bomSeen && text.Length > 0 && text[0] == '\uFEFF')
        {
            text = text[1..];
            _bomSeen = true;
        }

        if (_onLine is null) return;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r').TrimEnd();
            // robocopy rewrites its progress line in place; those are noise here.
            if (line.Length == 0) continue;
            _onLine(line);
        }
    }

    public void Dispose() => Stop();
}
