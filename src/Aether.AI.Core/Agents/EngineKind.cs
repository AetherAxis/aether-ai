namespace Aether.AI.Core.Agents;

/// <summary>
/// Agent 大脑类型。<see cref="Llm"/>（默认）由模型客户端决策，<see cref="Rule"/> 执行声明式规则，
/// <see cref="Code"/> 执行作者程序；三类均复用 Harness 的工具与会话设施。
/// </summary>
public enum EngineKind
{
    /// <summary>LLM 大脑（默认，既有行为）。保留序列化值 0。</summary>
    Llm = 0,

    /// <summary>声明式规则大脑。保留序列化值 1。</summary>
    Rule = 1,

    /// <summary>代码大脑；具体作者语言由 <see cref="CodeBrainLanguage"/> 表达。</summary>
    Code = 2,
}

/// <summary>代码大脑作者语言；与 <see cref="EngineKind"/> 和源码加载方式正交。</summary>
public enum CodeBrainLanguage
{
    CSharp = 0,
    FSharp = 1,
}
