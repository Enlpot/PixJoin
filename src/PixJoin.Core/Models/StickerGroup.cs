namespace PixJoin.Core.Models;

/// <summary>
/// 组合体：纯粹的「逻辑关系」，不合并窗口、不合并位图。
/// 位置不单独存储，始终由成员的相对坐标推导（见 <see cref="GroupManager"/>）。
/// </summary>
public sealed class StickerGroup
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>成员贴图 id 列表，顺序稳定（按加入先后）。</summary>
    public List<string> MemberIds { get; } = new();

    public DateTime CreatedAt { get; init; } = DateTime.Now;
}
