using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using PixJoin.Core.Models;
using PixJoin.Core.Services;

namespace PixJoin.Core.Tests;

/// <summary>共享标注渲染器测试：验证截图 / 贴图共用的 AnnotationPainter 对各工具正确产元素。</summary>
public static class AnnotationPainterTests
{
    public static void Run()
    {
        Check.Section("AnnotationPainter 统一标注渲染");

        // 矩形：Rectangle 元素（含描边居中外扩）
        {
            var cv = new Canvas();
            AnnotationPainter.Draw(cv, new Annotation { Tool = AnnotationTool.Rect, X = 2, Y = 3, W = 40, H = 20, Thickness = 4, Color = Colors.Red }, 1, 1);
            Check.True(cv.Children.Count == 1 && cv.Children[0] is Rectangle, "矩形：单个 Rectangle 元素");
        }

        // 圆角矩形：Path 元素
        {
            var cv = new Canvas();
            AnnotationPainter.Draw(cv, new Annotation { Tool = AnnotationTool.Rect, X = 0, Y = 0, W = 40, H = 20, CornerRadius = 6, Thickness = 4, Color = Colors.Red }, 1, 1);
            Check.True(cv.Children.Count == 1 && cv.Children[0] is System.Windows.Shapes.Path, "圆角矩形：Path 元素");
        }

        // 箭头：杆 Path + 头 Path（实心样式）→ 2 个元素
        {
            var cv = new Canvas();
            AnnotationPainter.Draw(cv, new Annotation
            {
                Tool = AnnotationTool.Arrow, Thickness = 4, Color = Colors.Blue,
                Points = new[] { 0.0, 0.0, 30.0, 10.0, 60.0, 0.0 },
            }, 1, 1);
            Check.True(cv.Children.Count == 2 && cv.Children[0] is System.Windows.Shapes.Path && cv.Children[1] is System.Windows.Shapes.Path, "箭头：杆+实心头两个 Path");
        }

        // V 形箭头：杆 Path + 两条 Line → 3 个元素
        {
            var cv = new Canvas();
            AnnotationPainter.Draw(cv, new Annotation
            {
                Tool = AnnotationTool.Arrow, Thickness = 4, Color = Colors.Blue, Arrow = ArrowStyle.Line,
                Points = new[] { 0.0, 0.0, 30.0, 10.0, 60.0, 0.0 },
            }, 1, 1);
            Check.True(cv.Children.Count == 3, "V 形箭头：杆+两条头线共 3 元素");
        }

        // 画笔：Polyline
        {
            var cv = new Canvas();
            AnnotationPainter.Draw(cv, new Annotation { Tool = AnnotationTool.Pen, Thickness = 3, Color = Colors.Green, Points = new[] { 0.0, 0.0, 5.0, 5.0, 10.0, 0.0 } }, 1, 1);
            Check.True(cv.Children.Count == 1 && cv.Children[0] is Polyline, "画笔：Polyline 元素");
        }

        // 高亮：无描边 Rectangle
        {
            var cv = new Canvas();
            AnnotationPainter.Draw(cv, new Annotation { Tool = AnnotationTool.Highlight, X = 0, Y = 0, W = 30, H = 12, Color = Colors.Yellow }, 1, 1);
            var r = cv.Children[0] as Rectangle;
            Check.True(cv.Children.Count == 1 && r is { Stroke: null }, "高亮：纯填充无描边");
        }

        // 缩放：sx/sy 非等比时坐标按各自系数
        {
            var cv = new Canvas();
            AnnotationPainter.Draw(cv, new Annotation { Tool = AnnotationTool.Rect, X = 10, Y = 20, W = 100, H = 50, Thickness = 2, Color = Colors.Red }, 2, 1);
            var r = (Rectangle)cv.Children[0];
            // 描边外扩 t/2=1 → Left=(10-1)*2=18, Top=(20-1)*1=19, Width=(100+2)*2=204, Height=(50+2)*1=52
            Check.Near(Canvas.GetLeft(r), 18, "缩放：Left 按 sx", 0.01);
            Check.Near(Canvas.GetTop(r), 19, "缩放：Top 按 sy", 0.01);
            Check.Near(r.Width, 204, "缩放：Width 按 sx", 0.01);
            Check.Near(r.Height, 52, "缩放：Height 按 sy", 0.01);
        }

        // 控制点：矩形 noFrame → 12 个控制点（4 角方块 + 4 边方块 + 4 圆角圆点），无选中框
        {
            var cv = new Canvas();
            AnnotationPainter.DrawHandles(cv, new Annotation { Tool = AnnotationTool.Rect, X = 0, Y = 0, W = 40, H = 20, Thickness = 4, Color = Colors.Red }, 1, 1);
            Check.True(cv.Children.Count == 12, $"矩形控制点 12 个（实际 {cv.Children.Count}）");
        }

        // 控制点：椭圆 noFrame → 8 个
        {
            var cv = new Canvas();
            AnnotationPainter.DrawHandles(cv, new Annotation { Tool = AnnotationTool.Ellipse, X = 0, Y = 0, W = 40, H = 20, Thickness = 2, Color = Colors.Red }, 1, 1);
            Check.True(cv.Children.Count == 8, $"椭圆控制点 8 个（实际 {cv.Children.Count}）");
        }

        // 控制点：箭头 3 个（弧顶在曲线上 t=0.5）
        {
            var cv = new Canvas();
            AnnotationPainter.DrawHandles(cv, new Annotation
            {
                Tool = AnnotationTool.Arrow, Thickness = 4, Color = Colors.Blue,
                Points = new[] { 0.0, 0.0, 30.0, 10.0, 60.0, 0.0 },
            }, 1, 1);
            Check.True(cv.Children.Count == 3, $"箭头控制点 3 个（实际 {cv.Children.Count}）");
            // 弧顶 = 可见曲线（P0→C→头部根部 hrx）上 t=0.5 点：hrx=(44.82,5.06) → (26.205,6.265)
            var mid = (Rectangle)cv.Children[1];
            Check.Near(Canvas.GetLeft(mid) + 3.5, 26.205, "箭头弧顶 X 骑线", 0.5);
            Check.Near(Canvas.GetTop(mid) + 3.5, 6.265, "箭头弧顶 Y 骑线", 0.5);
        }

        // 控制点：文字有选中框 + Move/4 角 = 6 个元素
        {
            var cv = new Canvas();
            AnnotationPainter.DrawHandles(cv, new Annotation { Tool = AnnotationTool.Text, X = 20, Y = 20, Text = "A", Thickness = 10, Color = Colors.Red }, 1, 1);
            Check.True(cv.Children.Count == 6, $"文字选中态 6 元素（实际 {cv.Children.Count}）");
            Check.True(cv.Children[0] is Rectangle && cv.Children[0].GetType().Name == "Rectangle", "文字选中框存在");
        }

        // 共享样式菜单：线型菜单 2 项（实线/虚线）
        {
            var m = AnnotationStyleMenus.BuildLineStyleMenu(currentDashed: false, _ => { });
            Check.True(m.Items.Count == 2, $"线型菜单 2 项（实际 {m.Items.Count}）");
            Check.True(m.Items[0] is MenuItem mi0 && mi0.IsChecked, "线型菜单默认选中实线");
        }

        // 共享样式菜单：箭头菜单 6 项（3 样式 + 分隔 + 2 线型）
        {
            var m = AnnotationStyleMenus.BuildArrowMenu(PixJoin.Core.Models.ArrowStyle.Line, true, (_, _) => { });
            Check.True(m.Items.Count == 6, $"箭头菜单 6 项（实际 {m.Items.Count}）");
            Check.True(m.Items[1] is MenuItem mi1 && mi1.IsChecked, "箭头菜单默认选中 V 形");
            Check.True(m.Items[3] is Separator, "箭头菜单第 4 项为分隔线");
        }

        // 共享调色板 8 色
        {
            Check.True(AnnotationStyleMenus.Colors.Length == 8, $"调色板 8 色（实际 {AnnotationStyleMenus.Colors.Length}）");
        }
    }
}
