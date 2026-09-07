using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PixJoin.Core.Models;

namespace PixJoin.Core.Services;

/// <summary>
/// 鎶婃爣娉ㄧ煝閲忓垪琛ㄦ覆鏌撹繘璐村浘锛氶┈璧涘厠鍏堝鍘熷浘鍍忕礌鍖栵紝鍏朵綑鏍囨敞鐢?DrawingVisual 鍙犲姞锛?/// 杈撳嚭涓庡師鍥惧悓灏哄銆佸悓 DPI 鐨勬柊 BitmapSource锛堜笉闄嶄綆娓呮櫚搴︼級銆?/// 鍧愭爣鍏ㄩ儴涓哄浘鐗囩墿鐞嗗儚绱犮€?/// </summary>
public static class AnnotationRenderer
{
    public static BitmapSource Render(BitmapSource image, IReadOnlyList<Annotation> annotations)
    {
        if (annotations is null || annotations.Count == 0) return image;

        int w = image.PixelWidth, h = image.PixelHeight;

        // 1) 搴曞浘 = 鍘熷浘 + 椹禌鍏嬪尯鍩熷儚绱犲寲
        BitmapSource baseImage = image;
        foreach (var a in annotations)
        {
            if (a.Tool != AnnotationTool.Mosaic) continue;
            baseImage = ImageProcessor.Pixelate(baseImage, new Rect(a.X, a.Y, a.W, a.H), Math.Max(6, (int)Math.Round(a.Thickness)));
        }

        // 2) 鐭㈤噺鍙犲姞
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(baseImage, new Rect(0, 0, w, h));

            foreach (var a in annotations)
            {
                if (a.Tool == AnnotationTool.Mosaic) continue;
                var pen = new Pen(new SolidColorBrush(a.Color), a.Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
                if (a.Dashed) pen.DashStyle = DashStyles.Dash;

                switch (a.Tool)
                {
                    case AnnotationTool.Rect:
                        if (a.CornerRadius > 0.5)
                            dc.DrawGeometry(null, pen, RoundRectGeometry(a.X, a.Y, a.W, a.H, a.CornerRadius));
                        else
                            dc.DrawRectangle(null, pen, new Rect(a.X, a.Y, a.W, a.H));
                        break;

                    case AnnotationTool.Ellipse:
                        dc.DrawEllipse(null, pen, new Point(a.X + a.W / 2, a.Y + a.H / 2), a.W / 2, a.H / 2);
                        break;

                    case AnnotationTool.Highlight:
                        var hb = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xE2, 0x3C));
                        hb.Freeze();
                        dc.DrawRectangle(hb, null, new Rect(a.X, a.Y, a.W, a.H));
                        break;

                    case AnnotationTool.Arrow:
                        if (a.Points is { Length: >= 6 })
                            DrawCurvedArrow(dc, a.Points[0], a.Points[1], a.Points[2], a.Points[3], a.Points[4], a.Points[5], a.Color, a.Thickness, a.Dashed, a.Arrow);
                        break;

                    case AnnotationTool.Pen:
                        if (a.Points is { Length: >= 4 })
                        {
                            var geo = new StreamGeometry();
                            using (var g = geo.Open())
                            {
                                g.BeginFigure(new Point(a.Points[0], a.Points[1]), false, false);
                                for (int i = 2; i < a.Points.Length; i += 2)
                                    g.LineTo(new Point(a.Points[i], a.Points[i + 1]), true, false);
                            }
                            geo.Freeze();
                            dc.DrawGeometry(null, pen, geo);
                        }
                        break;

                    case AnnotationTool.Text:
                        if (!string.IsNullOrEmpty(a.Text))
                            dc.DrawText(MakeText(a.Text, a.Color, Math.Max(12, a.Thickness * 4)), new Point(a.X, a.Y));
                        break;

                    case AnnotationTool.Number:
                        double r = Math.Max(10, a.Thickness * 2.5);
                        var fill = new SolidColorBrush(a.Color);
                        fill.Freeze();
                        var numPen = new Pen(Brushes.White, Math.Max(1.5, a.Thickness * 0.25));
                        dc.DrawEllipse(fill, numPen, new Point(a.X, a.Y), r, r);
                        var numText = MakeText(a.Number.ToString(CultureInfo.InvariantCulture), Colors.White, r * 1.3);
                        dc.DrawText(numText, new Point(a.X - numText.Width / 2, a.Y - numText.Height / 2));
                        break;
                }
            }
        }

        var rtb = new RenderTargetBitmap(w, h, image.DpiX, image.DpiY, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    /// <summary>
    /// 涓€浣撳紡绠ご锛氭潌涓庣澶村ご杩炴垚鍗曚竴灏侀棴杞粨锛團ill 濉厖锛夛紝閬垮厤"绾跨┛杩囩澶村ご"鐨勪笉缇庤锛?    /// 澶撮暱 h=max(10, thickness*4)锛屽崐澶村 hw=h*0.42锛堣緝灏栵級锛屾潌瀹?thickness銆?    /// </summary>
    private static void DrawArrow(DrawingContext dc, double x1, double y1, double x2, double y2, Color color, double thickness, bool dashed = false, ArrowStyle style = ArrowStyle.Solid)
    {
        double dx = x2 - x1, dy = y2 - y1;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-6) return;
        double ux = dx / len, uy = dy / len;      // 鍗曚綅鏂瑰悜
        double nx = -uy, ny = ux;                 // 鍗曚綅鍨傜洿

        var fill = new SolidColorBrush(color);
        fill.Freeze();

        switch (style)
        {
            case ArrowStyle.Solid:
            {
                double h = Math.Max(10, thickness * 4);
                double hw = h * 0.42;
                double w2 = thickness / 2;
                double hrx = x2 - ux * h, hry = y2 - uy * h;   // 澶存牴

                var geo = new StreamGeometry();
                using (var g = geo.Open())
                {
                    g.BeginFigure(new Point(x1 + nx * w2, y1 + ny * w2), true, true);
                    g.LineTo(new Point(hrx + nx * w2, hry + ny * w2), true, false);
                    g.LineTo(new Point(hrx + nx * hw, hry + ny * hw), true, false);
                    g.LineTo(new Point(x2, y2), true, false);
                    g.LineTo(new Point(hrx - nx * hw, hry - ny * hw), true, false);
                    g.LineTo(new Point(hrx - nx * w2, hry - ny * w2), true, false);
                }
                geo.Freeze();
                dc.DrawGeometry(fill, null, geo);

                if (dashed) DrawDashShaft(dc, fill, x1, y1, hrx, hry, thickness);
                break;
            }

            case ArrowStyle.Line:   // V 褰細鏉?+ 涓ゆ潯缁嗙嚎缁勬垚鐨勫ご
            {
                double h = Math.Max(10, thickness * 3.5);
                double hw = h * 0.38;
                var lp = new Pen(fill, Math.Max(1.5, thickness * 0.8))
                {
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round,
                };
                if (dashed) lp.DashStyle = DashStyles.Dash;
                double hrx = x2 - ux * h, hry = y2 - uy * h;
                dc.DrawLine(lp, new Point(x1, y1), new Point(x2, y2));
                dc.DrawLine(lp, new Point(x2, y2), new Point(hrx + nx * hw, hry + ny * hw));
                dc.DrawLine(lp, new Point(x2, y2), new Point(hrx - nx * hw, hry - ny * hw));
                break;
            }

            case ArrowStyle.Double:  // 鍙屽ご锛氫富澶村悓瀹炲績 + 璧风偣鍙嶅悜灏忓ご
            {
                double h = Math.Max(10, thickness * 4);
                double hw = h * 0.42;
                double w2 = thickness / 2;
                double hrx = x2 - ux * h, hry = y2 - uy * h;

                var geo = new StreamGeometry();
                using (var g = geo.Open())
                {
                    g.BeginFigure(new Point(x1 + nx * w2, y1 + ny * w2), true, true);
                    g.LineTo(new Point(hrx + nx * w2, hry + ny * w2), true, false);
                    g.LineTo(new Point(hrx + nx * hw, hry + ny * hw), true, false);
                    g.LineTo(new Point(x2, y2), true, false);
                    g.LineTo(new Point(hrx - nx * hw, hry - ny * hw), true, false);
                    g.LineTo(new Point(hrx - nx * w2, hry - ny * w2), true, false);

                    double h2 = h * 0.6, hw2 = hw * 0.6;   // 鍙嶅悜灏忓ご
                    g.BeginFigure(new Point(x1 - ux * h2 + nx * hw2, y1 - uy * h2 + ny * hw2), true, true);
                    g.LineTo(new Point(x1, y1), true, false);
                    g.LineTo(new Point(x1 - ux * h2 - nx * hw2, y1 - uy * h2 - ny * hw2), true, false);
                }
                geo.Freeze();
                dc.DrawGeometry(fill, null, geo);

                if (dashed) DrawDashShaft(dc, fill, x1, y1, hrx, hry, thickness);
                break;
            }
        }
    }

    private static void DrawDashShaft(DrawingContext dc, Brush brush, double x1, double y1, double x2, double y2, double thickness)
    {
        var dp = new Pen(brush, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            DashStyle = DashStyles.Dash,
        };
        dc.DrawLine(dp, new Point(x1, y1), new Point(x2, y2));
    }

    /// <summary>鍦嗚鐭╁舰璺緞锛堝洓瑙?ArcTo锛夈€俽 鈮?0 鏃堕€€鍖栦负鏅€氱煩褰€?/summary>
    private static StreamGeometry RoundRectGeometry(double x, double y, double w, double h, double r)
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

    /// <summary>
    /// 鏇茬嚎绠ご锛堜簩娆¤礉濉炲皵锛屼笁鐐规帶鍒讹細璧风偣 / 寮洸鎺у埗鐐?/ 缁堢偣锛夈€?    /// 鏉嗕负璐濆灏旀洸绾匡紱绠ご澶存部缁堢偣鍒囩嚎鏂瑰悜锛圥2 - C锛夈€?    /// </summary>
    private static void DrawCurvedArrow(DrawingContext dc, double x0, double y0, double cx, double cy, double x2, double y2,
        Color color, double thickness, bool dashed = false, ArrowStyle style = ArrowStyle.Solid)
    {
        // 缁堢偣鍒囩嚎鏂瑰悜 = P2 - C
        double dx = x2 - cx, dy = y2 - cy;
        double len = Math.Sqrt(dx * dx + dy * dy);
        double ux, uy, nx, ny;
        if (len < 1e-6) { ux = 0; uy = 0; nx = 1; ny = 0; }
        else { ux = dx / len; uy = dy / len; nx = -uy; ny = ux; }

        var fill = new SolidColorBrush(color);
        fill.Freeze();

        var shaft = new StreamGeometry();   // 杆：二次贝塞尔
        using (var g = shaft.Open())
        {
            g.BeginFigure(new Point(x0, y0), false, false);
            g.QuadraticBezierTo(new Point(cx, cy), new Point(x2, y2), true, false);
        }
        shaft.Freeze();
        var shaftPen = new Pen(fill, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (dashed) shaftPen.DashStyle = DashStyles.Dash;
        dc.DrawGeometry(null, shaftPen, shaft);

        switch (style)
        {
            case ArrowStyle.Solid:
            {
                double h = Math.Max(10, thickness * 4);
                double hw = h * 0.42;
                double hrx = x2 - ux * h, hry = y2 - uy * h;
                var geo = new StreamGeometry();
                using (var g = geo.Open())
                {
                    g.BeginFigure(new Point(hrx + nx * hw, hry + ny * hw), true, true);
                    g.LineTo(new Point(x2, y2), true, false);
                    g.LineTo(new Point(hrx - nx * hw, hry - ny * hw), true, false);
                }
                geo.Freeze();
                dc.DrawGeometry(fill, null, geo);
                break;
            }
            case ArrowStyle.Line:
            {
                double h = Math.Max(10, thickness * 3.5);
                double hw = h * 0.38;
                var lp = new Pen(fill, Math.Max(1.5, thickness * 0.8)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                double hrx = x2 - ux * h, hry = y2 - uy * h;
                dc.DrawLine(lp, new Point(x2, y2), new Point(hrx + nx * hw, hry + ny * hw));
                dc.DrawLine(lp, new Point(x2, y2), new Point(hrx - nx * hw, hry - ny * hw));
                break;
            }
            case ArrowStyle.Double:
            {
                double h = Math.Max(10, thickness * 4);
                double hw = h * 0.42;
                double hrx = x2 - ux * h, hry = y2 - uy * h;
                double h2 = h * 0.6, hw2 = hw * 0.6;
                // 璧风偣鍙嶅悜灏忓ご锛堟柟鍚?= P0 - C 鐨勫弽鍚?鈫?鐢?P0鈫扖 鐨勫垏绾匡級
                double dx0 = x0 - cx, dy0 = y0 - cy;
                double l0 = Math.Sqrt(dx0 * dx0 + dy0 * dy0);
                double u0x = l0 > 1e-6 ? dx0 / l0 : 0, u0y = l0 > 1e-6 ? dy0 / l0 : 0;
                double n0x = -u0y, n0y = u0x;
                var geo = new StreamGeometry();
                using (var g = geo.Open())
                {
                    g.BeginFigure(new Point(hrx + nx * hw, hry + ny * hw), true, true);
                    g.LineTo(new Point(x2, y2), true, false);
                    g.LineTo(new Point(hrx - nx * hw, hry - ny * hw), true, false);
                    g.BeginFigure(new Point(x0 - u0x * h2 + n0x * hw2, y0 - u0y * h2 + n0y * hw2), true, true);
                    g.LineTo(new Point(x0, y0), true, false);
                    g.LineTo(new Point(x0 - u0x * h2 - n0x * hw2, y0 - u0y * h2 - n0y * hw2), true, false);
                }
                geo.Freeze();
                dc.DrawGeometry(fill, null, geo);
                break;
            }
        }
    }

    private static FormattedText MakeText(string text, Color color, double size)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Microsoft YaHei"), size, new SolidColorBrush(color), 1.0);
        return ft;
    }
}
