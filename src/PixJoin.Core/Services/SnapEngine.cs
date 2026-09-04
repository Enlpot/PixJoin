using PixJoin.Core.Models;

namespace PixJoin.Core.Services;

/// <summary>
/// 吸附判定引擎 —— 纯逻辑、无 UI 依赖，可单元测试。
///
/// 判定规则（对需求"边缘距离 ≤ SNAP_DISTANCE 且对齐偏差在容差内"的落地解释）：
///   1. 主吸附轴：拖动贴图与目标的间隙 gap 在 [0, SNAP_DISTANCE] 且两者在垂直轴上存在重叠 → 可吸附；
///   2. 次对齐轴：若垂直轴上的"边对齐 / 居中对齐"偏差 ≤ 容差 → 顺带把该轴也吸附对齐（不强制）；
///   3. 多个候选取"总位移最小"者；总位移相同则取间隙更小者。
///
/// 说明：对齐容差用于"是否顺带对齐"，而不是"能否吸附"的硬门槛。
/// 否则"贴到目标侧面但垂直方向错开"这类最常见的用法会失效，与同类工具体验不符。
/// </summary>
public static class SnapEngine
{
    public sealed record Options(
        double SnapDistance = 10.0,
        double AlignTolerance = 5.0);

    public static readonly Options Default = new();

    /// <summary>
    /// 在候选目标中求解最佳吸附。
    /// </summary>
    /// <param name="moving">拖动贴图当前的包围盒（物理像素）。</param>
    /// <param name="selfId">拖动贴图自身 id（排除自身）。</param>
    /// <param name="selfGroupId">拖动贴图所属组合体（同组合成员不作为吸附目标）。</param>
    /// <param name="candidates">桌面上的其它贴图。</param>
    /// <param name="groupBoundsOf">给定贴图 id 返回其所属组合体的整体包围盒（无组合则返回 null）。</param>
    /// <param name="opt">阈值参数。</param>
    public static SnapResult? Resolve(
        Rect moving,
        string selfId,
        string? selfGroupId,
        IEnumerable<Sticker> candidates,
        Func<string, Rect?>? groupBoundsOf = null,
        Options? opt = null)
    {
        opt ??= Default;

        SnapCandidate? best = null;

        foreach (var target in candidates)
        {
            if (target.Id == selfId) continue;
            if (selfGroupId is not null && target.GroupId == selfGroupId) continue;

            var c = Evaluate(moving, target.Bounds, target, opt);
            Consider(ref best, c);

            // 额外候选：吸附到"整个组合体"的外轮廓（目标属于某个组合体时）
            if (target.GroupId is not null && groupBoundsOf is not null)
            {
                var gb = groupBoundsOf(target.GroupId);
                if (gb.HasValue && !gb.Value.IsEmpty)
                {
                    var gc = Evaluate(moving, gb.Value, target, opt);
                    // 与单个成员候选等价时优先保留成员候选（更贴合视觉）
                    if (gc is not null && (c is null || gc.Cost < c.Cost - 0.001))
                        Consider(ref best, gc);
                }
            }
        }

        return best?.ToResult();
    }

    private static void Consider(ref SnapCandidate? best, SnapCandidate? c)
    {
        if (c is null) return;
        if (best is null || c.Cost < best.Cost - 0.001 ||
            (Math.Abs(c.Cost - best.Cost) <= 0.001 && c.Gap < best.Gap))
            best = c;
    }

    private static SnapCandidate? Evaluate(Rect a, Rect b, Sticker target, Options opt)
    {
        // ---------- 水平贴合：A 在 B 左侧 / 右侧 ----------
        SnapCandidate? horizontal = null;

        double gapLeft = b.Left - a.Right;    // A 在 B 左：A 需右移 gapLeft
        double gapRight = a.Left - b.Right;   // A 在 B 右：A 需左移 gapRight
        double vOverlap = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);

        if (vOverlap >= 0)
        {
            if (gapLeft >= 0 && gapLeft <= opt.SnapDistance)
                horizontal = Build(a, b, target, dx: gapLeft, dy: AlignVertical(a, b, opt.AlignTolerance), gap: gapLeft);
            else if (gapRight >= 0 && gapRight <= opt.SnapDistance)
                horizontal = Build(a, b, target, dx: -gapRight, dy: AlignVertical(a, b, opt.AlignTolerance), gap: gapRight);
        }

        // ---------- 垂直贴合：A 在 B 上方 / 下方 ----------
        SnapCandidate? vertical = null;

