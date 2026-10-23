using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aether.AI.Brains.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RulesEngine.Models;
using RulesEngineInstance = global::RulesEngine.RulesEngine;

namespace Aether.AI.Brains.Rule.RulesEngine;

/// <summary>
/// 把 Microsoft RulesEngine 接成 <see cref="IBrain"/> 的适配器。
/// <para>职责边界：RulesEngine 只负责「用 JSON 表达式判断哪些规则命中」，本类负责把命中规则上的
/// <c>Properties</c> 动作载荷翻译成 <see cref="AIContent"/>（工具调用或终答）。工具执行、结果回填、
/// 循环终止仍由 MAF/Harness 负责，与 <c>RuleProgramBrain</c> 完全一致。</para>
/// <para>动作载荷（规则 <c>Properties</c>）：
/// <c>reply</c>（终答文本）或 <c>tool</c> + <c>args</c>（工具调用）；可选 <c>capture</c> 正则，
/// 其捕获组供模板 <c>{user.N}</c> 使用。字符串值支持 <c>{user}</c> / <c>{result}</c> 模板。
/// 同时给出 <c>reply</c> 与 <c>tool</c> 时以 <c>reply</c> 优先（与 <c>RuleProgramBrain</c> 一致）。</para>
/// <para>与 <c>RuleProgram</c> 的语义差异：RulesEngine 独立求值每条规则、不短路，因此可能同时命中多条。
/// 本类按声明顺序取「第一条命中且带动作」的规则，重叠命中会记一次 warning——作者应用
/// <c>brain.ToolCallCount</c> 等条件把规则写成互斥步骤。</para>
/// </summary>
public sealed class RulesEngineBrain : IBrain
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly RulesEngineInstance _engine;
    private readonly string _workflowName;
    private readonly RulesEngineRuleOptions _options;
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<string, byte> _warnedOverlaps = new(StringComparer.Ordinal);

    public RulesEngineBrain(string ruleSource, RulesEngineRuleOptions? options = null, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(ruleSource))
        {
            throw new ArgumentException("RulesEngine rule source is required.", nameof(ruleSource));
        }

        _options = options ?? new RulesEngineRuleOptions();
        _logger = logger;

        var workflows = Parse(ruleSource);
        var workflow = Select(workflows, _options.WorkflowName);
        _workflowName = workflow.WorkflowName;

        _engine = new RulesEngineInstance(new[] { workflow }, new ReSettings());
    }

    /// <summary>当前求值的 workflow 名称（诊断用）。</summary>
    public string WorkflowName => _workflowName;

    public async ValueTask<IReadOnlyList<AIContent>> DecideAsync(BrainContext context, CancellationToken cancellationToken)
    {
        // 安全闸：本轮工具调用数超限 → 强制终止，防死循环（与 RuleProgram.MaxSteps 对齐）。
        if (context.ToolCallsThisTurn >= _options.MaxSteps)
        {
            return Text($"规则步数超限（已 {_options.MaxSteps} 步）");
        }

        var userText = context.LatestUser?.Text ?? string.Empty;
        var resultText = context.LatestToolResult?.Result?.ToString() ?? string.Empty;
        var facts = new RulesEngineFacts
        {
            UserText = userText,
            LastResult = context.LatestToolResult is null ? null : resultText,
            ToolCallCount = context.ToolCallsThisTurn,
            SessionId = context.SessionId,
        };

        var results = await _engine
            .ExecuteAllRulesAsync(_workflowName, new[] { new RuleParameter("brain", facts) }, cancellationToken)
            .ConfigureAwait(false);

        RuleEmission? chosen = null;
        string? chosenRule = null;
        List<string>? overlapping = null;

        foreach (var result in results)
        {
            if (!result.IsSuccess)
            {
                if (_logger is not null && result.ExceptionMessage is { Length: > 0 } error)
                {
                    _logger.LogDebug("RulesEngine rule '{Rule}' failed to evaluate: {Error}", result.Rule.RuleName, error);
                }
                continue;
            }

            var emission = RuleEmission.TryCreate(result.Rule.Properties, userText, resultText);
            if (emission is null)
            {
                // 命中但没带动作 = 纯断言规则；不参与决策。
                continue;
            }

            if (chosen is null)
            {
                chosen = emission;
                chosenRule = result.Rule.RuleName;
            }
            else
            {
                (overlapping ??= new()).Add(result.Rule.RuleName);
            }
        }

        if (chosen is null)
        {
            return Text(_options.NoMatchReply);
        }

        if (overlapping is { Count: > 0 })
        {
            WarnOverlap(chosenRule, overlapping);
        }

        _logger?.LogDebug("RulesEngine selected rule '{Rule}' at step {Step}.", chosenRule, context.ToolCallsThisTurn);
        return chosen.ToContents();
    }

    private void WarnOverlap(string? chosenRule, List<string> others)
    {
        var signature = $"{chosenRule}>{string.Join(",", others)}";
        if (!_warnedOverlaps.TryAdd(signature, 0))
        {
            return;
        }

        _logger?.LogWarning(
            "RulesEngine workflow '{Workflow}' matched multiple action rules in one step; '{Chosen}' won by declaration order, also matched: {Others}. " +
            "Rules are evaluated independently—add conditions such as brain.ToolCallCount to make steps mutually exclusive.",
            _workflowName, chosenRule, string.Join(", ", others));
    }

    private static IReadOnlyList<AIContent> Text(string text) => new[] { new TextContent(text) };

    private static Workflow[] Parse(string ruleSource)
    {
        Workflow[]? workflows;
        try
        {
            workflows = JsonSerializer.Deserialize<Workflow[]>(ruleSource, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"RulesEngine RuleSource 不是合法的 workflow JSON 数组：{ex.Message}", ex);
        }

        if (workflows is null || workflows.Length == 0)
        {
            throw new InvalidOperationException("RulesEngine RuleSource 解析结果为空；至少需要一个 workflow。");
        }

        return workflows;
    }

    private static Workflow Select(Workflow[] workflows, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return workflows[0];
        }

        foreach (var workflow in workflows)
        {
            if (string.Equals(workflow.WorkflowName, name, StringComparison.Ordinal))
            {
                return workflow;
            }
        }

        throw new InvalidOperationException(
            $"RulesEngine 未找到 workflow '{name}'；可用：{string.Join(", ", workflows.Select(w => w.WorkflowName))}。");
    }
}

