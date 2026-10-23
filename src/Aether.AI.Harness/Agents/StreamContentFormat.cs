using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Aether.AI.Harness.Agents;

/// <summary>
/// MEAI 流内容的**纯展示格式化**函数集——把工具入参/结果/用量/web search 输出归一为可展示字典
/// （<c>Dictionary&lt;string, object&gt;</c>）或一行摘要。全部无状态、不碰运行时会话状态；
/// 从 <c>HarnessRuntime</c> 抽出，让核心流式循环只剩调度逻辑。
/// </summary>
internal static class StreamContentFormat
{
    /// <summary>内置 web search 工具的展示名（toolNamesByCallId 登记名 + 卡片名）。</summary>
    internal const string WebSearchToolName = "web_search";

    /// <summary>从工具入参提取主参数摘要（第一个非空字符串/数值，截断 ~20 字符），供关键节点信号展示。
    /// 拿不到返回 null（消费方退化为只显工具名）。</summary>
    internal static string? FirstArgSummary(IDictionary<string, object?>? arguments)
    {
        if (arguments is null) return null;
        foreach (var v in arguments.Values)
        {
            if (v is null) continue;
            var s = v.ToString();
            if (string.IsNullOrWhiteSpace(s)) continue;
            s = s.Trim();
            return s.Length > 20 ? s[..20] + "…" : s;
        }
        return null;
    }

    /// <summary>把 <see cref="UsageContent"/> 摘要为一行可展示文本。</summary>
    internal static string SummarizeUsage(UsageContent usage)
    {
        var d = usage.Details;
        var input = d.InputTokenCount;
        var output = d.OutputTokenCount;
        return $"输入 {(input is null ? 0 : input)} / 输出 {(output is null ? 0 : output)} token";
    }

    /// <summary>把 FunctionCallContent.Arguments（可能为字典/JsonElement/字符串）归一为可展示字典。</summary>
    internal static Dictionary<string, object>? ToInputDict(object? arguments)
    {
        if (arguments is null) return null;

        if (arguments is IDictionary<string, object?> nd)
            return nd.ToDictionary(kv => kv.Key, kv => kv.Value ?? string.Empty);
        if (arguments is IDictionary<string, object> d)
            return new Dictionary<string, object>(d);
        if (arguments is IReadOnlyDictionary<string, object?> rnd)
            return rnd.ToDictionary(kv => kv.Key, kv => kv.Value ?? string.Empty);

        if (arguments is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.Object)
            {
                try { return JsonSerializer.Deserialize<Dictionary<string, object>>(je); }
                catch { }
            }
            return new Dictionary<string, object> { ["input"] = je.ToString() };
        }

        return new Dictionary<string, object> { ["input"] = arguments.ToString() ?? string.Empty };
    }

    /// <summary>把 FunctionResultContent.Result 归一为可展示字典。</summary>
    internal static Dictionary<string, object>? ToOutputDict(object? result)
    {
        if (result is null) return null;

        if (result is string s)
            return new Dictionary<string, object> { ["result"] = s };

        if (result is IDictionary<string, object?> nd)
            return nd.ToDictionary(kv => kv.Key, kv => kv.Value ?? string.Empty);
        if (result is IDictionary<string, object> d)
            return new Dictionary<string, object>(d);

        if (result is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.Object)
            {
                try { return JsonSerializer.Deserialize<Dictionary<string, object>>(je); }
                catch { }
            }
            return new Dictionary<string, object> { ["result"] = je.ToString() };
        }

        return new Dictionary<string, object> { ["result"] = result.ToString() ?? string.Empty };
    }

    /// <summary>把 <see cref="WebSearchToolResultContent.Outputs"/>（<c>IList&lt;AIContent&gt;</c>）归一为可展示字典。
    /// 镜像 <see cref="ToOutputDict"/>：<see cref="ChatMessageItem"/> 的 NormalizeToolResult 读 "result" 键 →
    /// 多行渲染为 code 块。每条输出取 <c>TextContent.Text</c>，回退 <c>ToString()</c>；空集返 null。</summary>
    internal static Dictionary<string, object>? ToWebSearchOutputDict(IList<AIContent>? outputs)
    {
        if (outputs is null || outputs.Count == 0) return null;
        var sb = new StringBuilder();
        foreach (var o in outputs)
        {
            var t = o is TextContent tc ? tc.Text : o.ToString();
            if (!string.IsNullOrEmpty(t))
            {
                if (sb.Length > 0) sb.AppendLine();
                sb.Append(t);
            }
        }
        var text = sb.ToString();
        return string.IsNullOrEmpty(text) ? null : new Dictionary<string, object> { ["result"] = text };
    }
}