        double gapTop = b.Top - a.Bottom;      // A 在 B 上：A 需下移 gapTop
        double gapBottom = a.Top - b.Bottom;   // A 在 B 下：A 需上移 gapBottom
        double hOverlap = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);

        if (hOverlap >= 0)
        {
            if (gapTop >= 0 && gapTop <= opt.SnapDistance)
                vertical = Build(a, b, target, dx: AlignHorizontal(a, b, opt.AlignTolerance), dy: gapTop, gap: gapTop);
            else if (gapBottom >= 0 && gapBottom <= opt.SnapDistance)
                vertical = Build(a, b, target, dx: AlignHorizontal(a, b, opt.AlignTolerance), dy: -gapBottom, gap: gapBottom);
        }

        // 已经重叠（gap 为负）不产生吸附候选，避免贴图互相压盖
        if (horizontal is null) return vertical;
        if (vertical is null) return horizontal;
        return horizontal.Cost <= vertical.Cost ? horizontal : vertical;
    }

    private static SnapCandidate Build(Rect a, Rect b, Sticker target, double dx, double dy, double gap)
    {
        var moved = new Rect(a.Left + dx, a.Top + dy, a.Width, a.Height);
        var guides = new List<GuideLine>();

        // 主吸附轴：贴合边
        if (Math.Abs(dx) > 0.5 && Math.Abs(gap) <= Math.Abs(dx) + 0.5)
        {
            var x = moved.Right <= b.Left + 0.5 ? moved.Right : moved.Left;
            var top = Math.Min(moved.Top, b.Top);
            var bottom = Math.Max(moved.Bottom, b.Bottom);
            guides.Add(new GuideLine(true, x, top, bottom));
        }
        else if (Math.Abs(dy) > 0.5)
        {
            var y = moved.Bottom <= b.Top + 0.5 ? moved.Bottom : moved.Top;
            var left = Math.Min(moved.Left, b.Left);
            var right = Math.Max(moved.Right, b.Right);
            guides.Add(new GuideLine(false, y, left, right));
        }

        // 次对齐轴：边对齐 / 居中对齐
        if (Math.Abs(dx) > 0.5 && Math.Abs(dy) <= 0.5)
        {
            // 水平贴合 + 垂直方向对齐 → 画水平参考线
            var y = NearlyEqual(moved.Top, b.Top) ? moved.Top
                  : NearlyEqual(moved.Bottom, b.Bottom) ? moved.Bottom
                  : moved.Top + moved.Height / 2;
            guides.Add(new GuideLine(false, y, Math.Min(moved.Left, b.Left), Math.Max(moved.Right, b.Right)));
        }
        else if (Math.Abs(dy) > 0.5 && Math.Abs(dx) <= 0.5)
        {
            var x = NearlyEqual(moved.Left, b.Left) ? moved.Left
                  : NearlyEqual(moved.Right, b.Right) ? moved.Right
                  : moved.Left + moved.Width / 2;
            guides.Add(new GuideLine(true, x, Math.Min(moved.Top, b.Top), Math.Max(moved.Bottom, b.Bottom)));
        }

        return new SnapCandidate(target, dx, dy, moved, guides, gap, Math.Abs(dx) + Math.Abs(dy));
    }

    private static bool NearlyEqual(double p, double q) => Math.Abs(p - q) <= 0.5;

    // ==================== 边缘对齐吸附（同侧边对齐，不成组） ====================

    /// <summary>
    /// 拖动时的「边缘对齐」磁吸：moving 的边与候选目标的同侧边（上对上 / 下对下 / 左对左 / 右对右）
    /// 距离在容差内且正交投影靠近时，整体平移对齐。与 <see cref="Resolve"/>（贴合成组）互补：
    /// 两者都是「无 UI 依赖」的纯逻辑，由调用方决定优先级与是否成组。
    /// </summary>
    public static SnapResult? ResolveAlign(
        Rect moving,
        string selfId,
        string? selfGroupId,
        IEnumerable<Sticker> candidates,
        Func<string, Rect?>? groupBoundsOf = null,
        Options? opt = null)
    {
        opt ??= Default;
        SnapCandidate? best = null;

        foreach (var target in candidates)
        {
            if (target.Id == selfId) continue;
            if (selfGroupId is not null && target.GroupId == selfGroupId) continue;

            var c = EvaluateAlign(moving, target.Bounds, target, opt);
            Consider(ref best, c);

            if (target.GroupId is not null && groupBoundsOf is not null)
            {
                var gb = groupBoundsOf(target.GroupId);
                if (gb.HasValue && !gb.Value.IsEmpty)
                {
                    var gc = EvaluateAlign(moving, gb.Value, target, opt);
                    if (gc is not null && (c is null || gc.Cost < c.Cost - 0.001))
                        Consider(ref best, gc);
                }
            }
        }

        return best?.ToResult();
    }

    /// <summary>
    /// 缩放时的「移动边对齐」磁吸：缩放保持固定对角不动，只有「移动边」（Right/Bottom 或 Left/Top）可变。
    /// 移动边与候选目标边缘（贴合或同侧对齐）距离在容差内且正交投影靠近时，把移动边吸附到目标边缘。
    /// 返回值 Dx / Dy = 移动边需移动的位移（edge + Dx/Dy = 目标边缘位置）。
    /// </summary>
    /// <param name="moving">缩放后的新包围盒（对角已固定）。</param>
    /// <param name="fixedCorner">固定对角的物理位置（单点，决定哪些边是移动边）。</param>
    public static SnapResult? ResolveResizeAlign(
        Rect moving,
        Point fixedCorner,
        string selfId,
        string? selfGroupId,
        IEnumerable<Sticker> candidates,
        Func<string, Rect?>? groupBoundsOf = null,
        Options? opt = null)
    {
        opt ??= Default;
        bool moveRight = Math.Abs(moving.Left - fixedCorner.X) <= 0.5;
        bool moveBottom = Math.Abs(moving.Top - fixedCorner.Y) <= 0.5;

        SnapCandidate? best = null;
        foreach (var target in candidates)
        {
            if (target.Id == selfId) continue;
            if (selfGroupId is not null && target.GroupId == selfGroupId) continue;

            var c = EvaluateResizeAlign(moving, moveRight, moveBottom, target.Bounds, target, opt);
            Consider(ref best, c);

            if (target.GroupId is not null && groupBoundsOf is not null)
            {
                var gb = groupBoundsOf(target.GroupId);
                if (gb.HasValue && !gb.Value.IsEmpty)
                {
                    var gc = EvaluateResizeAlign(moving, moveRight, moveBottom, gb.Value, target, opt);
                    if (gc is not null && (c is null || gc.Cost < c.Cost - 0.001))
                        Consider(ref best, gc);
                }
            }
        }
        return best?.ToResult();
    }

    private static SnapCandidate? EvaluateAlign(Rect a, Rect b, Sticker target, Options opt)
    {
        double hGap = ProjGap(a.Left, a.Right, b.Left, b.Right);
        double vGap = ProjGap(a.Top, a.Bottom, b.Top, b.Bottom);

        double dx = 0, dy = 0;
        bool hasDx = false, hasDy = false;
        var guides = new List<GuideLine>();

        // 垂直边对齐（左对齐 / 右对齐）：要求垂直投影靠近（重叠或间隙小）
        if (vGap <= opt.SnapDistance)
        {
            double dLeft = b.Left - a.Left;
            double dRight = b.Right - a.Right;
            double d = PickSmaller(dLeft, dRight, opt.SnapDistance, out bool toLeft);
            if (Math.Abs(d) <= opt.SnapDistance)
            {
                dx = d; hasDx = true;
                guides.Add(new GuideLine(true, toLeft ? b.Left : b.Right, Math.Min(a.Top, b.Top), Math.Max(a.Bottom, b.Bottom)));
            }
        }

        // 水平边对齐（上对齐 / 下对齐）：要求水平投影靠近
        if (hGap <= opt.SnapDistance)
        {
            double dTop = b.Top - a.Top;
            double dBottom = b.Bottom - a.Bottom;
            double d = PickSmaller(dTop, dBottom, opt.SnapDistance, out bool toTop);
            if (Math.Abs(d) <= opt.SnapDistance)
            {
                dy = d; hasDy = true;
                guides.Add(new GuideLine(false, toTop ? b.Top : b.Bottom, Math.Min(a.Left, b.Left), Math.Max(a.Right, b.Right)));
            }
        }

        if (!hasDx && !hasDy) return null;

        var moved = new Rect(a.Left + dx, a.Top + dy, a.Width, a.Height);
        double cost = Math.Abs(dx) + Math.Abs(dy);
        return new SnapCandidate(target, dx, dy, moved, guides, cost, cost);
    }

    private static SnapCandidate? EvaluateResizeAlign(Rect a, bool moveRight, bool moveBottom, Rect b, Sticker target, Options opt)
    {
        double hGap = ProjGap(a.Left, a.Right, b.Left, b.Right);
        double vGap = ProjGap(a.Top, a.Bottom, b.Top, b.Bottom);

        double dx = 0, dy = 0;
        bool hasDx = false, hasDy = false;
        var guides = new List<GuideLine>();

        // 移动水平边（Right 或 Left）与目标左/右边对齐：要求垂直投影靠近
        if (vGap <= opt.SnapDistance)
        {
            double edge = moveRight ? a.Right : a.Left;
            double dLeft = b.Left - edge;
            double dRight = b.Right - edge;
            double d = PickSmaller(dLeft, dRight, opt.SnapDistance, out bool toLeft);
            if (Math.Abs(d) <= opt.SnapDistance)
            {
                dx = d; hasDx = true;
                guides.Add(new GuideLine(true, toLeft ? b.Left : b.Right, Math.Min(a.Top, b.Top), Math.Max(a.Bottom, b.Bottom)));
            }
        }

        // 移动垂直边（Bottom 或 Top）与目标上/下边对齐：要求水平投影靠近
        if (hGap <= opt.SnapDistance)
        {
            double edge = moveBottom ? a.Bottom : a.Top;
            double dTop = b.Top - edge;
            double dBottom = b.Bottom - edge;
            double d = PickSmaller(dTop, dBottom, opt.SnapDistance, out bool toTop);
            if (Math.Abs(d) <= opt.SnapDistance)
            {
                dy = d; hasDy = true;
                guides.Add(new GuideLine(false, toTop ? b.Top : b.Bottom, Math.Min(a.Left, b.Left), Math.Max(a.Right, b.Right)));
            }
        }

        if (!hasDx && !hasDy) return null;

        // 对齐后的完整包围盒（对角固定，移动边吸附）
        double newL = moveRight ? a.Left : a.Left + dx;
        double newT = moveBottom ? a.Top : a.Top + dy;
        double newW = moveRight ? a.Width + dx : a.Width - dx;
        double newH = moveBottom ? a.Height + dy : a.Height - dy;
        var moved = new Rect(newL, newT, newW, newH);
        double cost = Math.Abs(dx) + Math.Abs(dy);
        return new SnapCandidate(target, dx, dy, moved, guides, cost, cost);
    }

    /// <summary>正交投影间隙：>0 表示分离，&lt;=0 表示重叠。</summary>
    private static double ProjGap(double min1, double max1, double min2, double max2)
        => Math.Max(min1, min2) - Math.Min(max1, max2);

    /// <summary>在两个候选位移中取「绝对值更小且在容差内」者；都不在容差内时返回 d1（调用方再过滤）。</summary>
    private static double PickSmaller(double d1, double d2, double tol, out bool first)
    {
        bool in1 = Math.Abs(d1) <= tol;
        bool in2 = Math.Abs(d2) <= tol;
        if (in1 && (!in2 || Math.Abs(d1) <= Math.Abs(d2))) { first = true; return d1; }
        if (in2) { first = false; return d2; }
        first = true;
        return d1;
    }

    /// <summary>垂直方向对齐吸附：上边 / 下边 / 中线，偏差在容差内才吸附。</summary>
    private static double AlignVertical(Rect a, Rect b, double tol)
    {
        var candidates = new[]
        {
            b.Top - a.Top,
            b.Bottom - a.Bottom,
            (b.Top + b.Height / 2) - (a.Top + a.Height / 2),
        };
        foreach (var d in candidates)
            if (Math.Abs(d) <= tol) return d;
        return 0;
    }

    /// <summary>水平方向对齐吸附：左边 / 右边 / 中线。</summary>
    private static double AlignHorizontal(Rect a, Rect b, double tol)
    {
        var candidates = new[]
        {
            b.Left - a.Left,
            b.Right - a.Right,
            (b.Left + b.Width / 2) - (a.Left + a.Width / 2),
        };
        foreach (var d in candidates)
            if (Math.Abs(d) <= tol) return d;
        return 0;
    }

    private sealed class SnapCandidate
    {
        public SnapCandidate(Sticker target, double dx, double dy, Rect preview, List<GuideLine> guides, double gap, double cost)
        {
            Target = target; dx0 = dx; dy0 = dy; Preview = preview; Guides = guides; Gap = gap; Cost = cost;
        }

        private readonly double dx0;
        private readonly double dy0;
        public Sticker Target { get; }
        public Rect Preview { get; }
        public List<GuideLine> Guides { get; }
        public double Gap { get; }
        public double Cost { get; }

        public SnapResult ToResult() => new()
        {
            Target = Target,
            TargetGroupId = Target.GroupId,
            Dx = dx0,
            Dy = dy0,
            PreviewBounds = Preview,
            Guides = Guides,
            Gap = Gap,
        };
    }
}
