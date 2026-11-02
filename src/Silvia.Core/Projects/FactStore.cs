using Silvia.Storage.Sqlite;

namespace Silvia.Core.Projects;

/// <summary>A statement the user has confirmed for a project, given to every answer as trusted context.</summary>
public sealed record Fact(long Id, string Text, DateTimeOffset CreatedUtc);

/// <summary>
/// The project's confirmed facts, in <c>project.db</c>. They cover what the documents do not say, or
/// say only by implication, such as "the ten companies in the startup profiles are approved by SILA".
/// </summary>
public sealed class FactStore
{
    /// <summary>The longest fact accepted. Every fact goes into every prompt, so each stays short.</summary>
    public const int MaxLength = 2000;

    private readonly string _databasePath;

    public FactStore(string databasePath)
    {
        _databasePath = databasePath;
        using var connection = SqliteStorage.Open(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS facts (
                id INTEGER PRIMARY KEY,
                text TEXT NOT NULL,
                created_utc TEXT NOT NULL)
            """;
        command.ExecuteNonQuery();
    }

    /// <summary>Every fact, oldest first.</summary>
    public IReadOnlyList<Fact> List()
    {
        using var connection = SqliteStorage.Open(_databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, text, created_utc FROM facts ORDER BY id";
        using var reader = command.ExecuteReader();
        var facts = new List<Fact>();
        while (reader.Read())
        {
            facts.Add(new Fact(reader.GetInt64(0), reader.GetString(1), DateTimeOffset.Parse(reader.GetString(2), System.Globalization.CultureInfo.InvariantCulture)));
        }

        return facts;
    }

    public Fact Add(string text)
    {
        var fact = new Fact(0, Validate(text), DateTimeOffset.UtcNow);
        using var connection = SqliteStorage.Open(_databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO facts (text, created_utc) VALUES ($text, $created) RETURNING id";
        command.Parameters.AddWithValue("$text", fact.Text);
        command.Parameters.AddWithValue("$created", fact.CreatedUtc.ToString("O"));
        return fact with { Id = (long)command.ExecuteScalar()! };
    }

    /// <summary>Replaces a fact's text. Returns false if the project has no such fact.</summary>
    public bool Update(long id, string text)
    {
        using var connection = SqliteStorage.Open(_databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE facts SET text = $text WHERE id = $id";
        command.Parameters.AddWithValue("$text", Validate(text));
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteNonQuery() == 1;
    }

    /// <summary>Removes a fact. Returns false if the project has no such fact.</summary>
    public bool Delete(long id)
    {
        using var connection = SqliteStorage.Open(_databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM facts WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteNonQuery() == 1;
    }

    private static string Validate(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("A fact needs some text.", nameof(text));
        }

        if (trimmed.Length > MaxLength)
        {
            throw new ArgumentException($"A fact can be at most {MaxLength} characters.", nameof(text));
        }

        return trimmed;
    }
}
