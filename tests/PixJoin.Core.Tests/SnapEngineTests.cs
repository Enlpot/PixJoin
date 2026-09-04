using PixJoin.Core.Models;
using PixJoin.Core.Services;

namespace PixJoin.Core.Tests;

internal static class SnapEngineTests
{
    private static readonly SnapEngine.Options Opt = new(SnapDistance: 10, AlignTolerance: 5);

    public static void Run()
    {
        Check.Section("SnapEngine 吸附判定");

        // 1. 水平贴合：A 右边缘 95，B 左边缘 100，间隙 5 → 应吸附，dx = +5
        {
            var a = new Rect(0, 0, 95, 100);
            var b = TestBitmap.Sticker(100, 0, 100, 100);
            var r = SnapEngine.Resolve(a, "self", null, new[] { b }, null, Opt);
            Check.True(r is not null, "间隙 5px（≤10）应判定可吸附");
            Check.Near(5, r!.Dx, "水平吸附位移 Dx");
            Check.Near(100, r.PreviewBounds.Right, "吸附后右边缘应贴合目标左边缘");
        }

        // 2. 间隙 11px → 超出阈值，不吸附
        {
            var a = new Rect(0, 0, 89, 100);
            var b = TestBitmap.Sticker(100, 0, 100, 100);
            var r = SnapEngine.Resolve(a, "self", null, new[] { b }, null, Opt);
            Check.True(r is null, "间隙 11px（>10）不应吸附");
        }

        // 3. 垂直贴合：A 在 B 上方，间隙 8 → dy = +8
        {
            var a = new Rect(0, 0, 100, 92);
            var b = TestBitmap.Sticker(0, 100, 100, 100);
            var r = SnapEngine.Resolve(a, "self", null, new[] { b }, null, Opt);
            Check.True(r is not null, "垂直间隙 8px 应吸附");
            Check.Near(8, r!.Dy, "垂直吸附位移 Dy");
        }

        // 4. 对齐容差内：上边偏差 3px（≤5）→ 顺带对齐且产生参考线
        {
            var a = new Rect(0, 3, 95, 100);
            var b = TestBitmap.Sticker(100, 0, 100, 100);
            var r = SnapEngine.Resolve(a, "self", null, new[] { b }, null, Opt);
            Check.True(r is not null, "偏差 3px 应仍可吸附");
            Check.Near(-3, r!.Dy, "应顺带上边对齐（Dy = -3）");
            Check.True(r.Guides.Count > 0, "应生成对齐参考线");
        }

        // 5. 对齐偏差超出容差：仍吸附但不强制对齐
        {
            var a = new Rect(0, 40, 95, 100);
            var b = TestBitmap.Sticker(100, 0, 100, 100);
            var r = SnapEngine.Resolve(a, "self", null, new[] { b }, null, Opt);
            Check.True(r is not null, "垂直错开 40px 仍应可侧面吸附");
            Check.Near(0, r!.Dy, "超出容差时不应强制对齐");
        }

        // 6. 同组合成员不作为吸附目标
        {
            var a = new Rect(0, 0, 95, 100);
            var b = TestBitmap.Sticker(100, 0, 100, 100, groupId: "G1");
            var r = SnapEngine.Resolve(a, "self", "G1", new[] { b }, null, Opt);
            Check.True(r is null, "同组合成员不应成为吸附目标");
        }

        // 7. 自身不作为吸附目标
        {
            var a = new Rect(0, 0, 95, 100);
            var self = TestBitmap.Sticker(100, 0, 100, 100);
            var r = SnapEngine.Resolve(a, self.Id, null, new[] { self }, null, Opt);
            Check.True(r is null, "不应吸附到自身");
        }

        // 8. 重叠（gap 为负）不吸附
        {
            var a = new Rect(0, 0, 120, 100);
            var b = TestBitmap.Sticker(100, 0, 100, 100);
            var r = SnapEngine.Resolve(a, "self", null, new[] { b }, null, Opt);
            Check.True(r is null, "与目标重叠时不应吸附");
        }

        // 9. 多个候选取位移最小者
        {
            var a = new Rect(0, 0, 95, 100);
            var near = TestBitmap.Sticker(100, 0, 100, 100);      // gap 5
            var far = TestBitmap.Sticker(107, 0, 100, 100);       // gap 12 → 超阈值
            var r = SnapEngine.Resolve(a, "self", null, new[] { far, near }, null, Opt);
            Check.True(r is not null && r.Target == near, "应选中唯一在阈值内的候选");
        }

        // 10. 可吸附到组合体外轮廓（目标成员本身够不着，但组合体边缘够得着）
        {
            var a = new Rect(0, 0, 100, 100);
            var m1 = TestBitmap.Sticker(300, 0, 100, 100, groupId: "G1");
            var m2 = TestBitmap.Sticker(110, 0, 100, 100, groupId: "G1");
            Func<string, Rect?> boundsOf = gid => gid == "G1" ? new Rect(110, 0, 290, 100) : null;
            var r = SnapEngine.Resolve(a, "self", null, new[] { m1, m2 }, boundsOf, Opt);
            Check.True(r is not null, "应能吸附到组合体外轮廓");
            Check.Near(10, r!.Dx, "与组合体外轮廓间隙 10px 的吸附位移");
        }

        // ---------------- 边缘对齐吸附（ResolveAlign 拖动对齐 / ResolveResizeAlign 缩放对齐） ----------------

        // 11. 上边缘对齐：A 上边接近 B 上边 → 垂直对齐（小贴图跟大贴图上边缘对齐）
        {
            var a = new Rect(0, 95, 100, 100);
            var b = TestBitmap.Sticker(0, 100, 100, 100);
            var r = SnapEngine.ResolveAlign(a, "self", null, new[] { b }, null, Opt);
            Check.True(r is not null, "上边缘对齐：应命中");
            Check.Near(5, r!.Dy, "上边缘对齐：Dy = B.Top - A.Top");
            Check.Near(0, r.Dx, "上边缘对齐：水平不动");
        }

        // 12. 左边缘对齐：A 左边接近 B 左边 → 水平对齐
        {
            var a = new Rect(95, 0, 100, 100);
            var b = TestBitmap.Sticker(100, 0, 100, 100);
            var r = SnapEngine.ResolveAlign(a, "self", null, new[] { b }, null, Opt);
            Check.True(r is not null, "左边缘对齐：应命中");
            Check.Near(5, r!.Dx, "左边缘对齐：Dx = B.Left - A.Left");
        }

        // 13. 正交完全分离时不对齐（仅水平重叠、垂直相距远 → 不吸附）
        {
            var a = new Rect(0, 0, 100, 100);
            var b = TestBitmap.Sticker(0, 200, 100, 100);
            var r = SnapEngine.ResolveAlign(a, "self", null, new[] { b }, null, Opt);
            Check.True(r is null, "垂直相距 100px 不应对齐");
        }

        // 14. 缩放底边对齐：移动边（Bottom）接近目标底边 → 吸附（缩放小贴图到达大贴图底边缘）
        {
            var a = new Rect(0, 0, 100, 145);           // 缩放后 Bottom=145
            var b = TestBitmap.Sticker(0, 100, 100, 50); // 目标 Bottom=150
            var fixedCorner = new Point(0, 0);          // 左上角固定（拖右下角）
            var r = SnapEngine.ResolveResizeAlign(a, fixedCorner, "self", null, new[] { b }, null, Opt);
            Check.True(r is not null, "缩放底边对齐：应命中");
            Check.Near(5, r!.Dy, "缩放底边对齐：Dy = 目标Bottom - 移动边");
            Check.Near(150, r.PreviewBounds.Bottom, "缩放底边对齐：对齐后 Bottom = 150");
        }

        // 15. 缩放右边对齐：移动边（Right）接近目标右边 → 吸附
        {
            var a = new Rect(0, 0, 145, 100);           // 缩放后 Right=145
            var b = TestBitmap.Sticker(100, 0, 50, 100); // 目标 Right=150
            var fixedCorner = new Point(0, 0);
            var r = SnapEngine.ResolveResizeAlign(a, fixedCorner, "self", null, new[] { b }, null, Opt);
            Check.True(r is not null, "缩放右边对齐：应命中");
            Check.Near(5, r!.Dx, "缩放右边对齐：Dx = 目标Right - 移动边");
        }

        // 16. 左上角手柄缩放：固定右下，移动边是 Left/Top → 向内收缩对齐
        {
            var a = new Rect(5, 5, 140, 140);           // Left/Top=5（接近目标 0）
            var b = TestBitmap.Sticker(0, 0, 100, 100);
            var fixedCorner = new Point(145, 145);      // 右下角固定
            var r = SnapEngine.ResolveResizeAlign(a, fixedCorner, "self", null, new[] { b }, null, Opt);
            Check.True(r is not null, "左上角缩放：应命中对齐");
            Check.Near(-5, r!.Dx, "左上角缩放：Dx = 目标Left - 移动边Left");
            Check.Near(-5, r!.Dy, "左上角缩放：Dy = 目标Top - 移动边Top");
            Check.Near(0, r.PreviewBounds.Left, "左上角缩放：对齐后 Left = 0");
        }
    }
}
