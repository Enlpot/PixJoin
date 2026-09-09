using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using PixJoin.Core.Models;

namespace PixJoin.Core.Services;

/// <summary>
/// 标注统一渲染器：截图（CaptureOverlay）与贴图标注（StickerWindow）共用同一套绘制逻辑，
/// 保证两种入口下标注外观完全一致。
/// 坐标 = 物理像素 × 缩放系数（截图侧 sx=sy=dip 统一缩放；贴图侧 sx/sy 为窗口显示比例）。
/// 笔画 = thickness × (sx+sy)/2；闭合形状描边居中外扩 t/2，与最终固化渲染一致。
/// </summary>
public static class AnnotationPainter
{
    /// <summary>把一个标注画到画布上（与截图 / 贴图 / 最终渲染一致的矢量外观）。</summary>
    /// <param name="canvasSize">画布尺寸（DIP）。聚光灯需据此弱化画布内除中心区外的全部区域；其它工具可忽略。</param>
    /// <param name="sourceImage">源图像（标注坐标所在图像）。放大镜需据此截取并放大区域内容。</param>
    public static void Draw(Canvas canvas, Annotation a, double sx, double sy, Size? canvasSize = null, BitmapSource? sourceImage = null)
    {
        if (canvas is null || a is null) return;
        var brush = new SolidColorBrush(a.Color);
        brush.Freeze();
        double stroke = Math.Max(1, a.Thickness * (sx + sy) / 2);

        switch (a.Tool)
        {
            case AnnotationTool.Rect:
            case AnnotationTool.Mosaic:
            {
                double th = a.Thickness / 2;   // 描边居中外扩量（物理像素）
                if (a.CornerRadius > 0.5)
                {
                    var geo = RoundRectGeometry((a.X - th) * sx, (a.Y - th) * sy, (a.W + th * 2) * sx, (a.H + th * 2) * sy,
                        (a.CornerRadius + th) * ((sx + sy) / 2));
                    var p = new Path { Data = geo, Stroke = brush, StrokeThickness = stroke };
                    if (a.Dashed) p.StrokeDashArray = new DoubleCollection { 4, 3 };
                    if (a.Tool == AnnotationTool.Mosaic) p.Fill = new SolidColorBrush(Color.FromArgb(0x33, 0, 0, 0));
                    canvas.Children.Add(p);
                }
                else
                {
                    var r = new Rectangle
                    {
                        Width = Math.Max(1, (a.W + th * 2) * sx), Height = Math.Max(1, (a.H + th * 2) * sy),
                        Stroke = brush, StrokeThickness = stroke,
                        Fill = a.Tool == AnnotationTool.Mosaic ? new SolidColorBrush(Color.FromArgb(0x33, 0, 0, 0)) : null,
                    };
                    if (a.Dashed) r.StrokeDashArray = new DoubleCollection { 4, 3 };
                    Canvas.SetLeft(r, (a.X - th) * sx); Canvas.SetTop(r, (a.Y - th) * sy);
                    canvas.Children.Add(r);
                }
                break;
            }

            case AnnotationTool.Ellipse:
            {
                double th = a.Thickness / 2;
                var el = new Ellipse
                {
                    Width = Math.Max(1, (a.W + th * 2) * sx), Height = Math.Max(1, (a.H + th * 2) * sy),
                    Stroke = brush, StrokeThickness = stroke,
                };
                if (a.Dashed) el.StrokeDashArray = new DoubleCollection { 4, 3 };
                Canvas.SetLeft(el, (a.X - th) * sx); Canvas.SetTop(el, (a.Y - th) * sy);
                canvas.Children.Add(el);
                break;
            }

            case AnnotationTool.Highlight:
            {
                var r = new Rectangle { Width = a.W * sx, Height = a.H * sy, Fill = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xE2, 0x3C)) };
                Canvas.SetLeft(r, a.X * sx); Canvas.SetTop(r, a.Y * sy);
                canvas.Children.Add(r);
                break;
            }

            case AnnotationTool.Arrow:
                if (a.Points is { Length: >= 6 })
                    DrawArrow(canvas, a.Points[0] * sx, a.Points[1] * sy, a.Points[2] * sx, a.Points[3] * sy,
                        a.Points[4] * sx, a.Points[5] * sy, brush, stroke, a.Dashed, a.Arrow);
                break;

            case AnnotationTool.Pen:
                if (a.Points is { Length: >= 4 })
                {
                    var poly = new Polyline { Stroke = brush, StrokeThickness = stroke };
                    if (a.Dashed) poly.StrokeDashArray = new DoubleCollection { 4, 3 };
                    for (int i = 0; i + 1 < a.Points.Length; i += 2)
                        poly.Points.Add(new Point(a.Points[i] * sx, a.Points[i + 1] * sy));
                    canvas.Children.Add(poly);
                }
                break;

