using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PixJoin.Core.Models;
using PixJoin.Core.Services;

namespace PixJoin.App.UI;

/// <summary>
/// 标注工具条（独立小窗，可超出贴图窗口）。标注工具区复用共享组件 AnnotationToolsControl，
/// 与截图工具条同一份代码；两侧仅保留差异按钮（本窗：撤销 + 完成）。
/// </summary>
public partial class AnnotationBarWindow : Window
{
    public event Action<AnnotationTool?>? ToolChanged;
    public event Action<Color>? ColorChanged;
    public event Action<double>? ThicknessChanged;
    public event Action? UndoRequested;
    public event Action? DoneRequested;
    public event Action<ArrowStyle, bool>? StyleApplied;

    private AnnotationTool? _tool;
    private Color _color = AnnotationStyleMenus.Colors[0];
    private double _thickness = 4;
    private ArrowStyle _arrowStyle = ArrowStyle.Solid;
    private bool _dashed;
    private AnnotationToolsControl _tools = null!;

    public AnnotationBarWindow()
    {
        InitializeComponent();
        BuildBar();
    }

    private void BuildBar()
    {
        var bar = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xEE, 0x22, 0x22, 0x22)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(4),
        };
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        // 撤销
        var undo = MakeBtn("↶", "撤销标注 (Ctrl+Z)");
        undo.Click += (_, _) => UndoRequested?.Invoke();
        panel.Children.Add(undo);

        // 共享标注工具区（与截图工具条同一组件）
        _tools = new AnnotationToolsControl(_tool, _color, _thickness, _arrowStyle, _dashed);
        _tools.ToolChanged += t =>
        {
            _tool = t;
            ToolChanged?.Invoke(t);
        };
        _tools.ColorChanged += c =>
        {
            _color = c;
            ColorChanged?.Invoke(c);
        };
        _tools.ThicknessChanged += t =>
        {
            _thickness = t;
            ThicknessChanged?.Invoke(t);
        };
        _tools.StyleApplied += (s, d) =>
        {
            _arrowStyle = s;
            _dashed = d;
            StyleApplied?.Invoke(s, d);
        };
        panel.Children.Add(_tools);

        // 完成
        var done = MakeBtn("✓ 完成", "完成并固化标注 (Esc)", primary: true);
        done.Click += (_, _) => DoneRequested?.Invoke();
        panel.Children.Add(done);

        bar.Child = panel;
        Content = bar;
    }

    private static Button MakeBtn(string text, string tip, bool primary = false) => new()
    {
        Content = new TextBlock { Text = text, FontSize = 12, Foreground = Brushes.White },
        Background = new SolidColorBrush(primary ? Color.FromRgb(0x00, 0xA8, 0xFF) : Color.FromRgb(0x3A, 0x3A, 0x3A)),
        BorderThickness = new Thickness(0),
        Padding = new Thickness(6, 2, 6, 2),
        Margin = new Thickness(1, 0, 1, 0),
        Cursor = Cursors.Hand,
        ToolTip = tip,
    };
}
