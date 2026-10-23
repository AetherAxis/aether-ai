# Aether.AI 设计说明

> 本文件写「怎么设计 / 为什么这么设计 / 还能往哪升级」。工作区边界与迁移决策以根 `MIGRATION.md` 为准。

## 1. 定位与边界

MAF 之上的本地 Agent 运行时：把 `AgentDefinition` 变成可流式运行的 `AIAgent`，大脑可为 LLM、声明式规则或作者代码。

- **DAG 层**：层 0 叶子仓，20 个 `src` 项目引用全在本仓闭合，外部仅公共 NuGet，CI 无兄弟仓。
- **谁引它**：⑭ `aether-ai-integrations` 与后续桌面宿主。
- **它引谁**：仅仓内项目与公共 NuGet；`externals/MAF` submodule 不随迁。
- **不做**：具体工具（只留 `ToolRegistry`/`ToolApprovalLevel`/`HostCapability` 契约）、不假设大脑是 LLM、不复制 MAF 执行循环、不回写 monorepo。

## 2. 项目组成

前缀 `Aether.AI.` 省略；依赖列仅 ProjectReference。

| 项目 | 职责 | 关键类型 | 出向依赖 |
|---|---|---|---|
| Abstractions | MCP 契约 | `McpServerConfig` | — |
| Core | 领域模型 | `AgentDefinition`、`EngineKind` | — |
| Brains.Abstractions | 大脑接缝、能力集 | `IBrainFactory`、`BrainChatClient`、`BrainCapabilities` | Core |
| Brains.Llm | LLM 工厂 | `LlmBrainFactory` | Brains.Abstractions |
| Brains.Rule | `program` 后端 | `RuleProgramBrain` | Brains.Abstractions、Core |
| Brains.Rule.RulesEngine | `rulesEngine` 后端 | `RulesEngineBrain` | Brains.Abstractions、Core |
| Brains.Rule.NRules | `nRules` 后端 | `NRulesBrain` | Brains.Abstractions、Core |
| Brains.Code | 协程与 run 隔离 | `CodeRuleBrain`、`RunScopedBrainRouter` | Brains.Abstractions、Core |
| Brains.Code.CSharp | C# 作者入口 | `CSharpCodeBrainServiceCollectionExtensions` | Brains.Code |
| Brains.Code.FSharp | F# 作者契约 | `Step`、`IFsRuleBrain` | Brains.Abstractions、Brains.Code |
| Brains.Code.Compilation | 编译插件契约 | `CodeCompilationService`、`ICodeBrainCompiler` | Brains.Abstractions、Brains.Code、Core |
| Brains.Code.Compilation.CSharp | Roslyn 实现 | `CSharpCodeBrainCompiler` | Code.Compilation、Code.CSharp |
| Brains.Code.Compilation.FSharp | FCS 与 ref-pack | `FSharpCodeBrainCompiler` | Code.Compilation、Code.FSharp |
| Brains.Routing | Rule 优先路由 | `RuleFirstRoutingChatClient` | Brains.Abstractions、Core |
| Harness | MAF 构建与运行 | `HarnessRuntime`、`AgentFactory`、`MafAgentSessionStore` | Core、Brains.Abstractions |
| Hosting | `AddAetherAI()` DI 与 OTel | `AetherAIBuilder`、`AetherAIOptions` | Core、Brains.Abstractions、Harness、Memory |
| Mcp | MCP 注册表 | `McpServerRegistry`、`McpManagementService` | Hosting、Abstractions |
| Memory | 记忆契约 | `MemoryService`、`IMemoryStore` | Core |
| Memory.Sqlite | SQLite 实现 | `SqliteMemoryStore`、`SqliteStoreBase` | Memory |
| Workflows | Handoff 构建 | `WorkflowFactory` | Core |

## 3. 关键设计决策

### 3.1 接缝选在 `IChatClient`
- **决策**：`IBrainFactory.CreateChatClient` 是唯一入口；确定性大脑经 `IContentBrainFactory` 包进 `BrainChatClient`，每步只产出一个工具调用或终答。
- **理由**：工具循环、审批、历史都在 chat client 层；子类化 `AIAgent` 要重写它们并破坏三类大脑共用链路。
- **代价**：表达不了 run 生命周期、token 级流式、usage；后端能力只保留兼容子集。

### 3.2 能力集取代 `EngineKind`
- **决策**：`BrainCapabilities` 决定挂哪些 provider、是否应用采样参数。
- **理由**：`AgentFactory` 只按 `CapabilitiesFor` 接线，新增大脑不改 Harness；未注册工厂时回落 `BrainCapabilitiesDefaults.For`。
- **代价**：跨包契约，新增一位要同步默认集与消费点，漏改会静默失效。

### 3.3 多后端显式选择
- **决策**：Rule 下 `program`/`rulesEngine`/`nRules` 用唯一 `BackendId` 区分，多个注册时必须设 `BrainBackend`。
- **理由**：按注册顺序取胜会让换后端成为不可见副作用；`BrainFactoryRegistry` 报错并列出候选。
- **代价**：多引一个包就得改所有规则定义；路由内部规则分支要复制该字段。

