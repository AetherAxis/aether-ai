using System.Runtime.CompilerServices;
using Aether.AI.Core.Agents;
using Aether.AI.Harness.Agents;
using Aether.AI.Harness.Skills;
using Aether.AI.Harness.Tools;
using Microsoft.Extensions.AI;

namespace Aether.AI.Harness.Tests;

/// <summary>
/// LLM 大脑路径的行为钉子（2026-10-23 新增——此前全仓 IChatClient 测试桩均为抛异常 Null 桩，
/// <c>Engine = Llm</c> 的完整循环零行为覆盖）。<see cref="ScriptedChatClient"/> 模拟真实模型的
/// 工具回合：无工具结果 → 流出 <see cref="FunctionCallContent"/>（调 Echo 工具）；收到工具结果 →
/// 把<b>结果原文</b>织进终答。驱动 MAF ChatClientAgent 真实「调用→执行→回填→再调用→终答」循环，
/// 证明工具结果确实流回了模型输入侧（function-calling 闭环）。
/// </summary>
public class ScriptedLlmAgentTests
{
    [Fact]
    public async Task Llm_agent_executes_tool_then_weaves_result_into_final_reply()
    {
        var tools = new ToolRegistry();
        tools.Register("Echo", "回声", "测试",
            _ => AIFunctionFactory.Create((Func<string, string>)(text => $"echo:{text}"), name: "Echo"));

        var factory = new AgentFactory(new ScriptedClientFactory(), tools, new SkillRegistry());
        var agent = factory.Create(new AgentDefinition
        {
            Id = "llm-echo",
            Name = "LlmEcho",
            Instructions = "echo agent",
            Engine = EngineKind.Llm,       // 默认引擎——显式写出便于对照 Rule 分支测试
            Tools = new[] { "Echo" },
        });

        var session = await agent.CreateSessionAsync();
        var finalText = "";
        await foreach (var update in agent.RunStreamingAsync("hello", session))
            if (!string.IsNullOrEmpty(update.Text)) finalText += update.Text;

        // 终答含工具结果原文 = 工具结果真的回到了「模型」输入侧（ScriptedChatClient 从历史里读它）
        Assert.Contains("echo:hello", finalText);
    }

    private sealed class ScriptedClientFactory : Aether.AI.Brains.Abstractions.IChatClientFactory
    {
        public IChatClient Create(string? modelId) => new ScriptedChatClient();
    }

    /// <summary>脚本化 LLM：按对话历史是否有 <see cref="FunctionResultContent"/> 决定流出工具调用还是终答。</summary>
    private sealed class ScriptedChatClient : IChatClient
    {
        public void Dispose() { }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("测试桩仅支持流式（与 BrainChatClient 同约束）");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Stream(messages.ToList());

        private static async IAsyncEnumerable<ChatResponseUpdate> Stream(
            IList<ChatMessage> history, [EnumeratorCancellation] CancellationToken ct = default)
        {
            var result = history.SelectMany(m => m.Contents).OfType<FunctionResultContent>().FirstOrDefault();
            if (result is null)
            {
                // 第一回合：请求调用 Echo 工具
                yield return new ChatResponseUpdate
                {
                    Role = ChatRole.Assistant,
                    Contents = new List<AIContent>
                    {
                        new FunctionCallContent("call-1", "Echo", new Dictionary<string, object?> { ["text"] = "hello" }),
                    },
                };
            }
            else
            {
                // 第二回合：工具结果已在历史里——把它织进终答（证明回填闭环）
                yield return new ChatResponseUpdate
                {
                    Role = ChatRole.Assistant,
                    Contents = new List<AIContent> { new TextContent($"LLM 终答：{result.Result}") },
                };
            }
            await Task.Yield();
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }
}
