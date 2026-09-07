using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PixJoin.Core.Models;

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

    private Color _color = Color.FromRgb(0xE5, 0x39, 0x35);   // 默认红

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
