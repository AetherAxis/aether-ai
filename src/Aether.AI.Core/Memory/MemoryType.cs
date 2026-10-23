namespace Aether.AI.Core.Memory;

/// <summary>
/// The category of a <see cref="MemoryItem"/>. Mirrors the long-term-memory
/// taxonomy from the design document: user preferences, historical tasks,
/// business context, and general knowledge.
/// </summary>
public enum MemoryType
{
    UserPreference,
    TaskHistory,
    BusinessContext,
    Knowledge
}
