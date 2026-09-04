using PixJoin.Core.Services;

namespace PixJoin.Core.Tests;

internal static class GroupManagerTests
{
    public static void Run()
    {
        Check.Section("GroupManager 组合管理");

        // 1. 建立组合
        {
            var gm = new GroupManager();
            var a = TestBitmap.Sticker(0, 0, 100, 100);
            var b = TestBitmap.Sticker(110, 0, 100, 100);
            gm.Register(a); gm.Register(b);

            var gid = gm.Combine(a.Id, b.Id);
            Check.True(a.GroupId == gid && b.GroupId == gid, "两张独立贴图应进入同一组合");
            Check.Equal(2, gm.GetMembers(gid).Count, "组合成员数");
        }

        // 2. 组合是幂等的
        {
            var gm = new GroupManager();
            var a = TestBitmap.Sticker(0, 0, 100, 100);
            var b = TestBitmap.Sticker(110, 0, 100, 100);
            gm.Register(a); gm.Register(b);
            var g1 = gm.Combine(a.Id, b.Id);
            var g2 = gm.Combine(b.Id, a.Id);
            Check.Equal(g1, g2, "重复组合应幂等");
        }

        // 3. 整组平移：相对位置不变
        {
            var gm = new GroupManager();
            var a = TestBitmap.Sticker(0, 0, 100, 100);
            var b = TestBitmap.Sticker(110, 50, 100, 100);
            gm.Register(a); gm.Register(b);
            var gid = gm.Combine(a.Id, b.Id);

            gm.MoveGroup(gid, 30, -20);
            Check.Near(30, a.X, "成员 A 平移后 X");
            Check.Near(110, b.X - a.X, "成员 A/B 横向相对间距应保持 110px");
            Check.Near(50, b.Y - a.Y, "成员 A/B 纵向相对间距应保持 50px");
            Check.Near(-20, a.Y, "成员 A 平移后 Y");
            Check.Near(30, b.Y, "成员 B 平移后 Y");
        }

        // 4. 新贴图加入已有组合
        {
            var gm = new GroupManager();
            var a = TestBitmap.Sticker(0, 0, 100, 100);
            var b = TestBitmap.Sticker(110, 0, 100, 100);
            var c = TestBitmap.Sticker(0, 110, 100, 100);
            gm.Register(a); gm.Register(b); gm.Register(c);
            var gid = gm.Combine(a.Id, b.Id);
            gm.Combine(c.Id, a.Id);
            Check.Equal(3, gm.GetMembers(gid).Count, "第三张应加入同一组合");
        }

        // 5. 两个组合体合并
        {
            var gm = new GroupManager();
            var a = TestBitmap.Sticker(0, 0, 100, 100);
            var b = TestBitmap.Sticker(110, 0, 100, 100);
            var c = TestBitmap.Sticker(0, 200, 100, 100);
            var d = TestBitmap.Sticker(110, 200, 100, 100);
            foreach (var s in new[] { a, b, c, d }) gm.Register(s);
            var g1 = gm.Combine(a.Id, b.Id);
            var g2 = gm.Combine(c.Id, d.Id);

            var merged = gm.Combine(b.Id, c.Id);
            Check.Equal(g1, merged, "合并后应保留第一个组合体 id");
            Check.Equal(4, gm.GetMembers(merged).Count, "合并后成员数");
            Check.True(gm.GetGroup(g2) is null, "被合并的组合体应被删除");
            Check.True(d.GroupId == merged, "原组合成员应全部迁移到合并后的组合");
        }

        // 6. 拆分：剩余 ≥2 时组合保留
        {
            var gm = new GroupManager();
            var a = TestBitmap.Sticker(0, 0, 100, 100);
            var b = TestBitmap.Sticker(110, 0, 100, 100);
            var c = TestBitmap.Sticker(0, 110, 100, 100);
            foreach (var s in new[] { a, b, c }) gm.Register(s);
            var gid = gm.Combine(a.Id, b.Id);
            gm.Combine(c.Id, a.Id);

            gm.Detach(b.Id);
            Check.True(b.GroupId is null, "拆离后应为独立贴图");
            Check.Equal(2, gm.GetMembers(gid).Count, "组合剩余 2 个成员，组合体应保留");
        }

        // 7. 拆分：剩余 <2 时自动解散
        {
            var gm = new GroupManager();
            var a = TestBitmap.Sticker(0, 0, 100, 100);
            var b = TestBitmap.Sticker(110, 0, 100, 100);
            gm.Register(a); gm.Register(b);
            var gid = gm.Combine(a.Id, b.Id);

            gm.Detach(b.Id);
            Check.True(a.GroupId is null, "只剩一个成员时组合体应自动解散");
            Check.True(gm.GetGroup(gid) is null, "组合体应被移除");
        }

        // 8. 全部解散
        {
            var gm = new GroupManager();
            var a = TestBitmap.Sticker(0, 0, 100, 100);
            var b = TestBitmap.Sticker(110, 0, 100, 100);
            var c = TestBitmap.Sticker(0, 110, 100, 100);
            foreach (var s in new[] { a, b, c }) gm.Register(s);
            var gid = gm.Combine(a.Id, b.Id);
            gm.Combine(c.Id, a.Id);

            gm.Dissolve(gid);
            Check.True(a.GroupId is null && b.GroupId is null && c.GroupId is null, "解散后所有成员应独立");
        }

        // 9. 关闭贴图时自动退出组合，并在成员不足时解散
        {
            var gm = new GroupManager();
            var a = TestBitmap.Sticker(0, 0, 100, 100);
            var b = TestBitmap.Sticker(110, 0, 100, 100);
            gm.Register(a); gm.Register(b);
            var gid = gm.Combine(a.Id, b.Id);

            gm.Unregister(b.Id);
            Check.True(a.GroupId is null, "成员被关闭后组合体应解散");
            Check.Equal(1, gm.StickerCount, "剩余贴图数");
        }

        // 10. 组合体包围盒
        {
            var gm = new GroupManager();
            var a = TestBitmap.Sticker(0, 0, 100, 100);
            var b = TestBitmap.Sticker(110, 50, 100, 100);
            gm.Register(a); gm.Register(b);
            var gid = gm.Combine(a.Id, b.Id);

            var bounds = gm.GetGroupBounds(gid);
            Check.Near(210, bounds.Width, "组合体包围盒宽度");
            Check.Near(150, bounds.Height, "组合体包围盒高度");
        }

        // 11. 变更事件
        {
            var gm = new GroupManager();
            var fired = 0;
            gm.Changed += (_, _) => fired++;
            var a = TestBitmap.Sticker(0, 0, 100, 100);
            var b = TestBitmap.Sticker(110, 0, 100, 100);
            gm.Register(a); gm.Register(b);
            gm.Combine(a.Id, b.Id);
            gm.Dissolve(a.GroupId!);
            Check.Equal(2, fired, "组合与解散各应触发一次变更事件");
        }
    }
}
