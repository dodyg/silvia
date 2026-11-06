using System.Text.Json;
using Microsoft.Data.Sqlite;
using Silvia.Storage.Sqlite;

namespace Silvia.Core.Projects;

/// <summary>
/// <c>project.db</c>: one file per project holding every index and every Silvia table. Rag.NET
/// creates its own tables in the same file; this class owns the Silvia ones and their migrations.
/// </summary>
public static class ProjectDatabase
{
    /// <summary>2 added <c>web_pages</c>.</summary>
    public const int CurrentVersion = 2;

    /// <summary>Creates a new project file with the current schema and its settings.</summary>
    public static void Create(string path, string id, string name, ProjectSettings settings)
    {
        using var connection = SqliteStorage.Open(path);
        Migrate(connection);
        using var transaction = connection.BeginTransaction();

        Execute(connection, transaction, "DELETE FROM project_info");
        using (var info = connection.CreateCommand())
        {
            info.Transaction = transaction;
            info.CommandText = "INSERT INTO project_info (id, name, embed_model, embed_dims) VALUES ($id, $name, $model, $dims)";
            info.Parameters.AddWithValue("$id", id);
            info.Parameters.AddWithValue("$name", name);
            info.Parameters.AddWithValue("$model", settings.EmbedModel);
            info.Parameters.AddWithValue("$dims", settings.EmbedDims);
            info.ExecuteNonQuery();
        }

        WriteSettings(connection, transaction, settings);
        transaction.Commit();
    }

    /// <summary>Brings the file's Silvia tables up to <see cref="CurrentVersion"/>. Safe to call on every open.</summary>
    public static void Migrate(SqliteConnection connection)
    {
        SqliteSchema.Ensure(connection);
        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, """
            CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS project_info (
                id TEXT NOT NULL,
                name TEXT NOT NULL,
                embed_model TEXT NOT NULL,
                embed_dims INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS sources (
                id INTEGER PRIMARY KEY,
                path TEXT NOT NULL UNIQUE,
                include_globs TEXT NOT NULL DEFAULT '**/*',
                exclude_globs TEXT NOT NULL DEFAULT '',
                enabled INTEGER NOT NULL DEFAULT 1,
                last_scan_utc TEXT);
            CREATE TABLE IF NOT EXISTS ingest_jobs (
                id INTEGER PRIMARY KEY,
                path TEXT NOT NULL,
                kind TEXT NOT NULL,
                source_id INTEGER,
                status TEXT NOT NULL,
                attempts INTEGER NOT NULL DEFAULT 0,
                error TEXT,
                enqueued_utc TEXT NOT NULL,
                finished_utc TEXT);
            -- Finished jobs stay in the table for a week (the latest upsert of each file for good), so the worker's
            -- claim, the idle check and the waiting-job lookups would otherwise read all of them.
            CREATE INDEX IF NOT EXISTS ix_ingest_jobs_status ON ingest_jobs (status, kind);
            -- The pruning's "is there a later upsert of this file" check.
            CREATE INDEX IF NOT EXISTS ix_ingest_jobs_kind_path ON ingest_jobs (kind, path);
            CREATE TABLE IF NOT EXISTS settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS conversations (
                id TEXT PRIMARY KEY,
                title TEXT NOT NULL,
                created_utc TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS messages (
                id INTEGER PRIMARY KEY,
                conversation_id TEXT NOT NULL REFERENCES conversations(id) ON DELETE CASCADE,
                role TEXT NOT NULL,
                content TEXT NOT NULL,
                sources_json TEXT,
                created_utc TEXT NOT NULL);
            -- Each answer reads its conversation's recent turns, and deleting a conversation cascades to its messages.
            CREATE INDEX IF NOT EXISTS ix_messages_conversation ON messages (conversation_id);
            CREATE TABLE IF NOT EXISTS web_pages (
                id INTEGER PRIMARY KEY,
                url TEXT NOT NULL UNIQUE,
                final_url TEXT,
                title TEXT,
                file_name TEXT NOT NULL,
                content BLOB,
                content_type TEXT,
                content_hash TEXT,
                etag TEXT,
                last_modified TEXT,
                fetched_utc TEXT,
                status TEXT NOT NULL,
                error TEXT,
                added_utc TEXT NOT NULL);
            """);
        Execute(connection, transaction, "DELETE FROM schema_version");
        using (var version = connection.CreateCommand())
        {
            version.Transaction = transaction;
            version.CommandText = "INSERT INTO schema_version (version) VALUES ($v)";
            version.Parameters.AddWithValue("$v", CurrentVersion);
            version.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public static ProjectSettings ReadSettings(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = 'project'";
        var json = command.ExecuteScalar() as string;
        return json is null ? new ProjectSettings() : JsonSerializer.Deserialize<ProjectSettings>(json) ?? new ProjectSettings();
    }

    public static void WriteSettings(ProjectSettings settings, string path)
    {
        using var connection = SqliteStorage.Open(path);
        using var transaction = connection.BeginTransaction();
        WriteSettings(connection, transaction, settings);
        transaction.Commit();
    }

    /// <summary>Rewrites the project's identity, used when a copy is imported under a new id.</summary>
    public static void SetIdentity(string path, string id, string name)
    {
        using var connection = SqliteStorage.Open(path);
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE project_info SET id = $id, name = $name";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$name", name);
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private static void WriteSettings(SqliteConnection connection, SqliteTransaction transaction, ProjectSettings settings)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO settings (key, value) VALUES ('project', $value)
            ON CONFLICT (key) DO UPDATE SET value = excluded.value
            """;
        command.Parameters.AddWithValue("$value", JsonSerializer.Serialize(settings));
        command.ExecuteNonQuery();
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
