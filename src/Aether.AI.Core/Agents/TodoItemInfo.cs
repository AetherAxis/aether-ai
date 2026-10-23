namespace Aether.AI.Core.Agents;

/// <summary>
/// 待办项（框架无关 DTO）。MAF 的 <c>TodoItem</c> 经 Harness 映射为本类型上抛给 UI，
/// 供计划卡渲染（完成项 <see cref="IsComplete"/> 划横线）。MAF 为二元状态，无 in_progress。
/// </summary>
public sealed record TodoItemInfo(
    string Id,
    string Title,
    string? Description,
    bool IsComplete);
