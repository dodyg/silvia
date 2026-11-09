using Microsoft.Data.Sqlite;
using Silvia.Storage.Sqlite;

namespace Silvia.Core.Catalog;

/// <summary>
/// <c>catalog.db</c>: the list of projects and the defaults new projects copy. It never holds
/// document data, chunks or conversations.
/// </summary>
public sealed class ProjectCatalog : IDisposable
{
    private const int CurrentVersion = 1;
    private readonly SqliteConnection _connection;

    private ProjectCatalog(SqliteConnection connection)
    {
        _connection = connection;
    }

    /// <summary>Opens the catalog at <paramref name="path"/>, creating and migrating it as needed.</summary>
    public static ProjectCatalog Open(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var connection = SqliteStorage.Open(path);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                PRAGMA journal_mode = WAL;
                CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL);
                CREATE TABLE IF NOT EXISTS projects (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL UNIQUE,
                    description TEXT,
                    created_utc TEXT NOT NULL,
                    last_opened_utc TEXT,
                    status TEXT NOT NULL DEFAULT 'ready');
                CREATE TABLE IF NOT EXISTS global_settings (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL);
                """;
            command.ExecuteNonQuery();
        }

        using (var version = connection.CreateCommand())
        {
            version.CommandText = "SELECT COUNT(*) FROM schema_version";
            if (Convert.ToInt64(version.ExecuteScalar()) == 0)
            {
                using var insert = connection.CreateCommand();
                insert.CommandText = "INSERT INTO schema_version (version) VALUES ($v)";
                insert.Parameters.AddWithValue("$v", CurrentVersion);
                insert.ExecuteNonQuery();
            }
        }

        return new ProjectCatalog(connection);
    }

    public IReadOnlyList<ProjectRecord> List()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT id, name, description, created_utc, last_opened_utc, status FROM projects ORDER BY name";
        using var reader = command.ExecuteReader();
        var rows = new List<ProjectRecord>();
        while (reader.Read())
        {
            rows.Add(Read(reader));
        }

        return rows;
    }

    public ProjectRecord? Find(string id)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT id, name, description, created_utc, last_opened_utc, status FROM projects WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public void Insert(ProjectRecord record)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO projects (id, name, description, created_utc, last_opened_utc, status)
            VALUES ($id, $name, $description, $created, $opened, $status)
            """;
        command.Parameters.AddWithValue("$id", record.Id);
        command.Parameters.AddWithValue("$name", record.Name);
        command.Parameters.AddWithValue("$description", (object?)record.Description ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", record.CreatedUtc.ToString("O"));
        command.Parameters.AddWithValue("$opened", (object?)record.LastOpenedUtc?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", ProjectStatusNames.ToDb(record.Status));
        command.ExecuteNonQuery();
    }

    public void SetStatus(string id, ProjectStatus status) =>
        Update("UPDATE projects SET status = $value WHERE id = $id", id, ProjectStatusNames.ToDb(status));

    public void Rename(string id, string name) =>
        Update("UPDATE projects SET name = $value WHERE id = $id", id, name);

    public void MarkOpened(string id, DateTimeOffset when) =>
        Update("UPDATE projects SET last_opened_utc = $value WHERE id = $id", id, when.ToString("O"));

    public void Delete(string id)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "DELETE FROM projects WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public string? GetGlobal(string key)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT value FROM global_settings WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    public void SetGlobal(string key, string value)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO global_settings (key, value) VALUES ($key, $value)
            ON CONFLICT (key) DO UPDATE SET value = excluded.value
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    public void Dispose() => _connection.Dispose();

    private void Update(string sql, string id, string? value)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$value", (object?)value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static ProjectRecord Read(SqliteDataReader reader) => new(
        Id: reader.GetString(0),
        Name: reader.GetString(1),
        Description: reader.IsDBNull(2) ? null : reader.GetString(2),
        CreatedUtc: DateTimeOffset.Parse(reader.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind),
        LastOpenedUtc: reader.IsDBNull(4) ? null : DateTimeOffset.Parse(reader.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind),
        Status: ProjectStatusNames.FromDb(reader.GetString(5)));
}
