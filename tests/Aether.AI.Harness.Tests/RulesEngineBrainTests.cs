using Aether.AI.Brains.Abstractions;
using Aether.AI.Brains.Rule;
using Aether.AI.Brains.Rule.RulesEngine;
using Aether.AI.Core.Agents;
using Aether.AI.Harness;
using Aether.AI.Harness.Agents;
using Aether.AI.Harness.Sessions;
using Aether.AI.Harness.Skills;
using Aether.AI.Harness.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Aether.AI.Harness.Tests;

/// <summary>
/// Microsoft RulesEngine 作为「可选规则后端」的适配器契约：
/// ① 同一 <see cref="EngineKind.Rule"/> 下按 <see cref="AgentDefinition.BrainBackend"/> 选择；
/// ② RulesEngine 只做表达式求值，动作载荷（Properties）由适配器翻译成 <see cref="AIContent"/>；
/// ③ 工具执行/回填/多步循环仍由 Harness 负责，与 program 后端行为一致；
/// ④ 原生后端用 <see cref="AgentDefinition.RuleSource"/>，不与 <c>RuleProgram</c> 混用。
/// </summary>
public class RulesEngineBrainTests
{
    private const string TwoStepWorkflow = """
        [{
          "WorkflowName": "agent",
          "Rules": [
            {
              "RuleName": "query",
              "Expression": "brain.UserText.StartsWith(\"查\") && brain.ToolCallCount == 0",
              "Properties": {
                "tool": "Echo",
                "capture": "查(?<sym>.+)",
                "args": { "text": "{user.1}" }
              }
            },
            {
              "RuleName": "reply",
              "Expression": "brain.ToolCallCount == 1 && brain.LastResult != null",
              "Properties": { "reply": "结果：{result}" }
            }
          ]
        }]
        """;

    [Fact]
    public async Task RulesEngine_backend_drives_a_harness_agent_through_tool_call_and_reply()
    {
        var runtime = Runtime(new RulesEngineBrainFactory(), out var tools);
        RegisterEcho(tools);

        runtime.RegisterAgent(new AgentDefinition
        {
            Id = "rules-engine",
            Name = "RulesEngine",
            Engine = EngineKind.Rule,
            Tools = new[] { "Echo" },
            RuleSource = TwoStepWorkflow,
        });

        var session = runtime.StartSession("rules-engine");
        var text = await runtime.RunAsync(session.SessionId, "查600000");

        // 与 program 后端同一可观测行为：规则产出工具调用 → MAF 执行回填 → 规则产出终答。
        Assert.Equal("结果：echo:600000", text);
        Assert.Equal(new[] { "Echo" }, session.ToolCalls);
    }

    [Fact]
    public async Task RulesEngine_translates_a_matched_rule_into_a_tool_call_with_converted_args()
    {
        var brain = new RulesEngineBrain(TwoStepWorkflow);
        var context = new BrainContext { LatestUser = new ChatMessage(ChatRole.User, "查600000") };

        var contents = await brain.DecideAsync(context, CancellationToken.None);

        var call = Assert.Single(contents.OfType<FunctionCallContent>());
        Assert.Equal("Echo", call.Name);
        Assert.Equal("600000", call.Arguments!["text"]);
    }

    [Fact]
    public async Task RulesEngine_uses_the_tool_result_for_the_follow_up_rule()
    {
        var brain = new RulesEngineBrain(TwoStepWorkflow);
        var context = new BrainContext
        {
            LatestUser = new ChatMessage(ChatRole.User, "查600000"),
            LatestToolResult = new FunctionResultContent("call-1", "ok-600000"),
            ToolCallsThisTurn = 1,
        };

        var contents = await brain.DecideAsync(context, CancellationToken.None);

        Assert.Equal("结果：ok-600000", Assert.IsType<TextContent>(Assert.Single(contents)).Text);
    }

    [Fact]
    public async Task RulesEngine_returns_the_no_match_reply_when_no_action_rule_matches()
    {
        var brain = new RulesEngineBrain(TwoStepWorkflow, new RulesEngineRuleOptions { NoMatchReply = "不懂" });
        var context = new BrainContext { LatestUser = new ChatMessage(ChatRole.User, "随便聊聊") };

        var contents = await brain.DecideAsync(context, CancellationToken.None);

        Assert.Equal("不懂", Assert.IsType<TextContent>(Assert.Single(contents)).Text);
    }

    [Fact]
    public async Task RulesEngine_stops_at_the_tool_call_limit()
    {
        var brain = new RulesEngineBrain(TwoStepWorkflow, new RulesEngineRuleOptions { MaxSteps = 2 });
        var context = new BrainContext
        {
            LatestUser = new ChatMessage(ChatRole.User, "查600000"),
            ToolCallsThisTurn = 2,
        };

        var contents = await brain.DecideAsync(context, CancellationToken.None);

        Assert.Contains("步数超限", Assert.IsType<TextContent>(Assert.Single(contents)).Text);
    }

