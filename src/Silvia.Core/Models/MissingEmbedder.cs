using Microsoft.Extensions.AI;

namespace Silvia.Core.Models;

/// <summary>
/// Stands in for an embedder whose files are not installed, so a project still opens without local
/// models. The runtime checks <c>CanEmbed</c> before it embeds; if anything reaches
/// this object anyway, it fails with the same missing-files exception instead of an engine error.
/// </summary>
internal sealed class MissingEmbedder(ModelFilesMissingException missing) : IEmbeddingGenerator<string, Embedding<float>>
{
    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new ModelFilesMissingException(missing.ModelId, missing.ModelsDirectory);

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }
}
