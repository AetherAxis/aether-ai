using Microsoft.Extensions.AI;

namespace Aether.AI.Harness.Agents;

internal static class ChatMessageClassification
{
    public static bool IsBusinessUserMessage(ChatMessage message)
        => message.Role == ChatRole.User
            && message.Contents.OfType<TextContent>()
                .Any(content => !string.IsNullOrWhiteSpace(content.Text));
}
