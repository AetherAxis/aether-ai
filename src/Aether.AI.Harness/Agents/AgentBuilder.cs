using Aether.AI.Core.Agents;

namespace Aether.AI.Harness.Agents;

/// <summary>
/// Fluent builder for <see cref="AgentDefinition"/>. Produces a plain data
/// model; the <see cref="AgentFactory"/> is responsible for turning the
/// resulting definition into a live agent.
/// </summary>
public sealed class AgentBuilder
{
    private readonly string _id;
    private string _name = string.Empty;
    private string _instructions = string.Empty;
    private string _modelId = string.Empty;
    private EngineKind _engine = EngineKind.Llm;
    private RuleProgram? _rules;
    private bool _isCodeRule;
    private double? _temperature;
    private double? _topP;
    private int? _maxOutputTokens;
    private AgentResponseFormat _responseFormat = AgentResponseFormat.Default;
    private readonly List<string> _tools = new();
    private readonly Dictionary<string, string> _toolApprovals = new();
    private readonly List<string> _autoApprovalRules = new();
    private AgentMemoryMode _memory = AgentMemoryMode.Full;
    private int _maxHistory = 20;
    private bool _requireToolApproval;
    private bool _enableWebSearch, _enableTodoProvider, _enableAgentModeProvider;
    private bool _enableFileMemory, _enableOpenTelemetry, _enableLongTermMemory;
    private readonly List<string> _backgroundAgents = new();

    public AgentBuilder(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Agent id is required.", nameof(id));
        _id = id;
    }

    // —— 身份 ——
    public AgentBuilder Name(string name) { _name = name; return this; }
    public AgentBuilder Instructions(string instructions) { _instructions = instructions; return this; }
    public AgentBuilder ModelId(string modelId) { _modelId = modelId; return this; }

    // —— 引擎（大脑；默认 LLM）——
    public AgentBuilder Engine(EngineKind engine) { _engine = engine; return this; }
    public AgentBuilder Rules(RuleProgram rules) { _rules = rules; return this; }
    public AgentBuilder IsCodeRule(bool isCodeRule = true) { _isCodeRule = isCodeRule; return this; }

    // —— 采样 ——
    public AgentBuilder Temperature(double temperature) { _temperature = temperature; return this; }
    public AgentBuilder TopP(double topP) { _topP = topP; return this; }
    public AgentBuilder MaxOutputTokens(int max) { _maxOutputTokens = max; return this; }
    public AgentBuilder ResponseFormat(AgentResponseFormat format) { _responseFormat = format; return this; }

    // —— 工具 ——
    public AgentBuilder WithTool(string toolName) { _tools.Add(toolName); return this; }
    public AgentBuilder WithTools(IEnumerable<string> toolNames) { _tools.AddRange(toolNames); return this; }
    public AgentBuilder WithToolApproval(string toolName, string level) { _toolApprovals[toolName] = level; return this; }
    public AgentBuilder AutoApproveTool(string toolName) { _autoApprovalRules.Add(toolName); return this; }
    public AgentBuilder AutoApproveTools(IEnumerable<string> toolNames) { _autoApprovalRules.AddRange(toolNames); return this; }
    public AgentBuilder RequireToolApproval(bool require = true) { _requireToolApproval = require; return this; }

    // —— 记忆 ——
    public AgentBuilder Memory(AgentMemoryMode memory) { _memory = memory; return this; }
    public AgentBuilder MaxHistory(int max) { _maxHistory = max; return this; }
    public AgentBuilder EnableLongTermMemory(bool enable = true) { _enableLongTermMemory = enable; return this; }

    // —— 后台 Agent（动态多 agent 并行白名单，orchestrator 分派子任务）——
    public AgentBuilder WithBackgroundAgent(string agentName) { _backgroundAgents.Add(agentName); return this; }
    public AgentBuilder WithBackgroundAgents(IEnumerable<string> agentNames) { _backgroundAgents.AddRange(agentNames); return this; }

    // —— Provider 开关 ——
    public AgentBuilder EnableWebSearch(bool enable = true) { _enableWebSearch = enable; return this; }
    public AgentBuilder EnableTodoProvider(bool enable = true) { _enableTodoProvider = enable; return this; }
    public AgentBuilder EnableAgentModeProvider(bool enable = true) { _enableAgentModeProvider = enable; return this; }
    public AgentBuilder EnableFileMemory(bool enable = true) { _enableFileMemory = enable; return this; }
    public AgentBuilder EnableOpenTelemetry(bool enable = true) { _enableOpenTelemetry = enable; return this; }

    public AgentDefinition Build() => new()
    {
        Id = _id,
        Name = string.IsNullOrWhiteSpace(_name) ? _id : _name,
        Instructions = _instructions,
        ModelId = _modelId,
        Engine = _engine,
        Rules = _rules,
        IsCodeRule = _isCodeRule,
        Temperature = _temperature,
        TopP = _topP,
        MaxOutputTokens = _maxOutputTokens,
        ResponseFormat = _responseFormat,
        Tools = _tools,
        ToolApprovals = _toolApprovals.Count == 0 ? null : _toolApprovals,
        AutoApprovalRules = _autoApprovalRules.Count == 0 ? null : _autoApprovalRules,
        Memory = _memory,
        MaxHistory = _maxHistory,
        RequireToolApproval = _requireToolApproval,
        EnableWebSearch = _enableWebSearch,
        EnableTodoProvider = _enableTodoProvider,
        EnableAgentModeProvider = _enableAgentModeProvider,
        EnableFileMemory = _enableFileMemory,
        EnableOpenTelemetry = _enableOpenTelemetry,
        EnableLongTermMemory = _enableLongTermMemory,
        BackgroundAgents = _backgroundAgents.Count == 0 ? null : _backgroundAgents,
    };
}
