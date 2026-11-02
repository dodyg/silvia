using System.Collections.Concurrent;
using Microsoft.Extensions.FileSystemGlobbing;

namespace Silvia.Core.Ingestion;

/// <summary>
/// One <see cref="FileSystemWatcher"/> per enabled source. File events are debounced per path, so
/// a burst of saves becomes one job, and the job's kind is decided when it fires: if the file
/// exists it is (re)indexed, otherwise it is removed from the index.
/// </summary>
internal sealed class SourceWatchers(IngestQueue queue, TimeSpan debounce) : IDisposable
{
    private readonly Dictionary<long, FileSystemWatcher> _watchers = [];
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    /// <summary>Makes the set of watchers match the given sources.</summary>
    public void Sync(IEnumerable<SourceRow> sources)
    {
        lock (_gate)
        {
            var wanted = sources.Where(s => s.Enabled && Directory.Exists(s.Path)).ToDictionary(s => s.Id);

            foreach (var id in _watchers.Keys.Where(id => !wanted.ContainsKey(id)).ToList())
            {
                _watchers[id].Dispose();
                _watchers.Remove(id);
            }

            foreach (var source in wanted.Values.Where(s => !_watchers.ContainsKey(s.Id)))
            {
                _watchers[source.Id] = Watch(source);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var watcher in _watchers.Values)
            {
                watcher.Dispose();
            }

            _watchers.Clear();
        }

        foreach (var pending in _pending.Values)
        {
            pending.Cancel();
            pending.Dispose();
        }

        _pending.Clear();
    }

    private FileSystemWatcher Watch(SourceRow source)
    {
        var watcher = new FileSystemWatcher(source.Path)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = 64 * 1024,
        };

        watcher.Created += (_, e) => Schedule(source, e.FullPath);
        watcher.Changed += (_, e) => Schedule(source, e.FullPath);
        watcher.Deleted += (_, e) => Schedule(source, e.FullPath);
        watcher.Renamed += (_, e) =>
        {
            Schedule(source, e.OldFullPath);
            Schedule(source, e.FullPath);
        };
        watcher.Error += (_, _) => queue.Enqueue(JobKind.Scan, source.Path, source.Id);
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    private void Schedule(SourceRow source, string path)
    {
        var cancellation = new CancellationTokenSource();
        var previous = _pending.AddOrUpdate(path, cancellation, (_, old) =>
        {
            old.Cancel();
            return cancellation;
        });
        if (!ReferenceEquals(previous, cancellation))
        {
            previous.Dispose();
        }

        _ = FireAfterDebounceAsync(source, path, cancellation.Token);
    }

    private async Task FireAfterDebounceAsync(SourceRow source, string path, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(debounce, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        _pending.TryRemove(path, out _);

        var kind = Decide(source, path);
        if (kind is not { } decided)
        {
            return;
        }

        lock (_gate)
        {
            // The source may have been removed while this event waited in the debounce window.
            if (_watchers.ContainsKey(source.Id))
            {
                queue.Enqueue(decided, decided == JobKind.Scan ? source.Path : path, source.Id);
            }
        }
    }

    /// <summary>What a settled change to <paramref name="path"/> needs: none, a scan, an upsert, or a delete.</summary>
    private static JobKind? Decide(SourceRow source, string path)
    {
        // A folder changed or went away, possibly taking files with it: a scan reconciles the whole source.
        if (Directory.Exists(path) || (!File.Exists(path) && Path.GetExtension(path).Length == 0))
        {
            return JobKind.Scan;
        }

        if (!ContentTypes.IsSupported(path))
        {
            return null;
        }

        if (!File.Exists(path))
        {
            return JobKind.Delete;
        }

        return FolderFileProvider.Matches(source.Path, path, source.IncludeGlobs, source.ExcludeGlobs)
            ? JobKind.Upsert
            : null;
    }
}
