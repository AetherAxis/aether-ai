# aether-ai 使用指南（宿主接入）

> 2026-10-23 建立。目标读者：要接入 aether-ai 的宿主作者（console / 桌面 / 手机）。
> 本文所有代码形态都经真实验证（仓内 154 例测试 + /tmp 三宿主形态冒烟，见文末验证矩阵）。
> 面向对象的 API 细节以代码 XML 注释为准，本文管「怎么接、坑在哪」。

## 0. 一句话模型

aether-ai = **MAF（Microsoft Agent Framework）之上的本地 agent 运行时**。你（宿主）做的事只有三件：
**注册大脑**（agent 定义）、**注册工具**（业务能力）、**喂运行时**（会话 + 一轮用户输入）；其余
（工具执行回填循环、流式回调、审批门控、记忆策略、session 簿记）全部由平台+MAF 承担。

## 1. 标准接入（所有宿主通用的骨架）

```csharp
var services = new ServiceCollection();

var aether = services.AddAetherAI();          // 唯一 DI 入口（Aether.AI.Hosting）

// ① 工具：按适配器约定经 ConfigureToolRegistry 注册（名字 + 描述 + 分类 + 懒工厂 + 审批等级）
aether.ConfigureToolRegistry(tools => tools.Register(
    "Echo", "回声测试", "我的业务",
    _ => AIFunctionFactory.Create((Func<string, string>)(text => $"echo:{text}"), name: "Echo"),
    ToolApprovalLevel.Safe));

// ② 大脑：AddAgent（下面 §2 三选一）
aether.AddAgent("my-agent", ab => ab
    .Name("我的助手").Instructions("你是……")
    .WithTool("Echo"));

// ③ 记忆（可选，默认 InMemory；要持久化就换 Sqlite 双 store）
services.AddSingleton<IMemoryStore>(_ => SqliteMemoryStore.ForFile(dbPath));
services.AddSingleton<IConversationStore>(_ => SqliteConversationStore.ForFile(dbPath));

using var sp = services.BuildServiceProvider();
var runtime = sp.GetRequiredService<HarnessRuntime>();

// ④ 跑一轮（流式面——聊天/规则/代码规则统一走这里）
var session = runtime.StartSession("my-agent");
await runtime.RunStreamingAsync(session.SessionId, "用户输入", new StreamHandlers
{
    OnToken = (t, _) => { /* 逐字上抛 UI */ return Task.CompletedTask; },
    OnToolEvent = (e, _) => { /* 工具调用/结果成对出现 */ return Task.CompletedTask; },
});
```

业务工具更重的形态（CTP / 链上钱包 / 知识对象）不在本仓——按 ⑭ aether-ai-integrations 的
适配器约定单独建 `<Context>.AI` 项目，本仓只提供注册面。

## 2. 三种大脑（一个生态，换脑不换身）

同一个 agent 生态（工具 / 流式 / 审批 / 持久化 / MCP）支持三种「决定下一步」的大脑：

| 大脑 | 声明 | 适用 | 验证状态 |
|---|---|---|---|
| **LLM**（默认） | 不设 Engine（或 `Engine(EngineKind.Llm)`） | 真模型对话 / 自主工具编排 | ✅ 脚本化 IChatClient 全回合测试（`ScriptedLlmAgentTests`：调用→执行→回填→终答）；真实端点未验（无 key 环境，见 §6） |
| **规则** | `Engine(EngineKind.Rule)` + `Rules(RuleProgram)` | 零 LLM 零网络的确定性流程（客服路由 / 固定操作序列） | ✅ 5 文件规则测试 + 零 LLM 宿主钉子 |
| **代码规则** | 定义由源码文件加载（`IRuleScriptingService`，`.coderule.cs` / `.coderole.fs`） | 用 C#/F# 写脑、运行时 Roslyn/FCS 编译 | ✅ `FsRuleScriptCompilerTests` 9 例 + `CodeRuleBrainTests` |

规则大脑示例（上面骨架 ② 处替换）：

```csharp
aether.AddAgent("router", ab => ab
    .Name("路由").Engine(EngineKind.Rule)
    .Rules(new RuleProgram
    {
        Steps = new()
        {
            // 用户说「查600000」→ 调 Query 工具（{user.1} = 正则捕获组）
            new RuleStep { Match = new() { OnUserRegex = @"查(?<sym>\d+)", MaxToolCalls = 0 },
                           Action = new() { Tool = "Query", Args = new() { ["symbol"] = "{user.1}" } } },
            // 工具结果回来后终答（{result} = 最近一次工具结果）
            new RuleStep { Match = new() { MinToolCalls = 1 },
                           Action = new() { ReplyText = "终答:{result}" } },
        },
    })
    .WithTool("Query"));
```

要点：`MaxToolCalls` 限定步进（首步须 `MaxToolCalls = 0`，否则每轮重触发直到 20 步上限）；
`RuleChatClient` **仅支持流式**（`RunStreamingAsync`）——非流式 `RunAsync` 会 `NotSupportedException`。

## 3. 工具与审批

- 注册：`ConfigureToolRegistry`（见 §1）——**AIFunction 参数名 = 调用方传的参数键名**。
  `AIFunctionFactory.Create((Func<string,string>)(text => ...))` 的参数叫 `text`，那么规则/模型侧
  就必须传 `{"text": ...}`——**lambda 参数名与键名不一致 = 绑定失败，MAF 只回 "Error: Function
  failed."**（今天冒烟实踩；这是新接入最常见的坑）。
