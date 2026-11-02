using Microsoft.Data.Sqlite;

namespace Silvia.Storage.Sqlite;

/// <summary>The tables the vector store owns. Idempotent: safe to run on every open.</summary>
public static class SqliteSchema
{
    /// <summary>Creates the vector tables if they do not exist and switches the file to WAL.</summary>
    public static void Ensure(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS vec_chunks (
                document_id TEXT NOT NULL,
                chunk_index INTEGER NOT NULL,
                text        TEXT NOT NULL,
                metadata    TEXT NOT NULL,
                embedding   BLOB NOT NULL,
                model_id    TEXT NOT NULL,
                PRIMARY KEY (document_id, chunk_index)) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS ix_vec_chunks_model ON vec_chunks(model_id);
            CREATE TABLE IF NOT EXISTS vec_collections (
                model_id TEXT PRIMARY KEY,
                dims     INTEGER NOT NULL);
            """;
        command.ExecuteNonQuery();
    }
}
