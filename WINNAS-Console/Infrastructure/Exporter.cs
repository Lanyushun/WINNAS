using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Win32;
using Winnas.Console.Models;

namespace Winnas.Console.Infrastructure;

/// <summary>
/// CSV / text export. Excel on a Chinese Windows still assumes GBK for bare
/// CSV, so every file goes out as UTF-8 with a BOM — otherwise every Chinese
/// column turns into mojibake on double click.
/// </summary>
public static class Exporter
{
    public static string ToCsv(IEnumerable<string> header, IEnumerable<IEnumerable<object?>> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Join(header));
        foreach (var row in rows) sb.AppendLine(Join(row.Select(Cell)));
        return sb.ToString();
    }

    private static string Join(IEnumerable<string> cells) =>
        string.Join(",", cells.Select(Escape));

    private static string Escape(string s) =>
        s.Contains('"') || s.Contains(',') || s.Contains('\n') || s.Contains('\r')
            ? "\"" + s.Replace("\"", "\"\"") + "\""
            : s;

    private static string Cell(object? value) => value switch
    {
        null => "",
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        bool b => b ? "是" : "否",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture) ?? "",
        _ => value.ToString() ?? "",
    };

    /// <summary>Asks where to put it, writes, and offers to open the folder.</summary>
    public static OpResult Save(string suggestedName, string filter, string content)
    {
        var dlg = new SaveFileDialog
        {
            FileName = suggestedName,
            Filter = filter,
            DefaultExt = filter.Contains("*.csv") ? ".csv" : ".txt",
            AddExtension = true,
        };

        if (dlg.ShowDialog() != true) return OpResult.Fail("已取消");

        try
        {
            System.IO.File.WriteAllText(dlg.FileName, content, new UTF8Encoding(true));
            ActivityLog.Ok("导出", $"已导出 {Path.GetFileName(dlg.FileName)}", dlg.FileName);
            return OpResult.Good($"已导出到 {dlg.FileName}");
        }
        catch (Exception ex)
        {
            var r = OpResult.From(ex);
            ActivityLog.Error("导出", "导出失败", r.Message);
            return r;
        }
    }

    public static OpResult SaveCsv(string suggestedName, IEnumerable<string> header,
        IEnumerable<IEnumerable<object?>> rows) =>
        Save(suggestedName, "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*", ToCsv(header, rows));

    public static OpResult SaveText(string suggestedName, string content) =>
        Save(suggestedName, "文本文件 (*.txt)|*.txt|日志文件 (*.log)|*.log|所有文件 (*.*)|*.*", content);
}
