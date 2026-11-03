using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Rag.NET.Models;

namespace Silvia.Core.Tests.Harness;

/// <summary>
/// A deterministic embedder: each word hashes to one dimension, and the vector is normalized. Texts
/// that share words score higher, which is enough for ranking tests, and it needs no model file.
/// </summary>
public sealed class HashingEmbedder(int dimensions) : IEmbeddingGenerator<string, Embedding<float>>
{
    public int Dimensions => dimensions;

    /// <summary>Set when the pool disposes this embedder, so a test can see the release.</summary>
    public bool Disposed { get; private set; }

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var results = values.Select(text => new Embedding<float>(Vectorize(text))).ToList();
        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(results));
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(EmbeddingGeneratorMetadata)
            ? new EmbeddingGeneratorMetadata("hashing", null, $"hashing-{dimensions}", dimensions)
            : null;

    public void Dispose() => Disposed = true;

    private float[] Vectorize(string text)
    {
        var vector = new float[dimensions];
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            vector[Fnv1a(word.ToLowerInvariant()) % (uint)dimensions] += 1f;
        }

        var norm = MathF.Sqrt(vector.Sum(v => v * v));
        if (norm > 0)
        {
            for (var i = 0; i < vector.Length; i++)
            {
                vector[i] /= norm;
            }
        }

        return vector;
    }

    // A fixed hash: string.GetHashCode is randomized per process, and stored vectors must stay valid across runs.
    private static uint Fnv1a(string word)
    {
        var hash = 2166136261u;
        foreach (var ch in word)
        {
            hash = (hash ^ ch) * 16777619u;
        }

        return hash;
    }
}

/// <summary>
/// A deterministic reranker: a passage's relevance is the share of the query's words (three letters or
/// more) that it contains, from 0 to 1, as a cross-encoder's would run.
/// </summary>
public sealed class WordOverlapReranker : Rag.NET.Abstractions.IReranker
{
    public Task<IReadOnlyList<RerankResult>> RerankAsync(string query, IReadOnlyList<SearchResult> results, CancellationToken cancellationToken)
    {
        var words = Words(query);
        IReadOnlyList<RerankResult> ranked = results
            .Select(r => new RerankResult
            {
                SearchResult = r,
                RelevanceScore = words.Count == 0 ? 0 : words.Count(Words(r.Chunk.Text).Contains) / (double)words.Count,
            })
            .OrderByDescending(r => r.RelevanceScore)
            .ToList();
        return Task.FromResult(ranked);
    }

    private static HashSet<string> Words(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim('.', ',', '?', '!', ':', ';', '"').ToLowerInvariant())
            .Where(w => w.Length >= 3)
            .ToHashSet();
}

/// <summary>
/// A chat model that records every request and answers with a fixed reply, or with what
/// <see cref="Respond"/> returns for a request when that is set and gives a reply.
/// </summary>
public sealed class RecordingChatClient(string reply = "Answer [1].") : IChatClient
{
    public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

    public Func<IReadOnlyList<ChatMessage>, string?>? Respond { get; set; }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var request = messages.ToList();
        Requests.Add(request);
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Respond?.Invoke(request) ?? reply)));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var request = messages.ToList();
        Requests.Add(request);
        yield return new ChatResponseUpdate(ChatRole.Assistant, Respond?.Invoke(request) ?? reply);
        await Task.CompletedTask;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
