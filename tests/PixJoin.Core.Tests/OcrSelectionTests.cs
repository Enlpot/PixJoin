using System.Collections.Generic;
using PixJoin.Core.Models;
using PixJoin.Core.Services;

namespace PixJoin.Core.Tests;

public static class OcrSelectionTests
{
    // 构造一个两行词表（图片像素坐标）：
    // 行0: "Hello"(0,0,50,20) "PixJoin"(60,0,70,20)
    // 行1: "你好"(0,30,40,20) "测试"(50,30,40,20)
    private static List<OcrWord> Words() => new()
    {
        new OcrWord { Text = "Hello", X = 0, Y = 0, W = 50, H = 20, LineIndex = 0 },
        new OcrWord { Text = "PixJoin", X = 60, Y = 0, W = 70, H = 20, LineIndex = 0 },
        new OcrWord { Text = "你好", X = 0, Y = 30, W = 40, H = 20, LineIndex = 1 },
        new OcrWord { Text = "测试", X = 50, Y = 30, W = 40, H = 20, LineIndex = 1 },
    };

    public static void Run()
    {
        Check.Section("OcrSelection 文字命中 / 选区归一 / 文本拼接");

        var w = Words();

        // 命中检测
        Check.True(OcrSelection.HitTest(w, 10, 10) == 0, "命中 word0");
        Check.True(OcrSelection.HitTest(w, 90, 5) == 1, "命中 word1");
        Check.True(OcrSelection.HitTest(w, 20, 40) == 2, "命中 word2");
        Check.True(OcrSelection.HitTest(w, 60, 40) == 3, "命中 word3");
        Check.True(OcrSelection.HitTest(w, 55, 5) == -1, "词间空隙未命中");
        Check.True(OcrSelection.HitTest(w, 999, 999) == -1, "远处未命中");
        Check.True(OcrSelection.HitTest(new List<OcrWord>(), 1, 1) == -1, "空表未命中");

        // 选区归一
        var (s1, e1) = OcrSelection.Normalize(3, 1);
        Check.True(s1 == 1 && e1 == 3, "反向选区归一");
        var (s2, e2) = OcrSelection.Normalize(2, 2);
        Check.True(s2 == 2 && e2 == 2, "单点选区");

        // 文本拼接：同行英文补空格、中文直拼、跨行换行
        Check.True(OcrSelection.BuildText(w, 0, 1) == "Hello PixJoin", "英文同行补空格");
        Check.True(OcrSelection.BuildText(w, 2, 3) == "你好测试", "中文同行直拼");
        Check.True(OcrSelection.BuildText(w, 0, 2) == "Hello PixJoin\n你好", "跨行换行");
        Check.True(OcrSelection.BuildText(w, 0, 3) == "Hello PixJoin\n你好测试", "全部拼接");
        Check.True(OcrSelection.BuildText(w, 3, 1) == "PixJoin\n你好测试", "反向范围拼接");
        Check.True(OcrSelection.BuildText(new List<OcrWord>(), 0, 0) == "", "空词表");
        Check.True(OcrSelection.BuildText(w, -5, 100) == "Hello PixJoin\n你好测试", "越界裁剪");
    }
}
