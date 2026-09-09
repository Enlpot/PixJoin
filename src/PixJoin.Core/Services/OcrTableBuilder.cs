using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using PixJoin.Core.Models;

namespace PixJoin.Core.Services;

/// <summary>
/// 将 OCR 词框聚合成表格文本：按行（LineIndex）分组、行内按 X 排序，输出 CSV / Markdown。
/// 行内词按横向间隙聚类为单元格（间隙 &gt; 阈值视为新列），避免同格多词被拆散。
/// </summary>
public static class OcrTableBuilder
{
    /// <summary>横向间隙超过该像素视为新列（物理像素）。</summary>
    private const double ColGapPx = 24;

    public static string BuildCsv(IReadOnlyList<OcrWord> words)
    {
        var rows = BuildRows(words);
        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            sb.AppendLine(string.Join(",", row.Select(EscapeCsv)));
        }
        return sb.ToString().TrimEnd('\r', '\n');
    }

    public static string BuildMarkdown(IReadOnlyList<OcrWord> words)
    {
        var rows = BuildRows(words);
        if (rows.Count == 0) return "";
        int cols = rows.Max(r => r.Count);
        var sb = new StringBuilder();
        sb.Append("| ").Append(string.Join(" | ", Enumerable.Range(0, cols).Select(_ => " "))).AppendLine(" |");
        sb.Append("| ").Append(string.Join(" | ", Enumerable.Range(0, cols).Select(_ => "---"))).AppendLine(" |");
        foreach (var row in rows)
        {
            var cells = new List<string>();
            for (int i = 0; i < cols; i++) cells.Add(i < row.Count ? row[i] : "");
            sb.Append("| ").Append(string.Join(" | ", cells)).AppendLine(" |");
        }
        return sb.ToString().TrimEnd('\r', '\n');
    }

    private static List<List<string>> BuildRows(IReadOnlyList<OcrWord> words)
    {
        var result = new List<List<string>>();
        if (words is not { Count: > 0 }) return result;

        var byLine = new SortedDictionary<int, List<OcrWord>>();
        foreach (var w in words)
        {
            if (string.IsNullOrWhiteSpace(w.Text)) continue;
            if (!byLine.TryGetValue(w.LineIndex, out var list))
                byLine[w.LineIndex] = list = new List<OcrWord>();
            list.Add(w);
        }

        foreach (var kv in byLine)
        {
            var rowWords = kv.Value.OrderBy(w => w.X).ToList();
            var row = new List<string>();
            var cell = new StringBuilder();
            double lastRight = double.MinValue;
            foreach (var w in rowWords)
            {
                if (cell.Length > 0 && w.X - lastRight > ColGapPx)
                {
                    row.Add(cell.ToString().Trim());
                    cell.Clear();
                }
                cell.Append(w.Text);
                lastRight = w.X + w.W;
            }
            if (cell.Length > 0) row.Add(cell.ToString().Trim());
            result.Add(row);
        }
        return result;
    }

    private static string EscapeCsv(string s)
    {
        if (s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r'))
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }
}
