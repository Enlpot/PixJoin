using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PixJoin.App.Native;
using PixJoin.Core.Services;

namespace PixJoin.App.UI;

/// <summary>
/// 设置窗口：可视化编辑所有持久化配置。
/// 保存后通过 <paramref name="onApplied"/> 回调让 App 应用变更（重注册快捷键、开机自启等）。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsService _settings;
    private readonly Action _onApplied;

    private uint _newModifiers;
    private uint _newVk;
    private bool _capturingHotkey;

    private sealed class ComboItem
    {
        public ComboItem(string text, uint value) { Text = text; Value = value; }
        public string Text { get; }
        public uint Value { get; }
        public override string ToString() => Text;
    }

    public SettingsWindow(SettingsService settings, Action onApplied)
    {
        InitializeComponent();
        _settings = settings;
        _onApplied = onApplied;

        var s = settings.Current;
        _newModifiers = s.HotkeyModifiers;
        _newVk = s.HotkeyVirtualKey;

        // 保护键下拉
        CmbProtect.Items.Add(new ComboItem("Ctrl", Win32.MOD_CONTROL));
        CmbProtect.Items.Add(new ComboItem("Shift", Win32.MOD_SHIFT));
        CmbProtect.Items.Add(new ComboItem("Alt", Win32.MOD_ALT));
        CmbProtect.Items.Add(new ComboItem("Win", Win32.MOD_WIN));
        CmbProtect.Items.Add(new ComboItem("无（右键拆分）", 0u));
        CmbProtect.SelectedItem = CmbProtect.Items.Cast<ComboItem>()
            .FirstOrDefault(x => x.Value == s.ProtectModifier) ?? CmbProtect.Items[0];

        LoadValues();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void LoadValues()
    {
        var s = _settings.Current;
        TxtHotkey.Text = s.HotkeyDisplay;
        TxtSnap.Text = s.SnapDistance.ToString("0.##");
        TxtAlign.Text = s.AlignTolerance.ToString("0.##");
        TxtDetach.Text = s.DetachDistance.ToString("0.##");
        TxtMax.Text = s.MaxStickers.ToString();
        SldOpacity.Value = Math.Clamp(s.DefaultOpacity * 100, 10, 100);
        TxtOpacityVal.Text = $"{SldOpacity.Value:0}%";
        ChkOutline.IsChecked = s.ShowGroupOutline;
        ChkDblClick.IsChecked = s.DoubleClickCloseEnabled;
        ChkWheel.IsChecked = s.WheelZoomEnabled;
        ChkEsc.IsChecked = s.EscCloseEnabled;
        ChkTransparent.IsChecked = s.TransparentBackground;
        ChkTrim.IsChecked = s.AutoTrim;
        ChkAutoStart.IsChecked = s.StartWithWindows;
    }

    private void OnOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        => TxtOpacityVal.Text = $"{e.NewValue:0}%";

    // ---------------- 快捷键捕获 ----------------

    private void OnChangeHotkey(object sender, RoutedEventArgs e)
    {
        _capturingHotkey = true;
        BtnChangeHotkey.IsEnabled = false;
        TxtHotkeyHint.Text = "请按下新的组合键…（按 Esc 取消）";
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturingHotkey) return;

        if (e.Key == Key.Escape)
        {
            _capturingHotkey = false;
            BtnChangeHotkey.IsEnabled = true;
            TxtHotkeyHint.Text = "已取消修改";
            e.Handled = true;
            return;
        }

        bool modifierOnly = e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;
        if (modifierOnly) { e.Handled = true; return; }

        uint mods = 0;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) mods |= Win32.MOD_CONTROL;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) mods |= Win32.MOD_SHIFT;
        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0) mods |= Win32.MOD_ALT;
        if ((Keyboard.Modifiers & ModifierKeys.Windows) != 0) mods |= Win32.MOD_WIN;

        if (mods == 0)
        {
            TxtHotkeyHint.Text = "请包含至少一个修饰键（Ctrl / Shift / Alt / Win）";
            e.Handled = true;
            return;
        }

        uint vk = (uint)KeyInterop.VirtualKeyFromKey(e.Key);
        _newModifiers = mods;
        _newVk = vk;
        TxtHotkey.Text = HotkeyText.Build(mods, vk);
        _capturingHotkey = false;
        BtnChangeHotkey.IsEnabled = true;
        TxtHotkeyHint.Text = $"已设置：{TxtHotkey.Text}";
        e.Handled = true;
    }

    // ---------------- 保存 / 取消 ----------------

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_capturingHotkey) return;   // 仍在捕获中，先完成或取消

        var s = _settings.Current;
        s.HotkeyModifiers = _newModifiers;
        s.HotkeyVirtualKey = _newVk;

        if (double.TryParse(TxtSnap.Text, out var snap)) s.SnapDistance = Math.Clamp(snap, 0, 100);
        if (double.TryParse(TxtAlign.Text, out var align)) s.AlignTolerance = Math.Clamp(align, 0, 50);
        if (double.TryParse(TxtDetach.Text, out var detach)) s.DetachDistance = Math.Clamp(detach, 5, 200);
        if (int.TryParse(TxtMax.Text, out var max)) s.MaxStickers = Math.Clamp(max, 1, 500);

        s.ProtectModifier = (CmbProtect.SelectedItem as ComboItem)?.Value ?? s.ProtectModifier;
        s.DefaultOpacity = Math.Round(SldOpacity.Value / 100.0, 2);
        s.ShowGroupOutline = ChkOutline.IsChecked == true;
        s.DoubleClickCloseEnabled = ChkDblClick.IsChecked == true;
        s.WheelZoomEnabled = ChkWheel.IsChecked == true;
        s.EscCloseEnabled = ChkEsc.IsChecked == true;
        s.TransparentBackground = ChkTransparent.IsChecked == true;
        s.AutoTrim = ChkTrim.IsChecked == true;
        s.StartWithWindows = ChkAutoStart.IsChecked == true;

        _settings.Save();
        _onApplied();
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
