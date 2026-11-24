using System.IO;
using Winnas.Console.Infrastructure;
using Winnas.Console.Models;

namespace Winnas.Console.Services;

/// <summary>
/// File browsing and transfer for a NAS target.
///
/// This is the one part of WINNAS that deliberately does NOT go through CIM:
/// enumerating and copying files over WMI is slow and cannot stream. Instead
/// it uses normal .NET file APIs against a path that WinnasService.PathFor has
/// already rewritten to \\host\D$\... for remote targets, which rides the
/// built-in administrative share over SMB.
/// </summary>
public sealed class FileService
{
    /// <summary>Progress report for a transfer, 0..1.</summary>
    public sealed record TransferProgress(long Done, long Total, string Current);

    // ------------------------------------------------------------ browsing

    public Task<List<FileItem>> ListAsync(WinnasSession s, string targetPath) => Task.Run(() =>
    {
        // UNC paths to \host\C$ authenticate as whatever token the thread is
        // carrying, so a session opened as a named account has to be entered
        // again here — CIM impersonation does not leak into the file APIs.
        using var _ = s.Impersonate();
        var access = WinnasService.PathFor(s, targetPath);
        var result = new List<FileItem>();

        if (string.IsNullOrWhiteSpace(access) || !Directory.Exists(access))
            return result;

        var dir = new DirectoryInfo(access);

        foreach (var d in dir.EnumerateDirectories())
        {
            // Reparse points (junctions, symlinks) can loop a browser forever.
            if ((d.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            result.Add(new FileItem
            {
                Name = d.Name,
                FullPath = CombineDisplay(targetPath, d.Name),
                IsDirectory = true,
                LastWrite = Safe(() => d.LastWriteTime),
            });
        }

        foreach (var f in dir.EnumerateFiles())
        {
            if ((f.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            result.Add(new FileItem
            {
                Name = f.Name,
                FullPath = CombineDisplay(targetPath, f.Name),
                IsDirectory = false,
                Size = Safe(() => f.Length),
                LastWrite = Safe(() => f.LastWriteTime),
            });
        }

        // Folders first is what every file manager does; then alphabetical.
        return result
            .OrderByDescending(x => x.IsDirectory)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    });

    /// <summary>
    /// Entry points: the target's SMB shares make the best home view. When a
    /// machine has no shares at all (a fresh Windows install, for instance) the
    /// page would be empty, so fall back to the volumes themselves.
    /// </summary>
    public Task<List<FileItem>> ListSharesAsync(WinnasSession s) => Task.Run(() =>
    {
        using var _ = s.Impersonate();
        var list = s.Query(Cim.SmbNs, "SELECT * FROM MSFT_SmbShare")
            .Where(x => !Cim.Bool(x, "Special"))
            .Select(x => new FileItem
            {
                Name = Cim.Str(x, "Name"),
                FullPath = Cim.Str(x, "Path"),
                IsDirectory = true,
                IsShareRoot = true,
            })
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (list.Count > 0) return list;

        return s.Query(Cim.StorageNs, "SELECT * FROM MSFT_Volume")
            .Select(v => new { Letter = Cim.DriveLetter(v, "DriveLetter"), Label = Cim.Str(v, "FileSystemLabel") })
            .Where(v => v.Letter.Length > 0)
            .Select(v => new FileItem
            {
                Name = v.Label.Length > 0 ? $"{v.Letter}: {v.Label}" : $"本地磁盘 {v.Letter}:",
                FullPath = v.Letter + ":\\",
                IsDirectory = true,
                IsShareRoot = true,
                IsVolume = true,
            })
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    });

    public Task<OpResult> CreateFolderAsync(WinnasSession s, string parent, string name) => Task.Run(() =>
    {
        using var _ = s.Impersonate();
        try
        {
            if (string.IsNullOrWhiteSpace(name)) return OpResult.Fail("名称不能为空");
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return OpResult.Fail("名称包含非法字符");

            var access = Path.Combine(WinnasService.PathFor(s, parent), name.Trim());
            if (Directory.Exists(access)) return OpResult.Fail("同名文件夹已存在");

            Directory.CreateDirectory(access);
            return OpResult.Good($"已创建文件夹 {name.Trim()}");
        }
        catch (Exception ex) { return OpResult.From(ex); }
    });

    public Task<OpResult> DeleteAsync(WinnasSession s, string targetPath, bool isDirectory) => Task.Run(() =>
    {
        using var _ = s.Impersonate();
        try
        {
            var access = WinnasService.PathFor(s, targetPath);
            if (isDirectory)
            {
                if (!Directory.Exists(access)) return OpResult.Fail("目录不存在");
                Directory.Delete(access, recursive: true);
            }
            else
            {
                if (!File.Exists(access)) return OpResult.Fail("文件不存在");
                File.Delete(access);
            }
            return OpResult.Good("已删除");
        }
        catch (Exception ex) { return OpResult.From(ex); }
    });

    public Task<OpResult> RenameAsync(WinnasSession s, string targetPath, string newName, bool isDirectory) => Task.Run(() =>
    {
        using var _ = s.Impersonate();
        try
        {
            if (string.IsNullOrWhiteSpace(newName)) return OpResult.Fail("名称不能为空");
            if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return OpResult.Fail("名称包含非法字符");

            var access = WinnasService.PathFor(s, targetPath);
            var parent = Path.GetDirectoryName(access);
            if (string.IsNullOrEmpty(parent)) return OpResult.Fail("无法确定父目录");

            var dest = Path.Combine(parent, newName.Trim());
            if (isDirectory)
            {
                if (!Directory.Exists(access)) return OpResult.Fail("目录不存在");
                if (Directory.Exists(dest)) return OpResult.Fail("目标名称已存在");
                Directory.Move(access, dest);
            }
            else
            {
                if (!File.Exists(access)) return OpResult.Fail("文件不存在");
                if (File.Exists(dest)) return OpResult.Fail("目标名称已存在");
                File.Move(access, dest);
            }
            return OpResult.Good("已重命名");
        }
        catch (Exception ex) { return OpResult.From(ex); }
    });

    // ------------------------------------------------------------ transfer

    /// <summary>
    /// Copy a local file onto the target. Chunked rather than File.Copy so the
    /// UI can show progress and stays responsive on a slow SMB link.
    ///
    /// The two ends belong to two different security contexts: the source lives
    /// on this PC (current user), the destination lives on the NAS (the account
    /// in the connection). Each handle is therefore opened under the right
    /// context, and the copy itself just uses the handles — which is why this
    /// is a synchronous loop on a worker thread instead of async I/O, whose
    /// continuation could land on a thread without the impersonation.
    /// </summary>
    public Task<OpResult> UploadAsync(WinnasSession s, string localFile, string targetDirectory,
        IProgress<TransferProgress>? progress = null, CancellationToken ct = default) => Task.Run(() =>
    {
        try
        {
            if (!File.Exists(localFile)) return OpResult.Fail("本地文件不存在");

            var name = Path.GetFileName(localFile);
            var destDir = WinnasService.PathFor(s, targetDirectory);

            var dest = Path.Combine(destDir, name);
            var total = new FileInfo(localFile).Length;

            using var src = new FileStream(localFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, BufferSize);
            FileStream dst;
            using (s.Impersonate())
            {
                if (!Directory.Exists(destDir)) return OpResult.Fail("目标目录不存在");
                dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize);
            }

            using (dst)
            {
                var buffer = new byte[BufferSize];
                long done = 0;
                int read;
                while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    dst.Write(buffer, 0, read);
                    done += read;
                    progress?.Report(new TransferProgress(done, total, name));
                }
            }

            return OpResult.Good($"已上传 {name}（{Format.Bytes(total)}）");
        }
        catch (OperationCanceledException) { return OpResult.Fail("已取消"); }
        catch (Exception ex) { return OpResult.From(ex); }
    });

    /// <summary>Copy a file off the target into a local folder.</summary>
    public Task<OpResult> DownloadAsync(WinnasSession s, string targetFile, string localDirectory,
        IProgress<TransferProgress>? progress = null, CancellationToken ct = default) => Task.Run(() =>
    {
        try
        {
            var access = WinnasService.PathFor(s, targetFile);

            FileStream src;
            using (s.Impersonate())
            {
                if (!File.Exists(access)) return OpResult.Fail("目标文件不存在或无权访问");
                src = new FileStream(access, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, BufferSize);
            }

            var name = Path.GetFileName(access);
            Directory.CreateDirectory(localDirectory);

            var dest = UniquePath(Path.Combine(localDirectory, name));
            var total = src.Length;

            using (src)
            using (var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize))
            {
                var buffer = new byte[BufferSize];
                long done = 0;
                int read;
                while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    dst.Write(buffer, 0, read);
                    done += read;
                    progress?.Report(new TransferProgress(done, total, name));
                }
            }

            return OpResult.Good($"已下载到 {dest}");
        }
        catch (OperationCanceledException) { return OpResult.Fail("已取消"); }
        catch (Exception ex) { return OpResult.From(ex); }
    });

    private const int BufferSize = 1024 * 1024;

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path) ?? "";
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 1; i < 999; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        return path;
    }

    // ------------------------------------------------------------- helpers

    /// <summary>
    /// Joins in *display* space, not access space: the UI shows the target's
    /// own view of its filesystem (D:\NAS\Public), never the UNC rewrite.
    /// </summary>
    public static string CombineDisplay(string parent, string name)
    {
        if (parent.EndsWith('\\') || parent.EndsWith('/')) return parent + name;
        return parent + "\\" + name;
    }

    public static string ParentOf(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        var trimmed = path.TrimEnd('\\', '/');
        if (trimmed.Length <= 2) return "";

        var cut = trimmed.LastIndexOfAny(new[] { '\\', '/' });
        if (cut <= 0) return "";

        var parent = trimmed[..cut];
        // Keep the drive root looking like a root: D:\ not D:
        if (parent.Length == 2 && parent[1] == ':') return parent + "\\";
        return parent + "\\";
    }

    private static T Safe<T>(Func<T> f)
    {
        try { return f(); }
        catch { return default!; }
    }
}
