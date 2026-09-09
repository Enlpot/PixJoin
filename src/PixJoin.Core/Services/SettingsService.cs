using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PixJoin.Core.Services;

/// <summary>全局配置（持久化到 %APPDATA%\PixJoin\settings.json）。</summary>
public sealed class AppSettings
{
    // ---- 快捷键 ----
    /// <summary>修饰键位掩码（MOD_CONTROL=2 / MOD_SHIFT=4 / MOD_ALT=1 / MOD_WIN=8）。</summary>
    public uint HotkeyModifiers { get; set; } = 2 /*Ctrl*/ | 4 /*Shift*/;

    /// <summary>虚拟键码（Win32 VK_*）。默认 'A' = 0x41。</summary>
    public uint HotkeyVirtualKey { get; set; } = 0x41;

    public string HotkeyDisplay => HotkeyText.Build(HotkeyModifiers, HotkeyVirtualKey);

    // ---- 贴图快捷键（从剪贴板/复制的图片文件直接钉屏） ----
    public uint PinHotkeyModifiers { get; set; } = 2 /*Ctrl*/ | 4 /*Shift*/;

    /// <summary>默认 'P' = 0x50。</summary>
    public uint PinHotkeyVirtualKey { get; set; } = 0x50;

    public string PinHotkeyDisplay => HotkeyText.Build(PinHotkeyModifiers, PinHotkeyVirtualKey);

    // ---- 吸附 / 拆分 ----
    public double SnapDistance { get; set; } = 10;

    public double AlignTolerance { get; set; } = 5;

    /// <summary>保护键：按住时才允许拆分（位掩码，与 HotkeyModifiers 同编码）。</summary>
    public uint ProtectModifier { get; set; } = 2; // Ctrl

    public double DetachDistance { get; set; } = 25;

    // ---- 贴图 ----
    public int MaxStickers { get; set; } = 50;

    public double DefaultOpacity { get; set; } = 1.0;

    public bool ShowGroupOutline { get; set; } = true;

    // ---- 贴图交互快捷键（首次使用确认） ----
    /// <summary>双击贴图关闭（首次使用弹提示确认，之后按所选记忆）。</summary>
    public bool DoubleClickCloseEnabled { get; set; } = true;

    public bool DoubleClickClosePrompted { get; set; }

    /// <summary>直接滚动滚轮缩放贴图（Ctrl+滚轮为原有行为；Alt+滚轮为透明度）。</summary>
    public bool WheelZoomEnabled { get; set; } = true;

    public bool WheelZoomPrompted { get; set; }

    /// <summary>鼠标悬停在贴图上按 ESC 关闭该贴图。</summary>
    public bool EscCloseEnabled { get; set; } = true;

    public bool EscClosePrompted { get; set; }

    /// <summary>贴图自动 OCR 文字识别（悬停文字显示 IBeam、可拖选复制）。</summary>
    public bool OcrEnabled { get; set; } = true;

    // ---- 导出 ----
    public bool TransparentBackground { get; set; } = true;

    public bool AutoTrim { get; set; } = true;

    public string LastSaveDirectory { get; set; } =
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    // ---- 生命周期 ----
    public bool StartWithWindows { get; set; }

    public bool FirstRunDone { get; set; }
}

public static class HotkeyText
{
    public static string Build(uint modifiers, uint vk)
    {
        var parts = new List<string>();
        if ((modifiers & 8) != 0) parts.Add("Win");
        if ((modifiers & 2) != 0) parts.Add("Ctrl");
        if ((modifiers & 1) != 0) parts.Add("Alt");
        if ((modifiers & 4) != 0) parts.Add("Shift");
        parts.Add(VkToName(vk));
        return string.Join("+", parts);
    }

    public static string VkToName(uint vk) => vk switch
    {
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),          // 0-9
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),          // A-Z
        >= 0x70 and <= 0x87 => $"F{vk - 0x6F}",                // F1-F24
        0x20 => "Space",
        0x0D => "Enter",
        0x09 => "Tab",
        0x1B => "Esc",
        0x2C => "PrtSc",
        0xBA => ";",
        0xBB => "=",
        0xBC => ",",
        0xBD => "-",
        0xBE => ".",
        0xBF => "/",
        0xC0 => "`",
        0xDB => "[",
        0xDC => "\\",
        0xDD => "]",
        0xDE => "'",
        _ => $"0x{vk:X2}",
    };
}

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string ConfigDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PixJoin");

    public static string ConfigPath => Path.Combine(ConfigDirectory, "settings.json");

    public AppSettings Current { get; private set; } = new();

    public static SettingsService Load()
    {
        var svc = new SettingsService();
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                svc.Current = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts) ?? new AppSettings();
            }
        }
        catch
        {
            // 配置损坏时回退默认值，保证程序能起来
            svc.Current = new AppSettings();
        }
        return svc;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(ConfigDirectory);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(Current, JsonOpts));
        }
        catch
        {
            // 配置写入失败不应影响主流程
        }
    }
}
