using System.ComponentModel;
using System.Text.Encodings.Web;
using System.Text.Json;
using Aether.AI.Core.Memory;
using Aether.AI.Core.Agents;
using Aether.AI.Memory;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Aether.AI.Hosting.Agents;

/// <summary>
/// 把 <see cref="IMemoryStore"/>（跨会话长期记忆）暴露为两个工具：<c>recall_memory</c>（检索）/ <c>save_memory</c>（沉淀）。
/// <para>
/// 对代码规则大脑（<c>CodeRuleBrain</c>）而言，这是它获取长期记忆的唯一路径——<see cref="MemoryContextProvider"/>
/// 是注入式 NL provider（把召回记忆塞进 Instructions），规则大脑不消费注入式 NL（BuildRuleAgent 不接该 provider），
/// 故长期记忆对规则大脑走「显式调用的工具」而非「自动注入」。作者在 <c>RunAsync</c> 里
/// <c>await Call("recall_memory", new {{ query = UserText }})</c> 拿回记忆 JSON、<c>return</c> 前可选
/// <c>await Call("save_memory", new {{ content = ... }})</c> 沉淀本轮。
/// </para>
/// <para>
/// 对 LLM agent，本工具提供一条<strong>显式</strong>记忆路径，与 <see cref="MemoryContextProvider"/> 的自动注入互补
/// （自动注入是「框架按 Q/A 蒸馏」，本工具是「作者显式存/查」，语义可控）。记忆按 userId 作用域（桌面单用户 → null = 全局），
/// 与 <see cref="MemoryContextProvider"/> 同源——同一 <see cref="IMemoryStore"/> 单例，数据互通。
/// </para>
/// <para>注册：<c>aether.AddMemoryTools()</c>（见 <see cref="MemoryToolExtensions"/>）。召回标 Safe（只读），
/// 沉淀标 Confirm（写长期状态）。</para>
/// </summary>
public sealed class MemoryTool
{
    private readonly IMemoryStore _store;
    private readonly Func<string?> _userIdAccessor;
    private static readonly JsonSerializerOptions _json = new()
    {
        // 不转义中文等非 ASCII 字符，让工具结果可读 + 便于 CodeRuleBrain 作者用 Contains/正则匹配。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    public MemoryTool(IMemoryStore store, Func<string?> userIdAccessor)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _userIdAccessor = userIdAccessor ?? throw new ArgumentNullException(nameof(userIdAccessor));
    }

    /// <summary>
    /// 从长期记忆检索条目，返回 JSON 数组（每项 <c>{{type,key,content,importance}}</c>；无匹配返回 <c>[]</c>）。
    /// <paramref name="query"/> 非空 → 跨所有类型子串搜索；否则按 <paramref name="type"/> 取该类型；两者均空 → 取全部 Knowledge+UserPreference（稳定事实，镜像 <see cref="MemoryContextProvider"/> 召回口径）。
    /// </summary>
    [Description("从长期记忆（跨会话）检索相关条目。返回 JSON 数组，无匹配返回 []。")]
    public async Task<string> RecallAsync(
        [Description("检索关键词（子串匹配，跨所有类型）。留空则按 type 返回。")] string? query = null,
        [Description("记忆类型：Knowledge/TaskHistory/UserPreference/BusinessContext。仅当 query 留空时生效；也留空则返回所有 Knowledge+UserPreference。")] string? type = null)
    {
        var userId = _userIdAccessor();
        IReadOnlyList<MemoryItem> items;
        if (!string.IsNullOrWhiteSpace(query))
        {
            items = await _store.SearchAsync(userId, query, CancellationToken.None).ConfigureAwait(false);
        }
        else if (!string.IsNullOrWhiteSpace(type) && Enum.TryParse<MemoryType>(type, true, out var t))
        {
            items = await _store.FindByTypeAsync(userId, t, CancellationToken.None).ConfigureAwait(false);
        }
        else
        {
            // 默认召回口径镜像 MemoryContextProvider：稳定事实（知识 + 偏好）。情境记忆（TaskHistory）量大且时敏，作者需可显式按 type 取。
            var knowledge = await _store.FindByTypeAsync(userId, MemoryType.Knowledge, CancellationToken.None).ConfigureAwait(false);
            var preferences = await _store.FindByTypeAsync(userId, MemoryType.UserPreference, CancellationToken.None).ConfigureAwait(false);
            items = knowledge.Concat(preferences).ToList();
        }

        return FormatItems(items);
    }

