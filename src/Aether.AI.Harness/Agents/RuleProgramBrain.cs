using System.Collections;
using System.Globalization;
using System.Text.RegularExpressions;
using Aether.AI.Core.Agents;
using Microsoft.Extensions.AI;

namespace Aether.AI.Harness.Agents;

/// <summary>
/// <see cref="IRuleBrain"/> 的声明式实现：求值一个 <see cref="RuleProgram"/>。无状态——每次调用从
/// <see cref="RuleContext"/> 推导，自上而下取首个命中步骤执行。保证收敛：<see cref="RuleProgram.MaxSteps"/>
/// 到强制终止；无命中流 <see cref="RuleProgram.NoMatchReply"/> 终止。
/// </summary>
public sealed class RuleProgramBrain : IRuleBrain
{
    private readonly RuleProgram _program;

    public RuleProgramBrain(RuleProgram program)
    {
        _program = program ?? throw new ArgumentNullException(nameof(program));
    }

    public ValueTask<IReadOnlyList<AIContent>> DecideAsync(RuleContext context, CancellationToken cancellationToken)
        => new(Decide(context));

    private IReadOnlyList<AIContent> Decide(RuleContext ctx)
    {
        // 安全闸：本轮工具调用数超限 → 强制终止，防死循环。
        if (ctx.ToolCallsThisTurn >= _program.MaxSteps)
        {
            return new[] { new TextContent($"规则步数超限（已 {_program.MaxSteps} 步）") };
        }

        var userText = ctx.LatestUser?.Text ?? string.Empty;
        var resultText = ctx.LatestToolResult?.Result?.ToString() ?? string.Empty;

        // 程序级捕获：对最新 user 消息预匹配一次，捕获组供无 OnUserRegex 的步骤兜底用 {user.N}。
        Match? captureMatch = null;
        if (!string.IsNullOrEmpty(_program.Capture) && !string.IsNullOrEmpty(userText))
        {
            captureMatch = Regex.Match(userText, _program.Capture, RegexOptions.None, TimeSpan.FromSeconds(1));
            if (!captureMatch.Success) captureMatch = null;
        }

        // 程序级 Capture 设定但未匹配 user 输入 → 不让无 OnUserRegex 的步骤兜底命中，直接 no-match。
        // （capture 匹配时供参行为不变；工具链中段 LatestUser 不变仍匹配，OnResultRegex 步骤照常工作。）
        if (!string.IsNullOrEmpty(_program.Capture) && !string.IsNullOrEmpty(userText) && captureMatch is null)
        {
            return new[] { new TextContent(_program.NoMatchReply ?? "无匹配规则") };
        }

        foreach (var step in _program.Steps)
        {
            if (!Matches(step.Match, ctx, userText, resultText, out var userMatch))
            {
                continue;
            }

            // 步骤有自己的 OnUserRegex 捕获组优先；否则回退程序级 Capture。
            var match = userMatch ?? captureMatch;
            var action = step.Action;

            // 终答 → 流 TextContent，终止循环。
            if (!string.IsNullOrEmpty(action.ReplyText))
            {
                return new[] { new TextContent(Substitute(action.ReplyText, match, userText, resultText)) };
            }

            // 工具调用 → 流 FunctionCallContent，循环继续（MAF 执行后回填结果再调本大脑）。
            if (!string.IsNullOrEmpty(action.Tool))
            {
                var args = SubstituteArgs(action.Args, match, userText, resultText);
                var callId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
                return new[] { new FunctionCallContent(callId, action.Tool, args) };
            }

            // 既无 Tool 也无 Reply：穿透下一条。
        }

        // 无命中 → 兜底终止。
        return new[] { new TextContent(_program.NoMatchReply ?? "无匹配规则") };
    }

    /// <summary>所有非 null 条件同时满足才命中。<paramref name="userMatch"/> 输出 OnUserRegex 的匹配（供模板用）。</summary>
    private static bool Matches(RuleMatch match, RuleContext ctx, string userText, string resultText, out Match? userMatch)
    {
        userMatch = null;

        if (!string.IsNullOrEmpty(match.OnUserRegex))
        {
            if (string.IsNullOrEmpty(userText))
            {
                return false;
            }
            userMatch = Regex.Match(userText, match.OnUserRegex, RegexOptions.None, TimeSpan.FromSeconds(1));
            if (!userMatch.Success)
            {
                return false;
            }
        }

        if (!string.IsNullOrEmpty(match.OnResultRegex))
        {
            if (string.IsNullOrEmpty(resultText) || !Regex.IsMatch(resultText, match.OnResultRegex, RegexOptions.None, TimeSpan.FromSeconds(1)))
            {
                return false;
            }
        }

        if (match.MinToolCalls is int min && ctx.ToolCallsThisTurn < min)
        {
            return false;
        }

        if (match.MaxToolCalls is int max && ctx.ToolCallsThisTurn > max)
        {
            return false;
        }

        return true;
    }

    /// <summary>模板替换：{user.N}（第 N 捕获组）、{user}（整条 user 文本）、{result}（上一工具结果）。</summary>
    private static string Substitute(string? template, Match? userMatch, string userText, string resultText)
    {
        if (string.IsNullOrEmpty(template))
        {
            return string.Empty;
        }

        var result = template.Replace("{user}", userText, StringComparison.Ordinal)
                             .Replace("{result}", resultText, StringComparison.Ordinal);

        if (userMatch is not null)
        {
            result = Regex.Replace(result, @"\{user\.(\d+)\}", m =>
            {
                if (int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i <= userMatch.Groups.Count)
                {
                    return userMatch.Groups[i].Value;
                }
                return m.Value;
            });
        }

        return result;
    }

    /// <summary>对工具参数字典逐值做模板替换：字符串叶子走 <see cref="Substitute"/>，嵌套 dict/list 递归，其余原样。空→空字典。</summary>
    private static Dictionary<string, object?> SubstituteArgs(
        Dictionary<string, object?>? args, Match? userMatch, string userText, string resultText)
    {
        if (args is null || args.Count == 0)
        {
            return new Dictionary<string, object?>();
        }

        var result = new Dictionary<string, object?>(args.Count, StringComparer.Ordinal);
        foreach (var kv in args)
        {
            result[kv.Key] = SubstituteValue(kv.Value, userMatch, userText, resultText);
        }
        return result;
    }

    private static object? SubstituteValue(object? v, Match? userMatch, string userText, string resultText) => v switch
    {
        string s => Substitute(s, userMatch, userText, resultText),
        IDictionary d => SubstituteDict(d, userMatch, userText, resultText),
        IList l => SubstituteList(l, userMatch, userText, resultText),
        _ => v,
    };

    private static Dictionary<string, object?> SubstituteDict(IDictionary d, Match? userMatch, string userText, string resultText)
    {
        var result = new Dictionary<string, object?>(d.Count, StringComparer.Ordinal);
        foreach (DictionaryEntry kv in d)
        {
            result[kv.Key?.ToString() ?? string.Empty] = SubstituteValue(kv.Value, userMatch, userText, resultText);
        }
        return result;
    }

    private static List<object?> SubstituteList(IList l, Match? userMatch, string userText, string resultText)
    {
        var result = new List<object?>(l.Count);
        foreach (var item in l)
        {
            result.Add(SubstituteValue(item, userMatch, userText, resultText));
        }
        return result;
    }
}
