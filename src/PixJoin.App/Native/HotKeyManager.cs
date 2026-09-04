using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Interop;

namespace PixJoin.App.Native;

/// <summary>
/// 系统级全局快捷键（RegisterHotKey）。
/// 用一个隐藏的 HwndSource 作为消息接收窗口，注册失败即视为「被占用」并回报调用方。
/// </summary>
public sealed class HotKeyManager : IDisposable
{
    private readonly HwndSource _source;
    private readonly Dictionary<int, (uint Modifiers, uint Vk)> _registered = new();
    private bool _disposed;

    /// <summary>快捷键被按下（参数为注册 id）。</summary>
    public event EventHandler<int>? HotKeyPressed;

    public HotKeyManager()
    {
        var parms = new HwndSourceParameters("PixJoin.HotKeySink")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ExtendedWindowStyle = Win32.WS_EX_TOOLWINDOW,
        };
        _source = new HwndSource(parms);
        _source.AddHook(WndProc);
    }

    /// <summary>
    /// 注册快捷键。返回是否成功；失败通常意味着该组合已被其它程序占用。
    /// </summary>
    public bool Register(int id, uint modifiers, uint vk)
    {
        if (_registered.ContainsKey(id)) Unregister(id);

        // MOD_NOREPEAT：按住不放时只触发一次
        bool ok = Win32.RegisterHotKey(_source.Handle, id, modifiers | Win32.MOD_NOREPEAT, vk);
        if (!ok) ok = Win32.RegisterHotKey(_source.Handle, id, modifiers, vk); // 老系统兜底

        if (ok) _registered[id] = (modifiers, vk);
        return ok;
    }

    public void Unregister(int id)
    {
        if (_registered.Remove(id)) Win32.UnregisterHotKey(_source.Handle, id);
    }

    public void UnregisterAll()
    {
        foreach (var id in _registered.Keys.ToList()) Unregister(id);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32.WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (_registered.ContainsKey(id))
            {
                handled = true;
                HotKeyPressed?.Invoke(this, id);
            }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        UnregisterAll();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