    /// <summary>
    /// 把一条信息存入长期记忆供未来检索，返回 JSON（<c>{{saved,id,type,key}}</c>）。
    /// <paramref name="key"/> 留空取 content 前 40 字；<paramref name="type"/> 默认 Knowledge。
    /// </summary>
    [Description("把一条信息存入长期记忆供未来检索（跨会话）。返回 JSON。")]
    public async Task<string> SaveAsync(
        [Description("要记住的内容。")] string content,
        [Description("短标题/键，留空取内容前 40 字。")] string? key = null,
        [Description("记忆类型，默认 Knowledge。可选：Knowledge/TaskHistory/UserPreference/BusinessContext。")] string? type = "Knowledge")
    {
        var userId = _userIdAccessor();
        if (!Enum.TryParse<MemoryType>(type, true, out var t)) t = MemoryType.Knowledge;

        var item = new MemoryItem
        {
            UserId = userId,
            Type = t,
            Key = string.IsNullOrWhiteSpace(key) ? Truncate(content, 40) : key,
            Content = content,
            Importance = 0.5,
        };
        await _store.SaveAsync(item, CancellationToken.None).ConfigureAwait(false);

        return JsonSerializer.Serialize(new { saved = true, id = item.Id, type = t.ToString(), key = item.Key }, _json);
    }

    private static string FormatItems(IReadOnlyList<MemoryItem> items)
    {
        if (items.Count == 0) return "[]";
        return JsonSerializer.Serialize(items.Select(i => new
        {
            type = i.Type.ToString(),
            key = i.Key,
            content = i.Content,
            importance = i.Importance,
        }), _json);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}

/// <summary><see cref="MemoryTool"/> 的 DI 注册扩展——注册 <c>recall_memory</c>（Safe）+ <c>save_memory</c>（Confirm）工具。</summary>
public static class MemoryToolExtensions
{
    /// <summary>注册长期记忆工具（recall_memory / save_memory）。recall 只读标 Safe，save 写长期状态标 Confirm。</summary>
    public static AetherAIBuilder AddMemoryTools(this AetherAIBuilder aether)
    {
        aether.Services.AddTransient<MemoryTool>();
        aether.ConfigureToolRegistry(reg =>
        {
            reg.Register(
                "recall_memory",
                "从长期记忆（跨会话）检索相关条目。query=关键词（子串，跨所有类型）；type=类型(Knowledge/TaskHistory/UserPreference/BusinessContext，仅 query 留空时生效)；均留空返回所有知识+偏好。返回 JSON 数组，无匹配返回 []。",
                "记忆",
                sp => AIFunctionFactory.Create(
                    typeof(MemoryTool).GetMethod(nameof(MemoryTool.RecallAsync))!,
                    sp.GetRequiredService<MemoryTool>(),
                    "recall_memory"),
                ToolApprovalLevel.Safe);
            reg.Register(
                "save_memory",
                "把一条信息存入长期记忆供未来检索（跨会话）。content=内容；key=短标题(可选)；type=类型(可选,默认Knowledge)。返回 JSON。",
                "记忆",
                sp => AIFunctionFactory.Create(
                    typeof(MemoryTool).GetMethod(nameof(MemoryTool.SaveAsync))!,
                    sp.GetRequiredService<MemoryTool>(),
                    "save_memory"),
                ToolApprovalLevel.Confirm);
        });
        return aether;
    }
}
