using System.Windows;
using System.Windows.Controls;

namespace PixJoin.App.UI;

/// <summary>简易单行输入对话框（新建分组等场景用）。点空白关闭，Esc 取消。</summary>
public sealed class InputDialog : Window
{
    private readonly TextBox _box;

    public string Value => _box.Text.Trim();

    public InputDialog(string title, string label, string initial)
    {
        Title = title;
        Width = 320;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;

        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 6) });
        _box = new TextBox { Text = initial, Height = 26, VerticalContentAlignment = VerticalAlignment.Center };
        panel.Children.Add(_box);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        var ok = new Button { Content = "确定", Width = 72, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        ok.Click += (_, _) => { DialogResult = true; Close(); };
        var cancel = new Button { Content = "取消", Width = 72, IsCancel = true };
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        Content = panel;
        Loaded += (_, _) => _box.Focus();
    }
}
