using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.Pipeline;
using Rag.NET.DataProviders;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Storage;
using Silvia.Storage.Sqlite;

namespace Silvia.Core.Ingestion;

/// <summary>
/// The per-project ingest queue. Every job is written to <c>ingest_jobs</c> before it runs, so a
/// crash or restart loses nothing: jobs left <c>running</c> go back to <c>pending</c> on start.
/// One worker per project runs jobs in order, which keeps a single writer per file.
/// </summary>
internal sealed class IngestQueue : IAsyncDisposable
{
    private const int MaxAttempts = 3;

    private readonly string _databasePath;
    private readonly IRagPipeline _pipeline;
    private readonly IServiceProvider _services;
    private readonly IngestThrottle _throttle;
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _stop = new();
    private Task? _worker;
    private volatile Exception? _lastFault;
    private volatile string _step = "idle";
    private readonly KeywordIndex? _keywords;
    private readonly DocumentOverviews? _overviews;
    private readonly DocumentGraph? _graph;

    /// <param name="keywords">
    /// When given, jobs index files for keyword search instead of embedding them: the project has no
    /// local embedder, and searches and answers run on this index.
    /// </param>
    /// <param name="overviews">
    /// When given, every change to the index is followed by one overview job per new or changed document.
    /// </param>
    /// <param name="graph">
    /// When given, every change to the index is followed by one graph job per new or changed document,
    /// and once none is left, by one job that rebuilds the graph's communities.
    /// </param>
    public IngestQueue(
        string databasePath,
        IRagPipeline pipeline,
        IServiceProvider services,
        IngestThrottle throttle,
        KeywordIndex? keywords = null,
        DocumentOverviews? overviews = null,
        DocumentGraph? graph = null)
    {
        _databasePath = databasePath;
        _pipeline = pipeline;
        _services = services;
        _throttle = throttle;
        _keywords = keywords;
        _overviews = overviews;
        _graph = graph;
    }

    /// <summary>Starts the worker, after returning any interrupted jobs to the queue.</summary>
    public void Start()
    {
        using (var connection = SqliteStorage.Open(_databasePath))
        using (var reset = connection.CreateCommand())
        {
            reset.CommandText = "UPDATE ingest_jobs SET status = 'pending' WHERE status = 'running'";
            reset.ExecuteNonQuery();
        }

        _worker = Task.Run(() => RunAsync(_stop.Token));
        _wake.Release();
    }

