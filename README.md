# Aether.AI

Microsoft Agent Framework 之上的本地 Agent 运行时。基础大脑只有三类：`Llm`、`Rule`、`Code`；C# / F# 是 Code 的作者语言，Routing 是显式组合策略，源码编译是可选加载能力。

## 项目边界

| 项目 | 职责 |
|---|---|
| `Aether.AI.Core` | 领域模型；保留旧 `EngineKind` 数值并新增 `Code=2` |
| `Aether.AI.Abstractions` | MCP 等现有稳定契约 |
| `Aether.AI.Brains.Abstractions` | 大脑接缝：`IBrain` / `IBrainFactory.CreateChatClient` / 能力集 / 共享 `BrainContextBuilder` / `BrainChatClient` 适配器 / 定义校验 |
| `Aether.AI.Brains.Llm` | OpenAI-compatible 默认模型客户端；注册 `LlmBrainFactory`（LLM 也是一等大脑）；显式 opt-in |
| `Aether.AI.Brains.Rule` | 声明式 `RuleProgram` 求值与 Rule 大脑工厂（`program` 后端） |
| `Aether.AI.Brains.Rule.RulesEngine` | 可选规则后端：Microsoft RulesEngine workflow JSON（`rulesEngine` 后端） |
| `Aether.AI.Brains.Rule.NRules` | 可选规则后端：NRules C# 规则类 + Rete 议程（`nRules` 后端） |
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

## 大脑接缝

LLM 不是运行时的前提，只是三类大脑之一。Harness 只认一个接缝：

```text
AgentDefinition ──► IBrainFactoryRegistry ──► IBrainFactory.CreateChatClient() ──► IChatClient ──► MAF HarnessAgent
                         │                            │
                         │                            ├─ Llm  : 直接返回模型客户端
                         │                            ├─ Rule : BrainChatClient(RuleProgramBrain)
                         │                            └─ Code : BrainChatClient(RunScopedBrainRouter)
                         └─ Capabilities → 决定 Harness 挂哪些 provider / 是否应用采样参数
```

