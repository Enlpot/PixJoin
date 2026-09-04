using PixJoin.Core.Models;

namespace PixJoin.Core.Services;

/// <summary>
/// 组合体管理器 —— 纯逻辑、无 UI 依赖，可单元测试。
///
/// 设计要点：组合只是「逻辑关系」。成员依然是彼此独立的窗口与位图，
/// 因此拆分是 O(1) 的（改一个 GroupId 即可），无需重建窗口或重新拼图。
/// 组合体不存位置，位置永远由成员相对坐标推导；移动 = 各成员同步平移同一增量。
/// </summary>
public sealed class GroupManager
{
    private readonly Dictionary<string, Sticker> _stickers = new();
    private readonly Dictionary<string, StickerGroup> _groups = new();

    /// <summary>组合关系发生变化（建立 / 合并 / 拆分 / 解散），UI 需要重绘外框。</summary>
    public event EventHandler? Changed;

    public IReadOnlyCollection<Sticker> Stickers => _stickers.Values;

    public int StickerCount => _stickers.Count;

    public Sticker? Get(string id) => _stickers.TryGetValue(id, out var s) ? s : null;

    public StickerGroup? GetGroup(string? groupId) =>
        groupId is not null && _groups.TryGetValue(groupId, out var g) ? g : null;

    public void Register(Sticker sticker) => _stickers[sticker.Id] = sticker;

    /// <summary>
    /// 移除贴图（关闭窗口时调用）。若它是组合成员，先从组合中摘除；
    /// 组合剩余成员少于 2 个时自动解散，避免出现"只有一个成员的组合体"。
    /// </summary>
    public void Unregister(string id)
    {
        if (!_stickers.TryGetValue(id, out var s)) return;

        if (s.GroupId is { } gid)
        {
            var g = GetGroup(gid);
            g?.MemberIds.Remove(id);
            s.GroupId = null;

            if (g is not null && g.MemberIds.Count < 2)
                DissolveGroupInternal(g);
        }

        _stickers.Remove(id);
        OnChanged();
    }

    /// <summary>
    /// 建立 / 合并组合：把 <paramref name="movedId"/> 吸附到 <paramref name="targetId"/>。
    /// 双方原本各自所属的组合体会被合并为一个。返回最终组合体 id。
    /// </summary>
    public string Combine(string movedId, string targetId)
    {
        var moved = Get(movedId) ?? throw new ArgumentException($"未知贴图：{movedId}", nameof(movedId));
        var target = Get(targetId) ?? throw new ArgumentException($"未知贴图：{targetId}", nameof(targetId));

        if (moved.GroupId is not null && moved.GroupId == target.GroupId)
            return moved.GroupId; // 已在同一组合，幂等

        var ga = moved.GroupId is { } a ? GetGroup(a) : null;
        var gb = target.GroupId is { } b ? GetGroup(b) : null;

        if (ga is null && gb is null)
        {
            var g = new StickerGroup();
            _groups[g.Id] = g;
            g.MemberIds.Add(movedId);
            g.MemberIds.Add(targetId);
            moved.GroupId = g.Id;
            target.GroupId = g.Id;
        }
        else if (ga is null)
        {
            moved.GroupId = gb!.Id;
            gb.MemberIds.Add(movedId);
        }
        else if (gb is null)
        {
            target.GroupId = ga.Id;
            ga.MemberIds.Add(targetId);
        }
        else
        {
            // 合并两个组合体：全部并入 ga，删除 gb
            foreach (var id in gb.MemberIds)
            {
                var s = Get(id);
                if (s is null) continue;
                s.GroupId = ga.Id;
                if (!ga.MemberIds.Contains(id)) ga.MemberIds.Add(id);
            }
            _groups.Remove(gb.Id);
        }

        OnChanged();
        return moved.GroupId!;
    }

    /// <summary>单张贴图脱离组合（右键「拆离此贴图」或保护键拆分）。</summary>
    public void Detach(string id)
    {
        var s = Get(id);
        if (s?.GroupId is not { } gid) return;

        var g = GetGroup(gid);
        g?.MemberIds.Remove(id);
        s.GroupId = null;

        if (g is not null && g.MemberIds.Count < 2)
            DissolveGroupInternal(g);

        OnChanged();
    }

    /// <summary>解散组合体（右键「全部解散」）。</summary>
    public void Dissolve(string groupId)
    {
        if (GetGroup(groupId) is not { } g) return;
        DissolveGroupInternal(g);
        OnChanged();
    }

    private void DissolveGroupInternal(StickerGroup g)
    {
        foreach (var id in g.MemberIds)
        {
            var s = Get(id);
            if (s is not null) s.GroupId = null;
        }
        _groups.Remove(g.Id);
    }

    /// <summary>整组平移：所有成员施加同一增量，相对位置保持不变。</summary>
    public void MoveGroup(string groupId, double dx, double dy)
    {
        if (GetGroup(groupId) is not { } g) return;
        foreach (var id in g.MemberIds)
        {
            var s = Get(id);
            if (s is null) continue;
            s.X += dx;
            s.Y += dy;
        }
    }

    public IReadOnlyList<Sticker> GetMembers(string groupId)
    {
        var g = GetGroup(groupId);
        if (g is null) return Array.Empty<Sticker>();
        var list = new List<Sticker>(g.MemberIds.Count);
        foreach (var id in g.MemberIds)
            if (Get(id) is { } s) list.Add(s);
        return list;
    }

    /// <summary>组合体包围盒（物理像素）。成员为空时返回 <see cref="Rect.Empty"/>。</summary>
    public Rect GetGroupBounds(string groupId)
    {
        var members = GetMembers(groupId);
        if (members.Count == 0) return Rect.Empty;
        return UnionBounds(members.Select(m => m.Bounds));
    }

    public static Rect UnionBounds(IEnumerable<Rect> rects)
    {
        var l = double.MaxValue; var t = double.MaxValue; var r = double.MinValue; var b = double.MinValue;
        var any = false;
        foreach (var rc in rects)
        {
            any = true;
            l = Math.Min(l, rc.Left); t = Math.Min(t, rc.Top);
            r = Math.Max(r, rc.Right); b = Math.Max(b, rc.Bottom);
        }
        return any ? new Rect(l, t, r - l, b - t) : Rect.Empty;
    }

    /// <summary>供吸附引擎查询组合体包围盒。</summary>
    public Rect? BoundsOfGroup(string? groupId) =>
        groupId is null ? null : _groups.ContainsKey(groupId) ? GetGroupBounds(groupId) : null;

    public void Clear()
    {
        _stickers.Clear();
        _groups.Clear();
        OnChanged();
    }

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
