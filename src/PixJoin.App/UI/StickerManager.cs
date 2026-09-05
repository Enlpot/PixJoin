using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using PixJoin.App.Imaging;
using PixJoin.App.Native;
using PixJoin.Core.Models;
using PixJoin.Core.Services;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;

namespace PixJoin.App.UI;

/// <summary>
/// 贴图与组合的中枢：持有贴图窗口、驱动拖拽 / 吸附 / 拆分，并负责导出。
/// 窗口层只上报「开始拖动 / 拖动中 / 松手」，所有判定都在这里完成 —— 与组合逻辑解耦。
/// </summary>
public sealed class StickerManager
{
    private readonly GroupManager _groups = new();
    private readonly Dictionary<string, StickerWindow> _windows = new(StringComparer.Ordinal);
    private readonly SettingsService _settings;
    private readonly IOcrEngine? _ocr;
    private GuideLayer _guideLayer;
    private DragSession? _drag;
    private bool _shuttingDown;
    private StickerWindow? _hiddenExcept;
    private bool _clickThrough;

    public StickerManager(SettingsService settings, IOcrEngine? ocr = null)
    {
        _settings = settings;
        _ocr = ocr ?? new WindowsOcrEngine();
        _guideLayer = new GuideLayer();
        _guideLayer.Show();
        _groups.Changed += OnGroupsChanged;
    }

    private void OnGroupsChanged(object? sender, EventArgs e) => RefreshGroupVisuals();

    public bool IsDragging => _drag is not null;

    public int Count => _windows.Count;

    public IReadOnlyCollection<StickerWindow> Windows => _windows.Values;

    /// <summary>全局 OCR 开关 + 引擎可用（缺语言包时自动禁用）。</summary>
    public bool OcrEnabled => _settings.Current.OcrEnabled && (_ocr?.IsAvailable ?? false);

    public bool HasHiddenStickers => _hiddenExcept is not null;

    // ---------------- 生命周期 ----------------