- **能力集驱动接线**：`BrainCapabilities`（`Tools` / `NaturalLanguageContext` / `Sampling` / `Skills` / `StatefulRun`）取代 `EngineKind` 分支。没有 `NaturalLanguageContext` 的大脑不挂 todo/skill/memory-recall/agent-mode，也不应用温度等采样参数。
- **确定性大脑的传输是适配器**：`IContentBrainFactory` 只负责产出 `IBrain`，默认实现用 `BrainChatClient` 包装；工具执行、回填、循环、审批、流式事件仍全部归 MAF，三种大脑共用同一条执行链路。
- **流式与非流式同源**：`BrainChatClient` 与路由客户端都实现了 `GetResponseAsync`，`HarnessRuntime.RunAsync` 对规则/代码大脑同样可用。
- **未注册即报错，配置无效即告警**：`AgentDefinitionValidation` 拒绝「非 LLM 引擎 + 显式路由 / 后台子 agent」这类会改变语义的组合；对「规则 agent 上设置了温度、模型、长期记忆」等不会生效的配置输出一次构建期告警，不再静默忽略。
- **兼容路径**：宿主只注册 `IChatClientFactory`（未调用 `AddLlmBrains`）时，LLM 定义仍走原路径；一旦调用 `AddLlmBrains()`，LLM 就与 Rule/Code 使用同一注册表。
- **接缝选点**：大脑适配成 `IChatClient` 而不是继承 `AIAgent`——工具循环、审批与历史都在 chat client 层，接在这里才能让三类大脑共用同一条 MAF 执行链路。取舍与代价见 [`doc/brain-seam.md`](doc/brain-seam.md#接缝为什么在-ichatclient而不是-aiagent-子类)。
- **会话可持久化**：可选 `IAgentSessionStore`（默认不启用）。配置后每轮落盘，重启用 `StartSession(agentId, sessionId)` 续聊；`MafAgentSessionStore` 与 MAF 生态共用同一份存储。
- **按名字取 agent**：`AddAgent` 同时以 id 为键注册 `AIAgent`，MAF 生态按名取用。
- **规则后端可替换**：同一 `EngineKind.Rule` 下可有多个后端——内置 `program`（自研声明式）、`rulesEngine`（Microsoft RulesEngine）、`nRules`（NRules），三者都只是「如何求值规则」的适配器。只引用一个时零配置；引用多个时用 `AgentDefinition.BrainBackend` 显式选择，歧义报错而非按注册顺序取胜。后端只替换「如何求值规则」，工具回填与循环不变。

新增第四类大脑只需：实现 `IBrainFactory`（或 `IContentBrainFactory` + `IBrain`）→ 声明能力集 → 在一个 `AddXxxBrains()` 扩展里 `TryAddEnumerable` 注册；Harness、Hosting、路由代码都不需要改。详见 [`doc/brain-seam.md`](doc/brain-seam.md)。

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

## 数据迁移

`EngineKind.Llm=0`、`Rule=1` 的序列化值保持不变，`Code=2`。历史数据须一次性把 `Engine=Rule, IsCodeRule=true` 更新为 `Engine=Code`，随后删除 `IsCodeRule` 字段；运行时不再提供兼容映射。

## 状态与执行

Code brain 每个逻辑用户 turn 创建 fresh instance。工具调用产出后，以 `CallId` 绑定实例；结果回填原子恢复同一实例。正常终答、异常和取消均不复用该实例，不同 session 不共享协程续体、上下文、中间结果或作者字段。

三类大脑继续复用 Harness 的工具池、调用/回填循环、审批、会话、事件和进度，不复制执行循环。Rule/Code 不自动消费仅对自然语言模型有意义的 Skills、采样和长期记忆提示。

## 构建与测试

```bash
dotnet build aether-ai.slnx -c Release
dotnet test aether-ai.slnx -c Release --no-build
```

当前架构在 .NET SDK 10.0.301 下完成 Release 验收：38 个项目（20 个 src + 14 个测试程序集 + 4 个示例宿主）编译 0 警告、0 错误（全量重编时 NuGet 审计会对 OTel 1.12.0 的既有 pin 报 NU1902/NU1903，与代码无关），14 个测试程序集共 291 项全部通过。行覆盖率 77.1%（`dotnet test aether-ai.slnx -c Release --collect:"XPlat Code Coverage"`，按 Cobertura 逐行合并）：覆盖统一大脑接缝、非流式路径、多步路由链、定义校验、规则后端选择、`rulesEngine` / `nRules` 适配器、会话持久化、轻量路径配置一致性、MCP 配置读写与失败隔离、长期记忆注入/沉淀闭环，以及各语言的代码大脑注册与退化路径。

测试按被测项目拆分（`tests/<项目名>.Tests`），不再有一个跨全栈的 `Harness.Tests` 巨包：`Aether.AI.Core.Tests` / `Brains.Abstractions.Tests` / `Brains.Rule{,.RulesEngine,.NRules}.Tests` / `Brains.Code{,.Compilation}.Tests` / `Brains.Routing.Tests` / `Brains.Llm.Tests` / `Harness.Tests` / `Hosting.Tests` / `Mcp.Tests` / `Memory.Tests`；跨包属性集中在 `Integration.Tests`（见下）。已知未覆盖的缺口分两类：必须起外部进程/真实端点的（`SkillScriptRunner` 的脚本子进程、MCP 的实连与重连分支），由宿主侧验收兜底；以及刻意保持 `internal`、只经公开接缝间接验证的纯格式化代码（`StreamContentFormat`）。

跨包属性由 `tests/Aether.AI.Integration.Tests` 用普通 `dotnet test` 守住，不依赖额外的控制台工程：

- **同一宿主装下全部大脑**：program / rulesEngine / nRules / Code / LLM 在一个 DI 容器里共存，同一输入、同一工具、同一期望输出逐个实跑（LLM 用脚本化替身，本机无 key）；真实端点仍由宿主侧验收。
- **依赖闭包边界**：从形态的代表程序集递归读 `Assembly.GetReferencedAssemblies`——Rule-only 与预编译 Code 形态禁止 OpenAI / Roslyn / FCS；动态 C# 形态允许 Roslyn，仍禁止 OpenAI 与 FCS。另有一条反向对照测试，确保遍历机制本身有效（LLM 包的闭包确实含 OpenAI），避免「永远返回空集」式假绿。

`samples/` 下是四个可运行的最小宿主示例（RuleOnly / PrecompiledCode / CSharpCompilation / BrainMatrix），供接入方复制改造；它们随 solution 构建，但不承担断言职责。

具体工具实现（文件/搜索/HTTP/命令、链上钱包等）不放在本仓：工具是「业务/宿主 + AI」，归 ⑭ `aether-ai-integrations`（如 `Aether.SystemTools.AI` 的 `AddSystemTools()`）。本仓只提供工具契约 `ToolRegistry` / `ToolApprovalLevel` / `HostCapability`。
