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

| 包 | 后端语义 | 适用 | 当前状态 |
|---|---|---|---|
| `Aether.AI.Brains.Rule` | 内置 `RuleProgram`：步骤 + 正则/工具计数 → 工具调用或终答，无状态 | 意图路由、固定流程、移动端/禁动态编译宿主 | **已实现**（`BackendId = "program"`） |
| `Aether.AI.Brains.Rule.RulesEngine` | Microsoft RulesEngine：workflow JSON + 表达式求值，独立求值每条规则，无状态 | 非开发者维护的业务规则表、条件矩阵 | **已实现**（`BackendId = "rulesEngine"`） |
| `Aether.AI.Brains.Rule.NRules` | NRules：C# 规则类 + Rete 匹配 + 议程；每步新建会话，规则编译一次 | 已用 C# 表达的规则集、前向链推理、冲突消解 | **已实现**（`BackendId = "nRules"`） |

选择机制：

- 每个后端工厂声明唯一 `IBrainFactory.BackendId`（`program` / `rulesEngine` / `nRules`）。
- 宿主只引用一个后端包时，定义无需任何配置。
- 宿主引用多个后端时，每个规则 agent 必须设置 `AgentDefinition.BrainBackend`；未设置即解析，
  会**报错并列出全部候选**，不会按注册顺序静默取胜。
- `AgentBuilder.BrainBackend("rulesEngine")` 是同一字段的流式写法；路由组合会把该字段复制给内部规则分支。

### rulesEngine 适配器契约

RulesEngine 只做「哪些规则命中」，动作由适配器翻译成 `AIContent`：

| 定义/载荷 | 说明 |
|---|---|
| `AgentDefinition.RuleSource` | workflow 数组 JSON（RulesEngine 原生格式）；`RuleProgram` 不参与 |
| `RulesEngineRuleOptions.WorkflowName` | 选择 workflow；空 = 第一个 |
| 规则 `Properties.reply` | 终答文本 → `TextContent`，终止循环 |
| 规则 `Properties.tool` + `Properties.args` | 工具名与参数 → `FunctionCallContent`，MAF 执行后回填 |
| 规则 `Properties.capture` | 可选正则；其捕获组供模板 `{user.N}` 使用 |
| 模板 | 字符串值支持 `{user}` / `{result}`；`reply` 与 `tool` 同时存在时 `reply` 优先 |

语义差异（必须写进作者文档）：RulesEngine **独立求值、不短路**，同一步可能命中多条规则。
适配器按声明顺序取第一条命中且带动作的规则，重叠命中记一次 warning。作者应用
`brain.ToolCallCount`、`brain.LastResult` 等事实把规则写成互斥步骤；否则「查询」规则会在
拿到工具结果后再次命中，形成重复调用。事实对象固定为
`RulesEngineFacts { UserText, LastResult, ToolCallCount, SessionId }`。

原生后端只能用于固定大脑：路由的入口匹配固定使用 `RuleProgramMatcher`，只理解 `RuleProgram`。
`EnableBrainRouting + BrainBackend != "program"` 会产出 `RoutingMatcherBackendMismatch` 告警——
规则分支永不命中，整轮实际走 LLM。

### nRules 适配器契约

NRules 的规则是 C# 类（`NRules.Fluent.Dsl.Rule` 子类），字符串承载不了规则体，
因此 `RuleSource` 只声明**加载哪些规则**：

```json
{ "ruleTypes": ["MyApp.Rules.QueryRule", "MyApp.Rules.ReplyRule"] }
{ "ruleAssemblies": ["MyApp.Rules"] }
```

`ruleTypes` 可用简单名（在当前已加载程序集里查找）或程序集限定名（`Ns.Type, Assembly`）。

规则通过插入的事实对象声明动作：

```csharp
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

| 事实成员 | 说明 |
|---|---|
| `UserText` / `LastResult` / `ToolCallCount` / `SessionId` / `HasToolResult` | 与本轮 `BrainContext` 一致 |
| `Reply(string)` | 终答 → `TextContent` |
| `CallTool(string tool, params object?[] nameValuePairs)` | 工具调用 → `FunctionCallContent`；参数成对展开 |

- `Then().Do(...)` 是表达式树，所以发射接口做成单次调用即可完成（不能写语句块或对象/集合初始化器）。
- 每步只接受第一个动作，后续动作被忽略并记 warning；用 `ToolCallCount` 等守卫让规则互斥。
- **会话策略**：每个决策步新建 `ISession` 并插入新事实，规则编译（`ISessionFactory`）只在
  构造时做一次。Rete 匹配、前向链、议程、冲突消解都在这一步内生效；跨步状态不保留，
  与 `RuleProgram` 的无状态语义一致——需要跨步信息时用对话历史（`ToolCallCount`/`LastResult`）重建，
  而不是依赖上一次会话的事实。
- NRules 在运行时编译规则（表达式树），iOS/AOT 宿主需单独验收。

新增规则后端的步骤：在同一 `EngineKind.Rule` 下实现 `IContentBrainFactory`（`BackendId` 唯一），
把该引擎的原生规则格式映射为 `IBrain.DecideAsync` 产出的 `AIContent`；宿主侧提供
`AddXxxRuleBrains()` 并只引用需要的包。

**不要照搬后端的全部能力**：只保留与 `IBrain` 契约兼容的子集（每步一个工具调用或一个终答），
并在文档中写清丢失的语义。

## 接缝为什么在 `IChatClient`，而不是 `AIAgent` 子类

MAF 官方样例（`samples/02-agents/AgentProviders/custom/Agent_With_CustomImplementation`）演示过
「完全不用 AI 的 agent」——继承 `AIAgent`，实现 5 个 abstract 成员。本仓**故意不走这条路**：

- 工具循环不在 `AIAgent` 层。MAF 的 `FunctionCall → 执行 → 回填 → 再调用` 循环由 chat client 层的
  `FunctionInvokingChatClient` 承担，审批是 `ApprovalRequiredAIFunction` + `ToolApprovalAgent`。
- 直接子类化 `AIAgent` 就得自己重写这条循环、审批绑定和历史 provider——恰好是规则/代码大脑最不需要
  重造的部分，也让「三类大脑共用同一条执行链路」这个前提失效。
- 接在 `IChatClient` 上，`BrainChatClient` 只在每个模型步产出一条 `AIContent`，工具执行、回填、循环、
  审批、流式事件全部仍归 MAF。确定性大脑因此不需要、也不拥有执行循环。

代价要认：`IChatClient` 表达不了 `AIAgent` 才有语义——自有 run 生命周期、session 状态机、token 级
流式、usage 统计。需要这些语义的「大脑」（例如 future 的 workflow-as-brain）应新增 `AIAgent` 级接缝，
而不是把 `IBrain` 撑大。

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
