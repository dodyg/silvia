using Microsoft.Data.Sqlite;

namespace Silvia.Storage.Sqlite;

/// <summary>Opens connections to a project database with the PRAGMAs every connection needs.</summary>
internal static class SqliteStorage
{
    /// <summary>
    /// Opens (and creates, if missing) a database file. Every connection waits up to five seconds
    /// for a lock instead of failing at once, and enforces foreign keys.
    /// </summary>
    public static SqliteConnection Open(string databasePath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            ForeignKeys = true,
        };

        var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();

        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout = 5000;";
        pragma.ExecuteNonQuery();
        return connection;
    }
}
