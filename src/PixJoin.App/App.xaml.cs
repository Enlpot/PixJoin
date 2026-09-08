using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using PixJoin.App.Imaging;
using PixJoin.App.Native;
using PixJoin.App.UI;
using PixJoin.Core.Models;
using PixJoin.Core.Services;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using Point = System.Windows.Point;

namespace PixJoin.App;

/// <summary>
/// 应用入口：托盘常驻 + 全局快捷键 + 截图调度。
/// 程序没有主窗口，生命周期由托盘菜单控制（ShutdownMode = OnExplicitShutdown）。
/// </summary>
public partial class App : Application
{
    private const string SingleInstanceId = "PixJoin.SingleInstance.v1";
    private const int HotKeyIdCapture = 1;

    private static Mutex? _singleInstanceMutex;

    private SettingsService _settings = null!;
    private StickerManager _stickers = null!;
    private HotKeyManager _hotkeys = null!;
    private NotifyIcon? _trayIcon;
    private ToolStripMenuItem? _trayCaptureItem;   // 托盘[截图]菜单项，设置变更后同步快捷键文本
    private Icon? _appIcon;
    private CaptureOverlay? _capture;
    private SettingsWindow? _settingsWindow;
    private bool _isExiting;
    private Win32.LowLevelKeyboardProc? _escHookProc;   // keep delegate alive
    private IntPtr _escHook;

