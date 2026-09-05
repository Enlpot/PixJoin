using System;
using System.Collections.Generic;
using System.Text;
using PixJoin.Core.Models;

namespace PixJoin.Core.Services;

/// <summary>
/// 文字选择纯逻辑：词命中、选区归一化、按行拼接文本。
/// 不依赖任何 UI / 引擎，可单元测试。
/// </summary>
public static class OcrSelection
{
    /// <summary>命中检测：返回包含该点的词索引（文档顺序），未命中返回 -1。</summary>
    public static int HitTest(IReadOnlyList<OcrWord> words, double px, double py)
    {
        for (int i = 0; i < words.Count; i++)
            if (words[i].HitTest(px, py)) return i;
        return -1;
    }

    /// <summary>起点/终点归一化为 [start, end]（start ≤ end）。</summary>
    public static (int Start, int End) Normalize(int a, int b) =>
        a <= b ? (a, b) : (b, a);

    /// <summary>
    /// 把 [start, end] 范围内的词拼接为文本：跨行换行；行内英文/数字词之间补空格，中文直接拼接。
    /// </summary>
    public static string BuildText(IReadOnlyList<OcrWord> words, int start, int end)
    {
        if (words.Count == 0) return "";

        var (s, e) = Normalize(start, end);
        s = Math.Clamp(s, 0, words.Count - 1);
        e = Math.Clamp(e, 0, words.Count - 1);
        if (s > e) return "";

        var sb = new StringBuilder();
        for (int i = s; i <= e; i++)
        {
            var w = words[i];
            if (i > s)
            {
                var prev = words[i - 1];
                if (w.LineIndex != prev.LineIndex)
                    sb.Append('\n');
                else if (NeedSpace(prev.Text, w.Text))
                    sb.Append(' ');
            }
            sb.Append(w.Text);
        }
        return sb.ToString();
    }

    /// <summary>英文/数字词之间需要空格（中文与中文、中英之间直接拼接）。</summary>
    private static bool NeedSpace(string left, string right)
    {
        if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right)) return false;
        return IsAsciiAlnum(left[^1]) && IsAsciiAlnum(right[0]);
    }

    private static bool IsAsciiAlnum(char c) =>
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');
}
