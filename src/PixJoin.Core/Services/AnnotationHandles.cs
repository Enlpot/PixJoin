using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using PixJoin.Core.Models;

namespace PixJoin.Core.Services;

/// <summary>控制点种类：角点=对角缩放、边点=单边拉伸、内角点=圆角、箭头点=端点/弯曲、Move=整体移动锚。</summary>
public enum HandleKind { Corner, Edge, Round, ArrowPt, Move }

/// <summary>一个控制点：种类 + 物理像素位置 + 索引语义（Corner 0左上1右上2左下3右下；Edge 0上1右2下3左；Round 0-3 对应四内角；ArrowPt 0=起点 1=弧顶 2=终点）。</summary>
public readonly record struct AnnotHandle(HandleKind Kind, Point Pos, int Index);

/// <summary>
/// 标注编辑的几何与命中（截图 CaptureOverlay 与贴图 StickerWindow 共用，保证控制点完全一致）：
/// 包围盒 / 控制点生成 / 命中判定 / 拖动变形公式（锚定拉伸、圆角、箭头两点反解）。
/// 所有坐标为物理像素。
/// </summary>
public static class AnnotationHandles
{
    /// <summary>标注包围盒（箭头=三点外接矩形；文字/序号=文本范围）。</summary>
    public static Rect Bounds(Annotation a)
    {
        switch (a.Tool)
        {
            case AnnotationTool.Rect:
            case AnnotationTool.Ellipse:
            case AnnotationTool.Highlight:
            case AnnotationTool.Mosaic:
                return new Rect(a.X, a.Y, a.W, a.H);
            case AnnotationTool.Arrow:
                if (a.Points is { Length: >= 6 })
                {
                    double minX = Math.Min(a.Points[0], Math.Min(a.Points[2], a.Points[4]));
                    double minY = Math.Min(a.Points[1], Math.Min(a.Points[3], a.Points[5]));
                    double maxX = Math.Max(a.Points[0], Math.Max(a.Points[2], a.Points[4]));
                    double maxY = Math.Max(a.Points[1], Math.Max(a.Points[3], a.Points[5]));
                    return new Rect(minX, minY, maxX - minX, maxY - minY);
                }
                return new Rect(a.X, a.Y, 0, 0);
            case AnnotationTool.Pen:
                if (a.Points is { Length: >= 4 })
                {
                    double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
                    for (int i = 0; i + 1 < a.Points.Length; i += 2)
                    {
                        minX = Math.Min(minX, a.Points[i]); minY = Math.Min(minY, a.Points[i + 1]);
                        maxX = Math.Max(maxX, a.Points[i]); maxY = Math.Max(maxY, a.Points[i + 1]);
                    }
                    return new Rect(minX, minY, maxX - minX, maxY - minY);
                }
                return new Rect(a.X, a.Y, 0, 0);
            case AnnotationTool.Text:
            case AnnotationTool.Number:
                // 文字/序号以落点为中心的固定范围（与截图侧历史一致）
                double sz = Math.Max(24, a.Thickness * 4);
                return new Rect(a.X - sz / 2, a.Y - sz / 2, sz * 2, sz);
            default:
                return new Rect(a.X, a.Y, a.W, a.H);
        }
    }

