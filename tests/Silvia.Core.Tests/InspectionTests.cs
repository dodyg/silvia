using Silvia.Core.Runtime;
using Silvia.Core.Tests.Harness;
using Xunit;

namespace Silvia.Core.Tests;

/// <summary>
/// The read-only views of what a project knows: a document's passages, a traced search, the embedding map
/// and coverage. Each works with and without the local embedder, and none reaches another project.
/// </summary>
public sealed class InspectionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Document_Shows_Every_Passage_In_Order_And_Unknown_Documents_Show_None(bool localModels)
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = new CoreHarness(localModels: localModels);
        var project = harness.Manager.Create("Passages", settings: CoreHarness.FakeSettings() with { ChunkTokens = 20, ChunkOverlap = 0 });
        var folder = harness.NewFolder("passages");
        var text = string.Join("\n\n", Enumerable.Range(1, 12).Select(i => $"Paragraph {i} describes the Lumen kit battery number {i} in some detail."));
        var file = CoreHarness.WriteFile(folder, "kit.md", text);
        var runtime = harness.Manager.Open(project.Id);
        runtime.AddSource(folder);
        await runtime.WaitForIdleAsync(ct);

        var document = runtime.Document(file);

        Assert.NotNull(document);
        Assert.True(document.Passages.Count > 1);
        Assert.Equal(document.Passages.Select(p => p.ChunkIndex).Order(), document.Passages.Select(p => p.ChunkIndex));
        Assert.Contains(document.Passages, p => p.Text.Contains("Paragraph 12", StringComparison.Ordinal));
        Assert.Equal("kit.md", document.Passages[0].Metadata["file_name"]);
        Assert.Equal(runtime.Documents().Single().Chunks, document.Passages.Count);
        Assert.Null(runtime.Document(Path.Combine(folder, "missing.md")));
    }

    [Theory]
    [InlineData(true, RetrievalModes.Vector)]
    [InlineData(false, RetrievalModes.Keyword)]
    public async Task Trace_Returns_The_Same_Hits_As_Search_And_Explains_Them(bool localModels, string mode)
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = new CoreHarness(localModels: localModels);
        var project = harness.Manager.Create("Trace", settings: CoreHarness.FakeSettings());
        var folder = harness.NewFolder("trace");
        CoreHarness.WriteFile(folder, "solar.md", "The Lumen kit carries a 40 watt solar charger.");
        CoreHarness.WriteFile(folder, "travel.md", "Travel to Nairobi is booked through the office in Lisbon.");
        var runtime = harness.Manager.Open(project.Id);
        runtime.AddSource(folder);
        await runtime.WaitForIdleAsync(ct);

        var search = await runtime.SearchAsync("solar charger", 5, ct);
        var trace = await runtime.TraceAsync("solar charger", 5, ct);

        Assert.Equal(mode, trace.Mode);
        Assert.Equal(mode, runtime.RetrievalMode);
        Assert.False(trace.Reranked);
        Assert.Equal(
            search.Select(s => (s.Chunk.DocumentId.Value, s.Chunk.ChunkIndex)),
            trace.Hits.Select(h => (h.DocumentId, h.ChunkIndex)));
        Assert.Equal(Enumerable.Range(1, trace.Hits.Count), trace.Hits.Select(h => h.Position));
        Assert.Equal("solar.md", trace.Hits[0].FileName);
        if (localModels)
        {
            Assert.All(trace.Hits, h => Assert.InRange(h.Similarity!.Value, -1.0001, 1.0001));
            Assert.All(trace.CloseCalls, c => Assert.Equal(0, c.Position));
        }
        else
        {
            Assert.All(trace.Hits, h => Assert.Null(h.Similarity));
            Assert.Empty(trace.CloseCalls);
        }
    }

    [Fact]
    public async Task Map_Places_Every_Passage_And_Is_Absent_Without_An_Embedder()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = new CoreHarness();
        var project = harness.Manager.Create("Map", settings: CoreHarness.FakeSettings());
        var folder = harness.NewFolder("map");
        CoreHarness.WriteFile(folder, "a.md", "Solar chargers and battery packs for the field kit.");
        CoreHarness.WriteFile(folder, "b.md", "Flights to Nairobi and hotels in Lisbon for the launch team.");
        CoreHarness.WriteFile(folder, "c.md", "The support desk is open from eight in the morning.");
        var runtime = harness.Manager.Open(project.Id);
        runtime.AddSource(folder);
        await runtime.WaitForIdleAsync(ct);

        var map = runtime.Map();

        Assert.NotNull(map);
        var passages = runtime.Documents().Sum(d => d.Chunks);
        Assert.Equal(passages, map.Total);
        Assert.Equal(passages, map.Points.Count);
        Assert.InRange(map.FirstShare + map.SecondShare, 0.0, 1.0001);
        Assert.True(map.FirstShare >= map.SecondShare);
        Assert.Contains(map.Points, p => p.X != 0 || p.Y != 0);

        // The same index draws the same picture.
        Assert.Equal(map.Points.Select(p => (p.X, p.Y)), runtime.Map()!.Points.Select(p => (p.X, p.Y)));

        // A sample keeps the total but draws fewer points.
        var sampled = runtime.Map(maxPoints: 2)!;
        Assert.Equal(passages, sampled.Total);
        Assert.InRange(sampled.Points.Count, 1, 2);

        using var keyword = new CoreHarness(localModels: false);
        var plain = keyword.Manager.Create("Keyword", settings: CoreHarness.FakeSettings());
        Assert.Null(keyword.Manager.Open(plain.Id).Map());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Coverage_Counts_Each_Documents_Text_And_Stays_In_Its_Project(bool localModels)
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = new CoreHarness(localModels: localModels);
        var alpha = harness.Manager.Create("Alpha", settings: CoreHarness.FakeSettings());
        var beta = harness.Manager.Create("Beta", settings: CoreHarness.FakeSettings());
        var folder = harness.NewFolder("coverage");
        var big = CoreHarness.WriteFile(folder, "big.md", string.Join(" ", Enumerable.Repeat("The kit has a solar charger.", 40)));
        var small = CoreHarness.WriteFile(folder, "small.md", "Short note.");
        var runtime = harness.Manager.Open(alpha.Id);
        runtime.AddSource(folder);
        await runtime.WaitForIdleAsync(ct);

        var coverage = runtime.Coverage();

        Assert.Equal([big, small], coverage.Select(c => c.DocumentId).Order(StringComparer.Ordinal));
        var bigRow = coverage.Single(c => c.DocumentId == big);
        var smallRow = coverage.Single(c => c.DocumentId == small);
        Assert.True(bigRow.Characters > smallRow.Characters);
        Assert.Equal("Short note.".Length, smallRow.Characters);
        Assert.Equal(runtime.Documents().Single(d => d.DocumentId == big).Chunks, bigRow.Passages);
        Assert.False(bigRow.HasOverview);
        Assert.Empty(harness.Manager.Open(beta.Id).Coverage());
    }
}