    [Fact]
    public async Task RulesEngine_prefers_reply_when_a_rule_declares_both_reply_and_tool()
    {
        const string source = """
            [{
              "WorkflowName": "agent",
              "Rules": [
                { "RuleName": "both", "Expression": "true", "Properties": { "tool": "Echo", "reply": "先回答" } }
              ]
            }]
            """;
        var brain = new RulesEngineBrain(source);

        var contents = await brain.DecideAsync(new BrainContext(), CancellationToken.None);

        Assert.Equal("先回答", Assert.IsType<TextContent>(Assert.Single(contents)).Text);
    }

    [Fact]
    public void RulesEngine_requires_rule_source()
    {
        var factory = new RulesEngineBrainFactory();
        var definition = new AgentDefinition { Id = "rules-engine", Engine = EngineKind.Rule };

        var error = Assert.Throws<InvalidOperationException>(() => factory.Create(definition));

        Assert.Contains("RuleSource", error.Message);
    }

    [Fact]
    public void RulesEngine_rejects_invalid_or_missing_workflow_json()
    {
        Assert.Contains("workflow",
            Assert.Throws<InvalidOperationException>(() => new RulesEngineBrain("{ not json")).Message);
        Assert.Contains("workflow",
            Assert.Throws<InvalidOperationException>(() => new RulesEngineBrain("[]")).Message);
    }

    [Fact]
    public void RulesEngine_selects_the_configured_workflow_and_lists_candidates_when_missing()
    {
        const string source = """
            [
              { "WorkflowName": "first", "Rules": [ { "RuleName": "r", "Expression": "true", "Properties": { "reply": "first" } } ] },
              { "WorkflowName": "second", "Rules": [ { "RuleName": "r", "Expression": "true", "Properties": { "reply": "second" } } ] }
            ]
            """;

        Assert.Equal("second",
            new RulesEngineBrain(source, new RulesEngineRuleOptions { WorkflowName = "second" }).WorkflowName);

        var error = Assert.Throws<InvalidOperationException>(
            () => new RulesEngineBrain(source, new RulesEngineRuleOptions { WorkflowName = "missing" }));
        Assert.Contains("first", error.Message);
        Assert.Contains("second", error.Message);
    }

    [Fact]
    public void RulesEngine_is_selected_by_brain_backend_next_to_the_program_backend()
    {
        var registry = new BrainFactoryRegistry();
        registry.Register(new RuleBrainFactory());
        registry.Register(new RulesEngineBrainFactory());

        Assert.IsType<RulesEngineBrainFactory>(
            registry.Resolve(new AgentDefinition { Id = "re", Engine = EngineKind.Rule, BrainBackend = "rulesEngine" }));
        Assert.IsType<RuleBrainFactory>(
            registry.Resolve(new AgentDefinition { Id = "p", Engine = EngineKind.Rule, BrainBackend = "program" }));

        // 两个后端都在时不允许歧义。
        Assert.Throws<InvalidOperationException>(
            () => registry.Resolve(new AgentDefinition { Id = "none", Engine = EngineKind.Rule }));
    }

    [Fact]
    public void AddRulesEngineRuleBrains_registers_a_resolvable_factory()
    {
        var services = new ServiceCollection();
        services.AddRulesEngineRuleBrains(options => options.NoMatchReply = "x");
        using var provider = services.BuildServiceProvider();

        var factory = Assert.Single(provider.GetServices<IBrainFactory>());
        Assert.IsType<RulesEngineBrainFactory>(factory);
        Assert.Equal("x", provider.GetRequiredService<RulesEngineRuleOptions>().NoMatchReply);
    }

    [Fact]
    public void Routing_with_a_native_rule_backend_is_reported()
    {
        var issues = AgentDefinitionValidation.Check(new AgentDefinition
        {
            Id = "route",
            Engine = EngineKind.Llm,
            EnableBrainRouting = true,
            BrainBackend = "rulesEngine",
            RuleSource = TwoStepWorkflow,
        });

        Assert.Contains(issues, issue => issue.Code == "RoutingMatcherBackendMismatch" && !issue.IsError);
    }

    [Fact]
    public void Rule_payload_validation_accepts_a_native_source_only()
    {
        var issues = AgentDefinitionValidation.Check(new AgentDefinition
        {
            Id = "re",
            Engine = EngineKind.Rule,
            RuleSource = TwoStepWorkflow,
        });

        Assert.DoesNotContain(issues, issue => issue.Code == "EmptyRuleProgram");
    }

    // —— 辅助 ——

    private static HarnessRuntime Runtime(IBrainFactory factory, out ToolRegistry tools)
    {
        tools = new ToolRegistry();
        var brains = new BrainFactoryRegistry();
        brains.Register(factory);
        var agentFactory = new AgentFactory(new NullChatClientFactory(), tools, new SkillRegistry(), brainFactories: brains);
        return new HarnessRuntime(agentFactory, new SessionManager());
    }

    private static void RegisterEcho(ToolRegistry tools) => tools.Register(
        "Echo", "回显", "测试",
        _ => AIFunctionFactory.Create((Func<string, string>)(text => $"echo:{text}"), "Echo"));

    private sealed class NullChatClientFactory : IChatClientFactory
    {
        public IChatClient Create(string? modelId) => throw new NotSupportedException();
    }
}
