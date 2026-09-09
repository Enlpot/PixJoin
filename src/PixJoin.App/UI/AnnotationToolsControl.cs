using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PixJoin.Core.Models;
using PixJoin.Core.Services;

namespace PixJoin.App.UI;

/// <summary>
/// 标注工具区共享组件（截图工具条与贴图工具条同一份代码）：
/// 8 个工具图标（带 ▾ 样式下拉，再点取消选中）+ 颜色单块循环 + 粗细数字循环。
/// 操作通过事件抛给宿主；宿主负责状态落地（选中标注改样式、刷新画布、重建工具条）。
/// </summary>
public class AnnotationToolsControl : StackPanel
{
    public event Action<AnnotationTool?>? ToolChanged;
    public event Action<Color>? ColorChanged;
    public event Action<double>? ThicknessChanged;
    public event Action<ArrowStyle, bool>? StyleApplied;

    private static readonly (AnnotationTool Tool, string Glyph, string Tip)[] ToolDefs =
    {
        (AnnotationTool.Rect, "▭", "矩形"),
        (AnnotationTool.Ellipse, "◯", "椭圆"),
        (AnnotationTool.Arrow, "➔", "箭头"),
        (AnnotationTool.Pen, "✎", "画笔"),
        (AnnotationTool.Text, "T", "文字"),
        (AnnotationTool.Highlight, "🖍", "高亮"),
        (AnnotationTool.Mosaic, "▦", "马赛克"),
        (AnnotationTool.Number, "①", "序号"),
        (AnnotationTool.Spotlight, "◎", "聚光灯"),
        (AnnotationTool.Magnifier, "🔍", "放大镜"),
    };

    private static readonly double[] ThickSteps = { 2, 3, 4, 6, 8, 12 };

    private readonly double _scale;
    private readonly List<(Button Btn, AnnotationTool Tool)> _toolBtns = new();
    private readonly Border _colorBlock;
    private readonly Button _thickBtn;
    private AnnotationTool? _tool;
    private Color _color;
    private double _thickness;
    private ArrowStyle _arrowStyle;
    private bool _dashed;

    public AnnotationToolsControl(AnnotationTool? tool, Color color, double thickness,
                                  ArrowStyle arrowStyle, bool dashed, double scale = 1.0)
    {
        _tool = tool;
        _color = color;
        _thickness = thickness;
        _arrowStyle = arrowStyle;
        _dashed = dashed;
        _scale = scale;
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;

        foreach (var (t, glyph, tip) in ToolDefs)
        {
            var b = MakeToolButton(glyph, tip, BuildMenu(t), scale);
            var tt = t;
            b.Click += (_, _) =>
            {
                _tool = _tool == tt ? null : tt;   // 再点一次取消工具
                RefreshToolState();
                ToolChanged?.Invoke(_tool);
            };
            _toolBtns.Add((b, t));
            Children.Add(b);
        }

        _colorBlock = new Border
        {
            Width = 16 * scale, Height = 16 * scale,
            Background = new SolidColorBrush(_color),
            CornerRadius = new CornerRadius(2 * scale),
            Margin = new Thickness(6 * scale, 0, 2 * scale, 0),
            ToolTip = "颜色（点击切换）",
            Cursor = Cursors.Hand,
        };
        _colorBlock.MouseLeftButtonDown += (_, _) =>
        {
            int idx = Array.IndexOf(AnnotationStyleMenus.Colors, _color);
            _color = AnnotationStyleMenus.Colors[(idx + 1) % AnnotationStyleMenus.Colors.Length];
            _colorBlock.Background = new SolidColorBrush(_color);
            ColorChanged?.Invoke(_color);
        };
        Children.Add(_colorBlock);

        _thickBtn = MakeTextButton($"{_thickness:0.#}", "粗细", scale);
        _thickBtn.Click += (_, _) =>
        {
            int idx = Array.FindIndex(ThickSteps, s => s >= _thickness - 0.01);
            if (idx < 0) idx = 0;
            _thickness = ThickSteps[(idx + 1) % ThickSteps.Length];
            RefreshThickText();
            ThicknessChanged?.Invoke(_thickness);
        };
        Children.Add(_thickBtn);

        RefreshToolState();
    }

