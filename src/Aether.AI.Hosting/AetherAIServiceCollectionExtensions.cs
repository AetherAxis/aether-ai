using Aether.AI.Brains.Abstractions;
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

        // Tools — ToolRegistry collects metadata (name/description/category) + factory
        // from each adapter's IToolRegistryContributor. Factories are only invoked
        // when resolving tools for an agent, so GetAllMetas() never instantiates
        // AIFunction and never fails due to missing DI dependencies.
        services.AddSingleton<ToolRegistry>(sp => new ToolRegistry(sp, () => sp.GetRequiredService<AetherAIOptions>().Host));

        // 工具等级查询（Core 契约）：UI 层 AutoEdit 模式按等级区分自动批/弹框，不经此不直引 Harness。
        services.AddSingleton<IToolApprovalLevelLookup>(sp => new ToolApprovalLevelLookup(sp.GetRequiredService<ToolRegistry>()));

        // Skills — SkillRegistry 收集各适配器通过 ISkillRegistryContributor 注册的提示词级 skill。
        services.AddSingleton<SkillRegistry>(sp => new SkillRegistry(sp));

        // Concrete brain implementations are opt-in. The common registry is populated from
        // whichever IBrainFactory services the host selected.
        services.AddSingleton<IBrainFactoryRegistry>(sp =>
        {
            var registry = new BrainFactoryRegistry();
            foreach (var factory in sp.GetServices<IBrainFactory>()) registry.Register(factory);
            return registry;
        });

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
            var chatClientFactory = ResolveChatClientFactory(sp);
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
            var chatClientFactory = ResolveChatClientFactory(sp);
            var brainFactories = sp.GetRequiredService<IBrainFactoryRegistry>();
#pragma warning disable MEAI001 // SummarizingChatReducer 在 MEAI 10.7 仍是实验性 API，此处显式启用摘要记忆模式
            return definition => definition.Memory switch
            {
                AgentMemoryMode.Off => new NoMemoryChatHistoryProvider(),
                AgentMemoryMode.SlidingWindow => new RestoringChatHistoryProvider(store, map,
                    new SlidingWindowChatReducer(definition.MaxHistory), logger),
                // 摘要压缩：保留最近 MaxHistory 条，更早的由 LLM 摘要（threshold=MaxHistory → 达 2× 才触发摘要，避免每轮摘要）。
                // 只有具备自然语言上下文能力的大脑才执行摘要模型调用；确定性大脑回退 Full。
                AgentMemoryMode.Summarize
                    when brainFactories.CapabilitiesFor(definition).HasFlag(BrainCapabilities.NaturalLanguageContext)
                         && chatClientFactory is not null
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
                ResolveChatClientFactory(sp) ?? MissingChatClientFactory.Instance,
                sp.GetRequiredService<ToolRegistry>(),
                sp.GetRequiredService<SkillRegistry>(),
                sp.GetService<ILoggerFactory>(),
                sp.GetService<Func<AgentDefinition, AIContextProvider?>>(),
                sp.GetService<Func<AgentDefinition, ChatHistoryProvider?>>(),
                sp.GetService<Func<AgentDefinition, IReadOnlyList<AIAgent>?>>(),
                sp.GetRequiredService<IBrainFactoryRegistry>(),
                sp.GetService<IBrainRoutingClientFactory>());
            factory.UseChatClientAgent = sp.GetRequiredService<AetherAIOptions>().UseChatClientAgent;
            factory.FileMemoryRoot = sp.GetRequiredService<AetherAIOptions>().FileMemoryRoot;
            factory.SkillsDirectory = sp.GetRequiredService<AetherAIOptions>().SkillsDirectory;
            factory.PythonExecutable = sp.GetRequiredService<AetherAIOptions>().PythonExecutable;
            factory.EnableSkillScripts = sp.GetRequiredService<AetherAIOptions>().EnableSkillScripts;
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
                sp.GetService<IRunProgressSink>(),
                // 会话持久化：宿主在 DI 里注册 IAgentSessionStore，或经 AetherAIOptions.AgentSessionStore 指定。
                // 两者都没有 = 只活在进程内（默认，行为与之前一致）。
                sp.GetService<Aether.AI.Harness.Sessions.IAgentSessionStore>()
                    ?? sp.GetRequiredService<AetherAIOptions>().AgentSessionStore);
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

        return new AetherAIBuilder(services);
    }

    private static Aether.AI.Brains.Abstractions.IChatClientFactory? ResolveChatClientFactory(
        IServiceProvider services)
        => services.GetService<Aether.AI.Brains.Abstractions.IChatClientFactory>();

    private sealed class MissingChatClientFactory : Aether.AI.Brains.Abstractions.IChatClientFactory
    {
        public static MissingChatClientFactory Instance { get; } = new();

        public IChatClient Create(string? modelId)
            => throw new InvalidOperationException(
                "No LLM brain is registered. Add an LLM implementation before running an Llm agent.");
    }
}
