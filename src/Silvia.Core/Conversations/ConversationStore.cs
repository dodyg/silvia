using System.Text.Json;
using Microsoft.Data.Sqlite;
using Rag.NET.Models;
using Silvia.Core.Projects;
using Silvia.Storage.Sqlite;

namespace Silvia.Core.Conversations;

/// <summary>A saved chat, held in its project's file. A conversation id from another project finds nothing here.</summary>
public sealed record Conversation(string Id, string Title, DateTimeOffset CreatedUtc);

/// <summary>One stored message. Assistant messages carry the chunks that were cited.</summary>
public sealed record StoredMessage(string Role, string Content, IReadOnlyList<CitedChunk> Sources, DateTimeOffset CreatedUtc);

/// <summary>A chunk cited by an answer, kept so the citation still resolves after the chat is reopened.</summary>
/// <remarks>
/// For a web page, <see cref="FileName"/> is the page's title and <see cref="Url"/> its address; for a file,
/// <see cref="Url"/> is null.
/// </remarks>
public sealed record CitedChunk(string DocumentId, int ChunkIndex, string FileName, string Text, string? Url = null)
{
    /// <summary>The citation for a retrieved chunk, named by its file when the parser recorded one.</summary>
    public static CitedChunk From(SearchResult result) => new(
        result.Chunk.DocumentId.Value,
        result.Chunk.ChunkIndex,
        result.Chunk.Metadata.TryGetValue("file_name", out var name) ? name.ToString() : result.Chunk.DocumentId.Value,
        result.Chunk.Text);
}

/// <summary>Reads and writes conversations and their messages in <c>project.db</c>.</summary>
public sealed class ConversationStore(string databasePath)
{
    public Conversation Create(string title)
    {
        var conversation = new Conversation(Ulid.New(), Truncate(title, 80), DateTimeOffset.UtcNow);
        using var connection = SqliteStorage.Open(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO conversations (id, title, created_utc) VALUES ($id, $title, $created)";
        command.Parameters.AddWithValue("$id", conversation.Id);
        command.Parameters.AddWithValue("$title", conversation.Title);
        command.Parameters.AddWithValue("$created", conversation.CreatedUtc.ToString("O"));
        command.ExecuteNonQuery();
        return conversation;
    }

    public IReadOnlyList<Conversation> List()
    {
        using var connection = SqliteStorage.Open(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, title, created_utc FROM conversations ORDER BY created_utc DESC";
        using var reader = command.ExecuteReader();
        var rows = new List<Conversation>();
        while (reader.Read())
        {
            rows.Add(Read(reader));
        }

        return rows;
    }

    public Conversation? Find(string id)
    {
        using var connection = SqliteStorage.Open(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, title, created_utc FROM conversations WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public IReadOnlyList<StoredMessage> Messages(string conversationId, int? last = null)
    {
        using var connection = SqliteStorage.Open(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT role, content, sources_json, created_utc FROM (
                SELECT id, role, content, sources_json, created_utc FROM messages
                WHERE conversation_id = $id ORDER BY id DESC LIMIT $limit)
            ORDER BY id
            """;
        command.Parameters.AddWithValue("$id", conversationId);
        command.Parameters.AddWithValue("$limit", last ?? int.MaxValue);
        using var reader = command.ExecuteReader();
        var messages = new List<StoredMessage>();
        while (reader.Read())
        {
            var sources = reader.IsDBNull(2)
                ? []
                : JsonSerializer.Deserialize<List<CitedChunk>>(reader.GetString(2)) ?? [];
            messages.Add(new StoredMessage(
                reader.GetString(0),
                reader.GetString(1),
                sources,
                DateTimeOffset.Parse(reader.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind)));
        }

        return messages;
    }

    /// <summary>How many messages of <paramref name="role"/> the conversation holds, without reading them.</summary>
    public int Count(string conversationId, string role)
    {
        using var connection = SqliteStorage.Open(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM messages WHERE conversation_id = $id AND role = $role";
        command.Parameters.AddWithValue("$id", conversationId);
        command.Parameters.AddWithValue("$role", role);
        return (int)(long)command.ExecuteScalar()!;
    }

    public void Append(string conversationId, string role, string content, IReadOnlyList<CitedChunk>? sources = null)
    {
        using var connection = SqliteStorage.Open(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO messages (conversation_id, role, content, sources_json, created_utc)
            VALUES ($id, $role, $content, $sources, $created)
            """;
        command.Parameters.AddWithValue("$id", conversationId);
        command.Parameters.AddWithValue("$role", role);
        command.Parameters.AddWithValue("$content", content);
        command.Parameters.AddWithValue("$sources", sources is { Count: > 0 } ? JsonSerializer.Serialize(sources) : DBNull.Value);
        command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public bool Delete(string id)
    {
        using var connection = SqliteStorage.Open(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM conversations WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteNonQuery() > 0;
    }

    private static Conversation Read(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        DateTimeOffset.Parse(reader.GetString(2), null, System.Globalization.DateTimeStyles.RoundtripKind));

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max];
}
