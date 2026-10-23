using Microsoft.Extensions.AI;

namespace Aether.AI.Harness.Agents;

/// <summary>
/// 规则程序的**自包含执行器**：自己驱动「大脑决策 → 执行工具 → 回填结果 → 再决策」循环到终止，
/// 返回最终文本。与 <see cref="RuleChatClient"/> 的区别：后者把工具执行交给 MAF agent 循环（agent 形态）；
/// 本类**内部**完成全部工具调用，对调用方只暴露一个最终字符串——即「规则当工具」的执行核。
/// <para>把本类包成 <see cref="AIFunction"/>（见 <see cref="RuleTool"/>）注册进工具池，LLM agent 调一次即
/// 拿到压缩结果，中间的工具调用与原始结果对 LLM 不可见（省 token：省决策推理 + 省原始结果 + 缩小 LLM
/// 工具 schema 体积）。大脑逻辑（<see cref="IRuleBrain"/>/<see cref="RuleProgramBrain"/>）与 agent 形态完全复用。</para>
/// <para>大脑每次决策只产出一条内容：<see cref="TextContent"/>=终答（返回）；<see cref="FunctionCallContent"/>=
/// 调工具（执行后回填、继续）。收敛由大脑的 <c>MaxSteps</c>/<c>NoMatchReply</c> 保证；本类另设 <see cref="_hardCap"/>
/// 防御性硬上限，防异常/自定义大脑死循环。</para>
/// </summary>
public sealed class RuleToolExecutor
{
    private readonly IRuleBrain _brain;
    private readonly Func<string, AIFunction?> _toolResolver;
    private readonly int _hardCap;

    public RuleToolExecutor(IRuleBrain brain, Func<string, AIFunction?> toolResolver, int? hardCap = null)
    {
        _brain = brain ?? throw new ArgumentNullException(nameof(brain));
        _toolResolver = toolResolver ?? throw new ArgumentNullException(nameof(toolResolver));
        _hardCap = hardCap ?? 100;
    }

    /// <summary>运行规则程序到终止，返回最终文本。所有工具调用在内部完成，对调用方不可见。</summary>
    /// <param name="input">作为「本轮 user 消息」喂给大脑的输入（LLM 传入的查询/数据）；可为空。</param>
    public async Task<string> RunAsync(string? input, CancellationToken cancellationToken = default)
    {
        ChatMessage? inputMsg = string.IsNullOrEmpty(input) ? null : new ChatMessage(ChatRole.User, input);
        var history = new List<ChatMessage>();
        if (inputMsg is not null)
        {
            history.Add(inputMsg);
        }

        FunctionResultContent? latestResult = null;

        for (var step = 0; step < _hardCap; step++)
        {
            var ctx = new RuleContext
            {
                LatestUser = inputMsg,
                LatestToolResult = latestResult,
                ToolCallsThisTurn = step,
                History = history.ToList(),
            };
            var contents = await _brain.DecideAsync(ctx, cancellationToken).ConfigureAwait(false);
            if (contents is null || contents.Count == 0)
            {
                return string.Empty;
            }

            var first = contents[0];

            // 终答 → 返回文本，循环结束。
            if (first is TextContent tc)
            {
                return tc.Text ?? string.Empty;
            }

            // 工具调用 → 执行、回填、继续。
            if (first is not FunctionCallContent call)
            {
                return string.Empty; // 未知内容类型：终止，避免死循环。
            }

            var callMsg = new ChatMessage { Role = ChatRole.Assistant };
            callMsg.Contents.Add(call);
            history.Add(callMsg);

            var result = await InvokeToolAsync(call, cancellationToken).ConfigureAwait(false);

            var resultMsg = new ChatMessage { Role = ChatRole.User };
            resultMsg.Contents.Add(result);
            history.Add(resultMsg);

            latestResult = result;
        }

        return $"规则执行超限（已 {_hardCap} 步）";
    }

    private async Task<FunctionResultContent> InvokeToolAsync(FunctionCallContent call, CancellationToken ct)
    {
        var tool = _toolResolver(call.Name);
        if (tool is null)
        {
            return new FunctionResultContent(call.CallId, $"工具不存在: {call.Name}");
        }

        try
        {
            var result = await tool.InvokeAsync(ToArguments(call.Arguments), ct).ConfigureAwait(false);
            return new FunctionResultContent(call.CallId, result!);
        }
        catch (Exception ex)
        {
            return new FunctionResultContent(call.CallId, $"工具异常: {ex.Message}");
        }
    }

    /// <summary>把 <see cref="FunctionCallContent.Arguments"/>（IDictionary）转成
    /// <see cref="AIFunction.InvokeAsync"/> 需要的 <see cref="AIFunctionArguments"/>。</summary>
    private static AIFunctionArguments ToArguments(IDictionary<string, object?>? args)
    {
        var fa = new AIFunctionArguments();
        if (args is null)
        {
            return fa;
        }
        foreach (var kv in args)
        {
            fa[kv.Key] = kv.Value!;
        }
        return fa;
    }
}