            case AnnotationTool.Text:
            {
                var tb = new TextBlock
                {
                    Text = a.Text ?? string.Empty,
                    Foreground = brush,
                    FontSize = Math.Max(14, a.Thickness * 4) * (sx + sy) / 2,
                    FontFamily = new FontFamily("Microsoft YaHei UI"),
                };
                Canvas.SetLeft(tb, a.X * sx); Canvas.SetTop(tb, a.Y * sy);
                canvas.Children.Add(tb);
                break;
            }

            case AnnotationTool.Magnifier:
            {
                double zoom = Math.Max(1.5, a.Zoom);
                double cx = (a.X + a.W / 2) * sx;
                double cy = (a.Y + a.H / 2) * sy;
                double dw = Math.Max(2, a.W * zoom * sx);    // 放大镜显示尺寸（DIP）
                double dh = Math.Max(2, a.H * zoom * sy);

                if (sourceImage is not null && a.W > 1 && a.H > 1)
                {
                    int pw = sourceImage.PixelWidth, ph = sourceImage.PixelHeight;
                    int sx0 = Math.Clamp((int)Math.Round(a.X), 0, pw - 1);
                    int sy0 = Math.Clamp((int)Math.Round(a.Y), 0, ph - 1);
                    int sw = Math.Clamp((int)Math.Round(a.W), 1, pw - sx0);
                    int sh = Math.Clamp((int)Math.Round(a.H), 1, ph - sy0);
                    try
                    {
                        var crop = new CroppedBitmap(sourceImage, new Int32Rect(sx0, sy0, sw, sh));
                        var scaled = new TransformedBitmap(crop, new ScaleTransform(zoom, zoom));
                        scaled.Freeze();
                        var img = new Image
                        {
                            Source = scaled,
                            Stretch = Stretch.Fill,
                            Width = dw,
                            Height = dh,
                            Clip = a.MagnifierRound
                                ? new EllipseGeometry(new Point(dw / 2, dh / 2), dw / 2, dh / 2)
                                : new RectangleGeometry(new Rect(0, 0, dw, dh)),
                        };
                        Canvas.SetLeft(img, cx - dw / 2);
                        Canvas.SetTop(img, cy - dh / 2);
                        canvas.Children.Add(img);
                    }
                    catch { /* 裁剪失败只画框 */ }
                }

                // 镜头描边（居中外扩）
                if (a.MagnifierRound)
                {
                    var el = new Ellipse { Width = dw, Height = dh, Stroke = brush, StrokeThickness = stroke };
                    if (a.Dashed) el.StrokeDashArray = new DoubleCollection { 4, 3 };
                    Canvas.SetLeft(el, cx - dw / 2); Canvas.SetTop(el, cy - dh / 2);
                    canvas.Children.Add(el);
                }
                else
                {
                    var r = new Rectangle { Width = dw, Height = dh, Stroke = brush, StrokeThickness = stroke };
                    if (a.Dashed) r.StrokeDashArray = new DoubleCollection { 4, 3 };
                    Canvas.SetLeft(r, cx - dw / 2); Canvas.SetTop(r, cy - dh / 2);
                    canvas.Children.Add(r);
                }
                break;
            }

            case AnnotationTool.Spotlight:
            {
                if (canvasSize is { } cs)
                {
                    double th = a.Thickness / 2;
                    var outer = new RectangleGeometry(new Rect(0, 0, cs.Width, cs.Height));
                    Geometry hole = a.SpotlightRound
                        ? new EllipseGeometry(new Point((a.X + a.W / 2) * sx, (a.Y + a.H / 2) * sy),
                            Math.Max(1, a.W / 2 * sx), Math.Max(1, a.H / 2 * sy))
                        : new RectangleGeometry(new Rect((a.X - th) * sx, (a.Y - th) * sy,
                            Math.Max(1, (a.W + th * 2) * sx), Math.Max(1, (a.H + th * 2) * sy)));
                    var geo = new CombinedGeometry(GeometryCombineMode.Exclude, outer, hole);
                    var dim = new SolidColorBrush(Color.FromArgb(0xB3, 0, 0, 0));
                    dim.Freeze();
                    canvas.Children.Add(new Path { Data = geo, Fill = dim });

                    // 中心区域描边（与其它闭合形状一致的画笔）
                    if (a.SpotlightRound)
                    {
                        var el = new Ellipse
                        {
                            Width = Math.Max(1, (a.W + th * 2) * sx), Height = Math.Max(1, (a.H + th * 2) * sy),
                            Stroke = brush, StrokeThickness = stroke,
                        };
                        Canvas.SetLeft(el, (a.X - th) * sx); Canvas.SetTop(el, (a.Y - th) * sy);
                        canvas.Children.Add(el);
                    }
                    else
                    {
                        var r = new Rectangle
                        {
                            Width = Math.Max(1, (a.W + th * 2) * sx), Height = Math.Max(1, (a.H + th * 2) * sy),
                            Stroke = brush, StrokeThickness = stroke,
                        };
                        Canvas.SetLeft(r, (a.X - th) * sx); Canvas.SetTop(r, (a.Y - th) * sy);
                        canvas.Children.Add(r);
                    }
                }
                break;
            }

