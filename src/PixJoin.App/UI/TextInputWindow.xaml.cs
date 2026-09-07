using System.Windows;
using System.Windows.Input;

namespace PixJoin.App.UI;

/// <summary>标注文字输入框：回车确认，Esc 取消。</summary>
public partial class TextInputWindow : Window
{
    public string ResultText { get; private set; } = "";

    public TextInputWindow(string initial = "")
    {
        InitializeComponent();
        Input.Text = initial;
        Loaded += (_, _) => { Input.Focus(); Input.SelectAll(); };
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            Confirm();
            e.Handled = true;
        }
    }

    private void OnOk(object sender, RoutedEventArgs e) => Confirm();

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void Confirm()
    {
        ResultText = Input.Text.Trim();
        DialogResult = true;
    }
}