    /// <summary>
    /// 生成选中标注的控制点（物理像素）：
    /// 矩形/高亮/马赛克 = 12 点（4 角 + 4 边 + 4 内角圆角）；椭圆 = 8 点；箭头 = 3 点（尾/弯曲/头）；
    /// 文字/序号 = 本体移动锚 + 4 角缩放；画笔 = 本体移动锚。
    /// arrowT：箭头弧顶在曲线上的锁定参数（非拖动 0.5；拖动中由反解更新）。
    /// </summary>
    public static List<AnnotHandle> Get(Annotation a, double arrowT = 0.5)
    {
        var list = new List<AnnotHandle>();
        switch (a.Tool)
        {
            case AnnotationTool.Rect:
            case AnnotationTool.Highlight:
            case AnnotationTool.Mosaic:
            {
                double x = a.X, y = a.Y, w = a.W, h = a.H;
                list.Add(new AnnotHandle(HandleKind.Corner, new Point(x, y), 0));
                list.Add(new AnnotHandle(HandleKind.Corner, new Point(x + w, y), 1));
                list.Add(new AnnotHandle(HandleKind.Corner, new Point(x, y + h), 2));
                list.Add(new AnnotHandle(HandleKind.Corner, new Point(x + w, y + h), 3));
                list.Add(new AnnotHandle(HandleKind.Edge, new Point(x + w / 2, y), 0));
                list.Add(new AnnotHandle(HandleKind.Edge, new Point(x + w, y + h / 2), 1));
                list.Add(new AnnotHandle(HandleKind.Edge, new Point(x + w / 2, y + h), 2));
                list.Add(new AnnotHandle(HandleKind.Edge, new Point(x, y + h / 2), 3));
                double rd = Math.Min(Math.Max(8, a.CornerRadius * 0.5 + 6), Math.Min(w, h) * 0.4);
                list.Add(new AnnotHandle(HandleKind.Round, new Point(x + rd, y + rd), 0));
                list.Add(new AnnotHandle(HandleKind.Round, new Point(x + w - rd, y + rd), 1));
                list.Add(new AnnotHandle(HandleKind.Round, new Point(x + rd, y + h - rd), 2));
                list.Add(new AnnotHandle(HandleKind.Round, new Point(x + w - rd, y + h - rd), 3));
                break;
            }
            case AnnotationTool.Ellipse:
            case AnnotationTool.Spotlight:
            case AnnotationTool.Magnifier:
            {
                double x = a.X, y = a.Y, w = a.W, h = a.H;
                list.Add(new AnnotHandle(HandleKind.Corner, new Point(x, y), 0));
                list.Add(new AnnotHandle(HandleKind.Corner, new Point(x + w, y), 1));
                list.Add(new AnnotHandle(HandleKind.Corner, new Point(x, y + h), 2));
                list.Add(new AnnotHandle(HandleKind.Corner, new Point(x + w, y + h), 3));
                list.Add(new AnnotHandle(HandleKind.Edge, new Point(x + w / 2, y), 0));
                list.Add(new AnnotHandle(HandleKind.Edge, new Point(x + w, y + h / 2), 1));
                list.Add(new AnnotHandle(HandleKind.Edge, new Point(x + w / 2, y + h), 2));
                list.Add(new AnnotHandle(HandleKind.Edge, new Point(x, y + h / 2), 3));
                break;
            }
            case AnnotationTool.Arrow:
                if (a.Points is { Length: >= 6 })
                {
                    // 可见曲线终点 = 头部根部（大头部会截断杆的末端，若不折算，顶点会落在被头部遮住的曲线段上）
                    double hsh = Math.Max(8, Math.Max(1, a.Thickness) * 4);
                    double dxh = a.Points[4] - a.Points[2], dyh = a.Points[5] - a.Points[3];
                    double dlh = Math.Sqrt(dxh * dxh + dyh * dyh);
                    double hrxx = dlh > 1e-6 ? a.Points[4] - dxh / dlh * hsh : a.Points[4];
                    double hryy = dlh > 1e-6 ? a.Points[5] - dyh / dlh * hsh : a.Points[5];
                    // 弧顶控制点 = 曲线上锁定参数 arrowT 处的点（0.5=视觉中心；拖动中反解保证 B(t)=鼠标）
                    double bx = ApexX(arrowT, a.Points[0], a.Points[2], hrxx);
                    double by = ApexY(arrowT, a.Points[1], a.Points[3], hryy);
                    list.Add(new AnnotHandle(HandleKind.ArrowPt, new Point(a.Points[0], a.Points[1]), 0));
                    list.Add(new AnnotHandle(HandleKind.ArrowPt, new Point(bx, by), 1));
                    list.Add(new AnnotHandle(HandleKind.ArrowPt, new Point(a.Points[4], a.Points[5]), 2));
                }
                break;
            case AnnotationTool.Text:
            case AnnotationTool.Number:
            {
                var b = Bounds(a);
                list.Add(new AnnotHandle(HandleKind.Move, new Point(b.X, b.Y), 0));
                list.Add(new AnnotHandle(HandleKind.Corner, new Point(b.X, b.Y), 0));
                list.Add(new AnnotHandle(HandleKind.Corner, new Point(b.X + b.Width, b.Y), 1));
                list.Add(new AnnotHandle(HandleKind.Corner, new Point(b.X, b.Y + b.Height), 2));
                list.Add(new AnnotHandle(HandleKind.Corner, new Point(b.X + b.Width, b.Y + b.Height), 3));
                break;
            }
            case AnnotationTool.Pen:
                list.Add(new AnnotHandle(HandleKind.Move, Bounds(a).TopLeft, 0));
                break;
        }
        return list;
    }

