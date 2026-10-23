using Aether.AI.Brains.Abstractions;
using Aether.AI.Brains.Rule;
using Aether.AI.Brains.Rule.NRules;
using Aether.AI.Brains.Rule.RulesEngine;
using Aether.AI.Core.Agents;
using Aether.AI.Harness;
using Aether.AI.Harness.Agents;
using Aether.AI.Harness.Sessions;
using Aether.AI.Harness.Skills;
using Aether.AI.Harness.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
// 命名空间在 Aether.AI.Brains.Rule.* 下，裸 `Rule` 会解析成命名空间而非 NRules 基类——显式取别名。
using NRulesRule = global::NRules.Fluent.Dsl.Rule;

namespace Aether.AI.Brains.Rule.NRules.Tests;

/// <summary>
/// NRules 作为「可选声明式规则后端」的适配器契约：
/// ① 规则是 NRules 原生 C# 规则类，Rete 匹配与议程归 NRules；
/// ② 规则通过 <see cref="NRulesDecisionContext"/> 声明动作，适配器翻译成 <see cref="AIContent"/>；
/// ③ <c>RuleSource</c> 只声明加载哪些规则（类型/程序集），不承载规则体；
/// ④ 工具执行、回填与多步循环仍由 Harness 负责，与其他规则后端一致。
/// </summary>
public class NRulesBrainTests
{
    private const string RuleTypes = """
        {
          "ruleTypes": [
            "Aether.AI.Brains.Rule.NRules.Tests.NRulesQueryRule",
            "Aether.AI.Brains.Rule.NRules.Tests.NRulesReplyRule"
          ]
        }
        """;

    [Fact]
    public async Task NRules_backend_drives_a_harness_agent_through_tool_call_and_reply()
    {
        var runtime = Runtime(new NRulesBrainFactory(), out var tools);
        RegisterEcho(tools);

        runtime.RegisterAgent(new AgentDefinition
        {
            Id = "nrules",
            Name = "NRules",
            Engine = EngineKind.Rule,
            Tools = new[] { "Echo" },
            RuleSource = RuleTypes,
        });

        var session = runtime.StartSession("nrules");
        var text = await runtime.RunAsync(session.SessionId, "查600000");

        Assert.Equal("结果：echo:600000", text);
        Assert.Equal(new[] { "Echo" }, session.ToolCalls);
    }

    [Fact]
    public async Task NRules_translates_a_fired_rule_into_a_tool_call()
    {
        var brain = new NRulesBrain(RuleTypes);
        var context = new BrainContext { LatestUser = new ChatMessage(ChatRole.User, "查600000") };

        var contents = await brain.DecideAsync(context, CancellationToken.None);

        var call = Assert.Single(contents.OfType<FunctionCallContent>());
        Assert.Equal("Echo", call.Name);
        Assert.Equal("600000", call.Arguments!["text"]);
    }

