using System.Collections.Generic;
using System.Text.Json;
using Aether.AI.Core.Memory;
using Aether.AI.Harness.Agents;
using Aether.AI.Hosting.Agents;
using Aether.AI.Memory;
using Aether.AI.Memory.Stores;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Aether.AI.Memory.Tests;

/// <summary>
/// <see cref="MemoryTool"/>（recall_memory/save_memory）测试：验证把 <see cref="IMemoryStore"/> 暴露为工具的检索/沉淀逻辑，
/// 及代码规则大脑经协程 <c>await Call</c> 调用这两个工具的端到端闭环（save → recall → 终答）。
/// </summary>
public class MemoryToolTests
{
    private static MemoryTool NewTool() => new(new InMemoryMemoryStore(), () => null);

    // —— 工具逻辑单测 ——

    [Fact]
    public async Task Save_then_recall_by_query()
    {
        var tool = NewTool();
        await tool.SaveAsync("用户偏好：深色主题", "theme");

        var result = await tool.RecallAsync(query: "偏好");

        // 返回 JSON 数组，含已存内容
        Assert.Contains("深色主题", result);
        using var doc = JsonDocument.Parse(result);
        Assert.Equal(1, doc.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task Recall_by_type_filters()
    {
        var tool = NewTool();
        await tool.SaveAsync("一条偏好", "p", "UserPreference");
        await tool.SaveAsync("一条知识", "k", "Knowledge");

        var prefs = await tool.RecallAsync(type: "UserPreference");
        Assert.Contains("一条偏好", prefs);
        Assert.DoesNotContain("一条知识", prefs);

        var knowledge = await tool.RecallAsync(type: "Knowledge");
        Assert.Contains("一条知识", knowledge);
        Assert.DoesNotContain("一条偏好", knowledge);
    }

    [Fact]
    public async Task Recall_default_returns_knowledge_and_preferences_only()
    {
        var tool = NewTool();
        await tool.SaveAsync("知识A", "k", "Knowledge");
        await tool.SaveAsync("偏好B", "p", "UserPreference");
        await tool.SaveAsync("情境C", "e", "TaskHistory"); // 情境记忆默认不召回

        var result = await tool.RecallAsync(); // 无 query 无 type

        Assert.Contains("知识A", result);
        Assert.Contains("偏好B", result);
        Assert.DoesNotContain("情境C", result); // 默认口径镜像 MemoryContextProvider：知识+偏好，不含 TaskHistory
    }

    [Fact]
    public async Task Recall_empty_returns_empty_array()
    {
        var result = await NewTool().RecallAsync();
        Assert.Equal("[]", result);
    }

    [Fact]
    public async Task Save_returns_json_with_id_and_defaults_type_to_knowledge()
    {
        var result = await NewTool().SaveAsync("内容X");
        using var doc = JsonDocument.Parse(result);
        Assert.True(doc.RootElement.GetProperty("saved").GetBoolean());
        Assert.Equal("Knowledge", doc.RootElement.GetProperty("type").GetString());
        Assert.NotEqual(default, doc.RootElement.GetProperty("id").GetGuid());
        // key 留空 → 取 content 前 40 字
        Assert.Equal("内容X", doc.RootElement.GetProperty("key").GetString());
    }

    // —— 端到端：代码规则大脑经协程调记忆工具 ——

    [Fact]
    public async Task CodeRuleBrain_uses_memory_tools_end_to_end()
    {
        // 代码规则大脑：先 save_memory 存一条，再 recall_memory 按关键词召回，把召回结果当终答返回。
        // 证明 CodeRuleBrain 经协程 await Call 闭环获取长期记忆（补齐规则大脑不接 MemoryContextProvider 的缺口）。
        var memoryTool = new MemoryTool(new InMemoryMemoryStore(), () => null);
        var saveFn = AIFunctionFactory.Create(typeof(MemoryTool).GetMethod(nameof(MemoryTool.SaveAsync))!, memoryTool, "save_memory");
        var recallFn = AIFunctionFactory.Create(typeof(MemoryTool).GetMethod(nameof(MemoryTool.RecallAsync))!, memoryTool, "recall_memory");

        var client = new RuleChatClient(new RememberAndRecallBrain());
        var options = new HarnessAgentOptions
        {
            Id = "code-rule-memory",
            Name = "CodeRuleMemory",
            Description = "code rule with memory",
            DisableTodoProvider = true,
            ChatOptions = new ChatOptions
            {
                Instructions = "code",
                Tools = new List<AITool> { saveFn, recallFn },
            },
        };
        var agent = client.AsHarnessAgent(options);
        var session = await agent.CreateSessionAsync();

        var finalText = "";
        await foreach (var update in agent.RunStreamingAsync("记一下", session))
        {
            if (!string.IsNullOrEmpty(update.Text)) finalText += update.Text;
        }

        // 终答 = recall 的 JSON，含刚 save 的内容
        Assert.Contains("深色主题", finalText);
    }

    /// <summary>save 一条记忆 → recall 按关键词 → 返回召回结果。证明协程式多步 + 记忆闭环。</summary>
    private sealed class RememberAndRecallBrain : CodeRuleBrain
    {
        protected override async Task<string> RunAsync()
        {
            await Call("save_memory", new { content = "用户偏好：深色主题", key = "theme" });
            var recalled = await Call("recall_memory", new { query = "偏好" });
            return recalled;
        }
    }
}
