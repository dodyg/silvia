using System.Numerics.Tensors;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Data.Sqlite;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Silvia.Storage.Sqlite;

/// <summary>
/// A Rag.NET vector store kept in a SQLite file. Vectors are stored as normalized float32 blobs
/// and searched by exact brute force over an in-memory matrix, so results are exact and the
/// file stays the source of truth.
/// </summary>
/// <remarks>
/// One instance serves one embedding model in one database file: it searches only the rows
/// whose <c>model_id</c> matches, and rejects vectors of any other length. A project owns one
/// instance over its <c>project.db</c>.
/// </remarks>
public sealed class SqliteVectorStore : IVectorStore, ICollectionManageable, IChunkLookup, IDisposable
{
    private readonly string _databasePath;
    private readonly string _modelId;
    private readonly int _dimensions;
    private readonly VectorMatrix _matrix;
    private readonly ReaderWriterLockSlim _lock = new();
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private volatile bool _loaded;
    private volatile bool _schemaReady;

    /// <param name="databasePath">The SQLite file. Created with its schema if it does not exist.</param>
    /// <param name="dimensions">The length of every vector this store accepts.</param>
    /// <param name="modelId">The embedding model whose vectors this store holds and searches.</param>
    public SqliteVectorStore(string databasePath, int dimensions, string modelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimensions);