    [Fact]
    public async Task NRules_uses_the_tool_result_for_the_follow_up_rule()
    {
        var brain = new NRulesBrain(RuleTypes);
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
    public async Task NRules_returns_the_no_match_reply_when_no_rule_fires()
    {
        var brain = new NRulesBrain(RuleTypes, new NRulesRuleOptions { NoMatchReply = "不懂" });
        var context = new BrainContext { LatestUser = new ChatMessage(ChatRole.User, "随便聊聊") };

        var contents = await brain.DecideAsync(context, CancellationToken.None);

        Assert.Equal("不懂", Assert.IsType<TextContent>(Assert.Single(contents)).Text);
    }

    [Fact]
    public async Task NRules_stops_at_the_tool_call_limit()
    {
        var brain = new NRulesBrain(RuleTypes, new NRulesRuleOptions { MaxSteps = 2 });
        var context = new BrainContext
        {
            LatestUser = new ChatMessage(ChatRole.User, "查600000"),
            ToolCallsThisTurn = 2,
        };

        var contents = await brain.DecideAsync(context, CancellationToken.None);

        Assert.Contains("步数超限", Assert.IsType<TextContent>(Assert.Single(contents)).Text);
    }

    [Fact]
    public async Task NRules_loads_rules_from_an_assembly_declaration()
    {
        var assembly = typeof(NRulesQueryRule).Assembly.GetName().Name;
        var brain = new NRulesBrain($$"""{ "ruleAssemblies": ["{{assembly}}"] }""");

        var context = new BrainContext { LatestUser = new ChatMessage(ChatRole.User, "查600000") };
        var contents = await brain.DecideAsync(context, CancellationToken.None);

        Assert.Single(contents.OfType<FunctionCallContent>());
    }

    [Fact]
    public void NRules_requires_rule_source()
    {
        var factory = new NRulesBrainFactory();
        var definition = new AgentDefinition { Id = "nrules", Engine = EngineKind.Rule };

        var error = Assert.Throws<InvalidOperationException>(() => factory.Create(definition));

        Assert.Contains("RuleSource", error.Message);
    }

    [Fact]
    public void NRules_reports_unresolvable_rule_declarations()
    {
        Assert.Contains("找不到规则类型",
            Assert.Throws<InvalidOperationException>(
                () => new NRulesBrain("""{ "ruleTypes": ["No.Such.RuleType"] }""")).Message);

        Assert.Contains("未继承",
            Assert.Throws<InvalidOperationException>(
                () => new NRulesBrain("""{ "ruleTypes": ["System.String"] }""")).Message);

        Assert.Contains("未声明规则",
            Assert.Throws<InvalidOperationException>(
                () => new NRulesBrain("""{ "ruleTypes": [] }""")).Message);
    }

    [Fact]
    public void NRules_is_selected_by_brain_backend_among_all_rule_backends()
    {
        var registry = new BrainFactoryRegistry();
        registry.Register(new RuleBrainFactory());
        registry.Register(new RulesEngineBrainFactory());
        registry.Register(new NRulesBrainFactory());

        Assert.IsType<NRulesBrainFactory>(
            registry.Resolve(new AgentDefinition { Id = "n", Engine = EngineKind.Rule, BrainBackend = "nRules" }));
        Assert.IsType<RulesEngineBrainFactory>(
            registry.Resolve(new AgentDefinition { Id = "r", Engine = EngineKind.Rule, BrainBackend = "rulesEngine" }));
        Assert.IsType<RuleBrainFactory>(
            registry.Resolve(new AgentDefinition { Id = "p", Engine = EngineKind.Rule, BrainBackend = "program" }));

        var error = Assert.Throws<InvalidOperationException>(
            () => registry.Resolve(new AgentDefinition { Id = "none", Engine = EngineKind.Rule }));
        Assert.Contains("'nRules'", error.Message);
        Assert.Contains("'rulesEngine'", error.Message);
        Assert.Contains("'program'", error.Message);
    }

    [Fact]
    public void AddNRulesRuleBrains_registers_a_resolvable_factory()
    {
        var services = new ServiceCollection();
        services.AddNRulesRuleBrains(options => options.NoMatchReply = "x");
        using var provider = services.BuildServiceProvider();

        Assert.IsType<NRulesBrainFactory>(Assert.Single(provider.GetServices<IBrainFactory>()));
        Assert.Equal("x", provider.GetRequiredService<NRulesRuleOptions>().NoMatchReply);
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

/// <summary>测试用 NRules 规则：首步查证券代码并调用工具。</summary>
public sealed class NRulesQueryRule : NRulesRule
{
    public override void Define()
    {
        NRulesDecisionContext decision = null!;
        When().Match<NRulesDecisionContext>(() => decision,
            d => d.UserText.StartsWith("查") && d.ToolCallCount == 0);
        Then().Do(_ => decision.CallTool("Echo", "text", decision.UserText.Substring(1)));
    }
}

/// <summary>测试用 NRules 规则：拿到工具结果后回复。</summary>
public sealed class NRulesReplyRule : NRulesRule
{
    public override void Define()
    {
        NRulesDecisionContext decision = null!;
        When().Match<NRulesDecisionContext>(() => decision,
            d => d.ToolCallCount == 1 && d.LastResult != null);
        Then().Do(_ => decision.Reply("结果：" + decision.LastResult));
    }
}