    /// <summary>宿主状态变化后同步显示（贴图侧控件常驻时用；截图侧直接重建）。</summary>
    public void RefreshState(AnnotationTool? tool, Color color, double thickness)
    {
        _tool = tool;
        _color = color;
        _thickness = thickness;
        _colorBlock.Background = new SolidColorBrush(_color);
        RefreshThickText();
        RefreshToolState();
    }

    private void RefreshToolState()
    {
        foreach (var (btn, t) in _toolBtns)
        {
            bool active = _tool == t;
            var grid = (Grid)btn.Content;
            var body = (Border)grid.Children[0];
            body.Background = new SolidColorBrush(active ? Color.FromRgb(0x00, 0xE5, 0xC0) : Color.FromRgb(0x3A, 0x3A, 0x3A));
            if (body.Child is TextBlock tb) tb.Foreground = active ? Brushes.Black : Brushes.White;
        }
    }

    private void RefreshThickText()
    {
        var grid = (Grid)_thickBtn.Content;
        if (grid.Children[0] is Border body && body.Child is TextBlock tb)
            tb.Text = _thickness.ToString("0.#");
    }

    private ContextMenu BuildMenu(AnnotationTool t) =>
        t == AnnotationTool.Arrow
            ? AnnotationStyleMenus.BuildArrowMenu(_arrowStyle, _dashed, (s, d) =>
              {
                  _arrowStyle = s;
                  _dashed = d;
                  StyleApplied?.Invoke(s, d);
              })
            : AnnotationStyleMenus.BuildLineStyleMenu(_dashed, d =>
              {
                  _dashed = d;
                  StyleApplied?.Invoke(_arrowStyle, d);
              });

    /// <summary>工具按钮：图标 + 右侧小三角下拉，左键点 ▾ 弹样式菜单。</summary>
    private static Button MakeToolButton(string glyph, string tip, ContextMenu menu, double scale)
    {
        var body = new Border
        {
            Child = new TextBlock
            {
                Text = glyph,
                Foreground = Brushes.White,
                FontSize = 14 * scale,
                FontFamily = new FontFamily("Segoe UI Symbol, Microsoft YaHei UI"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
            Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)),
            Width = 26 * scale, Height = 26 * scale,
            CornerRadius = new CornerRadius(3 * scale),
        };

        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(body, 0);
        g.Children.Add(body);
        var caret = new TextBlock
        {
            Text = "▾",
            Foreground = new SolidColorBrush(Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF)),
            FontSize = 8 * scale,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(1 * scale, 0, 0, 0),
        };
        Grid.SetColumn(caret, 1);
        g.Children.Add(caret);

        var b = new Button
        {
            Content = g,
            Margin = new Thickness(2 * scale, 0, 2 * scale, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Cursor = Cursors.Arrow,
            ToolTip = tip,
        };
        b.Click += (_, _) => menu.PlacementTarget = b;
        b.ContextMenu = menu;
        caret.MouseLeftButtonDown += (_, _) =>
        {
            menu.PlacementTarget = b;
            menu.IsOpen = true;
        };
        return b;
    }

    /// <summary>文字按钮（粗细数字用）。</summary>
    private static Button MakeTextButton(string text, string tip, double scale)
    {
        var body = new Border
        {
            Child = new TextBlock
            {
                Text = text,
                Foreground = Brushes.White,
                FontSize = 12 * scale,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
            Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)),
            Width = 30 * scale, Height = 26 * scale,
            CornerRadius = new CornerRadius(3 * scale),
        };
        var g = new Grid();
        g.Children.Add(body);
        return new Button
        {
            Content = g,
            Margin = new Thickness(2 * scale, 0, 2 * scale, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Cursor = Cursors.Arrow,
            ToolTip = tip,
        };
    }
}