            case AnnotationTool.Number:
            {
                var tb = new TextBlock
                {
                    Text = a.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Foreground = Brushes.White,
                    FontSize = Math.Max(14, a.Thickness * 3) * (sx + sy) / 2,
                    FontWeight = FontWeights.Bold,
                    FontFamily = new FontFamily("Microsoft YaHei UI"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                };
                double sz = Math.Max(20, a.Thickness * 4) * (sx + sy) / 2;
                var bg = new Border { Width = sz, Height = sz, Background = brush, CornerRadius = new CornerRadius(sz / 2), Child = tb };
                Canvas.SetLeft(bg, a.X * sx - sz / 2); Canvas.SetTop(bg, a.Y * sy - sz / 2);
                canvas.Children.Add(bg);
                break;
            }
        }
    }

    /// <summary>
    /// 一体式箭头：杆为二次贝塞尔曲线（终点=头部根部，实心头一体不穿头），
    /// 支持实心 / V 形 / 双头三种样式与虚线杆。坐标为已缩放（DIP）值。
    /// </summary>
    public static void DrawArrow(Canvas canvas, double x0, double y0, double cx, double cy, double x2, double y2,
        Brush brush, double stroke, bool dashed = false, ArrowStyle style = ArrowStyle.Solid)
    {
        var color = ((SolidColorBrush)brush).Color;

        // 终点切线方向 = P2 - C
        double dx = x2 - cx, dy = y2 - cy;
        double len = Math.Sqrt(dx * dx + dy * dy);
        double ux, uy, nx, ny;
        if (len < 1e-6) { ux = 0; uy = 0; nx = 1; ny = 0; }
        else { ux = dx / len; uy = dy / len; nx = -uy; ny = ux; }

        double h = Math.Max(8, stroke * 4);
        double hw = h * 0.42;
        double hrx = x2 - ux * h, hry = y2 - uy * h;

        // 杆：二次贝塞尔（终点 = 头部根部）
        var shaft = new StreamGeometry();
        using (var g = shaft.Open())
        {
            g.BeginFigure(new Point(x0, y0), false, false);
            g.QuadraticBezierTo(new Point(cx, cy), new Point(hrx, hry), true, false);
        }
        shaft.Freeze();
        var sp = new Path { Data = shaft, Stroke = brush, StrokeThickness = stroke };
        if (dashed) sp.StrokeDashArray = new DoubleCollection { 4, 3 };
        canvas.Children.Add(sp);

        switch (style)
        {
            case ArrowStyle.Solid:
            {
                var head = new StreamGeometry();
                using (var g = head.Open())
                {
                    g.BeginFigure(new Point(hrx + nx * hw, hry + ny * hw), true, true);
                    g.LineTo(new Point(x2, y2), true, false);
                    g.LineTo(new Point(hrx - nx * hw, hry - ny * hw), true, false);
                }
                head.Freeze();
                canvas.Children.Add(new Path { Data = head, Fill = brush });
                break;
            }
            case ArrowStyle.Line:
            {
                double hl = Math.Max(8, stroke * 3.5);
                double hlw = hl * 0.38;
                double lhrx = x2 - ux * hl, lhry = y2 - uy * hl;
                double lw = Math.Max(1.5, stroke * 0.8);
                var lp = new SolidColorBrush(color);
                canvas.Children.Add(new Line { X1 = x2, Y1 = y2, X2 = lhrx + nx * hlw, Y2 = lhry + ny * hlw, Stroke = lp, StrokeThickness = lw });
                canvas.Children.Add(new Line { X1 = x2, Y1 = y2, X2 = lhrx - nx * hlw, Y2 = lhry - ny * hlw, Stroke = lp, StrokeThickness = lw });
                break;
            }
            case ArrowStyle.Double:
            {
                double h2 = h * 0.6, hw2 = hw * 0.6;
                double dx0 = x0 - cx, dy0 = y0 - cy;
                double l0 = Math.Sqrt(dx0 * dx0 + dy0 * dy0);
                double u0x = l0 > 1e-6 ? dx0 / l0 : 0, u0y = l0 > 1e-6 ? dy0 / l0 : 0;
                double n0x = -u0y, n0y = u0x;
                var head = new StreamGeometry();
                using (var g = head.Open())
                {
                    g.BeginFigure(new Point(hrx + nx * hw, hry + ny * hw), true, true);
                    g.LineTo(new Point(x2, y2), true, false);
                    g.LineTo(new Point(hrx - nx * hw, hry - ny * hw), true, false);
                    g.BeginFigure(new Point(x0 - u0x * h2 + n0x * hw2, y0 - u0y * h2 + n0y * hw2), true, true);
                    g.LineTo(new Point(x0, y0), true, false);
                    g.LineTo(new Point(x0 - u0x * h2 - n0x * hw2, y0 - u0y * h2 - n0y * hw2), true, false);
                }
                head.Freeze();
                canvas.Children.Add(new Path { Data = head, Fill = brush });
                break;
            }
        }
    }

    /// <summary>
    /// 画选中态：选中框（文字/高亮/马赛克画虚线蓝框；箭头/椭圆/矩形/画笔只画控制点）+ 控制点
    /// （角/边/箭头=白方块，圆角=白圆点，青色描边）。与截图侧选中渲染完全一致。
    /// 坐标为物理像素 × sx/sy；控制点尺寸固定（不随缩放放大）。
    /// </summary>
    public static void DrawHandles(Canvas canvas, Annotation a, double sx, double sy, double arrowT = 0.5)
    {
        if (canvas is null || a is null) return;

        var b = AnnotationHandles.Bounds(a);
        // 箭头 / 椭圆 / 矩形 / 画笔：只显示控制点，不画矩形外框
        bool noFrame = a.Tool is AnnotationTool.Arrow or AnnotationTool.Ellipse or AnnotationTool.Rect or AnnotationTool.Pen;
        if (!noFrame)
        {
            var selRect = new Rectangle
            {
                Width = Math.Max(1, b.Width * sx),
                Height = Math.Max(1, b.Height * sy),
                Stroke = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)),
                StrokeThickness = 2.5,
                StrokeDashArray = new DoubleCollection { 5, 3 },
                Fill = Brushes.Transparent,
            };
            Canvas.SetLeft(selRect, b.X * sx);
            Canvas.SetTop(selRect, b.Y * sy);
            canvas.Children.Add(selRect);
        }

        // 控制点：角/边/箭头=白方块，圆角=白圆点
        var outline = new SolidColorBrush(Color.FromRgb(0x00, 0xE5, 0xC0));
        foreach (var h in AnnotationHandles.Get(a, arrowT))
        {
            double px = h.Pos.X * sx, py = h.Pos.Y * sy;
            if (h.Kind == HandleKind.Round)
            {
                var dot = new Ellipse
                {
                    Width = 9, Height = 9,
                    Fill = Brushes.White,
                    Stroke = outline,
                    StrokeThickness = 1.5,
                };
                Canvas.SetLeft(dot, px - 4.5);
                Canvas.SetTop(dot, py - 4.5);
                canvas.Children.Add(dot);
            }
            else
            {
                var hb = new Rectangle
                {
                    Width = 7, Height = 7,
                    Fill = Brushes.White,
                    Stroke = outline,
                    StrokeThickness = 1.5,
                };
                Canvas.SetLeft(hb, px - 3.5);
                Canvas.SetTop(hb, py - 3.5);
                canvas.Children.Add(hb);
            }
        }
    }

    /// <summary>圆角矩形路径（坐标为已缩放 DIP 值；r≤0 时退化为普通矩形）。</summary>
    public static StreamGeometry RoundRectGeometry(double x, double y, double w, double h, double r)
    {
        r = Math.Clamp(r, 0, Math.Min(w, h) / 2);
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            if (r < 0.5)
            {
                g.BeginFigure(new Point(x, y), true, true);
                g.LineTo(new Point(x + w, y), true, false);
                g.LineTo(new Point(x + w, y + h), true, false);
                g.LineTo(new Point(x, y + h), true, false);
            }
            else
            {
                g.BeginFigure(new Point(x + r, y), true, true);
                g.LineTo(new Point(x + w - r, y), true, false);
                g.ArcTo(new Point(x + w, y + r), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
                g.LineTo(new Point(x + w, y + h - r), true, false);
                g.ArcTo(new Point(x + w - r, y + h), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
                g.LineTo(new Point(x + r, y + h), true, false);
                g.ArcTo(new Point(x, y + h - r), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
                g.LineTo(new Point(x, y + r), true, false);
                g.ArcTo(new Point(x + r, y), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
            }
        }
        geo.Freeze();
        return geo;
    }
}
