using System.Text.RegularExpressions;
using Aether.AI.Brains.Abstractions;
using Aether.AI.Core.Agents;
using Microsoft.Extensions.AI;

namespace Aether.AI.Brains.Routing;

/// <summary>Side-effect-free entry matcher for legacy declarative rule programs.</summary>
public sealed class RuleProgramMatcher : IBrainMatcher
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);
    public RuleProgramMatcher(string id, int priority = 0)
    {
        Id = id;
        Priority = priority;
    }

    public string Id { get; }
    public int Priority { get; }

    public BrainMatchResult Match(AgentDefinition definition, ChatMessage input)
    {
        var program = definition.Rules;
        if (program is null) return BrainMatchResult.NotMatched("No rule program configured.");
        var text = input.Text ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(program.Capture))
            return TryMatch(text, program.Capture, out var captureMatched)
                ? captureMatched
                    ? BrainMatchResult.Matched("Program capture matched.")
                    : BrainMatchResult.NotMatched("Program capture did not match.")
                : BrainMatchResult.NotMatched("Program capture timed out.");

        var entry = program.Steps.FirstOrDefault(step =>
            (step.Match.MinToolCalls is null || step.Match.MinToolCalls <= 0)
            && (step.Match.MaxToolCalls is null || step.Match.MaxToolCalls >= 0)
            && string.IsNullOrWhiteSpace(step.Match.OnResultRegex));
        if (entry is null || string.IsNullOrWhiteSpace(entry.Match.OnUserRegex))
            return BrainMatchResult.NotMatched("Rule program has no safe routing entry matcher.");

        return TryMatch(text, entry.Match.OnUserRegex, out var entryMatched)
            ? entryMatched
                ? BrainMatchResult.Matched($"Entry rule '{entry.Name ?? "unnamed"}' matched.")
                : BrainMatchResult.NotMatched($"Entry rule '{entry.Name ?? "unnamed"}' did not match.")
            : BrainMatchResult.NotMatched($"Entry rule '{entry.Name ?? "unnamed"}' timed out.");
    }

    private static bool TryMatch(string input, string pattern, out bool matched)
    {
        try
        {
            matched = Regex.IsMatch(input, pattern, RegexOptions.CultureInvariant, MatchTimeout);
            return true;
        }
        catch (RegexMatchTimeoutException)
        {
            matched = false;
            return false;
        }
    }
}
