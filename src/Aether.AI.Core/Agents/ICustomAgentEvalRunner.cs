namespace Aether.AI.Core.Agents;

/// <summary>
/// 自定义 Agent 评测运行器契约：跑 labs <c>evals/*.eval.yaml</c> 中指向指定 agent 的全部评测套件，
/// 产出逐 case 的判定报告。契约住 Core（镜像 <see cref="ICustomAgentRunner"/>——apps 层 VM 库经此
/// 契约消费评测而不引重型桥接适配器；impl 在 <c>ai/Agents</c> 适配器，需
/// <c>ICustomAgentCatalog</c> 与 <c>AgentFactory</c> 双视野）。
/// <para>评测文件是目录消费模式（镜像 <c>mcp-servers/</c>/<c>skills/</c>），刻意不是 NoteNodeType——
/// 无表单、无 catalog 节点、不进知识对象树。</para>
/// </summary>
public interface ICustomAgentEvalRunner
{
    /// <summary>跑指定 agent 的全部评测套件（逐 case：跑 agent → 本地检查 → 判定）。</summary>
    Task<AgentEvalRunResult> RunAsync(Guid agentId, CancellationToken cancellationToken = default);
}

/// <summary>一次评测运行的总结果（跨套件汇总）。<see cref="Error"/> 非空表示运行级失败（找不到 agent/labs 未配置）。</summary>
public sealed record AgentEvalRunResult(
    string AgentName,
    IReadOnlyList<AgentEvalSuiteResult> Suites,
    string? Error = null)
{
    public bool HasSuites => Suites.Count > 0;
    public int TotalCases => Suites.Sum(s => s.Total);
    public int PassedCases => Suites.Sum(s => s.Passed);
    public int FailedCases => Suites.Sum(s => s.Failed);
    public bool AllPassed => HasSuites && Suites.All(s => s.AllPassed);
}

/// <summary>单个评测套件（一个 *.eval.yaml 文件）的结果。<see cref="Error"/> 非空表示套件级失败（解析失败/运行异常），此时 <see cref="Cases"/> 为空。</summary>
public sealed record AgentEvalSuiteResult(
    string EvalName,
    string? EvalDescription,
    string EvalFile,
    IReadOnlyList<AgentEvalCaseResult> Cases,
    string? Error = null)
{
    public int Total => Cases.Count;
    public int Passed => Cases.Count(c => c.Passed);
    public int Failed => Cases.Count(c => !c.Passed);
    public bool AllPassed => Cases.Count > 0 && Cases.All(c => c.Passed);
}

/// <summary>单条评测 case 的结果：query + 实际回复（截断摘录）+ 各检查判定。</summary>
public sealed record AgentEvalCaseResult(
    string Query,
    string? Response,
    bool Passed,
    IReadOnlyList<AgentEvalCheckResult> Checks);

/// <summary>单条检查的判定（检查名固定四种：关键词/期望输出/工具调用/非空）。</summary>
public sealed record AgentEvalCheckResult(string CheckName, bool Passed, string Reason);
