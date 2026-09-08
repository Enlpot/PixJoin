using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PixJoin.Core.Models;
using PixJoin.Core.Services;

namespace PixJoin.App.UI;

/// <summary>
/// 标注工具条（独立小窗，可超出贴图窗口）。事件回传给贴图窗口。
/// </summary>
public partial class AnnotationBarWindow : Window
{
    public event Action<AnnotationTool>? ToolChanged;
    public event Action<Color>? ColorChanged;
    public event Action<double>? ThicknessChanged;
    public event Action? UndoRequested;
    public event Action? DoneRequested;
    public event Action<ArrowStyle, bool>? StyleApplied;   // 样式菜单：箭头样式 + 线型

    private Color _color = Color.FromRgb(0xE5, 0x39, 0x35);   // 默认红
    private ArrowStyle _arrowStyle = ArrowStyle.Solid;
    private bool _dashed;

    public AnnotationBarWindow()
    {
        InitializeComponent();
    }

    public AnnotationTool CurrentTool { get; private set; } = AnnotationTool.Arrow;

    private void OnToolChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || !Enum.TryParse(rb.Tag?.ToString(), out AnnotationTool tool)) return;
        CurrentTool = tool;
        ToolChanged?.Invoke(tool);
    }

    /// <summary>样式下拉（箭头=箭头样式+线型；其余=线型），点击后回传给贴图窗口统一应用。</summary>
    private void OnStyleMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        var menu = CurrentTool == AnnotationTool.Arrow
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
        menu.PlacementTarget = btn;
        menu.IsOpen = true;
    }

    private void OnColorClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string hex || !TryParseHex(hex, out var c)) return;
        _color = c;
        ColorChanged?.Invoke(c);
    }

    private void OnThickChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || !double.TryParse(rb.Tag?.ToString(), out double t)) return;
        ThicknessChanged?.Invoke(t);
    }

    private void OnUndo(object sender, RoutedEventArgs e) => UndoRequested?.Invoke();

    private void OnDone(object sender, RoutedEventArgs e) => DoneRequested?.Invoke();

    private static bool TryParseHex(string hex, out Color color)
    {
        color = Colors.Black;
        try
        {
            color = (Color)ColorConverter.ConvertFromString(hex);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
