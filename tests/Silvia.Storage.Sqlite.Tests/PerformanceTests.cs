using System.Diagnostics;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Silvia.Storage.Sqlite.Tests;

/// <summary>
/// M1 exit gate: p95 query latency under 30 ms at 100k chunks × 384 dimensions. Run in Release
/// (<c>dotnet test -c Release</c>); the timing is meaningless with optimizations off.
/// </summary>
public sealed class PerformanceTests
{
    private const int ChunkCount = 100_000;
    private const int Dimensions = 384;
    private const int BatchSize = 2_000;
    private const int WarmUpQueries = 20;
    private const int MeasuredQueries = 200;
    private const double P95BudgetMs = 30;

    [Fact]
    public async Task Query_P95_At100kChunks_IsUnderBudget()
    {
#if DEBUG
        Assert.Skip("The performance gate is measured in Release: dotnet test -c Release.");
#endif
        var ct = TestContext.Current.CancellationToken;
        using var db = new TempDatabase();
        using var store = new SqliteVectorStore(db.Path, Dimensions, "perf-model");

        var random = new Random(42);
        var insert = Stopwatch.StartNew();
        for (var start = 0; start < ChunkCount; start += BatchSize)
        {
            var batch = new List<EmbeddedChunk>(BatchSize);
            for (var i = start; i < start + BatchSize; i++)
            {
                batch.Add(new EmbeddedChunk
                {
                    Chunk = new TextChunk
                    {
                        Text = $"synthetic chunk {i} with some words to make the row realistic",
                        DocumentId = new DocumentId($"doc-{i / 50:D6}"),
                        ChunkIndex = i % 50,
                        Metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
                        {
                            ["file_name"] = $"doc-{i / 50:D6}.md",
                            ["page"] = i % 300,
                        },
                    },
                    Embedding = RandomUnitVector(random),
                });
            }

            await store.StoreAsync(batch, ct);
        }

        insert.Stop();
        TestContext.Current.SendDiagnosticMessage($"inserted {ChunkCount} chunks in {insert.Elapsed.TotalSeconds:F1} s");

        var queries = Enumerable.Range(0, WarmUpQueries + MeasuredQueries)
            .Select(_ => RandomUnitVector(random))
            .ToArray();

        // Warm up: the first query loads the matrix, so time it apart from the measured set.
        var load = Stopwatch.StartNew();
        await store.SearchAsync(queries[0], new SearchOptions { TopK = 6 }, ct);
        load.Stop();
        TestContext.Current.SendDiagnosticMessage($"first query (matrix load) {load.Elapsed.TotalMilliseconds:F0} ms");

        for (var i = 1; i < WarmUpQueries; i++)
        {
            await store.SearchAsync(queries[i], new SearchOptions { TopK = 6 }, ct);
        }

        var timings = new double[MeasuredQueries];
        for (var i = 0; i < MeasuredQueries; i++)
        {
            var clock = Stopwatch.StartNew();
            var results = await store.SearchAsync(queries[WarmUpQueries + i], new SearchOptions { TopK = 6 }, ct);
            clock.Stop();
            timings[i] = clock.Elapsed.TotalMilliseconds;
            Assert.Equal(6, results.Count);
        }

        Array.Sort(timings);
        var p50 = timings[(int)(MeasuredQueries * 0.50)];
        var p95 = timings[(int)Math.Ceiling(MeasuredQueries * 0.95) - 1];
        TestContext.Current.SendDiagnosticMessage($"query p50 {p50:F2} ms, p95 {p95:F2} ms (budget {P95BudgetMs} ms)");

        Assert.True(p95 < P95BudgetMs, $"p95 {p95:F2} ms exceeds the {P95BudgetMs} ms budget.");
    }

    private static ReadOnlyMemory<float> RandomUnitVector(Random random)
    {
        var vector = new float[Dimensions];
        var sumSquares = 0.0;
        for (var d = 0; d < Dimensions; d++)
        {
            var value = (float)(random.NextDouble() * 2 - 1);
            vector[d] = value;
            sumSquares += value * value;
        }

        var scale = (float)(1 / Math.Sqrt(sumSquares));
        for (var d = 0; d < Dimensions; d++)
        {
            vector[d] *= scale;
        }

        return vector;
    }
}
