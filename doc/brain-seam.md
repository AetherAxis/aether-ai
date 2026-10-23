# 大脑接缝（Brain Seam）

Aether.AI 建在 Microsoft Agent Framework 之上，但**不假设大脑是 LLM**。本文件说明接缝的
分层、不变量，以及新增一类大脑需要做什么。

## 三层

| 层 | 类型 | 职责 | 是否认识 LLM |
|---|---|---|---|
| 定义 | `AgentDefinition`（Core） | 纯数据：身份、工具、记忆策略、大脑参数 | 否（含 LLM 字段，但由校验标记是否生效） |
| 决策 | `IBrainFactory`（Brains.Abstractions） | 按定义产出 MAF 传输；声明能力集 | 否 |
| 执行 | MAF `HarnessAgent` | 工具调用/回填、审批、流式事件、历史、进度 | 否 |

`IBrainFactory.CreateChatClient(AgentDefinition)` 是唯一接缝。Harness 不出现
`EngineKind.Rule` / `EngineKind.Code` 分支，也不出现「非 LLM 就关掉这些 provider」的硬编码。

## 不变量

1. **同一执行链路**：确定性大脑通过 `BrainChatClient` 产出 `AIContent`，工具执行与循环仍由 MAF 完成；
   不允许大脑自行执行工具。
2. **同一上下文构造**：所有非 LLM 适配器必须用 `BrainContextBuilder.Build`。审批响应与工具结果也使用
   User 角色，只有「User 角色 + 非空文本」才算本轮业务输入。
3. **能力集决定接线**：`NaturalLanguageContext` 缺失时不挂 todo / skill / memory-recall / agent-mode
   provider；`Sampling` 缺失时忽略温度、TopP、max tokens 与 response format。
4. **会话状态可回收**：`BrainChatClient` 实现 `ISessionStateOwner` 并转发给内层大脑，
   Harness 只持有适配器也能在会话删除时清理协程状态。
5. **流式与非流式同源**：`GetResponseAsync` 与 `GetStreamingResponseAsync` 必须由同一决策函数驱动，
   保证 `HarnessRuntime.RunAsync` 与 `RunStreamingAsync` 行为一致。
6. **失败要显式**：未注册的大脑在创建时报错；定义与大脑能力冲突在注册/创建时报错（error）或告警（warning），
   不允许静默忽略。

## 能力集

| 能力 | 含义 | Llm | Rule | Code |
|---|---|---|---|---|
| `Tools` | 可以发出工具调用 | ✓ | ✓ | ✓ |
| `NaturalLanguageContext` | 消费注入式自然语言上下文（instructions、todo、skills、memory recall、agent mode） | ✓ | — | — |
| `Sampling` | 温度/TopP/max tokens/response format 有意义 | ✓ | — | — |
| `Skills` | 可加载技能正文与脚本 | ✓ | — | — |
| `StatefulRun` | 大脑跨工具步骤保留运行态 | ✓ | — | ✓（每个逻辑 turn 一实例） |

`RuleProgram` 是声明式、无状态求值，因此不带 `StatefulRun`；Code 大脑的 `RunScopedBrainRouter`
按 `CallId` 绑定实例，带 `StatefulRun`。

## 新增第四类大脑

1. 在 `Aether.AI.Brains.<Name>` 项目里实现：
   - 若决策可抽象为「上下文 → AIContent 列表」，实现 `IBrain` + `IContentBrainFactory`（只写 `Create`，
     传输由默认实现包装成 `BrainChatClient`）；
   - 若已有自己的传输（如本地小模型、远程 agent 服务），直接实现 `IBrainFactory.CreateChatClient`。
2. 声明 `EngineKind` 与 `BrainCapabilities`。需要新能力位时在 `BrainCapabilities` 增加 `[Flags]` 值，
   并在 `AgentFactory` 对应接线处消费它（而不是加引擎分支）。
