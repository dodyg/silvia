using NeoAstra.Rpc;
using Silvia.App.Hosting;
using Silvia.Core.Runtime;

namespace Silvia.App.Rpc;

/// <summary>
/// Read-only views of what a project knows: one document's passages, how a search chose its results,
/// a map of the passage embeddings, and how much of each document is indexed. None calls a cloud model.
/// </summary>
[NeoRpcService("inspect", Version = 1)]
public sealed class InspectService(AppHost host)
{
    // Map coordinates go over the wire as integers on a fixed grid, so the page needs no float handling.
    private const int MapGrid = 10_000;

    [NeoRpcMethod("document")]
    public ValueTask<DocumentDetailDto> DocumentAsync(DocumentRequest request, CancellationToken cancellationToken)
    {
        var runtime = Scope.Runtime(host.Manager, request.ProjectId);
        var document = runtime.Document(request.DocumentId ?? string.Empty) ?? throw Scope.NotFound("document");
        return ValueTask.FromResult(new DocumentDetailDto(
            document.DocumentId,
            Path.GetFileName(document.DocumentId),
            Path.GetDirectoryName(document.DocumentId) ?? string.Empty,
            document.Overview,
            document.IndexedUtc,
            document.Passages
                .Select(p => new PassageDto(p.ChunkIndex, p.Text, p.Metadata.Select(m => new MetadataEntryDto(m.Key, m.Value)).ToArray()))
                .ToArray()));
    }

    [NeoRpcMethod("trace")]
    public async ValueTask<TraceDto> TraceAsync(SearchRequest request, CancellationToken cancellationToken)
    {
        var runtime = Scope.Runtime(host.Manager, request.ProjectId);
        if (string.IsNullOrWhiteSpace(request.Query))
        {
            throw Scope.Invalid("Enter a search first.");
        }

        var trace = await runtime.TraceAsync(request.Query, request.TopK is > 0 and <= 50 ? request.TopK : null, cancellationToken);
        return new TraceDto(trace.Mode, trace.Reranked, trace.Hits.Select(ToDto).ToArray(), trace.CloseCalls.Select(ToDto).ToArray());
    }

    [NeoRpcMethod("map", TimeoutMilliseconds = 120_000)]
    public ValueTask<MapDto> MapAsync(ProjectIdScoped request, CancellationToken cancellationToken)
    {
        var runtime = Scope.Runtime(host.Manager, request.ProjectId);
        if (runtime.Map() is not { } map)
        {
            return ValueTask.FromResult(new MapDto(false, 0, 0, 0, []));
        }

        var (minX, spanX) = Range(map.Points.Select(p => p.X));
        var (minY, spanY) = Range(map.Points.Select(p => p.Y));
        var points = map.Points
            .Select(p => new MapPointDto(
                p.DocumentId,
                Path.GetFileName(p.DocumentId),
                p.ChunkIndex,
                (int)Math.Round((p.X - minX) / spanX * MapGrid),
                (int)Math.Round((p.Y - minY) / spanY * MapGrid),
                p.Preview))
            .ToArray();
        return ValueTask.FromResult(new MapDto(true, map.Total, Permille(map.FirstShare), Permille(map.SecondShare), points));
    }

    [NeoRpcMethod("coverage")]
    public ValueTask<CoverageDto> CoverageAsync(ProjectIdScoped request, CancellationToken cancellationToken)
    {
        var runtime = Scope.Runtime(host.Manager, request.ProjectId);
        var rows = runtime.Coverage();
        return ValueTask.FromResult(new CoverageDto(
            runtime.RetrievalMode,
            runtime.CanEmbed ? runtime.Settings.EmbedModel : null,
            runtime.Settings.DocumentOverviews,
            rows.Select(r => new DocumentCoverageDto(
                    r.DocumentId,
                    Path.GetFileName(r.DocumentId),
                    r.Passages,
                    (int)Math.Min(r.Characters, int.MaxValue),
                    r.HasOverview,
                    r.IndexedUtc))
                .ToArray()));
    }

    private static TracedHitDto ToDto(TracedHit hit) => new(
        hit.DocumentId,
        hit.ChunkIndex,
        hit.FileName,
        hit.Text,
        (int)Math.Round(hit.Score * 1000),
        hit.Position,
        hit.Similarity is { } similarity ? (int)Math.Round(similarity * 1000) : null,
        hit.VectorRank);

    private static int Permille(double share) => (int)Math.Round(share * 1000);

    /// <summary>The smallest value and the width of the range, never zero, so a single point lands at the origin.</summary>
    private static (float Min, float Span) Range(IEnumerable<float> values)
    {
        var list = values.ToList();
        if (list.Count == 0)
        {
            return (0, 1);
        }

        var min = list.Min();
        var span = list.Max() - min;
        return (min, span > 0 ? span : 1);
    }
}