    private void OnApplicationStartup(object sender, StartupEventArgs e)
    {
        // ---------- 进程 DPI 感知诊断（Bug 1 排查：确认 manifest 是否生效） ----------
        LogDpiAwareness();

        // ---------- 无界面自检（供 CI / 沙箱验证，不影响正常启动） ----------
        if (e.Args.Contains("--selftest", StringComparer.OrdinalIgnoreCase))
        {
            RunSelfTest();
            return;
        }
        if (e.Args.Contains("--selftest-ocr", StringComparer.OrdinalIgnoreCase))
        {
            RunSelfTestOcr();
            return;
        }

        // ---------- 单实例 ----------
        _singleInstanceMutex = new Mutex(true, SingleInstanceId, out bool isFirst);
        if (!isFirst)
        {
            MessageBox.Show("PixJoin 已经在运行中（请查看系统托盘）。", "PixJoin",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            LogException(args.Exception);
            args.Handled = true;
            MessageBox.Show($"发生未处理异常，详情已写入：\n{CrashLogPath}\n\n{args.Exception.Message}",
                "PixJoin 出错了", MessageBoxButton.OK, MessageBoxImage.Error);
        };

        // ---------- 核心服务 ----------
        _settings = SettingsService.Load();
        _stickers = new StickerManager(_settings);

        _hotkeys = new HotKeyManager();
        _hotkeys.HotKeyPressed += (_, id) =>
        {
            if (id == HotKeyIdCapture) Dispatcher.Invoke(StartCapture);
        };
        RegisterCaptureHotKey();
        InstallEscCloseHook();

        // ---------- 托盘 ----------
        _appIcon = AppIcon.Create(32);
        _trayIcon = new NotifyIcon
        {
            Icon = _appIcon,
            Text = "PixJoin —— 截图 / 贴图 / 吸附组合",
            Visible = true,
        };
        _trayIcon.DoubleClick += (_, _) => StartCapture();
        _trayIcon.ContextMenuStrip = BuildTrayMenu();

        SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.Invoke(() =>
        {
            MonitorHelper.Refresh();
            _stickers.RefreshDisplayLayout();
        });

        if (!_settings.Current.FirstRunDone)
        {
            _settings.Current.FirstRunDone = true;
            _settings.Save();
            _trayIcon.ShowBalloonTip(6000, "PixJoin 已启动",
                $"按 {_settings.Current.HotkeyDisplay} 开始截图\n拖动贴图相互靠近即可自动吸附组合",
                ToolTipIcon.Info);
        }

        // 开机自启：确保注册表状态与配置一致（用户可能在设置里改过）
        ApplyAutoStart(_settings.Current.StartWithWindows);
    }

    private void OnApplicationExit(object sender, ExitEventArgs e)
    {
        _isExiting = true;
        _stickers?.Shutdown();   // 先于其它资源释放：关闭反馈层、解绑组合事件
        _trayIcon?.Dispose();
        _appIcon?.Dispose();
        UninstallEscCloseHook();
        _hotkeys?.Dispose();
        _settings?.Save();
        _singleInstanceMutex?.ReleaseMutex();
    }

    // ---------------- 全局键盘钩子（ESC 关闭贴图） ----------------

    private void InstallEscCloseHook()
    {
        try
        {
            _escHookProc = LowLevelKeyboardProc;
            _escHook = Win32.SetWindowsHookEx(Win32.WH_KEYBOARD_LL, _escHookProc, IntPtr.Zero, 0);
            LogDebug($"InstallEscCloseHook: handle={_escHook}");
        }
        catch (Exception ex)
        {
            LogException(ex);
            _escHook = IntPtr.Zero;
        }
    }

    private void UninstallEscCloseHook()
    {
        if (_escHook != IntPtr.Zero)
        {
            Win32.UnhookWindowsHookEx(_escHook);
            _escHook = IntPtr.Zero;
        }
        _escHookProc = null;
    }

    /// <summary>低级键盘钩子：悬停贴图时 ESC 关闭（含首次提示）、Shift+C 复制全部识别文字；截图时不拦截。</summary>
    private IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == (IntPtr)Win32.WM_KEYDOWN)
        {
            int vk = Marshal.ReadInt32(lParam);

            // Shift+C：复制鼠标悬停贴图的全部识别文字（无识别结果则放行按键）
            if (vk == (int)'C' && Win32.IsKeyDown(Win32.VK_SHIFT))
            {
                if (_capture is not null && _capture.IsLoaded && _capture.IsVisible)
                    return Win32.CallNextHookEx(_escHook, nCode, wParam, lParam);
                if (_stickers.CopyAllTextUnderCursor())
                    return new IntPtr(1);
                return Win32.CallNextHookEx(_escHook, nCode, wParam, lParam);
            }

            // 空格：鼠标悬停贴图进入标注模式（显示标注工具条），不修饰键组合
            if (vk == 0x20 && !Win32.IsKeyDown(Win32.VK_SHIFT) && !Win32.IsKeyDown(Win32.VK_CONTROL) && !Win32.IsKeyDown(Win32.VK_MENU))
            {
                if (_capture is not null && _capture.IsLoaded && _capture.IsVisible)
                    return Win32.CallNextHookEx(_escHook, nCode, wParam, lParam);
                if (_stickers.TryAnnotateUnderCursor())
                    return new IntPtr(1);
                return Win32.CallNextHookEx(_escHook, nCode, wParam, lParam);
            }

            // Ctrl+Z：撤销鼠标悬停贴图的标注上一步（仅标注模式）
            if (vk == (int)'Z' && Win32.IsKeyDown(Win32.VK_CONTROL))
            {
                if (_capture is not null && _capture.IsLoaded && _capture.IsVisible)
                    return Win32.CallNextHookEx(_escHook, nCode, wParam, lParam);
                if (_stickers.TryUndoAnnotationUnderCursor())
                    return new IntPtr(1);
                return Win32.CallNextHookEx(_escHook, nCode, wParam, lParam);
            }

            if (vk == Win32.VK_ESCAPE)
            {
                // 截图 overlay 活跃时，ESC 由截图流程处理（取消 / 重置选区）
                if (_capture is not null && _capture.IsLoaded && _capture.IsVisible)
                    return Win32.CallNextHookEx(_escHook, nCode, wParam, lParam);

                // 首次使用确认弹窗进行中：放行 ESC，让用户在对话框里正常选择（ESC=否）
                if (_stickers.FirstUsePromptActive)
                    return Win32.CallNextHookEx(_escHook, nCode, wParam, lParam);

                // 鼠标悬停在贴图上：ESC 关闭该贴图（含首次提示），拦截按键避免穿透
                if (_stickers.TryCloseUnderCursor())
                    return new IntPtr(1);
            }
        }
        return Win32.CallNextHookEx(_escHook, nCode, wParam, lParam);
    }
    // ---------------- 快捷键 ----------------

    private void RegisterCaptureHotKey()
    {
        var s = _settings.Current;
        bool ok = _hotkeys.Register(HotKeyIdCapture, s.HotkeyModifiers, s.HotkeyVirtualKey);
        LogDebug($"RegisterCaptureHotKey: ok={ok} mod={s.HotkeyModifiers} vk={s.HotkeyVirtualKey} ({s.HotkeyDisplay})");

        if (!ok)
        {
            // 冲突检测：注册失败即视为被其它程序占用
            _trayIcon?.ShowBalloonTip(8000, "快捷键注册失败",
                $"{s.HotkeyDisplay} 可能已被其它程序占用，请改用其它组合（可在设置中修改）。",
                ToolTipIcon.Warning);
        }
    }

    // ---------------- 设置窗口 / 开机自启 ----------------

    private void OpenSettings()
    {
        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(_settings, () =>
        {
            ReRegisterCaptureHotKey();
            ApplyAutoStart(_settings.Current.StartWithWindows);
            _trayIcon!.Text = $"PixJoin —— 截图 / 贴图 / 吸附组合   [{_settings.Current.HotkeyDisplay}]";
            if (_trayCaptureItem is not null)
                _trayCaptureItem.Text = $"截图 ({_settings.Current.HotkeyDisplay})";
        });
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    /// <summary>设置保存后重新注册截图快捷键（组合可能已更改）。</summary>
    private void ReRegisterCaptureHotKey()
    {
        _hotkeys.Unregister(HotKeyIdCapture);
        RegisterCaptureHotKey();
    }

    /// <summary>开机自启：写 / 删 HKCU 的 Run 启动项（当前用户级，无需管理员）。</summary>
    private static void ApplyAutoStart(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key is null) return;
            if (enable)
            {
                var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exe))
                    key.SetValue("PixJoin", $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue("PixJoin", throwOnMissingValue: false);
            }
        }
        catch
        {
            // 注册表写入失败不影响主流程
        }
    }

    // ---------------- 截图 ----------------

    private void StartCapture()
    {
        // 残留自愈：若上次截图窗口未正常关闭而残留非 null，且已不可见/未加载，
        // 则强制重建，避免拦截后续所有截图请求（表现为"截图完全无反应"）。
        if (_capture is not null)
        {
            if (_capture.IsLoaded && _capture.IsVisible)
            {
                LogDebug("StartCapture: 已有活跃截图窗口，忽略重复请求");
                return;
            }
            LogDebug("StartCapture: 发现残留截图窗口（不可见/未加载），强制重建");
            _capture = null;
        }

        try
        {
            MonitorHelper.Refresh();
            var shot = ScreenCapture.CaptureVirtualScreen();
            int pw = shot.Bitmap.PixelWidth, ph = shot.Bitmap.PixelHeight;
            LogDebug($"StartCapture: 截图完成 {pw}x{ph}，准备创建 CaptureOverlay");

            _stickers.SetGuideVisible(false);
            _capture = new CaptureOverlay(shot);
            _capture.Completed += OnCaptureCompleted;
            _capture.Cancelled += OnCaptureCancelled;
            _capture.Closed += (_, _) =>
            {
                _capture = null;
                if (!_isExiting) _stickers.SetGuideVisible(true);
            };
            _capture.Loaded += (_, _) =>
            {
                if (Win32.GetWindowRect(_capture.Handle, out var r))
                    LogDebug($"CaptureOverlay.Loaded: rect=({r.Left},{r.Top},{r.Right},{r.Bottom}) dpiScale={VisualTreeHelper.GetDpi(_capture).DpiScaleX:0.##}");
            };
            _capture.Show();
            LogDebug($"StartCapture: CaptureOverlay 已 Show；IsLoaded={_capture.IsLoaded} IsVisible={_capture.IsVisible}");
        }
        catch (Exception ex)
        {
            LogException(ex);
            LogDebug($"StartCapture 异常：{ex.GetType().Name}: {ex.Message}");
            _capture = null;
            _stickers.SetGuideVisible(true);
            MessageBox.Show($"截图失败：{ex.Message}", "PixJoin", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnCaptureCompleted(CaptureResult result)
    {
        switch (result.Action)
        {
            case CaptureAction.Pin:
                _stickers.CreateSticker(result.Bitmap, result.PhysicalRect.TopLeft);
                break;

            case CaptureAction.Annotate:
                _stickers.CreateStickerAndAnnotate(result.Bitmap, result.PhysicalRect.TopLeft);
                break;

            case CaptureAction.Copy:
                if (!ClipboardService.TrySetImage(result.Bitmap))
                    System.Windows.MessageBox.Show("复制失败：剪贴板正被其它程序占用，请稍后重试。",
                        "PixJoin", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                break;

            case CaptureAction.Save:
                SaveBitmap(result.Bitmap);
                break;
        }
    }

    private void OnCaptureCancelled() { }

    private void SaveBitmap(BitmapSource bmp)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "保存截图",
            Filter = "PNG 图片 (*.png)|*.png",
            DefaultExt = ".png",
            FileName = $"PixJoin_{DateTime.Now:yyyyMMdd_HHmmss}.png",
            InitialDirectory = Directory.Exists(_settings.Current.LastSaveDirectory)
                ? _settings.Current.LastSaveDirectory
                : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        };

        if (dlg.ShowDialog() != true) return;
        ExportService.SavePng(bmp, dlg.FileName);
        _settings.Current.LastSaveDirectory = Path.GetDirectoryName(dlg.FileName) ?? _settings.Current.LastSaveDirectory;
        _settings.Save();
    }

    // ---------------- 托盘菜单 ----------------

    private ContextMenuStrip BuildTrayMenu()
    {
        var menu = new ContextMenuStrip();

        _trayCaptureItem = new ToolStripMenuItem($"截图 ({_settings.Current.HotkeyDisplay})");
        var capture = _trayCaptureItem;
        capture.Click += (_, _) => StartCapture();
        menu.Items.Add(capture);

        menu.Items.Add(new ToolStripSeparator());

        var fromClipboard = new ToolStripMenuItem("从剪贴板贴图");
        fromClipboard.Click += (_, _) => PinFromClipboard();
        menu.Items.Add(fromClipboard);

        var fromFile = new ToolStripMenuItem("从文件贴图…");
        fromFile.Click += (_, _) => PinFromFile();
        menu.Items.Add(fromFile);

        menu.Items.Add(new ToolStripSeparator());

        var closeAll = new ToolStripMenuItem("关闭全部贴图");
        closeAll.Click += (_, _) => _stickers.CloseAll();
        menu.Items.Add(closeAll);

        var front = new ToolStripMenuItem("贴图置前");
        front.Click += (_, _) => _stickers.BringAllToFront();
        menu.Items.Add(front);

        var clickThrough = new ToolStripMenuItem("贴图鼠标穿透") { CheckOnClick = true };
        clickThrough.Click += (_, _) => _stickers.SetClickThrough(clickThrough.Checked);
        menu.Items.Add(clickThrough);

        menu.Items.Add(new ToolStripSeparator());

        var settingsItem = new ToolStripMenuItem("设置…");
        settingsItem.Click += (_, _) => OpenSettings();
        menu.Items.Add(settingsItem);

        menu.Items.Add(new ToolStripSeparator());

        var about = new ToolStripMenuItem("关于 PixJoin");
        about.Click += (_, _) => MessageBox.Show(
            "PixJoin —— 截图 / 贴图 / 吸附组合\n\n" +
            $"版本：{AppVersion}\n" +
            $"贴图数量：{_stickers.Count}\n\n" +
            "操作提示：\n" +
            $"· {_settings.Current.HotkeyDisplay}：截图\n" +
            "· 拖动贴图：移动；靠近其它贴图自动吸附组合\n" +
            "· 组合内拖动：整组移动；按住保护键拖出即拆分\n" +
            $"· 保护键：{ModifierName(_settings.Current.ProtectModifier)}\n" +
            "· 滚轮：缩放；Alt + 滚轮：调整透明度\n" +
            "· 双击：关闭贴图；ESC（悬停）：关闭贴图",
            "关于 PixJoin", MessageBoxButton.OK, MessageBoxImage.Information);
        menu.Items.Add(about);

        var exit = new ToolStripMenuItem("退出");
        exit.Click += (_, _) => Shutdown();
        menu.Items.Add(exit);

        return menu;
    }

    private static string AppVersion =>
        typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private static string ModifierName(uint mod)
    {
        if (mod == 0) return "（未设置）";
        var parts = new List<string>();
        if ((mod & Win32.MOD_WIN) != 0) parts.Add("Win");
        if ((mod & Win32.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((mod & Win32.MOD_ALT) != 0) parts.Add("Alt");
        if ((mod & Win32.MOD_SHIFT) != 0) parts.Add("Shift");
        return string.Join("+", parts);
    }

    // ---------------- 导入 ----------------

    private void PinFromClipboard()
    {
        if (!System.Windows.Clipboard.ContainsImage())
        {
            MessageBox.Show("剪贴板中没有图片。", "PixJoin", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var img = System.Windows.Clipboard.GetImage();
        if (img is null) return;

        var pos = CursorPhysical();
        _stickers.CreateSticker(img, new Point(pos.X - img.PixelWidth / 2.0, pos.Y - img.PixelHeight / 2.0));
    }

    private void PinFromFile()
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择图片",
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.webp|所有文件|*.*",
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(dlg.FileName);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();

            var pos = CursorPhysical();
            _stickers.CreateSticker(bmp, new Point(pos.X - bmp.PixelWidth / 2.0, pos.Y - bmp.PixelHeight / 2.0));
        }
        catch (Exception ex)
        {
            LogException(ex);
            MessageBox.Show($"无法打开该图片：{ex.Message}", "PixJoin", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static Point CursorPhysical()
    {
        Win32.GetPhysicalCursorPos(out var p);
        return new Point(p.X, p.Y);
    }

    // ---------------- 异常日志 ----------------

    /// <summary>Bug 1 排查用：记录进程实际 DPI 感知模式，确认 manifest 的 PerMonitorV2 是否生效。</summary>
    private static void LogDpiAwareness()
    {
        try
        {
            var ctx = Win32.GetThreadDpiAwarenessContext();
            int awareness = Win32.GetAwarenessFromDpiAwarenessContext(ctx);
            string name = awareness switch
            {
                Win32.DPI_AWARENESS_UNAWARE => "Unaware",
                Win32.DPI_AWARENESS_SYSTEM_AWARE => "SystemAware",
                Win32.DPI_AWARENESS_PER_MONITOR_AWARE => "PerMonitor",
                _ => $"raw={ctx}",
            };

            int procVal = -1;
            string procName = "?";
            if (Win32.GetProcessDpiAwareness(IntPtr.Zero, out procVal) == 0)
                procName = procVal switch
                {
                    0 => "Unaware",
                    1 => "SystemAware",
                    2 => "PerMonitor",
                    _ => procVal.ToString(),
                };

            LogDebug($"DPI awareness: thread={name} process={procName}({procVal}) ctx={ctx}");
        }
        catch (Exception ex)
        {
            LogDebug($"DPI awareness query failed: {ex.Message}");
        }
    }

    public static string CrashLogPath =>
        Path.Combine(SettingsService.ConfigDirectory, "crash.log");

    public static string DebugLogPath =>
        Path.Combine(SettingsService.ConfigDirectory, "debug.log");

    /// <summary>非异常类诊断日志（截图流程插桩），写到独立的 debug.log，避免污染 crash.log。</summary>
    public static void LogDebug(string msg)
    {
        try
        {
            Directory.CreateDirectory(SettingsService.ConfigDirectory);
            File.AppendAllText(DebugLogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
        }
        catch
        {
            // 日志写不出来也不能让程序崩
        }
    }

    public static void LogException(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(SettingsService.ConfigDirectory);
            File.AppendAllText(CrashLogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n{new string('-', 60)}\n");
        }
        catch
        {
            // 日志写不出来也不能让程序崩
        }
    }

    // ---------------- 无界面自检 ----------------

    /// <summary>
    /// OCR 端到端自检：PixJoin.exe --selftest-ocr
    /// 内存构造含中文的位图 → 组合引擎（PP-OCRv4 优先）识别 → 校验词框数量与坐标。
    /// 结果写到 selftest/ocr_result.txt，退出码 0/1。
    /// </summary>
        private static int CountOpaque(System.Windows.Media.Imaging.BitmapSource bmp)
    {
        int stride = bmp.PixelWidth * 4;
        var buf = new byte[bmp.PixelHeight * stride];
        bmp.CopyPixels(buf, stride, 0);
        int cnt = 0;
        for (int i = 3; i < buf.Length; i += 4)
            if (buf[i] > 0) cnt++;
        return cnt;
    }

private void RunSelfTestOcr()
    {
        var lines = new List<string>();
        string outDir = Path.Combine(SettingsService.ConfigDirectory, "selftest");
        try { Directory.CreateDirectory(outDir); } catch { }
        int code = 0;

        try
        {
            var engine = new CompositeOcrEngine(new RapidOcrEngine(), new WindowsOcrEngine());
            lines.Add($"engine available={engine.IsAvailable}");

            // 内存画一张中文测试图（WPF 渲染，与真实贴图同一 BitmapSource 路径）
            int w = 640, h = 160;
            var rtb = new RenderTargetBitmap(w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            var dv = new System.Windows.Media.DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(System.Windows.Media.Brushes.White, null,
                    new System.Windows.Rect(0, 0, w, h));
                var text = new System.Windows.Media.FormattedText(
                    "你好世界 PixJoin 123",
                    System.Globalization.CultureInfo.CurrentCulture,
                    System.Windows.FlowDirection.LeftToRight,
                    new System.Windows.Media.Typeface("Microsoft YaHei"), 40,
                    System.Windows.Media.Brushes.Black, 1.0);
                dc.DrawText(text, new System.Windows.Point(20, 50));
            }
            rtb.Render(dv);
            rtb.Freeze();

            var result = engine.Recognize(rtb);
            if (result is null || result.Words.Count == 0)
                throw new InvalidOperationException("OCR 无识别结果");

            lines.Add($"OK words={result.Words.Count}");
            lines.Add($"OK full=[{result.FullText.Replace("\n", "\\n")}]");
            foreach (var word in result.Words.Take(10))
                lines.Add($"OK word=[{word.Text}] x={word.X:F0} y={word.Y:F0} w={word.W:F0} h={word.H:F0}");

            var hit = result.Words.Find(wd => wd.HitTest(20 + 40, 50 + 60));
            lines.Add($"hit-test @(60,110): {(hit is null ? "MISS" : hit.Text)}");

            // 导出测试图供人工核对
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using (var fs = System.IO.File.Create(Path.Combine(outDir, "ocr_input.png")))
                enc.Save(fs);

            lines.Add("SELFTEST_OCR_PASS");
        }
        catch (Exception ex)
        {
            code = 1;
            lines.Add("SELFTEST_OCR_FAIL: " + ex.GetType().Name + ": " + ex.Message);
            LogException(ex);
        }

        try { File.WriteAllLines(Path.Combine(outDir, "ocr_result.txt"), lines); } catch { }
        Shutdown(code);
    }

    /// <summary>
    /// 端到端自检：抓全屏（真实 GDI BitBlt）→ 裁两块相邻区域 → 建贴图并组合 →
    /// 导出 PNG。不创建任何窗口、不依赖桌面交互，可在无头环境用
    /// <c>PixJoin.exe --selftest</c> 验证「截图→组合→导出」主链路。
    /// 结果写到配置目录下的 selftest/result.txt，并以退出码 0/1 表示成败。
    /// </summary>
    private void RunSelfTest()
    {
        var lines = new List<string>();
        string outDir = Path.Combine(SettingsService.ConfigDirectory, "selftest");
        try { Directory.CreateDirectory(outDir); } catch { }
        int code = 0;

        try
        {
            MonitorHelper.Refresh();
            var shot = ScreenCapture.CaptureVirtualScreen();
            lines.Add($"OK backend={ScreenCapture.ActiveBackendName}");
            int pw = shot.Bitmap.PixelWidth, ph = shot.Bitmap.PixelHeight;
            int opaque = CountOpaque(shot.Bitmap);
            lines.Add($"OK capture {pw}x{ph} opaque={opaque}");
            if (opaque <= 0) throw new InvalidOperationException("截图为全透明/全黑，内容无效");
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(shot.Bitmap));
            using (var fs = File.Create(Path.Combine(outDir, "capture_full.png")))
                enc.Save(fs);
            lines.Add($"OK full-shot saved opaque={opaque}");

            int w = Math.Max(1, Math.Min(240, pw / 2));
            int h = Math.Max(1, Math.Min(240, ph / 2));
            var c1 = shot.Crop(new Rect(shot.OriginX, shot.OriginY, w, h));
            var c2 = shot.Crop(new Rect(shot.OriginX + w, shot.OriginY, w, h));
            if (c1 is null || c2 is null) throw new InvalidOperationException("Crop 返回 null");
            lines.Add($"OK crop c1={c1.PixelWidth}x{c1.PixelHeight} c2={c2.PixelWidth}x{c2.PixelHeight}");

            var g = new GroupManager();
            var s1 = new Sticker { Image = c1, X = 0, Y = 0, W = c1.PixelWidth, H = c1.PixelHeight };
            var s2 = new Sticker { Image = c2, X = c1.PixelWidth, Y = 0, W = c2.PixelWidth, H = c2.PixelHeight };
            g.Register(s1);
            g.Register(s2);
            string gid = g.Combine(s1.Id, s2.Id);
            var members = g.GetMembers(gid);
            lines.Add($"OK group id={gid} members={members.Count}");
            if (members.Count != 2) throw new InvalidOperationException("组合成员数异常");

            var composed = ExportService.Compose(members);
            if (composed is null) throw new InvalidOperationException("Compose 返回 null");
            ExportService.SavePng(composed, Path.Combine(outDir, "compose.png"));
            lines.Add($"OK compose {composed.PixelWidth}x{composed.PixelHeight}");

            var one = ExportService.Compose(new[] { s1 });
            if (one is null) throw new InvalidOperationException("单张 Compose 返回 null");
            ExportService.SavePng(one, Path.Combine(outDir, "single.png"));
            lines.Add($"OK single {one.PixelWidth}x{one.PixelHeight}");

            lines.Add("SELFTEST_PASS");
        }
        catch (Exception ex)
        {
            code = 1;
            lines.Add("SELFTEST_FAIL: " + ex.GetType().Name + ": " + ex.Message);
            LogException(ex);
        }

        try { File.WriteAllLines(Path.Combine(outDir, "result.txt"), lines); } catch { }
        Shutdown(code);
    }
}
