using System;
using System.Windows;

namespace PixJoin.App.UI;

/// <summary>
/// 文字选择后的浮动工具条（独立小窗口，可超出贴图窗口边界显示）。
/// 通过事件把「复制 / 取消」回传给贴图窗口。
/// </summary>
public partial class FloatingBarWindow : Window
{
    public event Action? CopyRequested;
    public event Action? DismissRequested;

    public FloatingBarWindow()
    {
        InitializeComponent();
    }

    private void OnCopy(object sender, RoutedEventArgs e) => CopyRequested?.Invoke();

    private void OnDismiss(object sender, RoutedEventArgs e) => DismissRequested?.Invoke();
}
