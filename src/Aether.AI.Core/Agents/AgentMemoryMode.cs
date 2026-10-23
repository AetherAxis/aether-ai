namespace Aether.AI.Core.Agents;

/// <summary>
/// Agent conversation memory policy. Determines how prior turns are retained
/// across runs within the same session.
/// </summary>
public enum AgentMemoryMode
{
    /// <summary>Full memory — retain all prior turns (framework default, unbounded).</summary>
    Full = 0,

    /// <summary>Sliding window — retain only the most recent <c>MaxHistory</c> messages.</summary>
    SlidingWindow = 1,

    /// <summary>No memory — each turn runs independently with no prior context.</summary>
    Off = 2,

    /// <summary>
    /// Summarize — retain only the most recent <c>MaxHistory</c> messages; older turns
    /// are LLM-compressed into a running summary (via <c>SummarizingChatReducer</c>) so
    /// key facts survive without unbounded growth. Distinct from <see cref="SlidingWindow"/>
    /// (which drops old turns outright).
    /// </summary>
    Summarize = 3,
}
