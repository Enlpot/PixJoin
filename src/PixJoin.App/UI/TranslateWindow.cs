using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PixJoin.Core.Services;
using PixJoin.Core.Models;

namespace PixJoin.App.UI;

/// <summary>
/// 截图翻译：对选区图片做 OCR 识别 → 在线翻译（Google 免费接口）→ 原文/译文展示，可复制。
/// Esc 关闭；双击译文复制；目标语言下拉即时重译。
/// </summary>
public sealed class TranslateWindow : Window
{
    private static readonly (string Tag, string Name)[] Languages =
    {
        ("zh-CN", "简体中文"),
        ("zh-TW", "繁体中文"),
        ("en", "English"),
        ("ja", "日本語"),
        ("ko", "한국어"),
    };

    private readonly BitmapSource _image;
    private readonly IOcrEngine _ocr;
    private readonly TextBlock _status;
    private readonly TextBlock _sourceText;
    private readonly TextBlock _resultText;
    private readonly ComboBox _langBox;
    private bool _closing;

    public TranslateWindow(BitmapSource image, IOcrEngine ocr)
    {
        _image = image;
        _ocr = ocr;

        Title = "截图翻译 - PixJoin";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x20, 0x20, 0x20));
        Width = 560; Height = 460;

        var root = new Grid { Background = Brushes.Transparent };
        root.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        };

        // 顶部栏：标题 + 语言 + 关闭
        var title = new TextBlock
        {
            Text = "截图翻译",
            Foreground = Brushes.White,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 0, 0),
        };
        Grid.SetColumn(title, 0);

        _langBox = new ComboBox
        {
            Width = 110,
            Height = 26,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
        };
        foreach (var (_, name) in Languages) _langBox.Items.Add(name);
        _langBox.SelectedIndex = 0;
        _langBox.SelectionChanged += async (_, _) => await RunAsync();
        Grid.SetColumn(_langBox, 1);

        var closeBtn = new Button
        {
            Content = "✕",
            Width = 28, Height = 26,
            Background = Brushes.Transparent,
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 8, 0),
        };
        closeBtn.Click += (_, _) => Close();
        Grid.SetColumn(closeBtn, 2);

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(title); top.Children.Add(_langBox); top.Children.Add(closeBtn);

        // 缩略图
        var thumb = new Image
        {
            Source = _image,
            Stretch = Stretch.Uniform,
            MaxHeight = 170,
            Margin = new Thickness(14, 8, 14, 4),
        };

        // 状态 / 原文 / 译文
        _status = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x8A, 0xB4, 0xF8)),
            FontSize = 12,
            Margin = new Thickness(14, 2, 14, 2),
        };
        _sourceText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xC8, 0xC8, 0xC8)),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 90,
            Margin = new Thickness(14, 6, 14, 0),
        };
        _resultText = new TextBlock
        {
            Foreground = Brushes.White,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 120,
            Margin = new Thickness(14, 10, 14, 0),
        };

        var copyBtn = new Button
        {
            Content = "复制译文",
            Width = 90, Height = 30,
            Margin = new Thickness(14, 12, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        copyBtn.Click += (_, _) => CopyResult();

        var rootGrid = new Grid();
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rootGrid.Children.Add(top); Grid.SetRow(top, 0);
        rootGrid.Children.Add(thumb); Grid.SetRow(thumb, 1);
        rootGrid.Children.Add(_status); Grid.SetRow(_status, 2);
        rootGrid.Children.Add(_sourceText); Grid.SetRow(_sourceText, 3);
        rootGrid.Children.Add(_resultText); Grid.SetRow(_resultText, 4);
        rootGrid.Children.Add(copyBtn); Grid.SetRow(copyBtn, 5);

        var wrap = new Grid();
        wrap.Children.Add(rootGrid);
        Content = wrap;

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Close(); }
            else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control) CopyResult();
        };
        Closed += (_, _) => _closing = true;

        Loaded += async (_, _) => await RunAsync();
    }

    private async Task RunAsync()
    {
        if (_closing) return;
        string tl = Languages[Math.Max(0, _langBox.SelectedIndex)].Tag;
        _status.Text = "正在识别文字…";
        _sourceText.Text = "";
        _resultText.Text = "";
        try
        {
            string text = await Task.Run(() => Recognize());
            if (_closing) return;
            _sourceText.Text = text.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                _status.Text = "未识别到文字";
                return;
            }
            _status.Text = "正在翻译…";
            var translated = await Task.Run(() => Translate(text, tl));
            if (_closing) return;
            _resultText.Text = string.IsNullOrWhiteSpace(translated) ? "（翻译失败，请检查网络后重试）" : translated.Trim();
            _status.Text = "完成 —— Esc 关闭，双击译文 / Ctrl+C 复制";
        }
        catch (Exception ex)
        {
            _status.Text = "翻译失败：" + ex.Message;
        }
    }

    private string Recognize()
    {
        var result = _ocr.Recognize(_image);
        return result?.FullText ?? "";
    }

    private static string? Translate(string text, string tl)
    {
        using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        hc.DefaultRequestHeaders.UserAgent.ParseAdd("PixJoin/1.0");
        var url = "https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl="
                  + Uri.EscapeDataString(tl) + "&dt=t&q=" + Uri.EscapeDataString(text);
        var json = hc.GetStringAsync(url).GetAwaiter().GetResult();
        using var doc = JsonDocument.Parse(json);
        var segs = doc.RootElement[0];
        var sb = new StringBuilder();
        foreach (var seg in segs.EnumerateArray())
        {
            if (seg.GetArrayLength() > 0 && seg[0].ValueKind == JsonValueKind.String)
                sb.Append(seg[0].GetString());
        }
        return sb.ToString();
    }

    private void CopyResult()
    {
        if (string.IsNullOrWhiteSpace(_resultText.Text)) return;
        try
        {
            System.Windows.Clipboard.SetText(_resultText.Text);
            _status.Text = "已复制译文";
        }
        catch
        {
            _status.Text = "复制失败：剪贴板正被占用";
        }
    }
}
