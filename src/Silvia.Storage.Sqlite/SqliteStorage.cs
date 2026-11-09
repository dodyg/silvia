using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

namespace Silvia.Storage.Sqlite;

/// <summary>Opens connections to a project database with the PRAGMAs every connection needs.</summary>
public static class SqliteStorage
{
    /// <summary>
    /// Opens (and creates, if missing) a database file. Every connection waits up to five seconds
    /// for a lock instead of failing at once, and enforces foreign keys.
    /// </summary>
    public static SqliteConnection Open(string databasePath)
    {
        var connection = new SqliteConnection(ConnectionStrings.GetOrAdd(databasePath, Build));
        connection.Open();

        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout = 5000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    // Every store opens a connection per call, so the string is built once per file rather than each time.
    private static readonly ConcurrentDictionary<string, string> ConnectionStrings = new(StringComparer.Ordinal);

    private static string Build(string databasePath) => new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Pooling = true,
        ForeignKeys = true,
    }.ConnectionString;
}
