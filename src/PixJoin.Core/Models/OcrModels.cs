using System.Collections.Generic;

namespace PixJoin.Core.Models;

/// <summary>OCR 识别出的一个词（含边界框，坐标为图片物理像素）。</summary>
public sealed class OcrWord
{
    public string Text { get; init; } = "";

    public double X { get; init; }
    public double Y { get; init; }
    public double W { get; init; }
    public double H { get; init; }

    /// <summary>所属行索引（文档顺序）。</summary>
    public int LineIndex { get; init; }

    public bool HitTest(double px, double py) =>
        px >= X && px < X + W && py >= Y && py < Y + H;
}

/// <summary>整张贴图的 OCR 结果。</summary>
public sealed class OcrResult
{
    public List<OcrWord> Words { get; init; } = new();

    /// <summary>引擎给出的全文（按行拼接，供"复制所有文本"直接使用）。</summary>
    public string FullText { get; init; } = "";
}
