using System.Text.Json.Serialization;

namespace Aether.AI.Core.Messages;

/// <summary>
/// A single message in an agent conversation. This is a framework-agnostic
/// representation that is independent of Microsoft Agent Framework's own
/// message types; the Harness layer is responsible for translating between
/// the two.
/// </summary>
public sealed class Message
{
    /// <summary>The role of the message author.</summary>
    public MessageRole Role { get; init; }

    /// <summary>The textual content of the message.</summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>UTC timestamp the message was created.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// When <see cref="Role"/> is <see cref="MessageRole.Tool"/>, the name of
    /// the tool that produced this message. Otherwise <c>null</c>.
    /// </summary>
    public string? ToolName { get; init; }

    /// <summary>
    /// When <see cref="Role"/> is <see cref="MessageRole.Tool"/>, the
    /// identifier of the tool call this message responds to. Otherwise <c>null</c>.
    /// </summary>
    public string? ToolCallId { get; init; }

    /// <summary>Optional metadata associated with the message.</summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    [JsonIgnore]
    public bool IsEmpty => string.IsNullOrWhiteSpace(Content);

    public static Message System(string content) => new() { Role = MessageRole.System, Content = content };
    public static Message User(string content) => new() { Role = MessageRole.User, Content = content };
    public static Message Assistant(string content) => new() { Role = MessageRole.Assistant, Content = content };
    public static Message Tool(string content, string toolName, string? toolCallId = null) => new()
    {
        Role = MessageRole.Tool,
        Content = content,
        ToolName = toolName,
        ToolCallId = toolCallId
    };

    public override string ToString() => $"[{Role}] {Content}";
}
