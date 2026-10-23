# Aether.AI 宿主接入

## 三类大脑

| 类型 | 决策方式 | 注册 |
|---|---|---|
| Llm | 模型理解上下文，决定回复或工具调用 | `AddLlmBrains(...)` |
| Rule | 声明式规则：`RuleProgram`（自研）／RulesEngine／NRules 后端 | `AddRuleBrains()` / `AddRulesEngineRuleBrains()` / `AddNRulesRuleBrains()` |
| Code | 作者程序决定下一步；C# / F# 为语言实现 | `AddCSharpCodeBrains()` / `AddFSharpCodeBrains()` |

Routing 是显式组合，不是第四类大脑；动态编译也是加载能力，不是大脑类别。

三类大脑都经 `IBrainFactory` 注册表解析，Harness 不再按 `EngineKind` 分支。调用 `AddLlmBrains()`
后 LLM 也只是「具备 `NaturalLanguageContext` 与 `Sampling` 能力的注册大脑」；宿主只提供
`IChatClientFactory` 而未注册 LLM 工厂时，LLM 定义仍走兼容路径。

### 非流式运行

`HarnessRuntime.RunAsync`（非流式）对三类大脑都可用。确定性大脑的适配器实现了
`GetResponseAsync`，与流式共用同一决策函数；路由客户端同样支持非流式。

## 通用骨架

```csharp
var services = new ServiceCollection();
services.AddRuleBrains();
var aether = services.AddAetherAI();

aether.ConfigureToolRegistry(tools => tools.Register(
    "Echo", "回声", "示例",
    _ => AIFunctionFactory.Create((Func<string, string>)(text => $"echo:{text}"), name: "Echo")));

aether.AddAgent("router", agent => agent
    .Name("路由")
    .Engine(EngineKind.Rule)
    .Rules(new RuleProgram
    {
        Steps = new()
        {
            new RuleStep
            {
                Match = new() { OnUserRegex = @"查(?<sym>\d+)", MaxToolCalls = 0 },
                Action = new() { Tool = "Echo", Args = new() { ["text"] = "{user.1}" } },
            },
            new RuleStep
            {
                Match = new() { MinToolCalls = 1 },
                Action = new() { ReplyText = "结果：{result}" },
            },
        },
    })
    .WithTool("Echo"));

using var provider = services.BuildServiceProvider();
var runtime = provider.GetRequiredService<HarnessRuntime>();
var session = runtime.StartSession("router");
await runtime.RunStreamingAsync(session.SessionId, "查600000", new StreamHandlers());
```

`AddAetherAI()` 只注册通用设施。缺少所选大脑实现时，在真正创建该 Agent 时给出明确错误，不会在 DI 组合阶段解析模型。

## 规则后端

`Engine(EngineKind.Rule)` 的求值后端可替换：声明式规则大脑既可以自己实现，也可以直接接入
现成规则引擎。内置 `program` 求值 `RuleProgram`；`rulesEngine` 求值 Microsoft RulesEngine 的
workflow JSON；`nRules` 求值 NRules 的 C# 规则类。

| 后端 | 注册 | 规则定义 |
|---|---|---|
| `program` | `AddRuleBrains()` | `Rules(new RuleProgram { ... })` |
| `rulesEngine` | `AddRulesEngineRuleBrains()` | `RuleSource(workflowJson)` |
| `nRules` | `AddNRulesRuleBrains()` | `RuleSource({"ruleTypes": [...]})` |

- 宿主只引用一个后端包：无需任何配置。
- 引用多个后端：每个规则 agent 必须显式选择，否则解析报错并列出全部候选——不会按注册顺序静默取胜。

```csharp
services.AddRuleBrains();
services.AddRulesEngineRuleBrains();

aether.AddAgent("rule", agent => agent
    .Engine(EngineKind.Rule)
    .BrainBackend("rulesEngine")   // 注册多个后端时必须
    .RuleSource("""
      [{
        "WorkflowName": "agent",
        "Rules": [
          {
            "RuleName": "query",
            "Expression": "brain.UserText.StartsWith(\"查\") && brain.ToolCallCount == 0",
            "Properties": { "tool": "Echo", "capture": "查(?<sym>.+)", "args": { "text": "{user.1}" } }
          },
          {
            "RuleName": "reply",
            "Expression": "brain.ToolCallCount == 1 && brain.LastResult != null",
            "Properties": { "reply": "结果：{result}" }
          }
        ]
      }]
      """)
    .WithTool("Echo"));
```

