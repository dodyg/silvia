using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Search;
using Rag.NET.Storage;
using Silvia.Storage.Sqlite;

namespace Silvia.Core.Ingestion;

/// <summary>
/// Indexes a project's files for keyword (BM25) search, with no embedding model. A project uses it when
/// the local embedder is not installed, so indexing, search and answers all work with only a cloud chat
/// model. Files are parsed and chunked exactly as for vectors; <c>keyword_files</c> records the ETag and
/// passage count of each indexed file, so a rescan skips files that have not changed.
/// </summary>
internal sealed class KeywordIndex : IDisposable
{
    private const string Collection = "silvia-keyword";

    private readonly string _databasePath;
    private readonly IReadOnlyList<IDocumentParser> _parsers;
    private readonly IChunkingStrategy _chunker;
    private readonly ChunkingOptions _chunking;
    private readonly SqliteBm25Index _bm25;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _initialized;

    public KeywordIndex(string databasePath, IServiceProvider services, int chunkCharacters, int overlapCharacters)
    {
        _databasePath = databasePath;
        _parsers = services.GetServices<IDocumentParser>().ToList();
        _chunker = services.GetService<IChunkingStrategy>() ?? new Rag.NET.Chunking.RecursiveChunkingStrategy();
        _chunking = new ChunkingOptions { MaxChunkSize = chunkCharacters, Overlap = overlapCharacters };
        _bm25 = new SqliteBm25Index(databasePath, Collection, new SynonymMap());
        EnsureTable();
    }

    /// <summary>
    /// Parses, chunks and indexes one file, replacing any passages it had. A supported type with no
    /// registered parser fails, so the file shows up in Activity instead of being left out silently.
    /// </summary>
    public async Task IndexFileAsync(string path, CancellationToken cancellationToken)
    {
        var documentId = DocumentIds.For(path);
        var info = new FileInfo(documentId);
        if (!info.Exists)
        {
            await RemoveFileAsync(documentId, cancellationToken);
            return;
        }

        var contentType = ContentTypes.For(documentId);
        var parser = _parsers.FirstOrDefault(p => p.CanParse(contentType))
            ?? throw new NotSupportedException($"No parser can read {info.Name} ({contentType}).");

        var metadata = new DocumentMetadata
        {
            DocumentId = new DocumentId(documentId),
            FileName = info.Name,
            ContentType = contentType,
        };

        var chunks = new List<TextChunk>();
        await using (var stream = new FileStream(documentId, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.Asynchronous))
        {
            await foreach (var section in parser.ParseAsync(stream, metadata, cancellationToken))
            {
                await foreach (var chunk in _chunker.ChunkAsync(section, _chunking, cancellationToken))
                {
                    if (string.IsNullOrWhiteSpace(chunk.Text))
                    {
                        continue;
                    }

                    chunks.Add(new TextChunk
                    {
                        Text = chunk.Text,
                        DocumentId = metadata.DocumentId,
                        ChunkIndex = chunks.Count,
                        StartPosition = chunk.StartPosition,
                        EndPosition = chunk.EndPosition,
                        Metadata = new Dictionary<string, MetadataValue>(chunk.Metadata ?? new Dictionary<string, MetadataValue>())
                        {
                            ["file_name"] = info.Name,
                        },
                    });
                }
            }
        }

        await InitializeAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _bm25.Remove(documentId);
            foreach (var chunk in chunks)
            {
                _bm25.Add(chunk);
            }

            Record(documentId, ETagOf(info), chunks.Count);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Removes a file's passages from the keyword index.</summary>
    public async Task RemoveFileAsync(string path, CancellationToken cancellationToken)
    {
        var documentId = DocumentIds.For(path);
        await InitializeAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _bm25.Remove(documentId);
            Forget(documentId);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Brings a folder's passages in step with its files: new and changed files are indexed, files that
    /// went away are removed, and unchanged files are skipped by their ETag. A file that cannot be read
    /// does not stop the others; the scan fails afterwards and names it, so it is retried and reported.
    /// </summary>
    public async Task ScanAsync(FolderFileProvider provider, string folder, CancellationToken cancellationToken)
    {
        var known = IndexedUnder(folder);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var failed = new List<string>();
        foreach (var path in provider.ListFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var documentId = DocumentIds.For(path);
            seen.Add(documentId);
            if (known.TryGetValue(documentId, out var etag) && etag == ETagOf(new FileInfo(documentId)))
            {
                continue;
            }

            try
            {
                await IndexFileAsync(documentId, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                failed.Add($"{Path.GetFileName(documentId)}: {ex.Message}");
            }
        }

        foreach (var gone in known.Keys.Where(id => !seen.Contains(id)))
        {
            await RemoveFileAsync(gone, cancellationToken);
        }

        if (failed.Count > 0)
        {
            throw new InvalidOperationException(
                $"{failed.Count} file{(failed.Count == 1 ? "" : "s")} could not be indexed. {string.Join(" ", failed.Take(5))}");
        }
    }

    /// <summary>The best passages for a query by BM25, highest first.</summary>
    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int topK, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return _bm25.Search(query, topK, new Dictionary<string, MetadataValue>())
                .Select(hit => new SearchResult { Chunk = hit.Item1, Score = (float)hit.Item2 })
                .ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The files in the keyword index, with how many passages each has.</summary>
    public IReadOnlyList<(string DocumentId, int Chunks)> Documents()
    {
        using var connection = SqliteStorage.Open(_databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT path, chunks FROM keyword_files WHERE chunks > 0 ORDER BY path";
        using var reader = command.ExecuteReader();
        var rows = new List<(string, int)>();
        while (reader.Read())
        {
            rows.Add((reader.GetString(0), (int)reader.GetInt64(1)));
        }

        return rows;
    }

    public void Dispose()
    {
        _bm25.Dispose();
        _gate.Dispose();
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_initialized)
            {
                await _bm25.InitializeAsync(cancellationToken);
                _initialized = true;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    // The same ETag the folder provider gives Rag.NET, so both indexes agree on what changed.
    private static string ETagOf(FileInfo info) => $"{info.LastWriteTimeUtc.Ticks}-{info.Length}";

    private void EnsureTable()
    {
        using var connection = SqliteStorage.Open(_databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS keyword_files (
                path TEXT PRIMARY KEY,
                etag TEXT NOT NULL,
                chunks INTEGER NOT NULL)
            """;
        command.ExecuteNonQuery();
    }

    private void Record(string documentId, string etag, int chunks)
    {
        using var connection = SqliteStorage.Open(_databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO keyword_files (path, etag, chunks) VALUES ($path, $etag, $chunks)
            ON CONFLICT (path) DO UPDATE SET etag = excluded.etag, chunks = excluded.chunks
            """;
        command.Parameters.AddWithValue("$path", documentId);
        command.Parameters.AddWithValue("$etag", etag);
        command.Parameters.AddWithValue("$chunks", chunks);
        command.ExecuteNonQuery();
    }

    private void Forget(string documentId)
    {
        using var connection = SqliteStorage.Open(_databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM keyword_files WHERE path = $path";
        command.Parameters.AddWithValue("$path", documentId);
        command.ExecuteNonQuery();
    }

    private Dictionary<string, string> IndexedUnder(string folder)
    {
        var prefix = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using var connection = SqliteStorage.Open(_databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT path, etag FROM keyword_files WHERE substr(path, 1, length($prefix)) = $prefix";
        command.Parameters.AddWithValue("$prefix", prefix);
        using var reader = command.ExecuteReader();
        var rows = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            rows[reader.GetString(0)] = reader.GetString(1);
        }

        return rows;
    }
}