    /// <summary>控制点命中：弧顶控制点（曲线正中部）命中区 4px（只有精确按中小方块才变形，按中部=整体移动），其余 8px。</summary>
    public static AnnotHandle? HitTest(List<AnnotHandle> handles, Point rel, double tol = 8)
    {
        AnnotHandle? best = null;
        double bestD = tol;
        foreach (var h in handles)
        {
            double htol = (h.Kind == HandleKind.ArrowPt && h.Index == 1) ? 4 : tol;
            double dx = h.Pos.X - rel.X, dy = h.Pos.Y - rel.Y;
            double d = Math.Sqrt(dx * dx + dy * dy);
            if (d <= htol && d <= bestD) { bestD = d; best = h; }
        }
        return best;
    }

    /// <summary>精确命中标注本体：闭合形状=包围盒；箭头=到可见曲线/头部距离；画笔=到折线距离（与截图侧一致）。</summary>
    public static bool HitShape(Annotation a, Point rel, double tol)
    {
        switch (a.Tool)
        {
            case AnnotationTool.Arrow:
                if (a.Points is { Length: >= 6 })
                {
                    double hit = Math.Max(tol, Math.Max(6, a.Thickness * 1.5));
                    double x0 = a.Points[0], y0 = a.Points[1], cx = a.Points[2], cy = a.Points[3], x2 = a.Points[4], y2 = a.Points[5];
                    double hsh = Math.Max(8, Math.Max(1, a.Thickness) * 4);
                    double dxh = x2 - cx, dyh = y2 - cy, dlh = Math.Sqrt(dxh * dxh + dyh * dyh);
                    double hrx = dlh > 1e-6 ? x2 - dxh / dlh * hsh : x2;
                    double hry = dlh > 1e-6 ? y2 - dyh / dlh * hsh : y2;
                    double px = x0, py = y0;
                    for (int i = 1; i <= 64; i++)
                    {
                        double t = i / 64.0, u = 1 - t;
                        double qx = u * u * x0 + 2 * t * u * cx + t * t * hrx;
                        double qy = u * u * y0 + 2 * t * u * cy + t * t * hry;
                        if (PointSegDist(rel.X, rel.Y, px, py, qx, qy) <= hit) return true;
                        px = qx; py = qy;
                    }
                    return Math.Sqrt((rel.X - x2) * (rel.X - x2) + (rel.Y - y2) * (rel.Y - y2)) <= hsh + hit;
                }
                return false;
            case AnnotationTool.Pen:
                if (a.Points is { Length: >= 4 })
                {
                    double hit = Math.Max(tol, Math.Max(6, a.Thickness * 1.5));
                    for (int i = 2; i + 1 < a.Points.Length; i += 2)
                        if (PointSegDist(rel.X, rel.Y, a.Points[i - 2], a.Points[i - 1], a.Points[i], a.Points[i + 1]) <= hit) return true;
                    return false;
                }
                return false;
            default:
                var b = Bounds(a);
                b.Inflate(tol, tol);
                return b.Contains(rel);
        }
    }

