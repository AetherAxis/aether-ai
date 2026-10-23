using Aether.AI.Core.Agents;
using Aether.AI.Harness;
using Aether.AI.Harness.Agents;
using Aether.AI.Harness.Sessions;
using Aether.AI.Harness.Skills;
using Aether.AI.Harness.Tools;
using Aether.AI.Hosting.Agents;
using Aether.AI.Hosting.Tools;
using Aether.AI.Memory;
using Aether.AI.Memory.Stores;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;

namespace Aether.AI.Hosting;

/// <summary>
/// DI registration entry point for the Aether.AI platform. Calling
/// <see cref="AddAetherAI"/> wires the Microsoft Agent Framework chat client,
/// the Harness runtime, session/tool management, and the Memory layer.
/// </summary>
public static class AetherAIServiceCollectionExtensions
{
    /// <summary>
    /// Adds Aether.AI platform services to <paramref name="services"/> and
    /// returns a builder for registering agents and tools.
    /// </summary>
    public static AetherAIBuilder AddAetherAI(this IServiceCollection services)
    {
        services.AddOptions();

        // Options
        services.AddSingleton<AetherAIOptions>(sp => sp.GetRequiredService<IOptions<AetherAIOptions>>().Value);

        // Chat client (OpenAI / Azure OpenAI). Built lazily on first resolve so
        // that a missing API key does not crash DI composition.
        services.AddSingleton<IChatClient>(sp =>
        {
            var options = sp.GetRequiredService<AetherAIOptions>();
            var apiKey = string.IsNullOrWhiteSpace(options.OpenAI.ApiKey)
                ? Environment.GetEnvironmentVariable("OPENAI_API_KEY")
                : options.OpenAI.ApiKey;

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException(
                    "No OpenAI API key configured. Set AetherAI:OpenAI:ApiKey or the OPENAI_API_KEY environment variable.");
            }

            // Wires an OpenAI-compatible chat client. When an Endpoint is set
            // (e.g. DeepSeek, Azure-compatible, local), it is used; otherwise the
            // public OpenAI service is used. Azure OpenAI can be substituted by
            // replacing this with an AzureOpenAIClient in a later phase.
            OpenAIClient client = string.IsNullOrWhiteSpace(options.OpenAI.Endpoint)
                ? new OpenAIClient(apiKey)
                : new OpenAIClient(
                    new System.ClientModel.ApiKeyCredential(apiKey),
                    new OpenAIClientOptions { Endpoint = new Uri(options.OpenAI.Endpoint!) });
            return client.GetChatClient(options.OpenAI.ModelId).AsIChatClient();
        });

        // Tools — ToolRegistry collects metadata (name/description/category) + factory
        // from each adapter's IToolRegistryContributor. Factories are only invoked
        // when resolving tools for an agent, so GetAllMetas() never instantiates
        // AIFunction and never fails due to missing DI dependencies.
        services.AddSingleton<ToolRegistry>(sp => new ToolRegistry(sp, () => sp.GetRequiredService<AetherAIOptions>().Host));

        // 工具等级查询（Core 契约）：UI 层 AutoEdit 模式按等级区分自动批/弹框，不经此不直引 Harness。
        services.AddSingleton<IToolApprovalLevelLookup>(sp => new ToolApprovalLevelLookup(sp.GetRequiredService<ToolRegistry>()));

        // Skills — SkillRegistry 收集各适配器通过 ISkillRegistryContributor 注册的提示词级 skill。
        services.AddSingleton<SkillRegistry>(sp => new SkillRegistry(sp));

        // Harness
        services.AddSingleton<IChatClientFactory, DefaultChatClientFactory>();

        // 长期记忆 userId 解析器（运行时取当前用户）。默认 () => null（全局记忆）；
        // 桌面宿主用 ICurrentUser 覆盖。AgentFactory 经此构造 MemoryContextProvider。
        Func<string?> defaultUserIdAccessor = () => null;
        services.AddSingleton(defaultUserIdAccessor);

        // 长期记忆 provider 工厂：仅 EnableLongTermMemory 的 agent 注入 MemoryContextProvider。
        services.AddSingleton<Func<AgentDefinition, AIContextProvider?>>(sp =>
        {
            var store = sp.GetRequiredService<IMemoryStore>();
            var userIdAccessor = sp.GetRequiredService<Func<string?>>();
            var logger = sp.GetService<ILoggerFactory>();
            var chatClientFactory = sp.GetService<IChatClientFactory>();
            return definition => definition.EnableLongTermMemory
                ? new MemoryContextProvider(store, userIdAccessor, logger, chatClientFactory)
                : null;
        });