    /// <summary>Records a job and wakes the worker. The job survives a restart even if it never runs.</summary>
    public void Enqueue(JobKind kind, string path, long? sourceId = null)
    {
        using (var connection = SqliteStorage.Open(_databasePath))
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO ingest_jobs (path, kind, source_id, status, attempts, enqueued_utc)
                VALUES ($path, $kind, $source, 'pending', 0, $now)
                """;
            insert.Parameters.AddWithValue("$path", path);
            insert.Parameters.AddWithValue("$kind", kind.ToString().ToLowerInvariant());
            insert.Parameters.AddWithValue("$source", (object?)sourceId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            insert.ExecuteNonQuery();
        }

        _wake.Release();
    }

    /// <summary>Jobs not yet finished, by status.</summary>
    public IReadOnlyDictionary<string, int> CountByStatus()
    {
        using var connection = SqliteStorage.Open(_databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT status, COUNT(*) FROM ingest_jobs GROUP BY status";
        using var reader = command.ExecuteReader();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        while (reader.Read())
        {
            counts[reader.GetString(0)] = (int)reader.GetInt64(1);
        }

        return counts;
    }

    /// <summary>True while the worker is running a job. An idle runtime is safe to close.</summary>
    public volatile bool IsBusy;

    /// <summary>Resolves once no job is pending or running.</summary>
    public async Task WaitForIdleAsync(CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromMinutes(2));
        while (!IsIdle())
        {
            if (DateTime.UtcNow > deadline)
            {
                var counts = string.Join(", ", CountByStatus().Select(p => $"{p.Key}={p.Value}"));
                throw new TimeoutException($"Ingest queue did not drain in time: {counts}. Last step: {_step}. Last fault: {_lastFault?.ToString() ?? "none"}");
            }

            await Task.Delay(25, cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_worker is not null)
        {
            try
            {
                await _worker;
            }
            catch (OperationCanceledException)
            {
                // Expected: the worker stops when cancelled.
            }
        }

        _stop.Dispose();
        _wake.Dispose();
    }

    private bool IsIdle()
    {
        var counts = CountByStatus();
        return !IsBusy && !counts.ContainsKey("running") && !counts.ContainsKey("pending");
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _wake.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                Job? job;
                try
                {
                    job = TakeNext();
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    // The database was busy or unreadable. Record it and keep the worker alive.
                    _lastFault = ex;
                    await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ContinueWith(_ => { }, TaskScheduler.Default);
                    break;
                }

                if (job is null)
                {
                    break;
                }

                IsBusy = true;
                try
                {
                    await RunJobAsync(job, cancellationToken);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    // Bookkeeping itself failed. Record it and requeue the job so it is not stuck as running.
                    _lastFault = ex;
                    TryRequeue(job.Id);
                }
                finally
                {
                    IsBusy = false;
                }
            }
        }
    }

    private async Task RunJobAsync(Job job, CancellationToken cancellationToken)
    {
        try
        {
            await ExecuteAsync(job, cancellationToken);
            Finish(job.Id, "done", null);
            if (job.Kind is not (JobKind.Overview or JobKind.Graph or JobKind.Communities))
            {
                QueueOverviews();
                await QueueGraphAsync(cancellationToken);
            }
            else if (job.Kind == JobKind.Graph)
            {
                QueueCommunities();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down: leave the job pending so the next start runs it.
            Finish(job.Id, "pending", null);
        }
        catch (Exception ex)
        {
            var attempts = job.Attempts + 1;
            if (attempts < MaxAttempts)
            {
                Finish(job.Id, "pending", ex.Message, attempts);
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempts)), cancellationToken).ContinueWith(_ => { }, TaskScheduler.Default);
                _wake.Release();
            }
            else
            {
                Finish(job.Id, "failed", ex.Message, attempts);
            }
        }
    }

    private async Task ExecuteAsync(Job job, CancellationToken cancellationToken)
    {
        _step = $"{job.Kind} {job.Id} started";
        // A job queued before its source was removed must not bring the removed documents back.
        // Deletes still run, so the source's documents are cleared either way.
        if (job.Kind != JobKind.Delete && job.SourceId is { } sourceId && !IsSourceEnabled(sourceId))
        {
            return;
        }

        if (job.Kind == JobKind.Overview)
        {
            if (_overviews is not null)
            {
                await _overviews.BuildAsync(job.Path, cancellationToken);
            }

            return;
        }

        if (job.Kind == JobKind.Graph)
        {
            if (_graph is not null)
            {
                await _graph.BuildAsync(job.Path, cancellationToken);
            }

            return;
        }

        if (job.Kind == JobKind.Communities)
        {
            if (_graph is not null)
            {
                await _graph.RebuildCommunitiesAsync(cancellationToken);
            }

            return;
        }

        if (_keywords is not null)
        {
            await ExecuteKeywordAsync(_keywords, job, cancellationToken);
            return;
        }

        switch (job.Kind)
        {
            case JobKind.Upsert when File.Exists(job.Path):
                await IngestFileAsync(job.Path, cancellationToken);
                break;

            case JobKind.Upsert:
            case JobKind.Delete:
                await DeleteFileAsync(job.Path, job.SourceId, cancellationToken);
                break;

            case JobKind.Reindex:
                await _pipeline.ReindexStaleAsync(_services, cancellationToken: cancellationToken);
                break;

            case JobKind.Scan:
                await ScanSourceAsync(job.SourceId!.Value, cancellationToken);
                break;
        }
    }

    private async Task IngestFileAsync(string path, CancellationToken cancellationToken)
    {
        using var slot = await _throttle.AcquireAsync(cancellationToken);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.Asynchronous);
        var metadata = new DocumentMetadata
        {
            DocumentId = new DocumentId(DocumentIds.For(path)),
            FileName = Path.GetFileName(path),
            ContentType = ContentTypes.For(path),
        };

        var result = await _pipeline.IngestAsync(
            document: stream,
            metadata: metadata,
            options: new IngestionOptions { Overwrite = true },
            cancellationToken: cancellationToken);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException($"Ingest of '{path}' failed: {result.Error}");
        }
    }

    /// <summary>The same jobs, run against the keyword index. Re-embedding has nothing to do here.</summary>
    private async Task ExecuteKeywordAsync(KeywordIndex keywords, Job job, CancellationToken cancellationToken)
    {
        switch (job.Kind)
        {
            case JobKind.Upsert:
                using (await _throttle.AcquireAsync(cancellationToken))
                {
                    await keywords.IndexFileAsync(job.Path, cancellationToken);
                }

                break;

            case JobKind.Delete:
                await keywords.RemoveFileAsync(job.Path, cancellationToken);
                break;

            case JobKind.Scan:
                var source = ReadSource(job.SourceId!.Value);
                if (source is { Enabled: true })
                {
                    using (await _throttle.AcquireAsync(cancellationToken))
                    {
                        await keywords.ScanAsync(new FolderFileProvider(source.Path, source.IncludeGlobs, source.ExcludeGlobs), source.Path, cancellationToken);
                    }

                    StampScan(source.Id);
                }

                break;
        }
    }

    /// <summary>
    /// Removes a file's chunks, and forgets that its source scanned it. Without the second step a scan
    /// of the same unchanged file (after a folder is removed and added back, for example) would match the
    /// stored ETag, skip the file, and leave it out of the index.
    /// </summary>
    private async Task DeleteFileAsync(string path, long? sourceId, CancellationToken cancellationToken)
    {
        var documentId = DocumentIds.For(path);
        await _pipeline.DeleteAsync(documentId, cancellationToken);
        if (sourceId is { } id)
        {
            var hashStore = _services.GetRequiredService<IContentHashStore>();
            await hashStore.RemoveAsync(SourceProvider(id), new EntryId(documentId), cancellationToken);
        }
    }

    /// <summary>
    /// Queues an overview job for each document whose overview is missing or out of date, unless one is
    /// already waiting. A failed overview leaves its own failed job; the next change or scan tries again.
    /// </summary>
    private void QueueOverviews()
    {
        if (_overviews is null)
        {
            return;
        }

        var waiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var connection = SqliteStorage.Open(_databasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT path FROM ingest_jobs WHERE kind = 'overview' AND status IN ('pending', 'running')";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                waiting.Add(reader.GetString(0));
            }
        }

        foreach (var documentId in _overviews.Stale().Where(id => !waiting.Contains(id)))
        {
            Enqueue(JobKind.Overview, documentId);
        }
    }

    /// <summary>
    /// Queues a graph job for each document whose part of the knowledge graph is missing or out of date,
    /// unless one is already waiting. A document that left the index leaves the graph here, and the
    /// communities are then rebuilt even when nothing else is queued.
    /// </summary>
    private async Task QueueGraphAsync(CancellationToken cancellationToken)
    {
        if (_graph is null)
        {
            return;
        }

        var removed = false;
        var stale = await _graph.StaleAsync(dropped => removed = dropped, cancellationToken);
        var waiting = Waiting(JobKind.Graph);
        foreach (var documentId in stale.Where(id => !waiting.Contains(id)))
        {
            Enqueue(JobKind.Graph, documentId);
        }

        if (removed)
        {
            QueueCommunities();
        }
    }

    /// <summary>Queues one communities rebuild once no graph job is left, unless one is already waiting.</summary>
    private void QueueCommunities()
    {
        if (Waiting(JobKind.Graph).Count == 0 && Waiting(JobKind.Communities).Count == 0)
        {
            Enqueue(JobKind.Communities, string.Empty);
        }
    }

    /// <summary>The paths of the jobs of one kind that are pending or running.</summary>
    private HashSet<string> Waiting(JobKind kind)
    {
        var waiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var connection = SqliteStorage.Open(_databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT path FROM ingest_jobs WHERE kind = $kind AND status IN ('pending', 'running')";
        command.Parameters.AddWithValue("$kind", kind.ToString().ToLowerInvariant());
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            waiting.Add(reader.GetString(0));
        }

        return waiting;
    }

    private static ProviderId SourceProvider(long sourceId) => new($"source-{sourceId}");

    private async Task ScanSourceAsync(long sourceId, CancellationToken cancellationToken)
    {
        var source = ReadSource(sourceId);
        if (source is null || !source.Enabled)
        {
            return;
        }

        var provider = new FolderFileProvider(source.Path, source.IncludeGlobs, source.ExcludeGlobs);
        _step = "scan waiting for throttle";
        using var slot = await _throttle.AcquireAsync(cancellationToken);
        _step = "scan ingesting";
        var hashStore = _services.GetRequiredService<IContentHashStore>();
        // Per-file failures are kept by Rag.NET in the run result; they never fail the scan job itself.
        _ = await _pipeline.IngestFromProviderAsync(
            provider,
            SourceProvider(sourceId),
            hashStore: hashStore,
            cleanupMode: CleanupMode.Full,
            cancellationToken: cancellationToken);

        StampScan(sourceId);
    }

    private void StampScan(long sourceId)
    {
        using var connection = SqliteStorage.Open(_databasePath);
        using var stamp = connection.CreateCommand();
        stamp.CommandText = "UPDATE sources SET last_scan_utc = $now WHERE id = $id";
        stamp.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        stamp.Parameters.AddWithValue("$id", sourceId);
        stamp.ExecuteNonQuery();
    }

    private bool IsSourceEnabled(long id) => ReadSource(id) is { Enabled: true };

    private SourceRow? ReadSource(long id)
    {
        using var connection = SqliteStorage.Open(_databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT path, include_globs, exclude_globs, enabled FROM sources WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new SourceRow(id, reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3) == 1);
    }

    /// <summary>
    /// Claims the oldest pending job in one statement, so a job is never left marked running without
    /// the worker having it.
    /// </summary>
    private Job? TakeNext()
    {
        using var connection = SqliteStorage.Open(_databasePath);
        using var claim = connection.CreateCommand();
        claim.CommandText = """
            UPDATE ingest_jobs SET status = 'running'
            WHERE id = (SELECT id FROM ingest_jobs WHERE status = 'pending' ORDER BY id LIMIT 1)
            RETURNING id, path, kind, source_id, attempts
            """;
        using var reader = claim.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new Job(
            reader.GetInt64(0),
            reader.GetString(1),
            Enum.Parse<JobKind>(reader.GetString(2), ignoreCase: true),
            reader.IsDBNull(3) ? null : reader.GetInt64(3),
            reader.GetInt32(4));
    }

    private void TryRequeue(long id)
    {
        try
        {
            Finish(id, "pending", "Worker fault; will retry.");
        }
        catch (Exception ex)
        {
            _lastFault = ex;
        }
    }

    private void Finish(long id, string status, string? error, int? attempts = null)
    {
        using var connection = SqliteStorage.Open(_databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ingest_jobs
            SET status = $status, error = $error, attempts = COALESCE($attempts, attempts),
                finished_utc = CASE WHEN $status IN ('done', 'failed') THEN $now ELSE finished_utc END
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$attempts", (object?)attempts ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    private sealed record Job(long Id, string Path, JobKind Kind, long? SourceId, int Attempts);
}

internal enum JobKind
{
    Upsert,
    Delete,
    Scan,
    Reindex,
    Overview,
    Graph,
    Communities,
}

/// <summary>A watched folder as stored in <c>sources</c>.</summary>
public sealed record SourceRow(long Id, string Path, string IncludeGlobs, string ExcludeGlobs, bool Enabled);

/// <summary>Limits how many projects embed at once, so the CPU stays bounded across the app.</summary>
public sealed class IngestThrottle(int maxConcurrent = 1)
{
    private readonly SemaphoreSlim _slots = new(maxConcurrent, maxConcurrent);

    public async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        await _slots.WaitAsync(cancellationToken);
        return new Slot(_slots);
    }

    private sealed class Slot(SemaphoreSlim slots) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                slots.Release();
            }
        }
    }
}