        _databasePath = databasePath;
        _modelId = modelId;
        _dimensions = dimensions;
        _matrix = new VectorMatrix(dimensions);
    }

    /// <summary>The embedding model this store searches.</summary>
    public string ModelId => _modelId;

    /// <summary>The vector length this store accepts.</summary>
    public int Dimensions => _dimensions;

    /// <inheritdoc />
    public Task InitializeAsync(CancellationToken cancellationToken = default) => EnsureLoadedAsync(cancellationToken);

    /// <inheritdoc />
    public async Task StoreAsync(IReadOnlyList<EmbeddedChunk> chunks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        cancellationToken.ThrowIfCancellationRequested();

        if (chunks.Count == 0)
        {
            return;
        }

        foreach (var chunk in chunks)
        {
            if (chunk.Embedding.Length != _dimensions)
            {
                throw new ArgumentException(
                    $"Chunk '{chunk.Chunk.DocumentId.Value}#{chunk.Chunk.ChunkIndex}' has {chunk.Embedding.Length} dimensions; the store expects {_dimensions}.",
                    nameof(chunks));
            }
        }

        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

        // Normalize outside the lock; only the database write and matrix patch need it.
        var vectors = new float[chunks.Count][];
        for (var i = 0; i < chunks.Count; i++)
        {
            vectors[i] = Normalize(chunks[i].Embedding.Span);
        }

        _lock.EnterWriteLock();
        try
        {
            using var connection = OpenDatabase();
            using var transaction = connection.BeginTransaction();

            EnsureCollection(connection, transaction, _modelId, _dimensions);

            using var upsert = connection.CreateCommand();
            upsert.Transaction = transaction;
            upsert.CommandText = """
                INSERT INTO vec_chunks (document_id, chunk_index, text, metadata, embedding, model_id)
                VALUES ($document_id, $chunk_index, $text, $metadata, $embedding, $model_id)
                ON CONFLICT (document_id, chunk_index) DO UPDATE SET
                    text = excluded.text,
                    metadata = excluded.metadata,
                    embedding = excluded.embedding,
                    model_id = excluded.model_id
                """;
            var documentId = upsert.Parameters.Add("$document_id", SqliteType.Text);
            var chunkIndex = upsert.Parameters.Add("$chunk_index", SqliteType.Integer);
            var text = upsert.Parameters.Add("$text", SqliteType.Text);
            var metadata = upsert.Parameters.Add("$metadata", SqliteType.Text);
            var embedding = upsert.Parameters.Add("$embedding", SqliteType.Blob);
            var modelId = upsert.Parameters.Add("$model_id", SqliteType.Text);
            upsert.Prepare();

            for (var i = 0; i < chunks.Count; i++)
            {
                var chunk = chunks[i].Chunk;
                documentId.Value = chunk.DocumentId.Value;
                chunkIndex.Value = chunk.ChunkIndex;
                text.Value = chunk.Text;
                metadata.Value = MetadataCodec.Encode(chunk.Metadata);
                embedding.Value = MemoryMarshal.AsBytes(vectors[i].AsSpan()).ToArray();
                modelId.Value = _modelId;
                upsert.ExecuteNonQuery();
            }

            transaction.Commit();

            for (var i = 0; i < chunks.Count; i++)
            {
                var key = new ChunkKey(chunks[i].Chunk.DocumentId.Value, chunks[i].Chunk.ChunkIndex);
                _matrix.Upsert(key, vectors[i]);
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        ReadOnlyMemory<float> queryEmbedding,
        SearchOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        if (options.TopK <= 0)
        {
            return [];
        }

        if (queryEmbedding.Length != _dimensions)
        {
            throw new ArgumentException(
                $"Query has {queryEmbedding.Length} dimensions; the store expects {_dimensions}.",
                nameof(queryEmbedding));
        }

        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        var query = Normalize(queryEmbedding.Span);

        _lock.EnterReadLock();
        try
        {
            using var connection = OpenDatabase();

            IReadOnlyList<int>? candidates = null;
            if (options.MetadataFilter is { Count: > 0 })
            {
                candidates = FilteredRows(connection, options.MetadataFilter);
            }

            var top = _matrix.TopK(query, options.TopK, options.MinScore, candidates);
            var results = new List<SearchResult>(top.Count);
            foreach (var (row, score) in top)
            {
                var key = _matrix.KeyOf(row);
                if (ReadChunk(connection, key, includeEmbedding: true) is { } chunk)
                {
                    results.Add(new SearchResult { Chunk = chunk, Score = score });
                }
            }

            return results;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <inheritdoc />
    public Task DeleteByDocumentIdAsync(string documentId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documentId);
        cancellationToken.ThrowIfCancellationRequested();

        _lock.EnterWriteLock();
        try
        {
            using var connection = OpenDatabase();
            using var transaction = connection.BeginTransaction();
            using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM vec_chunks WHERE document_id = $document_id";
            delete.Parameters.AddWithValue("$document_id", documentId);
            delete.ExecuteNonQuery();
            transaction.Commit();

            // Rows of this document in the matrix, found by key, removed highest index first.
            var rows = new List<int>();
            for (var row = 0; row < _matrix.Count; row++)
            {
                if (string.Equals(_matrix.KeyOf(row).DocumentId, documentId, StringComparison.Ordinal))
                {
                    rows.Add(row);
                }
            }

            rows.Reverse();
            _matrix.RemoveRowsDescending(rows);
        }
        finally
        {
            _lock.ExitWriteLock();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TextChunk>> GetChunksAsync(
        IReadOnlyList<ChunkKey> keys,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        cancellationToken.ThrowIfCancellationRequested();

        if (keys.Count == 0)
        {
            return [];
        }

        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

        _lock.EnterReadLock();
        try
        {
            using var connection = OpenDatabase();
            var chunks = new List<TextChunk>(keys.Count);
            foreach (var key in keys)
            {
                if (ReadChunk(connection, key, includeEmbedding: false) is { } chunk)
                {
                    chunks.Add(chunk);
                }
            }

            return chunks;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <inheritdoc />
    public Task CreateCollectionAsync(string name, int vectorDimensions, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(vectorDimensions);
        cancellationToken.ThrowIfCancellationRequested();

        _lock.EnterWriteLock();
        try
        {
            using var connection = OpenDatabase();
            using var transaction = connection.BeginTransaction();
            EnsureCollection(connection, transaction, name, vectorDimensions);
            transaction.Commit();
        }
        finally
        {
            _lock.ExitWriteLock();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Deletes every vector of the named model (a collection is the set of vectors for one model
    /// id) and forgets the collection. Used to drop the old model after a re-embed.
    /// </summary>
    public Task DeleteCollectionAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        cancellationToken.ThrowIfCancellationRequested();

        _lock.EnterWriteLock();
        try
        {
            using var connection = OpenDatabase();
            using var transaction = connection.BeginTransaction();

            using (var deleteChunks = connection.CreateCommand())
            {
                deleteChunks.Transaction = transaction;
                deleteChunks.CommandText = "DELETE FROM vec_chunks WHERE model_id = $model_id";
                deleteChunks.Parameters.AddWithValue("$model_id", name);
                deleteChunks.ExecuteNonQuery();
            }

            using (var deleteCollection = connection.CreateCommand())
            {
                deleteCollection.Transaction = transaction;
                deleteCollection.CommandText = "DELETE FROM vec_collections WHERE model_id = $model_id";
                deleteCollection.Parameters.AddWithValue("$model_id", name);
                deleteCollection.ExecuteNonQuery();
            }

            transaction.Commit();

            if (string.Equals(name, _modelId, StringComparison.Ordinal))
            {
                _matrix.Clear();
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> CollectionExistsAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        cancellationToken.ThrowIfCancellationRequested();

        using var connection = OpenDatabase();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM vec_collections WHERE model_id = $model_id)";
        command.Parameters.AddWithValue("$model_id", name);
        return Task.FromResult(Convert.ToInt64(command.ExecuteScalar()) == 1);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _lock.Dispose();
        _loadGate.Dispose();
    }

    // Every connection goes through here, so the schema exists before any statement runs,
    // including the synchronous delete and collection calls that never load the matrix.
    private SqliteConnection OpenDatabase()
    {
        var connection = SqliteStorage.Open(_databasePath);
        if (!_schemaReady)
        {
            SqliteSchema.Ensure(connection);
            _schemaReady = true;
        }

        return connection;
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_loaded)
        {
            return;
        }

        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_loaded)
            {
                return;
            }

            LoadMatrix();
            _loaded = true;
        }
        finally
        {
            _loadGate.Release();
        }
    }

    // Synchronous on purpose: a ReaderWriterLockSlim write lock is owned by the thread that took it,
    // so the lock must not be held across an await.
    private void LoadMatrix()
    {
        using var connection = OpenDatabase();

        _lock.EnterWriteLock();
        try
        {
            _matrix.Clear();

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT document_id, chunk_index, embedding FROM vec_chunks WHERE model_id = $model_id";
            command.Parameters.AddWithValue("$model_id", _modelId);

            using var reader = command.ExecuteReader();
            var buffer = new byte[_dimensions * sizeof(float)];
            var vector = new float[_dimensions];
            while (reader.Read())
            {
                var length = reader.GetBytes(2, 0, buffer, 0, buffer.Length);
                if (length != buffer.Length)
                {
                    throw new InvalidDataException(
                        $"Stored vector for '{reader.GetString(0)}#{reader.GetInt32(1)}' has {length} bytes; expected {buffer.Length}.");
                }

                buffer.AsSpan().CopyTo(MemoryMarshal.AsBytes(vector.AsSpan()));
                _matrix.Upsert(new ChunkKey(reader.GetString(0), reader.GetInt32(1)), vector);
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    private int[] FilteredRows(SqliteConnection connection, IDictionary<string, MetadataValue> filter)
    {
        var sql = new StringBuilder("SELECT document_id, chunk_index FROM vec_chunks WHERE model_id = $model_id");
        using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$model_id", _modelId);

        var i = 0;
        foreach (var (key, value) in filter)
        {
            var (kindPath, valuePath) = MetadataCodec.Paths(key);
            var (kind, filterValue) = MetadataCodec.FilterParameter(value);
            sql.Append($" AND json_extract(metadata, $kp{i}) = $kk{i} AND json_extract(metadata, $vp{i}) = $vv{i}");
            command.Parameters.AddWithValue($"$kp{i}", kindPath);
            command.Parameters.AddWithValue($"$kk{i}", kind);
            command.Parameters.AddWithValue($"$vp{i}", valuePath);
            command.Parameters.AddWithValue($"$vv{i}", filterValue);
            i++;
        }

        command.CommandText = sql.ToString();
        var rows = new List<int>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (_matrix.TryGetRow(new ChunkKey(reader.GetString(0), reader.GetInt32(1)), out var row))
            {
                rows.Add(row);
            }
        }

        return rows.ToArray();
    }

    private TextChunk? ReadChunk(SqliteConnection connection, ChunkKey key, bool includeEmbedding)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT text, metadata FROM vec_chunks
            WHERE document_id = $document_id AND chunk_index = $chunk_index AND model_id = $model_id
            """;
        command.Parameters.AddWithValue("$document_id", key.DocumentId);
        command.Parameters.AddWithValue("$chunk_index", key.ChunkIndex);
        command.Parameters.AddWithValue("$model_id", _modelId);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        ReadOnlyMemory<float>? embedding = null;
        if (includeEmbedding && _matrix.TryGetRow(key, out var row))
        {
            embedding = _matrix.Vector(row).ToArray();
        }

        return new TextChunk
        {
            Text = reader.GetString(0),
            DocumentId = new DocumentId(key.DocumentId),
            ChunkIndex = key.ChunkIndex,
            Embedding = embedding,
            Metadata = MetadataCodec.Decode(reader.GetString(1)),
        };
    }

    private float[] Normalize(ReadOnlySpan<float> vector)
    {
        var result = vector.ToArray();
        var norm = TensorPrimitives.Norm(result);
        if (norm > 0)
        {
            TensorPrimitives.Divide(result, norm, result);
        }

        return result;
    }

    private static void EnsureCollection(SqliteConnection connection, SqliteTransaction transaction, string name, int dimensions)
    {
        using var existing = connection.CreateCommand();
        existing.Transaction = transaction;
        existing.CommandText = "SELECT dims FROM vec_collections WHERE model_id = $model_id";
        existing.Parameters.AddWithValue("$model_id", name);
        if (existing.ExecuteScalar() is long storedDims && storedDims != dimensions)
        {
            throw new InvalidOperationException(
                $"Collection '{name}' holds {storedDims}-dimensional vectors; cannot write {dimensions}-dimensional ones.");
        }

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT OR IGNORE INTO vec_collections (model_id, dims) VALUES ($model_id, $dims)";
        insert.Parameters.AddWithValue("$model_id", name);
        insert.Parameters.AddWithValue("$dims", dimensions);
        insert.ExecuteNonQuery();
    }
}