        // 会话真恢复：session→conversationId 映射（HarnessRuntime 建 session 时写入）+ history provider 工厂。
        // Full/SlidingWindow → RestoringChatHistoryProvider（从 IConversationStore 加载 transcript 重建 LLM 历史）；
        // Off → NoMemoryChatHistoryProvider。
        services.AddSingleton<SessionConversationMap>();
        services.AddSingleton<Func<AgentDefinition, ChatHistoryProvider?>>(sp =>
        {
            var store = sp.GetService<IConversationStore>();
            var map = sp.GetRequiredService<SessionConversationMap>();
            var logger = sp.GetService<ILoggerFactory>();
            var chatClientFactory = sp.GetService<IChatClientFactory>();
#pragma warning disable MEAI001 // SummarizingChatReducer 在 MEAI 10.7 仍是实验性 API，此处显式启用摘要记忆模式
            return definition => definition.Memory switch
            {
                AgentMemoryMode.Off => new NoMemoryChatHistoryProvider(),
                AgentMemoryMode.SlidingWindow => new RestoringChatHistoryProvider(store, map,
                    new SlidingWindowChatReducer(definition.MaxHistory), logger),
                // 摘要压缩：保留最近 MaxHistory 条，更早的由 LLM 摘要（threshold=MaxHistory → 达 2× 才触发摘要，避免每轮摘要）。
                // 规则 agent 无真 IChatClient（RuleChatClient 的 GetResponseAsync 会抛），回退 Full。
                AgentMemoryMode.Summarize when definition.Engine != EngineKind.Rule && chatClientFactory is not null
                    => new RestoringChatHistoryProvider(store, map,
                        new SummarizingChatReducer(chatClientFactory.Create(definition.ModelId),
                            definition.MaxHistory, definition.MaxHistory)
                        { SummarizationPrompt = SummarizeDefaults.Prompt }, logger),
                _ => new RestoringChatHistoryProvider(store, map, null, logger), // Full / Rule+Summarize 回退
            };
#pragma warning restore MEAI001
        });

        services.AddSingleton<AgentFactory>(sp =>
        {
            var factory = new AgentFactory(
                sp.GetRequiredService<IChatClientFactory>(),
                sp.GetRequiredService<ToolRegistry>(),
                sp.GetRequiredService<SkillRegistry>(),
                sp.GetService<ILoggerFactory>(),
                sp.GetService<Func<AgentDefinition, AIContextProvider?>>(),
                sp.GetService<Func<AgentDefinition, ChatHistoryProvider?>>(),
                sp.GetService<Func<AgentDefinition, IReadOnlyList<AIAgent>?>>(),
                sp.GetService<ICodeBrainRegistry>());
            factory.UseChatClientAgent = sp.GetRequiredService<AetherAIOptions>().UseChatClientAgent;
            factory.FileMemoryRoot = sp.GetRequiredService<AetherAIOptions>().FileMemoryRoot;
            factory.SkillsDirectory = sp.GetRequiredService<AetherAIOptions>().SkillsDirectory;
            factory.PythonExecutable = sp.GetRequiredService<AetherAIOptions>().PythonExecutable;
            return factory;
        });
        services.AddSingleton<SessionManager>();
        // 运行关键节点信号出口：HarnessRuntime 经构造参数注入，Harness 内每轮 run 直接广播
        // （不再依赖聊天 VM 在场——Automation 定时任务/工作流同样拿到）。宿主（桌面/手机）可
        // 往这个 fanout 上 Add 自己的消费者（如桌宠路由器）后覆盖注册 IRunProgressSink。
        services.AddSingleton<RunProgressFanout>();
        services.AddSingleton<IRunProgressSink>(sp => sp.GetRequiredService<RunProgressFanout>());
        services.AddSingleton<HarnessRuntime>(sp =>
        {
            var runtime = new HarnessRuntime(
                sp.GetRequiredService<AgentFactory>(),
                sp.GetRequiredService<SessionManager>(),
                sp.GetService<SessionConversationMap>(),
                sp.GetService<IRunProgressSink>());
            // Register every agent definition contributed via the builder.
            foreach (var definition in sp.GetServices<AgentDefinition>())
            {
                runtime.RegisterAgent(definition);
            }
            return runtime;
        });

        // Memory（平台默认 InMemory；宿主可覆盖为 SqliteMemoryStore——桌面即如此，memory.db）
        services.AddSingleton<IMemoryStore, InMemoryMemoryStore>();
        services.AddSingleton<IConversationStore, InMemoryConversationStore>();
        services.AddSingleton<MemoryService>();

        // 会话标题自动生成（LLM，首轮后异步补；无 key 时退化为 no-op）
        services.AddSingleton<IConversationTitleService, ConversationTitleService>();

        // 手机远程控制回复摘要（LLM，agent 跑完把 fullText 压成概览发回手机；失败退化为截断全量）
        services.AddSingleton<IConversationSummaryService, ConversationSummaryService>();

        // 应用内 trace 收集器（ActivityListener 收集最近 50 条 agent.run，供 trace 查看页展示）。
        services.AddSingleton<Aether.AI.Harness.Telemetry.TraceCollector>();

        // 聊天页 LLM 选择器 holder（agent 与 LLM 解耦：ModelId 为空时用此值）
        services.AddSingleton<Aether.AI.Harness.SelectedLlmHolder>();

        return new AetherAIBuilder(services);
    }
}
