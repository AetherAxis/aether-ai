# Aether.AI

Microsoft Agent Framework 之上的本地 Agent 运行时。基础大脑只有三类：`Llm`、`Rule`、`Code`；C# / F# 是 Code 的作者语言，Routing 是显式组合策略，源码编译是可选加载能力。

## 项目边界

| 项目 | 职责 |
|---|---|
| `Aether.AI.Core` | 领域模型；保留旧 `EngineKind` 数值并新增 `Code=2` |
| `Aether.AI.Abstractions` | MCP 等现有稳定契约 |
| `Aether.AI.Brains.Abstractions` | `IBrain`、工厂注册表、能力与无副作用匹配契约 |
| `Aether.AI.Brains.Llm` | OpenAI-compatible 默认模型客户端；显式 opt-in |
| `Aether.AI.Brains.Rule` | 声明式 `RuleProgram` 求值、注册表与 `run_rule` 工具 |
| `Aether.AI.Brains.Code` | 语言无关协程驱动、代码大脑注册表、按 run 状态隔离 |
| `Aether.AI.Brains.Code.CSharp` | 已编译 C# 作者层入口；不携带 Roslyn |
| `Aether.AI.Brains.Code.FSharp` | F# `Step` / `BrainCtx` / 线性大脑作者契约；不携带 FCS |
| `Aether.AI.Brains.Code.Compilation` | 编译器插件契约、源码加载与注册协调 |
| `.Compilation.CSharp` | Roslyn 编译实现；不引用 FCS |
| `.Compilation.FSharp` | FCS 编译实现与 .NET ref-pack 定位 |
| `Aether.AI.Brains.Routing` | Rule 优先、未命中时懒解析 LLM 的显式组合 |
| `Aether.AI.Harness` | 通用 MAF 构建、工具/审批、流式事件、会话和进度；不引用具体大脑实现 |
| `Aether.AI.Hosting` | `AddAetherAI()` 通用 DI、Memory 与 OTel；不引用 OpenAI/Roslyn/FCS |
| `Aether.AI.Memory* / Mcp / Workflows` | 原有可选设施 |

`Aether.AI.Harness.Scripting` 暂保留为兼容组合入口，`AddRuleScripting()` 同时安装 C# 和 F# 编译插件；新宿主应优先选择语言专用项目。

## 最小组合

```csharp
services.AddRuleBrains();
var aether = services.AddAetherAI();
```

纯 Rule 组合不需要模型 key，也不会通过 Hosting 传递引入 OpenAI 实现。

```csharp
services.AddCSharpCodeBrains();       // 运行已编译 Code brain，无 Roslyn/FCS
services.AddCSharpBrainCompilation(); // 仅需要动态 C# 源码时添加
services.AddAetherAI();
```

```csharp
services.AddLlmBrains(o =>
{
    o.ApiKey = "...";
    o.ModelId = "...";
    o.Endpoint = "..."; // 可选 OpenAI-compatible endpoint
});
services.AddAetherAI();
```

显式 Routing 还需 `AddRuleBrains()`、`AddBrainRouting()`，并在定义上设置 `EnableBrainRouting=true`。Rule 匹配与执行分离；Rule 接管后的异常、拒批、取消和工具失败不会切换 LLM。

## 兼容映射

- `Engine=Llm` -> Llm。
- `Engine=Rule, IsCodeRule=false` -> Rule。
- `Engine=Rule, IsCodeRule=true` -> Code。
- 新定义可直接使用 `Engine=Code`，`CodeLanguage` 默认 `CSharp`。
- `EngineKind.Llm=0`、`Rule=1` 保持不变；`Code=2`。
- `Aether.AI.Harness.Agents.IChatClientFactory`、`RuleContext`、`IRuleBrain`、`RuleChatClient` 暂保留源码兼容入口。

## 状态与执行

Code brain 每个逻辑用户 turn 创建 fresh instance。工具调用产出后，以 `CallId` 绑定实例；结果回填原子恢复同一实例。正常终答、异常和取消均不复用该实例，不同 session 不共享协程续体、上下文、中间结果或作者字段。

三类大脑继续复用 Harness 的工具池、调用/回填循环、审批、会话、事件和进度，不复制执行循环。Rule/Code 不自动消费仅对自然语言模型有意义的 Skills、采样和长期记忆提示。

## 构建与测试

```bash
dotnet build aether-ai.slnx -c Release
dotnet test aether-ai.slnx -c Release --no-build
```

当前变更环境没有可用 `dotnet`，因此本次只完成了 XML、项目引用路径、引用无环和补丁格式的静态检查；编译与测试结果以具备 .NET 10 SDK 的 CI/开发环境为准。真实模型端点、外部 MCP、GUI 与移动端仍需宿主侧验收。