    /// <summary>新建一张贴图。坐标与尺寸均为物理像素。</summary>
    public StickerWindow? CreateSticker(BitmapSource image, Point physicalTopLeft)
    {
        int max = Math.Max(1, _settings.Current.MaxStickers);
        if (_windows.Count >= max)
        {
            MessageBox.Show($"贴图数量已达上限（{max} 张）。\n请先关闭一些贴图，或在设置中调高上限。",
                "PixJoin", MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }

        if (image.CanFreeze) image.Freeze();

        var monitor = MonitorHelper.MonitorAtPhysicalPoint(physicalTopLeft.X, physicalTopLeft.Y);
        double w = image.PixelWidth;
        double h = image.PixelHeight;

        // 超出显示器时等比缩小到可容纳（保证整张图可见）。
        // 【防御】monitor.Width/Height 任一为 0（GetMonitorInfo 返回失败 / 多屏边界）时 f 会变 0 → 贴图 8×8；
        // 这里同时拦 IsFinite + 退化值，并兜底回原图尺寸，杜绝"贴图缩没了"。
        double maxW = monitor.Width * 0.9;
        double maxH = monitor.Height * 0.9;
        if (w > maxW || h > maxH)
        {
            double f = Math.Min(maxW / w, maxH / h);
            if (!double.IsFinite(f) || f <= 0) f = 1;   // monitor 异常时不要缩小成 0
            w *= f; h *= f;
        }
        if (w < 1 || h < 1) { w = image.PixelWidth; h = image.PixelHeight; }  // 最终兜底：贴图最小 1px

        var sticker = new Sticker
        {
            Image = image,
            X = physicalTopLeft.X,
            Y = physicalTopLeft.Y,
            W = w,
            H = h,
            Opacity = _settings.Current.DefaultOpacity,
        };

        _groups.Register(sticker);

        var window = new StickerWindow(sticker, this);
        _windows[sticker.Id] = window;
        window.Show();
        window.RefreshFrame();
        RefreshGroupVisuals();
        KickOffOcr(window);
        return window;
    }

    // ---------------- OCR 文字识别 ----------------

    /// <summary>贴图后后台识别整张图；结果挂到 Sticker.OcrWords（词框为图片物理像素）。</summary>
    private void KickOffOcr(StickerWindow window)
    {
        if (!OcrEnabled) return;

        var sticker = window.Sticker;
        var engine = _ocr!;
        Task.Run(() =>
        {
            try
            {
                var result = engine.Recognize(sticker.Image);
                if (result is null || result.Words.Count == 0) return;
                Application.Current.Dispatcher.Invoke(() =>
                {
                    if (!_windows.ContainsKey(sticker.Id)) return;   // 窗口已关闭
                    sticker.OcrWords = result.Words;
                    window.OnOcrReady();
                });
            }
            catch
            {
                // OCR 失败不影响贴图本身
            }
        });
    }

    /// <summary>复制整张贴图的全部识别文字。</summary>
    public void CopyAllOcrText(StickerWindow window)
    {
        var words = window.Sticker.OcrWords;
        if (words is null || words.Count == 0) return;
        SetTextOrWarn(OcrSelection.BuildText(words, 0, words.Count - 1));
    }

    /// <summary>复制选中的一段文字（词索引范围，文档顺序）。</summary>
    public void CopyOcrSelection(StickerWindow window, int start, int end)
    {
        var words = window.Sticker.OcrWords;
        if (words is null || words.Count == 0) return;
        SetTextOrWarn(OcrSelection.BuildText(words, start, end));
    }

    /// <summary>Shift+C 全局快捷键：复制鼠标悬停贴图的全部识别文字。返回是否已处理。</summary>
    public bool CopyAllTextUnderCursor()
    {
        if (!OcrEnabled) return false;
        Win32.GetPhysicalCursorPos(out var p);
        foreach (var w in _windows.Values)
        {
            if (w.Handle == IntPtr.Zero || !Win32.GetWindowRect(w.Handle, out var r)) continue;
            if (p.X < r.Left || p.X >= r.Right || p.Y < r.Top || p.Y >= r.Bottom) continue;
            var words = w.Sticker.OcrWords;
            if (words is null || words.Count == 0) return false;
            if (!ClipboardService.TrySetText(OcrSelection.BuildText(words, 0, words.Count - 1)))
            {
                WarnClipboardBusy();
                return true;   // 仍拦截 Shift+C，避免按键穿透到下层应用
            }
            return true;
        }
        return false;
    }

    /// <summary>写文本到剪贴板；失败时弹提示（剪贴板被占用）。</summary>
    private static void SetTextOrWarn(string text)
    {
        if (!ClipboardService.TrySetText(text)) WarnClipboardBusy();
    }

    private static void WarnClipboardBusy() =>
        System.Windows.MessageBox.Show("复制失败：剪贴板正被其它程序占用，请稍后重试。",
            "PixJoin", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);

    // ---------------- 隐藏其他 / 鼠标穿透 ----------------

    /// <summary>隐藏其它贴图；再次调用恢复全部（切换语义）。</summary>
    public void ToggleHideOthers(StickerWindow keep)
    {
        if (_hiddenExcept is not null) { ShowAllStickers(); return; }
        _hiddenExcept = keep;
        foreach (var w in _windows.Values)
            if (!ReferenceEquals(w, keep)) w.Visibility = Visibility.Hidden;
    }

    public void ShowAllStickers()
    {
        if (_hiddenExcept is null) return;
        _hiddenExcept = null;
        foreach (var w in _windows.Values) w.Visibility = Visibility.Visible;
    }

    /// <summary>托盘全局开关：贴图鼠标穿透（点击穿过到下层窗口）。</summary>
    public void SetClickThrough(bool on)
    {
        _clickThrough = on;
        foreach (var w in _windows.Values) w.SetClickThrough(on);
    }

    public void OnWindowClosed(StickerWindow window)
    {
        var id = window.Sticker.Id;
        if (!_windows.ContainsKey(id)) return;
        _windows.Remove(id);
        if (ReferenceEquals(window, _hiddenExcept)) ShowAllStickers();   // 隐藏源的贴图被关 → 恢复显示
        _groups.Unregister(id);   // 先摘除组合关系，成员不足时自动解散
        RefreshGroupVisuals();
    }

    public void CloseSticker(StickerWindow window) => window.Close();

    public void CloseAll()
    {
        foreach (var w in _windows.Values.ToList()) w.Close();
        _groups.Clear();
        _guideLayer?.ClearAll();
    }

    // ---------------- 首次使用确认（双击关闭 / 滚轮缩放 / ESC 关闭） ----------------

    /// <summary>首次使用确认弹窗进行中（供全局 ESC 钩子判断：弹窗期间放行 ESC，避免被钩子重复拦截）。</summary>
    public bool FirstUsePromptActive { get; private set; }

    /// <summary>
    /// 首次触发某交互功能时弹窗询问「是否保持启用」，选择后写入配置；之后按配置直接执行。
    /// 返回 true 表示应执行该功能（用户保持启用），false 表示禁用。
    /// </summary>
    private bool GateFirstUse(Window owner, string title, string message,
        Func<AppSettings, bool> prompted, Action<AppSettings> markPrompted,
        Func<AppSettings, bool> enabled, Action<AppSettings, bool> setEnabled)
    {
        var s = _settings.Current;
        if (prompted(s)) return enabled(s);

        FirstUsePromptActive = true;
        try
        {
            bool keep = MessageBox.Show(owner, message, title,
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
            markPrompted(s);
            setEnabled(s, keep);
            _settings.Save();
            return keep;
        }
        finally
        {
            FirstUsePromptActive = false;
        }
    }

    public bool ConfirmDoubleClickClose(StickerWindow window) => GateFirstUse(window,
        "双击关闭贴图",
        "双击贴图将关闭该贴图。\n\n是否保持启用此功能？\n（选择“是”：保持启用，以后不再提示；选择“否”：禁用）",
        s => s.DoubleClickClosePrompted, s => s.DoubleClickClosePrompted = true,
        s => s.DoubleClickCloseEnabled, (s, v) => s.DoubleClickCloseEnabled = v);

    public bool ConfirmWheelZoom(StickerWindow window) => GateFirstUse(window,
        "滚轮缩放贴图",
        "直接滚动滚轮将缩放贴图（以贴图中心为锚点）。\n\n是否保持启用此功能？\n（选择“是”：保持启用，以后不再提示；选择“否”：禁用）",
        s => s.WheelZoomPrompted, s => s.WheelZoomPrompted = true,
        s => s.WheelZoomEnabled, (s, v) => s.WheelZoomEnabled = v);

    public bool ConfirmEscClose(StickerWindow window) => GateFirstUse(window,
        "ESC 关闭贴图",
        "鼠标悬停在贴图上时，按 ESC 将关闭该贴图。\n\n是否保持启用此功能？\n（选择“是”：保持启用，以后不再提示；选择“否”：禁用）",
        s => s.EscClosePrompted, s => s.EscClosePrompted = true,
        s => s.EscCloseEnabled, (s, v) => s.EscCloseEnabled = v);

    /// <summary>鼠标坐标命中的贴图：按「ESC 关闭」语义处理（含首次提示），返回是否已拦截 ESC。</summary>
    public bool TryCloseUnderCursor()
    {
        Win32.GetPhysicalCursorPos(out var p);
        foreach (var w in _windows.Values)
        {
            if (w.Handle == IntPtr.Zero || !Win32.GetWindowRect(w.Handle, out var r)) continue;
            if (p.X < r.Left || p.X >= r.Right || p.Y < r.Top || p.Y >= r.Bottom) continue;

            // 有活动文字选区：ESC 先取消选择，不关闭贴图
            if (w.CancelTextSelectionIfActive()) return true;

            if (!ConfirmEscClose(w)) return false;   // 功能禁用 → 放行 ESC，不拦截
            CloseSticker(w);
            return true;
        }
        return false;
    }

    /// <summary>显式收尾：解绑组合事件、关闭反馈层并清空所有窗口。必须在 App 退出流程中先于其它资源释放调用。</summary>
    public void Shutdown()
    {
        if (_shuttingDown) return;
        _shuttingDown = true;

        _groups.Changed -= OnGroupsChanged;

        foreach (var w in _windows.Values.ToList()) w.Close();
        _windows.Clear();
        _groups.Clear();

        _guideLayer?.Close();
        _guideLayer = null;
    }

    // ---------------- 拖拽 / 吸附 / 拆分 ----------------

    /// <summary>保护键（默认 Ctrl）是否被按住。</summary>
    private bool ProtectPressed
    {
        get
        {
            uint m = _settings.Current.ProtectModifier;
            if (m == 0) return false;  // 未配置保护键 → 禁止拖拽拆分，只能走右键菜单

            if ((m & Win32.MOD_CONTROL) != 0 && !Win32.IsKeyDown(Win32.VK_CONTROL)) return false;
            if ((m & Win32.MOD_SHIFT) != 0 && !Win32.IsKeyDown(Win32.VK_SHIFT)) return false;
            if ((m & Win32.MOD_ALT) != 0 && !Win32.IsKeyDown(Win32.VK_MENU)) return false;
            if ((m & Win32.MOD_WIN) != 0 && !Win32.IsKeyDown(Win32.VK_LWIN)) return false;
            return true;
        }
    }

    public void BeginDrag(StickerWindow window, Point _)
    {
        var s = window.Sticker;
        var session = new DragSession
        {
            Source = window,
            StartCursor = CursorPhysical(),
            GroupId = s.GroupId,
            DetachMode = s.GroupId is not null && ProtectPressed,
        };

        session.Moving = (s.GroupId is not null && !session.DetachMode)
            ? _groups.GetMembers(s.GroupId).Select(m => _windows[m.Id]).ToList()
            : new List<StickerWindow> { window };

        SnapshotOrigins(session);
        _drag = session;
    }

    public void UpdateDrag(StickerWindow window)
    {
        if (_drag is null || !ReferenceEquals(_drag.Source, window)) return;

        var s = window.Sticker;
        var cursor = CursorPhysical();

        // 拖动过程中保护键可能按下 / 松开，实时切换模式
        if (s.GroupId is not null && !_drag.Detached)
        {
            bool protect = ProtectPressed;
            if (protect && !_drag.DetachMode)
            {
                _drag.DetachMode = true;
                _drag.Moving = new List<StickerWindow> { window };
                _drag.StartCursor = cursor;
                SnapshotOrigins(_drag);
            }
            else if (!protect && _drag.DetachMode)
            {
                _drag.DetachMode = false;
                _drag.Moving = _groups.GetMembers(s.GroupId!).Select(m => _windows[m.Id]).ToList();
                _drag.StartCursor = cursor;
                SnapshotOrigins(_drag);
            }
        }

        double dx = cursor.X - _drag.StartCursor.X;
        double dy = cursor.Y - _drag.StartCursor.Y;

        // 保护键拆分：累计位移超过阈值才真正脱离
        if (_drag.DetachMode && !_drag.Detached && s.GroupId is not null)
        {
            if (Math.Sqrt(dx * dx + dy * dy) > _settings.Current.DetachDistance)
            {
                _groups.Detach(s.Id);                 // 组合成员 <2 时会自动解散
                _drag.Detached = true;
                _drag.DetachMode = true;
                _drag.Moving = new List<StickerWindow> { window };
                SnapshotOrigins(_drag);
                _drag.StartCursor = cursor;
                dx = 0; dy = 0;
                window.RefreshFrame();
            }
        }

        ApplyDelta(_drag, dx, dy);

        // ---------- 吸附判定 ----------
        var opt = new SnapEngine.Options(_settings.Current.SnapDistance, _settings.Current.AlignTolerance);
        var assembly = GroupManager.UnionBounds(_drag.Moving.Select(m => m.Sticker.Bounds));
        var snap = SnapEngine.Resolve(
            assembly,
            s.Id,
            _drag.Detached ? null : s.GroupId,
            _groups.Stickers,
            _groups.BoundsOfGroup,
            opt);

        _drag.Snap = snap;
        if (snap is not null)
        {
            ApplyDelta(_drag, dx + snap.Dx, dy + snap.Dy);
            _guideLayer.ShowSnap(
                new Rect(assembly.Left + snap.Dx, assembly.Top + snap.Dy, assembly.Width, assembly.Height),
                snap.Target.Bounds,
                snap.Guides);
        }
        else
        {
            // 无贴合成组候选时，尝试「边缘对齐」磁吸（同侧边对齐，不成组）
            var align = SnapEngine.ResolveAlign(
                assembly, s.Id, _drag.Detached ? null : s.GroupId,
                _groups.Stickers, _groups.BoundsOfGroup, opt);
            _drag.Align = align;
            if (align is not null)
            {
                ApplyDelta(_drag, dx + align.Dx, dy + align.Dy);
                _guideLayer.ShowSnap(
                    new Rect(assembly.Left + align.Dx, assembly.Top + align.Dy, assembly.Width, assembly.Height),
                    align.Target.Bounds,
                    align.Guides);
            }
            else
            {
                _guideLayer.ClearSnap();
            }
        }

        foreach (var m in _drag.Moving) m.ApplyGeometry();
        RefreshGroupOutlines();
    }

    public void EndDrag(StickerWindow window)
    {
        if (_drag is null || !ReferenceEquals(_drag.Source, window)) return;

        var session = _drag;
        _drag = null;

        foreach (var m in session.Moving) m.ApplyGeometry();

        // 有吸附目标 → 建立 / 加入组合
        if (session.Snap is { } snap)
            _groups.Combine(window.Sticker.Id, snap.Target.Id);

        _guideLayer.ClearSnap();
        RefreshGroupVisuals();
    }

    private static void SnapshotOrigins(DragSession session)
    {
        session.Origins.Clear();
        foreach (var w in session.Moving)
            session.Origins[w.Sticker.Id] = (w.Sticker.X, w.Sticker.Y);
    }

    private static void ApplyDelta(DragSession session, double dx, double dy)
    {
        foreach (var w in session.Moving)
        {
            if (!session.Origins.TryGetValue(w.Sticker.Id, out var o)) continue;
            w.Sticker.X = o.X + dx;
            w.Sticker.Y = o.Y + dy;
        }
    }

    // ---------------- 组合操作 ----------------

    public void DetachSticker(StickerWindow window)
    {
        _groups.Detach(window.Sticker.Id);
        window.RefreshFrame();
        RefreshGroupVisuals();
    }

    public void DissolveGroup(StickerWindow window)
    {
        if (window.Sticker.GroupId is not { } gid) return;
        _groups.Dissolve(gid);
        RefreshGroupVisuals();
    }

    public void OnStickerResized(StickerWindow window)
    {
        window.ApplyGeometry();
        _guideLayer.ClearSnap();   // 缩放结束：清除缩放对齐参考线
        RefreshGroupOutlines();
    }

    // ---------------- 缩放对齐 / 滚轮缩放锚点 ----------------

    /// <summary>
    /// 四角缩放时的边缘对齐吸附：固定对角不动，移动边接近其它贴图边缘时自动吸附。
    /// 返回对齐后的尺寸（调用方据 left/top 更新位置）。
    /// </summary>
    public (double nw, double nh) ApplyResizeAlign(StickerWindow window, double nw, double nh, bool left, bool top)
    {
        var s = window.Sticker;
        var (ox, oy, ow, oh) = window.GeometrySnapshot;
        double nx = left ? ox + ow - nw : ox;
        double ny = top ? oy + oh - nh : oy;
        var bounds = new Rect(nx, ny, nw, nh);
        var fixedCorner = new Point(left ? ox + ow : ox, top ? oy + oh : oy);

        var opt = new SnapEngine.Options(_settings.Current.SnapDistance, _settings.Current.AlignTolerance);
        // 缩放对齐包含同组贴图：组合内缩放单张时也能贴着成员边缘
        var align = SnapEngine.ResolveResizeAlign(bounds, fixedCorner, s.Id, selfGroupId: null,
            _groups.Stickers, _groups.BoundsOfGroup, opt);

        if (align is null)
        {
            _guideLayer.ClearSnap();
            return (nw, nh);
        }

        // left=true（Left 移动，Right 固定）：nw -= Dx；left=false（Right 移动）：nw += Dx。垂直同理。
        nw += left ? -align.Dx : align.Dx;
        nh += top ? -align.Dy : align.Dy;
        nw = Math.Max(16, nw);
        nh = Math.Max(16, nh);

        var moved = new Rect(left ? ox + ow - nw : ox, top ? oy + oh - nh : oy, nw, nh);
        _guideLayer.ShowSnap(moved, align.Target.Bounds, align.Guides);
        return (nw, nh);
    }

    /// <summary>
    /// 滚轮缩放：等比缩放贴图。锚点规则——
    ///   组合 / 吸附状态：只有一条边吸附时，以该吸附边为锚（贴着边缩放）；
    ///   无吸附边（独立贴图）或两条及以上边吸附（四周/两边都有）时，以中心为锚。
    /// </summary>
    public void ApplyWheelZoom(StickerWindow window, double f)
    {
        var s = window.Sticker;
        double nw = Math.Max(16, s.W * f);
        double nh = Math.Max(16, s.H * f);

        var edges = DetectAnchorEdges(s);
        int edgeCount = (edges.Left ? 1 : 0) + (edges.Right ? 1 : 0) + (edges.Top ? 1 : 0) + (edges.Bottom ? 1 : 0);

        double cx = s.X + s.W / 2;
        double cy = s.Y + s.H / 2;

        if (edgeCount >= 2 || edgeCount == 0)
        {
            // 中心缩放（独立，或四周/两边都有吸附）
            s.X = cx - nw / 2;
            s.Y = cy - nh / 2;
        }
        else if (edges.Left)
        {
            s.Y = cy - nh / 2;          // 左边缘不动，向右扩展
        }
        else if (edges.Right)
        {
            s.X = s.X + s.W - nw;       // 右边缘不动，向左扩展
            s.Y = cy - nh / 2;
        }
        else if (edges.Top)
        {
            s.X = cx - nw / 2;          // 上边缘不动，向下扩展
        }
        else if (edges.Bottom)
        {
            s.X = cx - nw / 2;
            s.Y = s.Y + s.H - nh;       // 下边缘不动，向上扩展
        }

        s.W = nw;
        s.H = nh;
        window.ApplyGeometry();
        OnStickerResized(window);
    }

    /// <summary>检测贴图哪些边与其它贴图「贴合」（距离 ≤2px 且正交投影重叠），作为滚轮缩放的锚定边。</summary>
    private (bool Left, bool Right, bool Top, bool Bottom) DetectAnchorEdges(Sticker s)
    {
        const double tol = 2.0;
        bool l = false, r = false, t = false, b = false;
        var sb = s.Bounds;

        foreach (var o in _groups.Stickers)
        {
            if (o.Id == s.Id) continue;
            var ob = o.Bounds;

            double vOverlap = Math.Min(sb.Bottom, ob.Bottom) - Math.Max(sb.Top, ob.Top);
            if (vOverlap >= 0)
            {
                if (Math.Abs(sb.Right - ob.Left) <= tol) r = true;
                if (Math.Abs(sb.Left - ob.Right) <= tol) l = true;
            }
            double hOverlap = Math.Min(sb.Right, ob.Right) - Math.Max(sb.Left, ob.Left);
            if (hOverlap >= 0)
            {
                if (Math.Abs(sb.Top - ob.Bottom) <= tol) t = true;
                if (Math.Abs(sb.Bottom - ob.Top) <= tol) b = true;
            }
        }
        return (l, r, t, b);
    }

    // ---------------- 导出 ----------------

    private IReadOnlyList<Sticker> ExportTargets(StickerWindow window)
    {
        var s = window.Sticker;
        return s.GroupId is { } gid && _groups.GetGroup(gid) is not null
            ? _groups.GetMembers(gid)
            : new List<Sticker> { s };
    }

    public void CopyStickerOrGroup(StickerWindow window)
    {
        var opt = new ExportService.ExportOptions(_settings.Current.TransparentBackground, _settings.Current.AutoTrim);
        var bmp = ExportService.Compose(ExportTargets(window), opt);
        if (bmp is null) return;
        if (!ClipboardService.TrySetImage(bmp)) WarnClipboardBusy();
    }

    public void SaveStickerOrGroup(StickerWindow window)
    {
        var opt = new ExportService.ExportOptions(_settings.Current.TransparentBackground, _settings.Current.AutoTrim);
        var bmp = ExportService.Compose(ExportTargets(window), opt);
        if (bmp is null) return;

        var dlg = new SaveFileDialog
        {
            Title = "保存图片",
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

    // ---------------- 可视化 ----------------

    private void RefreshGroupVisuals()
    {
        foreach (var w in _windows.Values) w.RefreshFrame();
        RefreshGroupOutlines();
    }

    /// <summary>只重绘组合体外框（拖拽过程中高频调用，避免不必要的窗口刷新）。</summary>
    private void RefreshGroupOutlines()
    {
        if (_guideLayer is null) return;
        if (!_settings.Current.ShowGroupOutline)
        {
            _guideLayer.UpdateGroups(Array.Empty<(Rect, int)>());
            return;
        }

        var outlines = new List<(Rect Bounds, int Count)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var w in _windows.Values)
        {
            var gid = w.Sticker.GroupId;
            if (gid is null || !seen.Add(gid)) continue;

            var bounds = _groups.GetGroupBounds(gid);
            if (bounds.IsEmpty) continue;
            outlines.Add((bounds, _groups.GetMembers(gid).Count));
        }

        _guideLayer.UpdateGroups(outlines);
    }

    /// <summary>显示 / 隐藏反馈层。截图时隐藏，避免组合外框干扰选区。</summary>
    public void SetGuideVisible(bool visible)
    {
        // 退出 / 窗口已销毁时绝不触碰已关闭窗口的可见性，否则触发 VerifyCanShow 崩溃
        if (_shuttingDown || _guideLayer is null || !_guideLayer.IsLoaded) return;
        _guideLayer.Visibility = visible ? Visibility.Visible : Visibility.Hidden;
        if (visible)
        {
            _guideLayer.Place();
            RefreshGroupOutlines();
        }
    }

    /// <summary>把所有贴图窗口重新置顶（被其它窗口盖住时自救）。</summary>
    public void BringAllToFront()
    {
        foreach (var w in _windows.Values) w.ApplyGeometry();
    }

    /// <summary>显示器布局变化时重新校准所有窗口位置。</summary>
    public void RefreshDisplayLayout()
    {
        MonitorHelper.Refresh();
        _guideLayer.Place();
        foreach (var w in _windows.Values) w.ApplyGeometry();
        RefreshGroupOutlines();
    }

    private static Point CursorPhysical()
    {
        Win32.GetPhysicalCursorPos(out var p);
        return new Point(p.X, p.Y);
    }

    private sealed class DragSession
    {
        public StickerWindow Source { get; init; } = null!;
        public Point StartCursor { get; set; }
        public string? GroupId { get; init; }
        public bool DetachMode { get; set; }
        public bool Detached { get; set; }
        public List<StickerWindow> Moving { get; set; } = new();
        public Dictionary<string, (double X, double Y)> Origins { get; } = new();
        public SnapResult? Snap { get; set; }
        public SnapResult? Align { get; set; }
    }
}