- 审批等级：`Safe`（自动执行）/ `Confirm` / `Dangerous`。agent 定义 `RequireToolApproval(true)`
  或 `EnableAgentModeProvider` 开启门控后，非 Safe 工具被包成 `ApprovalRequiredAIFunction`——运行时
  经 `StreamHandlers.OnApprovalRequest` 上抛，宿主决定后**同 session 再调用**恢复执行。
  真实形态参考 ⑭ 的 Ledger 工具（转账 Dangerous / 充值 Confirm）。
- MCP（opt-in）：引 `Aether.AI.Mcp`（alpha 包隔离于此）后 `AddMcpServersFromDirectory(dir)`；
  业务侧只该引 `Aether.AI.Abstractions` 拿稳定契约（`IMcpServerRegistry` 等）。

## 4. 三种宿主形态

### 4.1 Console（已验证 ✅）
零 UI、无 `SynchronizationContext`。纯规则/代码规则 agent 的 **零 LLM 形态不需要任何模型配置**：
无 key、不覆盖任何工厂即可 `AddAetherAI` → 跑（2026-10-23 懒解析修复后；回归钉子
`AddAetherAICompositionTests`）。要用真 LLM：设 `AetherAI:OpenAI:ApiKey`（或 env `OPENAI_API_KEY`）
+ `ModelId`；兼容端点（DeepSeek/Azure 兼容/本地）设 `AetherAI:OpenAI:Endpoint`。

### 4.2 桌面（UI 线程形态已验证 ✅ / 真桌面 App 待 P3）
- **线程**：平台库全链 `ConfigureAwait(false)`；在 Dispatcher 式单线程 `SynchronizationContext` 上
  跑流式 agent 已冒烟验证不死锁（UI 宿主正常 async/await 即可，**勿在 UI 线程 sync-over-async**）。
  `StreamHandlers` 回调到达线程不定——宿主自己编组回 UI 线程（Avalonia `Dispatcher.UIThread.Post`）。
- **每 agent 换模型**：`AgentDefinition.ModelId` 经 `IChatClientFactory.Create(modelId)` 解析。默认实现
  走 `AetherAIOptions.OpenAI`；要按自己的模型配置库（多 provider）就**注册自己的
  `IChatClientFactory` 覆盖**（monorepo 桌面的 `DesktopChatClientFactory` 即此模式）。工厂只在
  首个 LLM agent 真正用到时才被调用。
- OpenTelemetry：`AetherTelemetryExtensions`（`EnableOpenTelemetry` 的 agent 才发 trace）。

### 4.3 手机（静态核查 ✅ / 真机待 P3）
- **引用子集**：Abstractions / Core / Hosting / Memory / Memory.Sqlite——**不含 Harness.Scripting**
  （FCS 编译器服务桌面专用；monorepo 手机侧即此引用集）。纯 net10.0 无平台包，`Microsoft.Data.Sqlite`
  走 SQLitePCLRaw（iOS/Android 均可）。
- **iOS 禁子进程** → **不要设置 `AetherAIOptions.SkillsDirectory`**（设了才会挂
  `AgentFileSkillsSource` 脚本执行；不设 = 回退 SkillRegistry 纯提示词，零子进程）。同理
  `PythonExecutable` 无效。`FileMemoryRoot` 不设 = 文件记忆静默禁用。
- 平台差异工具用 `ToolRegistry.Register(..., hostCapability: HostCapability.DesktopOnly/MobileOnly)`
  声明——运行时按 `AetherAIOptions.Host` 过滤，手机上勾了桌面工具也不会崩（跳过 + 日志）。

## 5. 配置速查

| 配置 | 途径 | 默认 | 说明 |
|---|---|---|---|
| 模型 key / 端点 / 模型 | `AetherAIOptions.OpenAI.*`（`Configure(o => ...)`）或 env `OPENAI_API_KEY` | 无 | 仅 LLM agent 需要；首个 LLM agent 创建时才校验（零 LLM 宿主可不配） |
| 每模型工厂覆盖 | `services.AddSingleton<IChatClientFactory>(…)` | DefaultChatClientFactory | 多 provider / 自有模型配置库的宿主覆盖 |
| 会话记忆 | DI 覆盖 `IMemoryStore` / `IConversationStore` | InMemory | Sqlite 版在本仓 `Aether.AI.Memory.Sqlite` |
| Skills 目录 | `AetherAIOptions.SkillsDirectory` | null（禁用） | 设了才启用 MAF 文件技能（含子进程脚本——iOS 勿设） |
| 文件记忆根 | `AetherAIOptions.FileMemoryRoot` | null（禁用） | `EnableFileMemory` 生效前提 |
| 宿主能力 | `AetherAIOptions.Host` | All | 工具过滤用（DesktopOnly/MobileOnly） |

## 6. 验证矩阵（诚实边界）

| 面 | 状态 |
|---|---|
| 规则大脑全链（工具执行/审批/会话/记忆） | ✅ 仓内测试 + 三宿主冒烟 |
| LLM 大脑全链 | ✅ 脚本化 IChatClient（工具回填闭环织进终答断言）；**真实模型端点未验**（需 key——宿主接入后跑一次真模型对话即闭环） |
| 代码规则（C#/F# 运行时编译） | ✅ 仓内测试（FCS ref-pack 定位、缓存、卸载） |
| Console / UI 线程形态 | ✅ 冒烟（免 key 零 LLM + Dispatcher 泵不死锁） |
| MCP / OpenTelemetry / Skills / 长期记忆召回 | ✅ 仓内单元级；**跨进程 MCP server 实连未验** |
| 真桌面 App（Avalonia）/ 真手机（iOS/Android） | ⏳ P3（桌面产品接续批次） |

—— 未验项不宣称通过；新宿主接入时按上表补自己的验收。
