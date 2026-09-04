namespace PixJoin.Core.Models;

/// <summary>对齐参考线（用于吸附反馈的可视化）。坐标为物理像素。</summary>
public readonly struct GuideLine
{
    public GuideLine(bool isVertical, double position, double start, double end)
    {
        IsVertical = isVertical;
        Position = position;
        Start = start;
        End = end;
    }

    /// <summary>true = 竖直参考线（x = Position）；false = 水平参考线（y = Position）。</summary>
    public bool IsVertical { get; }

    public double Position { get; }

    public double Start { get; }

    public double End { get; }
}

/// <summary>一次吸附判定的结果。</summary>
public sealed class SnapResult
{
    /// <summary>吸附到的目标贴图。</summary>
    public required Sticker Target { get; init; }

    /// <summary>目标所属组合体（可作为"吸附到组合体"的语义依据）。</summary>
    public string? TargetGroupId { get; init; }

    /// <summary>需要施加到拖动贴图上的位移，使其贴合 / 对齐。</summary>
    public double Dx { get; init; }

    public double Dy { get; init; }

    /// <summary>贴合后拖动贴图的包围盒。</summary>
    public Rect PreviewBounds { get; init; }

    /// <summary>需要绘制的对齐参考线。</summary>
    public IReadOnlyList<GuideLine> Guides { get; init; } = Array.Empty<GuideLine>();

    /// <summary>本次吸附的间隙距离（调试 / 日志用）。</summary>
    public double Gap { get; init; }
}
