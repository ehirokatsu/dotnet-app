namespace OMMS.Models;

public sealed class GeminiConversation
{
    public Guid Id { get; init; }
    public string Title { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public int MessageCount { get; init; }
}

public sealed class GeminiMessage
{
    public long Id { get; init; }
    public Guid ConversationId { get; init; }
    public string Role { get; init; } = "";
    public string Content { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public GeminiAttachment? Attachment { get; init; }
}

public sealed class GeminiAttachment
{
    public long Id { get; init; }
    public long MessageId { get; init; }
    public string FileName { get; init; } = "";
    public string ContentType { get; init; } = "";
    public byte[] Data { get; init; } = [];
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed record GeminiChatTurn(string Role, string Text, GeminiAttachment? Attachment = null);
