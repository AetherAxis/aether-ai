# Aether.AI

AI 平台层——Microsoft Agent Framework（MAF）之上的本地 agent 运行时：agent 构建 / 流式运行 / 工具注册与审批 / 会话与记忆 / Skills / MCP / 规则 agent（零 LLM）/ 多 agent 工作流 / C#·F# 代码规则运行时编译。

**零业务依赖**：不引用交易、CTP、账本、知识对象任何业务实现；业务工具适配在（规划中的）`aether-ai-integrations`，由宿主选择注册。**零工作区兄弟仓依赖**（层 0 叶子）：全部 ProjectReference 在本仓内，外部依赖均为公共 NuGet。

## 组成（十项目）

| 项目 | 说明 |
|---|---|
| `src/Aether.AI.Abstractions` | 零依赖契约叶子：MCP 稳定契约（`IMcpServerRegistry` / `McpServerConfig` / 管理面 DTO）+ `AIFunction` 类型面——business 侧引 AI 的唯一合法出口（不背 Hosting/alpha 依赖） |
| `src/Aether.AI.Core` | 框架无关域模型：`AgentDefinition`（引擎/记忆/审批/后台 agent 选项）、规则程序（`RuleProgram`）、会话/消息/对话/记忆条目、`StreamHandlers` 流式回调契约、`RunProgress` 信号 |
| `src/Aether.AI.Harness` | MAF 封装层：`AgentFactory`（定义→agent，规则大脑经 `RuleChatClient` 接缝换脑）、`HarnessRuntime`（流式运行核心 + 会话簿记）、`SessionManager`、`ToolRegistry`（元数据+懒工厂+审批等级+宿主能力过滤）、`SkillRegistry` |
| `src/Aether.AI.Harness.FSharp` | F# 作者契约（唯一 F# 项目）：`Step` DU / `IFsRuleBrain` / `FsCodeRuleBrain`（线性式，继承 C# `CodeRuleBrain`）——exhaustive matching 是 YAML/C# 给不了的编译期保证 |
| `src/Aether.AI.Harness.Scripting` | 代码规则运行时编译：Roslyn（`.coderule.cs`）+ FCS（`.coderule.fs`，`FsRuleScriptCompiler`）——SHA256 缓存 + 可卸载 ALC + 失败不抛 |
| `src/Aether.AI.Hosting` | DI 入口：`AddAetherAI` → `AetherAIBuilder`（AddAgent / AddTool / ConfigureToolRegistry / ConfigureSkillRegistry）+ `AetherAIOptions` + OpenTelemetry + 记忆接线 |
| `src/Aether.AI.Mcp` | MCP 集成（alpha 包 `Microsoft.Agents.AI.Mcp` 隔离于此，宿主 opt-in）：`McpServerRegistry`（stdio/HTTP server 连接 + 工具预解析）+ `McpManagementService` |
| `src/Aether.AI.Memory` | 长期记忆抽象（`IMemoryStore` / `IConversationStore` / `MemoryService`）+ InMemory 实现（`AddAetherAI` 默认） |
| `src/Aether.AI.Memory.Sqlite` | SQLite 双 store（`memory.db` / 会话转录）——含 `SqliteStoreBase` 吸收件（见下） |
| `src/Aether.AI.Workflows` | 多 agent Handoff 工作流（`WorkflowFactory.BuildAsAIAgent`） |

测试三项目随迁（`tests/`）：Harness（规则 agent / F# 编译器 / 会话生命周期 / 工具审批 / 技能注册表 / 流式信号）+ Mcp（配置解析）+ Memory（InMemory / Sqlite 双后端）。

## 设计要点

- **MAF 版本纪律**：`Microsoft.Agents.AI` 1.24.0 稳定线；`Microsoft.Extensions.AI.Abstractions` 钉 10.10.1（1.24 传递链唯一连带——MEAI.OpenAI 10.10.0 带工具 Responses 调用 TypeLoadException 的修复版；MEAI 本体仍 10.10.0）。monorepo 的 `externals/MAF` submodule 只是追上游 breaking 的源码参考，**不是运行依赖**——本仓不持 submodule，运行时全走 NuGet。升级前查 MAF 仓库 `dotnet/Directory.Packages.props`。
- **FCS 精确依赖**：`FSharp.Compiler.Service` 对 `FSharp.Core` 是 `=` 依赖（43.12.401 → 10.1.401），两处版本必须同步升。F# 编译器系统契约程序集来自 `Microsoft.NETCore.App.Ref`（Scripting 以 `ExcludeAssets="all"` 引用——仅物化进 NuGet 全局缓存供运行时定位，零编译输出影响；自包含发布下是 ref-pack 兜底主力）。
- **规则 agent（零 LLM）**：`Engine == Rule` → `RuleChatClient`（`IChatClient` 接缝）产 `AIContent`，工具执行/回填/循环仍归 MAF HarnessAgent——复用整个 agent 生态（工具/MCP/审批/持久化），只换「决定下一步」的大脑。`RuleChatClient` 仅支持流式（`GetStreamingResponseAsync`）。
- **工具审批**：`ToolRegistry` 每工具声明 Safe/Confirm/Dangerous；agent 定义 `RequireToolApproval` / `ToolApprovals` 覆盖 → 非 Safe 工具包成 `ApprovalRequiredAIFunction`，运行时流冒 `ToolApprovalRequest` 由宿主 UI 弹框。取消平台登录≠删除工具执行控制（MIGRATION.md P1 红线）。
- **MAAI001**：MAF 把部分 Harness/Responses API 标 experimental——本仓以稳定类型封装，slnx 范围内抑制该诊断。
- **`SqliteStoreBase` 吸收件**（2026-10-23 迁入）：原 monorepo `src/Acme.Persistence` 的共享基类（连接串校验 / WAL+busy_timeout PRAGMA / 幂等懒建表），按 Exchange / Client 两仓既有吸收件模式以「仅改 namespace」原样吸收进 `Aether.AI.Memory.Sqlite`——工作区现有第三份（MIGRATION.md §6：不因第三个消费者建 Common 仓）。**上游改动须手动同步三份**。
- **消费入口**：宿主调 `AddAetherAI()` 拿 builder（默认 `IChatClientFactory` 构造期即解析 `IChatClient`，无 OpenAI key 会炸——真宿主注册自己的工厂覆盖，桌面即如此；纯规则/零 LLM 形态可注 Null 工厂）。冒烟形态见 MIGRATION.md §16 验证记录。

## 构建与测试

```bash
dotnet test aether-ai.slnx    # tests/：152 例（Harness 112 + Mcp 10 + Memory 30）——零真实模型调用
```

CI（`.github/workflows/ci.yml`）：Release 构建 + 全量测试；零兄弟仓 checkout（层 0 叶子仓）。

## 来历

2026-10-23 拆自 Aether monorepo `src/Aether.AI.*` + `tests/Aether.AI.*.Tests`（源提交 `8686503`，P1 批次；版本基线随迁不升）。迁移时唯一的跨域引用 `Acme.Persistence` 以吸收件方式解除。业务工具适配（ai/Ctp、ai/Trading、ai/Ledger、ai/Agents 等）与桌面消费属后续批次（P2 / P3）。完整决策记录见工作区根 `MIGRATION.md`。
