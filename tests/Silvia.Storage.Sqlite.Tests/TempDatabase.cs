using Microsoft.Data.Sqlite;

namespace Silvia.Storage.Sqlite.Tests;

/// <summary>A database file in a fresh temp folder, removed on dispose.</summary>
internal sealed class TempDatabase : IDisposable
{
    private readonly string _directory;

    public TempDatabase()
    {
        _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "silvia-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        Path = System.IO.Path.Combine(_directory, "project.db");
    }

    public string Path { get; }

    public void Dispose()
    {
        // Pooled connections keep the file open on Windows; release them before deleting.
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Antivirus or the indexer can hold the file briefly. Leaving a temp folder behind is harmless.
        }
    }
}
