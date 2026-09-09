using System.Collections.Generic;
using PixJoin.Core.Models;
using PixJoin.Core.Services;

namespace PixJoin.Core.Tests;

public static class OcrTableBuilderTests
{
    public static void Run()
    {
        TableCsv();
        TableMarkdown();
        Escaping();
        Empty();
        Check.Section("OcrTableBuilderTests 全部通过");
    }

    private static void TableCsv()
    {
        var words = new List<OcrWord>
        {
            new() { Text = "姓名", X = 0, Y = 0, W = 30, H = 16, LineIndex = 0 },
            new() { Text = "年龄", X = 120, Y = 0, W = 30, H = 16, LineIndex = 0 },
            new() { Text = "张三", X = 0, Y = 20, W = 30, H = 16, LineIndex = 1 },
            new() { Text = "25", X = 120, Y = 20, W = 20, H = 16, LineIndex = 1 },
        };
        var csv = OcrTableBuilder.BuildCsv(words);
        Check.True(csv.Replace("\r\n", "\n") == "姓名,年龄\n张三,25", "CSV 表格行/列正确，got: " + csv);
    }

    private static void TableMarkdown()
    {
        var words = new List<OcrWord>
        {
            new() { Text = "A", X = 0, Y = 0, W = 10, H = 10, LineIndex = 0 },
            new() { Text = "B", X = 100, Y = 0, W = 10, H = 10, LineIndex = 0 },
            new() { Text = "1", X = 0, Y = 20, W = 10, H = 10, LineIndex = 1 },
        };
        var md = OcrTableBuilder.BuildMarkdown(words);
        Check.True(md.Contains("| A | B |"), "Markdown 表头，got: " + md);
        Check.True(md.Contains("| 1 |") && md.Contains("| A | B |"), "Markdown 空单元格补齐，got: " + md);
    }

    private static void Escaping()
    {
        var words = new List<OcrWord>
        {
            new() { Text = "a,b", X = 0, Y = 0, W = 20, H = 10, LineIndex = 0 },
            new() { Text = "c\"d", X = 100, Y = 0, W = 20, H = 10, LineIndex = 0 },
        };
        var csv = OcrTableBuilder.BuildCsv(words);
        Check.True(csv == "\"a,b\",\"c\"\"d\"", "CSV 转义，got: " + csv);
    }

    private static void Empty()
    {
        Check.True(OcrTableBuilder.BuildCsv(new List<OcrWord>()) == "", "空表格 CSV");
        Check.True(OcrTableBuilder.BuildMarkdown(new List<OcrWord>()) == "", "空表格 Markdown");
    }
}
