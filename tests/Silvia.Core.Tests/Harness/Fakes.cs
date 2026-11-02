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

/// <summary>A chat model that records every request and answers with a fixed reply.</summary>
public sealed class RecordingChatClient(string reply = "Answer [1].") : IChatClient
{
    public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Requests.Add(messages.ToList());
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Requests.Add(messages.ToList());
        yield return new ChatResponseUpdate(ChatRole.Assistant, reply);
        await Task.CompletedTask;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