3. 提供 `Add<Name>Brains()` 扩展，用 `TryAddEnumerable` 注册 `IBrainFactory`。
4. 若需要持久化，`EngineKind` 追加枚举值；已有数值不得重排（`Llm=0`、`Rule=1`、`Code=2` 是序列化契约）。
5. 测试至少覆盖：工厂解析与能力集、流式、非流式、工具回填后续步、会话淘汰、定义校验。

## 同一引擎下的多实现（规则后端）

规则引擎是可以替换的**后端**，不是新的大脑类别：`Engine` 仍是 `Rule`，执行链路（工具回填、
审批、事件）不变，变的是「如何求值规则」。

| 包（规划命名） | 后端语义 | 适用 | 当前状态 |
|---|---|---|---|
| `Aether.AI.Brains.Rule` | 内置 `RuleProgram`：步骤 + 正则/工具计数 → 工具调用或终答，无状态 | 意图路由、固定流程、移动端/禁动态编译宿主 | **已实现**（`BackendId = "program"`） |
| `Aether.AI.Brains.Rule.RulesEngine` | Microsoft RulesEngine：JSON 规则表 + 表达式求值，无状态 | 非开发者维护的业务规则表、复杂条件矩阵 | 未实现（按需） |
| `Aether.AI.Brains.Rule.NRules` | NRules：Rete 前向链、事实 + 议程，有状态 | 事实网络推理、冲突消解、长规则集 | 未实现（按需） |

选择机制：

- 每个后端工厂声明唯一 `IBrainFactory.BackendId`（`program` / `nRules` / `rulesEngine`）。
- 宿主只引用一个后端包时，定义无需任何配置。
- 宿主引用多个后端时，每个规则 agent 必须设置 `AgentDefinition.BrainBackend`；未设置即解析，
  会**报错并列出全部候选**，不会按注册顺序静默取胜。
- `AgentBuilder.BrainBackend("nRules")` 是同一字段的流式写法；路由组合会把该字段复制给内部规则分支。

新增规则后端的步骤：在同一 `EngineKind.Rule` 下实现 `IContentBrainFactory`（`BackendId` 唯一），
把该引擎的原生规则格式映射为 `IBrain.DecideAsync` 产出的 `AIContent`；宿主侧提供
`AddXxxRuleBrains()` 并只引用需要的包。

**不要照搬后端的全部能力**：`RuleProgram` 的「首个命中步骤」与 NRules 的议程、RulesEngine 的
多规则聚合不是同一语义。后端映射只保留与 `IBrain` 契约兼容的子集（每步一个工具调用或一个终答），
并在文档中写清丢失的语义。两者都涉及运行时表达式/规则编译，在 iOS/AOT 等禁动态编译宿主上需单独验收。

**何时才值得加**：出现真实消费者（需要非开发者维护的规则表，或前向链推理）时再加；
没有消费者时新增后端只会增加依赖与两套测试，而不会提升现有 `RuleProgram` 的能力。

## 组合策略（Routing）

`Aether.AI.Brains.Routing` 是**显式组合**，不是第四类大脑：它先做无副作用的入口匹配，命中后把整轮固定
交给 Rule，未命中才懒加载 LLM。路由本身通过 `IBrainRoutingClientFactory` 注册，宿主可提供其他组合实现。

已知边界：

- matcher 只选择一个入口，不组合多个候选；
- 混合分支不会完整保留 LLM sampling / response format / provider-specific capabilities；
- 路由只支持 Rule ↔ LLM；要路由到 Code 或自定义大脑，应新增一个组合实现，而不是把目标硬编码进现有类。

## 兼容与迁移

- `AgentDefinition.Engine` 与 `EngineKind` 数值不变；`IsCodeRule` 的历史迁移见
  [`brain-engine-migration.md`](brain-engine-migration.md)。
- `AgentDefinition` 仍是扁平模型（避免破坏已持久化定义）；跨大脑的无效字段由
  `AgentDefinitionValidation` 显式化为告警，而不是拆分子类型。
- 宿主未调用 `AddLlmBrains()` 但提供了 `IChatClientFactory` 时，LLM 定义走兼容路径（能力集按
  `BrainCapabilitiesDefaults.Llm`）。
