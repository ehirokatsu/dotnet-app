using Npgsql;
using OMMS.Models;

namespace OMMS.Db.dao;

public sealed class GeminiConversationDao(IConfiguration configuration, TimeProvider timeProvider)
{
    private readonly string _connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? "Host=localhost;Database=omms;Username=hiro;";
    private readonly int _retentionDays = Math.Max(1,
        configuration.GetValue("Gemini:HistoryRetentionDays", 3));

    public async Task EnsureTablesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            CREATE TABLE IF NOT EXISTS gemini_conversations (
                id UUID PRIMARY KEY,
                title VARCHAR(120) NOT NULL,
                created_at TIMESTAMPTZ NOT NULL,
                updated_at TIMESTAMPTZ NOT NULL
            );

            CREATE TABLE IF NOT EXISTS gemini_messages (
                id BIGSERIAL PRIMARY KEY,
                conversation_id UUID NOT NULL REFERENCES gemini_conversations(id) ON DELETE CASCADE,
                role VARCHAR(10) NOT NULL CHECK (role IN ('user', 'model')),
                content TEXT NOT NULL,
                created_at TIMESTAMPTZ NOT NULL
            );

            CREATE TABLE IF NOT EXISTS gemini_attachments (
                id BIGSERIAL PRIMARY KEY,
                message_id BIGINT NOT NULL UNIQUE REFERENCES gemini_messages(id) ON DELETE CASCADE,
                file_name VARCHAR(255) NOT NULL,
                content_type VARCHAR(100) NOT NULL,
                data BYTEA NOT NULL,
                created_at TIMESTAMPTZ NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_gemini_conversations_updated_at
                ON gemini_conversations(updated_at DESC);
            CREATE INDEX IF NOT EXISTS ix_gemini_messages_conversation_id_id
                ON gemini_messages(conversation_id, id);
            """, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteExpiredAsync(CancellationToken cancellationToken = default)
    {
        var cutoff = timeProvider.GetUtcNow().AddDays(-_retentionDays);
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "DELETE FROM gemini_conversations WHERE updated_at < @cutoff", connection);
        command.Parameters.AddWithValue("cutoff", cutoff);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<List<GeminiConversation>> GetRecentAsync(
        CancellationToken cancellationToken = default)
    {
        await DeleteExpiredAsync(cancellationToken);
        var conversations = new List<GeminiConversation>();
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT c.id, c.title, c.created_at, c.updated_at, COUNT(m.id)::int
            FROM gemini_conversations c
            LEFT JOIN gemini_messages m ON m.conversation_id = c.id
            GROUP BY c.id, c.title, c.created_at, c.updated_at
            ORDER BY c.updated_at DESC
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            conversations.Add(new GeminiConversation
            {
                Id = reader.GetGuid(0),
                Title = reader.GetString(1),
                CreatedAt = new DateTimeOffset(reader.GetDateTime(2).ToUniversalTime()),
                UpdatedAt = new DateTimeOffset(reader.GetDateTime(3).ToUniversalTime()),
                MessageCount = reader.GetInt32(4)
            });
        }
        return conversations;
    }

    public async Task<GeminiConversation?> GetConversationAsync(
        Guid id, CancellationToken cancellationToken = default)
    {
        await DeleteExpiredAsync(cancellationToken);
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT id, title, created_at, updated_at
            FROM gemini_conversations WHERE id = @id
            """, connection);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new GeminiConversation
        {
            Id = reader.GetGuid(0),
            Title = reader.GetString(1),
            CreatedAt = new DateTimeOffset(reader.GetDateTime(2).ToUniversalTime()),
            UpdatedAt = new DateTimeOffset(reader.GetDateTime(3).ToUniversalTime())
        };
    }

    public async Task<List<GeminiMessage>> GetMessagesAsync(
        Guid conversationId, CancellationToken cancellationToken = default)
    {
        var messages = new List<GeminiMessage>();
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT m.id, m.conversation_id, m.role, m.content, m.created_at,
                   a.id, a.file_name, a.content_type, a.data, a.created_at
            FROM gemini_messages m
            LEFT JOIN gemini_attachments a ON a.message_id = m.id
            WHERE m.conversation_id = @conversationId
            ORDER BY m.id
            """, connection);
        command.Parameters.AddWithValue("conversationId", conversationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            messages.Add(new GeminiMessage
            {
                Id = reader.GetInt64(0),
                ConversationId = reader.GetGuid(1),
                Role = reader.GetString(2),
                Content = reader.GetString(3),
                CreatedAt = new DateTimeOffset(reader.GetDateTime(4).ToUniversalTime()),
                Attachment = reader.IsDBNull(5) ? null : new GeminiAttachment
                {
                    Id = reader.GetInt64(5),
                    MessageId = reader.GetInt64(0),
                    FileName = reader.GetString(6),
                    ContentType = reader.GetString(7),
                    Data = reader.GetFieldValue<byte[]>(8),
                    CreatedAt = new DateTimeOffset(reader.GetDateTime(9).ToUniversalTime())
                }
            });
        }
        return messages;
    }

    public async Task<Guid> CreateWithMessagesAsync(
        string title, string userText, string modelText, GeminiAttachment? attachment = null,
        CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        var now = timeProvider.GetUtcNow();
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await InsertConversationAsync(connection, transaction, id, title, now, cancellationToken);
        var userMessageId = await InsertMessageAsync(
            connection, transaction, id, "user", userText, now, cancellationToken);
        if (attachment is not null)
            await InsertAttachmentAsync(connection, transaction, userMessageId, attachment, now, cancellationToken);
        await InsertMessageAsync(connection, transaction, id, "model", modelText, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return id;
    }

    public async Task AppendExchangeAsync(
        Guid conversationId, string userText, string modelText, GeminiAttachment? attachment = null,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var userMessageId = await InsertMessageAsync(
            connection, transaction, conversationId, "user", userText, now, cancellationToken);
        if (attachment is not null)
            await InsertAttachmentAsync(connection, transaction, userMessageId, attachment, now, cancellationToken);
        await InsertMessageAsync(connection, transaction, conversationId, "model", modelText, now, cancellationToken);
        await using var update = new NpgsqlCommand(
            "UPDATE gemini_conversations SET updated_at = @now WHERE id = @id",
            connection, transaction);
        update.Parameters.AddWithValue("now", now);
        update.Parameters.AddWithValue("id", conversationId);
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("保存対象の会話が見つかりません。");
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "DELETE FROM gemini_conversations WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertConversationAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id,
        string title, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO gemini_conversations (id, title, created_at, updated_at)
            VALUES (@id, @title, @now, @now)
            """, connection, transaction);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("title", title);
        command.Parameters.AddWithValue("now", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<long> InsertMessageAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid conversationId,
        string role, string content, DateTimeOffset createdAt, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO gemini_messages (conversation_id, role, content, created_at)
            VALUES (@conversationId, @role, @content, @createdAt)
            RETURNING id
            """, connection, transaction);
        command.Parameters.AddWithValue("conversationId", conversationId);
        command.Parameters.AddWithValue("role", role);
        command.Parameters.AddWithValue("content", content);
        command.Parameters.AddWithValue("createdAt", createdAt);
        return (long)(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("メッセージを保存できませんでした。"));
    }

    private static async Task InsertAttachmentAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long messageId,
        GeminiAttachment attachment, DateTimeOffset createdAt, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO gemini_attachments
                (message_id, file_name, content_type, data, created_at)
            VALUES (@messageId, @fileName, @contentType, @data, @createdAt)
            """, connection, transaction);
        command.Parameters.AddWithValue("messageId", messageId);
        command.Parameters.AddWithValue("fileName", attachment.FileName);
        command.Parameters.AddWithValue("contentType", attachment.ContentType);
        command.Parameters.AddWithValue("data", attachment.Data);
        command.Parameters.AddWithValue("createdAt", createdAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