/// <summary>一条命中规则翻译出的动作，尚未变成 <see cref="AIContent"/>。</summary>
internal sealed class RuleEmission
{
    private RuleEmission(string? tool, Dictionary<string, object?> args, string? reply)
    {
        Tool = tool;
        Args = args;
        Reply = reply;
    }

    public string? Tool { get; }

    public Dictionary<string, object?> Args { get; }

    public string? Reply { get; }

    /// <summary>
    /// 从规则的 <c>Properties</c> 读取动作。既无 <c>reply</c> 也无 <c>tool</c> 时返回 null。
    /// 值可能是 <see cref="JsonElement"/>（JSON 反序列化）或原生对象（代码构造 workflow），两者都支持。
    /// </summary>
    public static RuleEmission? TryCreate(IDictionary<string, object>? properties, string userText, string resultText)
    {
        if (properties is null || properties.Count == 0)
        {
            return null;
        }

        var capture = MatchCapture(StringValue(properties, "capture"), userText);
        var reply = StringValue(properties, "reply");
        var tool = StringValue(properties, "tool");

        if (!string.IsNullOrEmpty(reply))
        {
            return new RuleEmission(null, new Dictionary<string, object?>(0), Substitute(reply, capture, userText, resultText));
        }

        if (!string.IsNullOrEmpty(tool))
        {
            var args = properties.TryGetValue("args", out var raw) ? ToArguments(raw, capture, userText, resultText) : new();
            return new RuleEmission(tool, args, null);
        }

        return null;
    }

