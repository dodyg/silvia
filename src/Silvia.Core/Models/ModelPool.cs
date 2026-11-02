using Microsoft.Extensions.AI;
using Rag.NET.Abstractions;

namespace Silvia.Core.Models;

/// <summary>
/// Shares the expensive, stateless model objects between projects. Each model id loads once. A
/// lease counts the projects using it, and the model is disposed when the last lease is returned.
/// </summary>
public sealed class ModelPool(
    Func<string, IEmbeddingGenerator<string, Embedding<float>>> createEmbedder,
    Func<string, IReranker> createReranker,
    Func<Uri, string, string, IChatClient>? createChatClient = null) : IDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry<IEmbeddingGenerator<string, Embedding<float>>>> _embedders = [];
    private readonly Dictionary<string, Entry<IReranker>> _rerankers = [];
    private readonly Dictionary<(Uri, string, string), IChatClient> _chatClients = [];

    /// <summary>Number of embedders currently loaded.</summary>
    public int LoadedEmbedderCount
    {
        get { lock (_gate) return _embedders.Count; }
    }

    /// <summary>Number of rerankers currently loaded.</summary>
    public int LoadedRerankerCount
    {
        get { lock (_gate) return _rerankers.Count; }
    }

    /// <summary>Leases the embedder for <paramref name="modelId"/>, loading it on first use.</summary>
    public ModelLease<IEmbeddingGenerator<string, Embedding<float>>> AcquireEmbedder(string modelId) =>
        Acquire(_embedders, modelId, createEmbedder);

    /// <summary>Leases the reranker for <paramref name="modelId"/>, loading it on first use.</summary>
    public ModelLease<IReranker> AcquireReranker(string modelId) =>
        Acquire(_rerankers, modelId, createReranker);

    /// <summary>
    /// The chat client for an endpoint and model. Clients are cheap and stateless, so they are
    /// cached for the life of the pool and never disposed early.
    /// </summary>
    public IChatClient GetChatClient(Uri endpoint, string apiKey, string model)
    {
        ArgumentNullException.ThrowIfNull(createChatClient);
        lock (_gate)
        {
            var key = (endpoint, apiKey, model);
            if (!_chatClients.TryGetValue(key, out var client))
            {
                client = createChatClient(endpoint, apiKey, model);
                _chatClients[key] = client;
            }

            return client;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var entry in _embedders.Values) (entry.Value as IDisposable)?.Dispose();
            foreach (var entry in _rerankers.Values) (entry.Value as IDisposable)?.Dispose();
            foreach (var client in _chatClients.Values) (client as IDisposable)?.Dispose();
            _embedders.Clear();
            _rerankers.Clear();
            _chatClients.Clear();
        }
    }

    private ModelLease<T> Acquire<T>(
        Dictionary<string, Entry<T>> entries,
        string modelId,
        Func<string, T> create)
        where T : class
    {
        lock (_gate)
        {
            if (!entries.TryGetValue(modelId, out var entry))
            {
                entry = new Entry<T>(create(modelId));
                entries[modelId] = entry;
            }

            entry.Leases++;
            return new ModelLease<T>(entry.Value, () => Release(entries, modelId));
        }
    }

    private void Release<T>(Dictionary<string, Entry<T>> entries, string modelId)
        where T : class
    {
        lock (_gate)
        {
            if (!entries.TryGetValue(modelId, out var entry))
            {
                return;
            }

            entry.Leases--;
            if (entry.Leases == 0)
            {
                entries.Remove(modelId);
                (entry.Value as IDisposable)?.Dispose();
            }
        }
    }

    private sealed class Entry<T>(T value)
    {
        public T Value { get; } = value;

        public int Leases { get; set; }
    }
}

/// <summary>A reference to a pooled model. Dispose it exactly once to return the reference.</summary>
public sealed class ModelLease<T>(T value, Action release) : IDisposable where T : class
{
    private Action? _release = release;

    public T Value => value;

    public void Dispose()
    {
        var release = Interlocked.Exchange(ref _release, null);
        release?.Invoke();
    }
}
