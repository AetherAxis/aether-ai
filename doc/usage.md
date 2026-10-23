# Aether.AI 宿主接入

## 三类大脑

| 类型 | 决策方式 | 注册 |
|---|---|---|
| Llm | 模型理解上下文，决定回复或工具调用 | `AddLlmBrains(...)` |
| Rule | `RuleProgram` 声明式条件、捕获和步骤 | `AddRuleBrains()` |
| Code | 作者程序决定下一步；C# / F# 为语言实现 | `AddCSharpCodeBrains()` / `AddFSharpCodeBrains()` |

Routing 是显式组合，不是第四类大脑；动态编译也是加载能力，不是大脑类别。

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

旧 `services.AddRuleScripting()` 仍可一次安装两种编译器，但会同时携带 Roslyn 与 FCS，仅用于兼容。

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

Routing 先执行无副作用匹配：`RuleProgram.Capture`，或安全的首步 `OnUserRegex`。命中后固定由 Rule 处理整轮；未命中才懒解析 LLM，并传递原始会话历史。工具失败、业务拒绝、审批拒绝、取消或代码异常都不会自动改由 LLM 重做。

LLM 主动调用 `run_rule` 仍是独立混合模式，与输入 Routing 不同。

## 兼容性

| 旧定义 | 新语义 |
|---|---|
| `Engine=Llm` | Llm |
| `Engine=Rule, IsCodeRule=false` | Rule |
| `Engine=Rule, IsCodeRule=true` | Code |
| `language` 空或 `csharp` | `CodeLanguage.CSharp` |
| `language=fsharp` | `CodeLanguage.FSharp` |

旧 `EngineKind` 数值 `0/1` 不变。新代码应使用 `GetEffectiveEngine()` 解释旧字段，不要继续散落判断 `IsCodeRule`。

## 能力边界

- 工具池、工具调用/回填、审批、事件、会话和进度由 Harness 统一提供。
- Rule/Code 不会因为共享 Harness 就理解自然语言 Skills、模型采样或摘要提示。
- `RuleToolExecutor` 是工具内自包含循环，不自动继承外层每个审批与事件钩子。
- iOS 等禁止子进程的宿主不要启用文件 Skill 脚本或动态源码编译。
- 当前仓内未验证真实模型端点、跨进程 MCP、GUI 和移动端；测试使用可控替身。