    public IReadOnlyList<AIContent> ToContents()
    {
        if (!string.IsNullOrEmpty(Reply))
        {
            return new[] { new TextContent(Reply) };
        }

        var callId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        return new[] { new FunctionCallContent(callId, Tool!, Args) };
    }

    private static string? StringValue(IDictionary<string, object> properties, string key)
    {
        if (!properties.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } json => json.GetString(),
            JsonElement json => json.GetRawText(),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture),
        };
    }

    private static Match? MatchCapture(string? pattern, string userText)
    {
        if (string.IsNullOrEmpty(pattern) || string.IsNullOrEmpty(userText))
        {
            return null;
        }

        Match match;
        try
        {
            match = Regex.Match(userText, pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException($"规则动作 capture 不是合法正则：{pattern}", ex);
        }

        return match.Success ? match : null;
    }

    /// <summary>模板替换：<c>{user.N}</c>（capture 捕获组）、<c>{user}</c>、<c>{result}</c>；与 RuleProgram 模板一致。</summary>
    private static string Substitute(string? template, Match? capture, string userText, string resultText)
    {
        if (string.IsNullOrEmpty(template))
        {
            return string.Empty;
        }

        var result = template.Replace("{user}", userText, StringComparison.Ordinal)
                             .Replace("{result}", resultText, StringComparison.Ordinal);

        if (capture is not null)
        {
            result = Regex.Replace(result, @"\{user\.(\d+)\}", m =>
            {
                if (int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                    && index < capture.Groups.Count)
                {
                    return capture.Groups[index].Value;
                }
                return m.Value;
            });
        }

        return result;
    }

    private static Dictionary<string, object?> ToArguments(object? raw, Match? capture, string userText, string resultText)
    {
        switch (raw)
        {
            case null:
                return new Dictionary<string, object?>(0);
            case JsonElement { ValueKind: JsonValueKind.Object } json:
                return json.EnumerateObject().ToDictionary(
                    p => p.Name,
                    p => ConvertJson(p.Value, capture, userText, resultText),
                    StringComparer.Ordinal);
            case IDictionary dictionary:
                return ToDictionary(dictionary, capture, userText, resultText);
            default:
                return new Dictionary<string, object?>(0);
        }
    }

    private static object? ConvertValue(object? value, Match? capture, string userText, string resultText) => value switch
    {
        null => null,
        string s => Substitute(s, capture, userText, resultText),
        JsonElement json => ConvertJson(json, capture, userText, resultText),
        IDictionary dictionary => ToDictionary(dictionary, capture, userText, resultText),
        IList list => ToList(list, capture, userText, resultText),
        _ => value,
    };

    private static object? ConvertJson(JsonElement json, Match? capture, string userText, string resultText)
    {
        switch (json.ValueKind)
        {
            case JsonValueKind.Object:
                return json.EnumerateObject().ToDictionary(
                    p => p.Name,
                    p => ConvertJson(p.Value, capture, userText, resultText),
                    StringComparer.Ordinal);
            case JsonValueKind.Array:
                var items = new List<object?>();
                foreach (var item in json.EnumerateArray())
                {
                    items.Add(ConvertJson(item, capture, userText, resultText));
                }
                return items;
            case JsonValueKind.String:
                return Substitute(json.GetString(), capture, userText, resultText);
            case JsonValueKind.Number:
                if (json.TryGetInt64(out var integer))
                {
                    return integer;
                }
                return json.GetDouble();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            default:
                return null;
        }
    }

    private static Dictionary<string, object?> ToDictionary(IDictionary dictionary, Match? capture, string userText, string resultText)
    {
        var result = new Dictionary<string, object?>(dictionary.Count, StringComparer.Ordinal);
        foreach (DictionaryEntry entry in dictionary)
        {
            result[entry.Key?.ToString() ?? string.Empty] = ConvertValue(entry.Value, capture, userText, resultText);
        }
        return result;
    }

    private static List<object?> ToList(IList list, Match? capture, string userText, string resultText)
    {
        var result = new List<object?>(list.Count);
        foreach (var item in list)
        {
            result.Add(ConvertValue(item, capture, userText, resultText));
        }
        return result;
    }
}
