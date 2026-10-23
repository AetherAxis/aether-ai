using System.Runtime.CompilerServices;
using Aether.AI.Brains.Abstractions;
using Aether.AI.Brains.Code;
using Aether.AI.Brains.Code.CSharp;
using Aether.AI.Brains.Rule.NRules;
using Microsoft.Extensions.AI;
using NRules.Fluent.Dsl;

namespace Aether.AI.BrainMatrix.Consumer;

/// <summary>Code 大脑：把「查」后的内容交给 Echo，再用工具结果收尾。</summary>
internal sealed class EchoCodeBrain : CodeRuleBrain
{
    protected override async Task<string> RunAsync()
        => "done:" + await Call("Echo", new { text = Trim(UserText) });

    private static string Trim(string text) => text.StartsWith('查') ? text[1..] : text;
}

/// <summary>NRules 规则一：首步调用 Echo。</summary>
public sealed class MatrixQueryRule : Rule
{
    public override void Define()
    {
        NRulesDecisionContext decision = null!;
        When().Match<NRulesDecisionContext>(() => decision,
            d => d.UserText.StartsWith("查") && d.ToolCallCount == 0);
        Then().Do(_ => decision.CallTool("Echo", "text", decision.UserText.Substring(1)));
    }
}

/// <summary>NRules 规则二：拿到工具结果后回复。</summary>
public sealed class MatrixReplyRule : Rule
{
    public override void Define()
    {
        NRulesDecisionContext decision = null!;
        When().Match<NRulesDecisionContext>(() => decision,
            d => d.ToolCallCount == 1 && d.LastResult != null);
        Then().Do(_ => decision.Reply("done:" + decision.LastResult));
    }
}

/// <summary>无 key 环境下的 LLM 替身：第一回合请求调用 Echo，第二回合把工具结果织进终答。</summary>
internal sealed class ScriptedChatClientFactory : IChatClientFactory
{
    private readonly ScriptedChatClient _client = new();

    public IChatClient Create(string? modelId) => _client;
}

internal sealed class ScriptedChatClient : IChatClient
{
    public void Dispose()
    {
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("脚本化替身仅支持流式调用。");

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => Stream(messages.ToList(), cancellationToken);

    private static async IAsyncEnumerable<ChatResponseUpdate> Stream(
        IList<ChatMessage> history, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var toolResult = history.SelectMany(message => message.Contents).OfType<FunctionResultContent>().LastOrDefault();

        yield return new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents = toolResult is null
                ? new List<AIContent>
                {
                    new FunctionCallContent("call-1", "Echo", new Dictionary<string, object?> { ["text"] = "600000" }),
                }
                : new List<AIContent> { new TextContent($"done:{toolResult.Result}") },
        };

        await Task.Yield();
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
}
