using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Rag.NET.Abstractions;
using Rag.NET.Chunking;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Silvia.Core.Ingestion;

/// <summary>
/// Recursive chunking that records where each passage sits: the document's title and the section, as in
/// <c>Travel policy (2025 edition) › Hotels</c>. The answer's context shows it next to the file name, so a
/// passage that says "the cap is 150 euros" also says which edition it is from. It is metadata rather than
/// text: repeated in every passage, the title's words skewed keyword and vector search toward documents
/// whose names match the question.
/// </summary>
public sealed class SectionChunkingStrategy : IChunkingStrategy
{
    /// <summary>The metadata key of a passage's title and section.</summary>
    public const string SectionPathKey = "section_path";

    private const string Separator = " › ";

    private readonly RecursiveChunkingStrategy _inner = new();

    // The title is the first top-level heading; sections of a document arrive in order, so it is seen first.
    private readonly ConcurrentDictionary<string, string> _titles = new(StringComparer.OrdinalIgnoreCase);

    public async IAsyncEnumerable<TextChunk> ChunkAsync(
        DocumentSection section,
        ChunkingOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var path = SectionPath(section);
        await foreach (var chunk in _inner.ChunkAsync(section, options, cancellationToken))
        {
            if (path.Length > 0)
            {
                chunk.Metadata[SectionPathKey] = path;
            }

            yield return chunk;
        }
    }

    /// <summary>The title and section of a passage, or nothing if its parser recorded neither.</summary>
    public static string? Of(SearchResult result) =>
        result.Chunk.Metadata.TryGetValue(SectionPathKey, out var path) ? path.ToString() : null;

    private string SectionPath(DocumentSection section)
    {
        var documentId = section.DocumentId.Value;
        var heading = section.Heading?.Trim();
        if (section.SectionIndex == 0)
        {
            // A file indexed again starts over, so a changed title replaces the old one.
            _titles.TryRemove(documentId, out _);
        }

        if (section.HeadingLevel == 1 && !string.IsNullOrEmpty(heading))
        {
            _titles.TryAdd(documentId, heading);
        }

        string[] parts = [_titles.GetValueOrDefault(documentId) ?? string.Empty, heading ?? string.Empty];
        return string.Join(Separator, parts.Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase));
    }
}