### 3.4 依赖闭包用测试守住
- **决策**：`DependencyClosureTests` 读 `Assembly.GetReferencedAssemblies`，断言 Rule-only 与预编译 Code 形态不含 OpenAI/Roslyn/FCS。
- **理由**：闭包是产品程序集属性，普通 `dotnet test` 即可守，取代构建期 `DependencyBoundary.targets`。
- **代价**：必须有反向对照（LLM 闭包含 OpenAI），否则遍历失效会假绿。

## 4. 运行与验证

```bash
dotnet build aether-ai.slnx -c Release
dotnet test aether-ai.slnx -c Release --no-build
```

- **规模**：38 项目 = 20 src + 14 测试 + 4 `samples`。
- **测试分布（292 项）**：Core 13、Brains.Abstractions 20、Brains.Rule 15、RulesEngine 13、NRules 10、Brains.Code 24、Code.Compilation 13、Routing 13、Llm 5、Harness 93、Hosting 15、Mcp 21、Memory 32、Integration 5。
- **覆盖率**：README 记录行覆盖率 77.0%（3346/4346，C# 行）。
- **CI**：checkout → 注入 GitHub Packages 凭据 → `build -c Release` → `test --no-build`；零真实模型/MCP。
- **L2 实跑**：`dotnet run --project samples/Aether.AI.Samples.{RuleOnly,PrecompiledCode,CSharpCompilation,BrainMatrix}`；BrainMatrix 在一个 DI 容器注册五路，同输入同工具输出 `done:echo:600000`。

## 5. 升级记录（2026-10-24）

涉及 `Directory.Packages.props`、`MafAgentSessionStore.cs`、`AgentSessionStoreTests.cs`（另 README/.gitignore 同步说明与忽略）。

| 项 | 变更前 | 变更后 | 依据 / 验证 |
|---|---|---|---|
| MAF 五包 | 1.24.0 | 1.26.0 | 1.26 把 `DeleteSessionAsync` 提为抽象成员 |
| MAF Mcp | 1.24.0-alpha.261006.1 | 1.26.0-alpha.261023.1 | 仍 alpha |
| MEAI（AI/Abstractions） | 10.10.0 / 10.10.1 | 10.11.0 / 10.11.0 | 两包同版 |
| M.E.Logging/DI/DI.Abstractions | 10.0.12 / 10.0.11 / 10.0.12 | 10.0.13 ×3 | CPM |
| OpenTelemetry 四包 | 1.12.0 | 1.20.1 | NU1902/NU1903 消除 |
| Microsoft.Data.Sqlite | 10.0.10 | 10.0.13 | 唯一依赖 |
| Roslyn | 4.13.0 | 5.9.0 | 动态 C# 编译 |
| F# 三包（Core/FCS/ref-pack） | 10.1.401 / 43.12.401 / 10.0.9 | 10.1.402 / 43.12.402 / 10.0.13 | FCS 精确依赖 FSharp.Core，两处同步 |
| 测试栈 | Test.Sdk 17.14.1、runner 3.1.4、coverlet 6.0.4 | 18.11.0、4.0.1、10.1.0 | `xunit` 保持 2.9.3 |
| `DeleteSessionAsync` | 无重写 | 转发 `_inner.RemoveAsync` | 编译所需 |
| 新增删除会话测试 | — | `Maf_store_adapter_deletes_through_to_the_inner_store` | 内外两侧均查不到 |

验证：14 个程序集 292 项（291 + 新增 1），CI `dotnet test --no-build` 全量执行。

## 6. 待升级项

| # | 优先级 | 项目 | 现状证据 | 建议 | 代价 / 风险 |
|---|---|---|---|---|---|
| 1 | P1 | `Workflows` 零测试 | 14 个测试程序集无一引用它 | 补入口缺失/悬空 handoff 用例 | 需构造参与者；Handoff 属实验面 |
| 2 | P1 | Harness.Tests 闭包全量 | 10 个引用含 RulesEngine、NRules、两个 Compilation | 后端用例移回各自程序集 | 需确认无隐式依赖 |
| 3 | P1 | 实验/预发布面无护栏 | `Harness`/`Hosting`/`Mcp` 均 `NoWarn` `MAAI001`；Mcp 另加 `NU5104` 且为 alpha 构建 | 集中抑制点/GA 跟踪 | 签名变更不报错；alpha 随时破坏 |
| 4 | P2 | xunit 仍为 2.x | CPM 中 `xunit` 2.9.3，runner 已到 4.0.1 | 评估 xunit v3 | 需改测试属性/运行器；收益有限 |
| 5 | P2 | 外部进程分支无覆盖 | README 承认 `SkillScriptRunner` 与 MCP 实连缺口 | 注入替身补测 | 需开 internal 接缝 |
| 6 | P2 | 路由边界未收敛 | `doc/usage.md`：单入口、只支持 Rule↔LLM、丢 sampling | 定义扩展点或文档固化 | 新组合要复制分支逻辑 |
| 7 | P3 | `SqliteStoreBase` 三份同步 | `Memory.Sqlite` csproj 注释：手动同步三份 | 抽公共包或加检查 | 与「不建 Common 仓」冲突 |