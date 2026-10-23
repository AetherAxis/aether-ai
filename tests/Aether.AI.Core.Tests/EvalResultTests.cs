using Aether.AI.Core.Agents;

namespace Aether.AI.Core.Tests;

/// <summary>
/// 评测结果记录的聚合口径。UI 的「通过 X/Y」直接读这些派生属性——统计若有偏差，
/// 用户看到的是「评测全绿/全红」的错误结论，而不是一条报错。
/// </summary>
public class EvalResultTests
{
    [Fact]
    public void Run_result_sums_cases_across_suites()
    {
        var run = new AgentEvalRunResult("客服 Agent", [
            Suite("a", cases: [Case(true), Case(false)]),
            Suite("b", cases: [Case(true), Case(true)]),
        ]);

        Assert.True(run.HasSuites);
        Assert.Equal(4, run.TotalCases);
        Assert.Equal(3, run.PassedCases);
        Assert.Equal(1, run.FailedCases);
        Assert.False(run.AllPassed); // 有一条失败就不算全通过
    }

    [Fact]
    public void Run_result_reports_failure_when_there_is_nothing_to_run()
    {
        // 没有套件（labs 未配置 / agent 无 eval 文件）不能算「通过」。
        var empty = new AgentEvalRunResult("客服 Agent", []);
        Assert.False(empty.HasSuites);
        Assert.False(empty.AllPassed);
        Assert.Equal(0, empty.TotalCases);

        // 空套件（解析失败但记了 Error）同样不算通过。
        var suiteErrored = new AgentEvalRunResult("客服 Agent", [Suite("broken", cases: [], error: "YAML 解析失败")]);
        Assert.False(suiteErrored.AllPassed);
        Assert.Equal("YAML 解析失败", Assert.Single(suiteErrored.Suites).Error);
    }

    [Fact]
    public void Suite_result_counts_only_all_green_as_passed()
    {
        var suite = Suite("s", cases: [Case(true), Case(true)]);
        Assert.Equal(2, suite.Total);
        Assert.Equal(2, suite.Passed);
        Assert.Equal(0, suite.Failed);
        Assert.True(suite.AllPassed);

        Assert.False(Suite("s", cases: [Case(true), Case(false)]).AllPassed);
        Assert.False(Suite("s", cases: []).AllPassed); // 空套件 ≠ 通过
    }

    [Fact]
    public void Case_result_keeps_every_check_reason()
    {
        var kase = new AgentEvalCaseResult("上海天气", "晴", Passed: false, Checks: [
            new AgentEvalCheckResult("关键词", Passed: false, Reason: "缺少「晴」"),
            new AgentEvalCheckResult("工具调用", Passed: true, Reason: "调用了 GetWeather"),
        ]);

        Assert.False(kase.Passed);
        Assert.Equal(2, kase.Checks.Count);
        Assert.Equal("缺少「晴」", kase.Checks[0].Reason);
        Assert.Equal("调用了 GetWeather", kase.Checks[1].Reason);
    }

    [Fact]
    public void Remote_approval_decision_is_a_value_record_with_optional_reason()
    {
        // 手机端 wire DTO：只带 Approved（+可选 Reason），RequestId 由桌面侧补齐。
        Assert.Equal(new RemoteApprovalDecision(true), new RemoteApprovalDecision(true) with { });
        Assert.Null(new RemoteApprovalDecision(false).Reason);
        Assert.Equal("用户拒绝", new RemoteApprovalDecision(false, "用户拒绝").Reason);
    }

    private static AgentEvalSuiteResult Suite(
        string name, IReadOnlyList<AgentEvalCaseResult> cases, string? error = null)
        => new(EvalName: name, EvalDescription: null, EvalFile: $"{name}.eval.yaml", Cases: cases, Error: error);

    private static AgentEvalCaseResult Case(bool passed) => new($"q-{passed}", "r", passed, []);
}