    /// <summary>拖动控制点变形：角=锚定对角、边=锚定对边、圆角=按投影调半径、箭头=端点直设/弧顶两步反解、Move=整体移动。</summary>
    public static Annotation? ApplyDrag(Annotation a, AnnotHandle h, Point rel, double arrowT = 0.5)
    {
        switch (h.Kind)
        {
            case HandleKind.Corner:
            {
                // 文字 / 序号：角点缩放 = 以中心为基准按包围盒比例调整字号（粗细）
                if (a.Tool is AnnotationTool.Text or AnnotationTool.Number)
                {
                    var bb = Bounds(a);
                    double bxc = bb.Left + bb.Width / 2, byc = bb.Top + bb.Height / 2;
                    double nw = Math.Max(4, Math.Abs(rel.X - bxc) * 2);
                    double nh = Math.Max(4, Math.Abs(rel.Y - byc) * 2);
                    double s = Math.Max(nw / Math.Max(1, bb.Width), nh / Math.Max(1, bb.Height));
                    return Make(a, thickness: Math.Clamp(a.Thickness * s, 2, 40));
                }

                // 锚定对角的标准拉伸：拖 A 角 → 对角固定（防翻转，最小尺寸 6）
                double x = a.X, y = a.Y, w = a.W, hh = a.H;
                double right = x + w, bottom = y + hh;
                switch (h.Index)
                {
                    case 0: x = Math.Min(rel.X, right - 6); y = Math.Min(rel.Y, bottom - 6); w = right - x; hh = bottom - y; break;
                    case 1: y = Math.Min(rel.Y, bottom - 6); w = Math.Max(rel.X - x, 6); hh = bottom - y; break;
                    case 2: x = Math.Min(rel.X, right - 6); w = right - x; hh = Math.Max(rel.Y - y, 6); break;
                    default: w = Math.Max(rel.X - x, 6); hh = Math.Max(rel.Y - y, 6); break;
                }
                return Make(a, x: x, y: y, w: w, h: hh);
            }
            case HandleKind.Edge:
            {
                // 锚定对边：拖上边 → 下边固定（防翻转，最小尺寸 6）
                double x = a.X, y = a.Y, w = a.W, hh = a.H;
                double right = x + w, bottom = y + hh;
                switch (h.Index)
                {
                    case 0: y = Math.Min(rel.Y, bottom - 6); hh = bottom - y; break;
                    case 1: w = Math.Max(rel.X - x, 6); break;
                    case 2: hh = Math.Max(rel.Y - y, 6); break;
                    default: x = Math.Min(rel.X, right - 6); w = right - x; break;
                }
                return Make(a, x: x, y: y, w: w, h: hh);
            }
            case HandleKind.Round:
            {
                double w = a.W, hh = a.H;
                double maxR = Math.Min(w, hh) / 2;
                double cx, cy;
                switch (h.Index)
                {
                    case 0: cx = a.X; cy = a.Y; break;
                    case 1: cx = a.X + w; cy = a.Y; break;
                    case 2: cx = a.X; cy = a.Y + hh; break;
                    default: cx = a.X + w; cy = a.Y + hh; break;
                }
                double dcx = (a.X + w / 2) - cx, dcy = (a.Y + hh / 2) - cy;
                double dl = Math.Sqrt(dcx * dcx + dcy * dcy) + 1e-9;
                double t = ((rel.X - cx) * dcx + (rel.Y - cy) * dcy) / dl;
                return Make(a, corner: Math.Clamp(t * 0.8, 0, maxR));
            }
            case HandleKind.ArrowPt:
                if (a.Points is not { Length: >= 6 }) return null;
                {
                    var pts = (double[])a.Points.Clone();
                    if (h.Index == 1)
                    {
                        // 拖动弧顶 → 用锁定参数 t 反解控制点 C = (Q - (1-t)²P0 - t²P2) / (2t(1-t))，保证 B(t)=鼠标
                        double hsh = Math.Max(8, Math.Max(1, a.Thickness) * 4);
                        double dxh = a.Points[4] - a.Points[2], dyh = a.Points[5] - a.Points[3];
                        double dlh = Math.Sqrt(dxh * dxh + dyh * dyh);
                        double hrxx = dlh > 1e-6 ? a.Points[4] - dxh / dlh * hsh : a.Points[4];
                        double hryy = dlh > 1e-6 ? a.Points[5] - dyh / dlh * hsh : a.Points[5];
                        double t = arrowT;
                        double k = 2 * t * (1 - t);
                        if (Math.Abs(k) < 1e-6) { pts[2] = 2 * rel.X - (pts[0] + pts[4]) / 2; pts[3] = 2 * rel.Y - (pts[1] + pts[5]) / 2; }
                        else
                        {
                            double u = 1 - t;
                            // 两步反解：先用尖点 P2 粗解出 C → 由其头部根部 hrx 精化一次 → B(t)=鼠标（<1px）
                            double cx1 = (rel.X - u * u * pts[0] - t * t * pts[4]) / k;
                            double cy1 = (rel.Y - u * u * pts[1] - t * t * pts[5]) / k;
                            double dxh2 = pts[4] - cx1, dyh2 = pts[5] - cy1;
                            double dlh2 = Math.Sqrt(dxh2 * dxh2 + dyh2 * dyh2);
                            double hrx2 = dlh2 > 1e-6 ? pts[4] - dxh2 / dlh2 * hsh : pts[4];
                            double hry2 = dlh2 > 1e-6 ? pts[5] - dyh2 / dlh2 * hsh : pts[5];
                            pts[2] = (rel.X - u * u * pts[0] - t * t * hrx2) / k;
                            pts[3] = (rel.Y - u * u * pts[1] - t * t * hry2) / k;
                        }
                    }
                    else
                    {
                        pts[h.Index * 2] = rel.X;
                        pts[h.Index * 2 + 1] = rel.Y;
                    }
                    return Make(a, points: pts);
                }
            case HandleKind.Move:
            {
                var delta = new Point(rel.X - h.Pos.X, rel.Y - h.Pos.Y);
                return Move(a, delta);
            }
        }
        return null;
    }

