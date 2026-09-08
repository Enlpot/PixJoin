using System;
using System.Windows.Controls;
using System.Windows.Media;
using PixJoin.Core.Models;

namespace PixJoin.Core.Services;

/// <summary>
/// 标注样式菜单（截图与贴图共用）：线型菜单（实线/虚线）+ 箭头菜单（实心/V形/双头 + 线型）。
/// 点击通过回调把新样式交给调用方（截图侧作用于选中标注 + 默认样式；贴图侧同样）。
/// </summary>
public static class AnnotationStyleMenus
{
    /// <summary>标注颜色板（截图/贴图/浮动条共用，8 色，与截图侧历史一致）。</summary>
    public static readonly Color[] Colors =
    {
        Color.FromRgb(0xE5, 0x39, 0x35),  // 红
        Color.FromRgb(0xF5, 0x6C, 0x2C),  // 橙
        Color.FromRgb(0xF7, 0xC9, 0x48),  // 黄
        Color.FromRgb(0x34, 0xC7, 0x59),  // 绿
        Color.FromRgb(0x29, 0x9D, 0xF0),  // 蓝
        Color.FromRgb(0x9C, 0x27, 0xB0),  // 紫
        Color.FromRgb(0xF0, 0x62, 0x92),  // 粉
        Color.FromRgb(0x60, 0x70, 0x8B),  // 灰
    };

    /// <summary>线型菜单：实线 / 虚线。onApply(dashed)。</summary>
    public static ContextMenu BuildLineStyleMenu(bool currentDashed, Action<bool> onApply)
    {
        var menu = new ContextMenu();
        var solid = new MenuItem { Header = "实线", IsCheckable = true, IsChecked = !currentDashed };
        solid.Click += (_, _) => onApply(false);
        var dash = new MenuItem { Header = "虚线", IsCheckable = true, IsChecked = currentDashed };
        dash.Click += (_, _) => onApply(true);
        menu.Items.Add(solid);
        menu.Items.Add(dash);
        return menu;
    }

    /// <summary>箭头菜单：样式 3 种（实心 / V 形 / 双头）+ 分隔 + 线型 2 种。onApply(style, dashed)。</summary>
    public static ContextMenu BuildArrowMenu(ArrowStyle currentStyle, bool currentDashed, Action<ArrowStyle, bool> onApply)
    {
        var menu = new ContextMenu();
        foreach (var (style, label) in new[]
        {
            (ArrowStyle.Solid, "实心箭头"),
            (ArrowStyle.Line, "V 形箭头"),
            (ArrowStyle.Double, "双头箭头"),
        })
        {
            var mi = new MenuItem { Header = label, IsCheckable = true, IsChecked = currentStyle == style };
            var s = style;
            mi.Click += (_, _) => onApply(s, currentDashed);
            menu.Items.Add(mi);
        }
        menu.Items.Add(new Separator());
        var ls = new MenuItem { Header = "实线", IsCheckable = true, IsChecked = !currentDashed };
        ls.Click += (_, _) => onApply(currentStyle, false);
        var ld = new MenuItem { Header = "虚线", IsCheckable = true, IsChecked = currentDashed };
        ld.Click += (_, _) => onApply(currentStyle, true);
        menu.Items.Add(ls);
        menu.Items.Add(ld);
        return menu;
    }
}