RulesEngine 独立求值每条规则，同一步可能命中多条。适配器按声明顺序取「第一条命中且带动作」的
规则，重叠命中记一次 warning；请用 `brain.ToolCallCount` / `brain.LastResult` 把步骤写成互斥条件，
否则首条规则会在拿到工具结果后再次命中。动作载荷（`reply` / `tool` + `args` / `capture`）与事实
字段见 [`brain-seam.md`](brain-seam.md#rulesengine-适配器契约)。

### NRules 后端

NRules 的规则是 C# 类，因此 `RuleSource` 只声明加载哪些规则（类型或程序集）：

```csharp
services.AddNRulesRuleBrains();

aether.AddAgent("rule", agent => agent
    .Engine(EngineKind.Rule)
    .RuleSource("""{ "ruleTypes": ["MyApp.Rules.QueryRule"] }""")
    .WithTool("Echo"));

// 规则本身是 NRules 原生写法：
public sealed class QueryRule : Rule
{
    public override void Define()
    {
        NRulesDecisionContext d = null!;
        When().Match<NRulesDecisionContext>(() => d,
            x => x.UserText.StartsWith("查") && x.ToolCallCount == 0);
        Then().Do(_ => d.CallTool("Echo", "text", d.UserText.Substring(1)));
    }
}
```

每个决策步新建会话、插入新事实，规则编译只在创建 agent 时做一次；Rete 匹配与议程在步内生效，
跨步状态用 `ToolCallCount` / `LastResult` 从对话历史重建。细节见
[`brain-seam.md`](brain-seam.md#nrules-适配器契约)。

后端选择会传播到路由组合内部的规则分支，但路由的入口匹配只理解 `RuleProgram`：
`EnableBrainRouting` 搭配原生规则后端会产出 `RoutingMatcherBackendMismatch` 告警（规则分支永不命中）。

## LLM

```csharp
services.AddLlmBrains(options =>
{
    options.ApiKey = configuration["OPENAI_API_KEY"] ?? "";
    options.ModelId = "your-model";
    options.Endpoint = "https://your-openai-compatible-endpoint"; // 可选
});
services.AddAetherAI();
```

多 provider 宿主可直接注册 `Aether.AI.Brains.Abstractions.IChatClientFactory`。工厂仅在 Llm Agent 实际创建或 Routing 未命中时调用。

## Code 与编译器

运行已编译 C# Code brain：

```csharp
services.AddCSharpCodeBrains();
services.AddAetherAI();
```

动态编译按语言独立选择：

```csharp
services.AddCSharpCodeBrains();
services.AddCSharpBrainCompilation(); // Roslyn，不携带 FCS
```

```csharp
services.AddFSharpCodeBrains();
services.AddFSharpBrainCompilation(); // FCS + ref-pack
```

Code brain 生命周期按逻辑 turn 隔离：同一 run 的多次工具调用复用一个实例，不同 session 或下一 turn 使用新实例。作者字段不跨 turn 保留。工具结果通过 `CallId` 恢复原实例；取消和异常不会创建替代实例。

## Rule 优先 Routing

```csharp
services.AddRuleBrains();
services.AddLlmBrains(options => { /* ... */ }); // 可选；无 LLM 时返回明确未匹配提示
services.AddBrainRouting();
services.AddAetherAI();

aether.AddAgent("mixed", agent => agent
    .Engine(EngineKind.Llm)
    .EnableBrainRouting()
    .Rules(ruleProgram));
```

Routing 默认关闭，仅在显式设置 `EnableBrainRouting=true` 时启用。它先执行无副作用匹配：`RuleProgram.Capture`，或安全的首步 `OnUserRegex`。命中后固定由 Rule 处理整轮；未命中才懒解析 LLM，并传递原始会话历史。工具失败、业务拒绝、审批拒绝、取消或代码异常都不会自动改由 LLM 重做。

多步规则链现在只消费「本客户端发出且尚未回填」的工具结果：历史里更早步骤已消费的结果会被跳过，不会误判 `StateLost`（回归钉：`Routing_consumes_only_pending_call_results_across_multiple_tool_steps`）。当前仍有两项已知限制：matcher 只选择单一入口，不组合多个候选；混合分支不会完整保留 LLM sampling、response format 和 provider-specific capabilities。

## 定义校验

`HarnessRuntime.RegisterAgent` 与 `AgentFactory.Create` 都会校验定义：

- **错误**（抛 `InvalidOperationException`）：非 LLM 引擎启用 `EnableBrainRouting`（路由会接管所选大脑）；非 LLM 引擎声明 `BackgroundAgents`（委派由 LLM 工具回合驱动）。
- **告警**（构建期记录一次，不刷屏）：规则/代码 agent 上设置了 `ModelId`、`Temperature`、`TopP`、`MaxOutputTokens`、`ResponseFormat`、`EnableWebSearch`、`EnableTodoProvider`、`EnableAgentModeProvider`、`EnableFileMemory`、`EnableLongTermMemory`——这些只对具备 `NaturalLanguageContext` 的大脑生效；LLM agent 携带 `Rules`/`RuleSource` 但未开路由；Rule agent 既无 `Rules` 也无 `RuleSource`（`EmptyRuleProgram`）；路由选择了非 `program` 规则后端（`RoutingMatcherBackendMismatch`）。

宿主可用 `AgentDefinitionValidation.Check(definition, capabilities)` 在编辑器中提前展示同一套诊断。

## 历史数据迁移

`EngineKind.Llm=0`、`Rule=1` 的数值保持不变，`Code=2`。对历史定义执行一次性迁移：将 `Engine=Rule, IsCodeRule=true` 更新为 `Engine=Code`，将其余定义保留原 `Engine`，然后删除 `IsCodeRule` 字段。运行时不读取该旧字段。

## 能力边界

- 工具池、工具调用/回填、审批、事件、会话和进度由 Harness 统一提供。
- Rule/Code 不会因为共享 Harness 就理解自然语言 Skills、模型采样或摘要提示——这一边界由
  `BrainCapabilities` 显式表达，并由定义校验提示无效配置。
- 接缝与扩展方式见 [`brain-seam.md`](brain-seam.md)。
- iOS 等禁止子进程的宿主不要启用文件 Skill 脚本或动态源码编译。
- 当前仓内未验证真实模型端点、跨进程 MCP、GUI 和移动端；测试使用可控替身。