    /// <summary>整体移动标注（箭头/画笔 Points 与锚点 X/Y 同步平移，保证 delta 为帧增量 1:1 跟手）。</summary>
    public static Annotation Move(Annotation a, Point delta)
    {
        switch (a.Tool)
        {
            case AnnotationTool.Arrow:
                if (a.Points is { Length: >= 6 })
                    return Make(a, x: a.X + delta.X, y: a.Y + delta.Y, points: new[]
                    {
                        a.Points[0] + delta.X, a.Points[1] + delta.Y,
                        a.Points[2] + delta.X, a.Points[3] + delta.Y,
                        a.Points[4] + delta.X, a.Points[5] + delta.Y,
                    });
                return Make(a, x: a.X + delta.X, y: a.Y + delta.Y);
            case AnnotationTool.Pen:
                if (a.Points is { Length: >= 2 })
                {
                    var pts = new double[a.Points.Length];
                    for (int i = 0; i < a.Points.Length; i += 2)
                    {
                        pts[i] = a.Points[i] + delta.X;
                        pts[i + 1] = a.Points[i + 1] + delta.Y;
                    }
                    return Make(a, x: a.X + delta.X, y: a.Y + delta.Y, points: pts);
                }
                return Make(a, x: a.X + delta.X, y: a.Y + delta.Y);
            default:
                return Make(a, x: a.X + delta.X, y: a.Y + delta.Y);
        }
    }

    /// <summary>按需重建标注（改颜色/粗细/虚线/箭头样式，其余字段保留）。</summary>
    public static Annotation Rebuild(Annotation a, Color? color = null, double? thickness = null, bool? dashed = null, ArrowStyle? arrow = null)
        => Make(a,
            color: color ?? a.Color,
            thickness: thickness ?? a.Thickness,
            dashed: dashed ?? a.Dashed,
            arrow: arrow ?? a.Arrow);

    private static double PointSegDist(double px, double py, double ax, double ay, double bx, double by)
    {
        double dx = bx - ax, dy = by - ay;
        double l2 = dx * dx + dy * dy;
        double t = l2 > 1e-9 ? Math.Clamp(((px - ax) * dx + (py - ay) * dy) / l2, 0, 1) : 0;
        double qx = ax + t * dx, qy = ay + t * dy;
        return Math.Sqrt((px - qx) * (px - qx) + (py - qy) * (py - qy));
    }

    private static double ApexX(double t, double x0, double cx, double x2)
    {
        double u = 1 - t;
        return u * u * x0 + 2 * t * u * cx + t * t * x2;
    }

    private static double ApexY(double t, double y0, double cy, double y2)
    {
        double u = 1 - t;
        return u * u * y0 + 2 * t * u * cy + t * t * y2;
    }

    private static Annotation Make(Annotation a, double? x = null, double? y = null, double? w = null, double? h = null,
        double[]? points = null, double? corner = null, double? thickness = null, Color? color = null, bool? dashed = null, ArrowStyle? arrow = null)
        => new()
        {
            Tool = a.Tool,
            X = x ?? a.X, Y = y ?? a.Y, W = w ?? a.W, H = h ?? a.H,
            Points = points ?? a.Points, Text = a.Text, Number = a.Number,
            Color = color ?? a.Color, Thickness = thickness ?? a.Thickness,
            Dashed = dashed ?? a.Dashed, Arrow = arrow ?? a.Arrow, CornerRadius = corner ?? a.CornerRadius,
        };
}
