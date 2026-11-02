using Microsoft.Data.Sqlite;

namespace Silvia.Storage.Sqlite;

/// <summary>The tables the vector store owns. Idempotent: safe to run on every open.</summary>
public static class SqliteSchema
{
    /// <summary>
    /// Creates a vector table, and its collections table, if they do not exist, and switches the file
    /// to WAL. Each vector table is named by the caller, so documents and memory keep separate tables.
    /// </summary>
    public static void Ensure(SqliteConnection connection, string table = "vec_chunks")
    {
        var collections = table + "_collections";
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS {table} (
                document_id TEXT NOT NULL,
                chunk_index INTEGER NOT NULL,
                text        TEXT NOT NULL,
                metadata    TEXT NOT NULL,
                embedding   BLOB NOT NULL,
                model_id    TEXT NOT NULL,
                PRIMARY KEY (document_id, chunk_index)) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS ix_{table}_model ON {table}(model_id);
            CREATE TABLE IF NOT EXISTS {collections} (
                model_id TEXT PRIMARY KEY,
                dims     INTEGER NOT NULL);
            """;
        command.ExecuteNonQuery();
    }
}
