using Microsoft.Extensions.AI;
using Rag.NET.Models;
using Silvia.Core.Conversations;
using Silvia.Core.Runtime;
using Silvia.Core.Tests.Harness;
using Xunit;

namespace Silvia.Core.Tests;

/// <summary>
/// The context budget: over it, an answer loses the recalled exchange, then the graph, then the overviews, then
/// the bridge passages, then the weakest passages, but never the best passage or the facts.
/// </summary>
public sealed class ContextBudgetTests
{
    // 400 characters: 100 tokens, plus 20 for the header of a passage or an overview.
    private static readonly string Block = new('x', 400);

    private static SearchResult Hit(string document, int index) =>
        new() { Chunk = new TextChunk { Text = Block, DocumentId = new DocumentId(document), ChunkIndex = index }, Score = 1 };

    // 3 passages (360), 2 bridge passages (240), 2 overviews (240), a recalled exchange (100), a graph (100): 1,040.
    private static ContextBudget.Material Full() => new(
        [Hit("a", 0), Hit("a", 1), Hit("a", 2)],
        [Hit("b", 0), Hit("b", 1)],
        [new CitedChunk("a", ProjectRuntime.OverviewChunkIndex, "a.md", Block), new CitedChunk("b", ProjectRuntime.OverviewChunkIndex, "b.md", Block)],
        Block,
        Block);

    [Fact]
    public void Material_Within_The_Budget_Or_With_No_Budget_Is_Kept_Whole()
    {
        Assert.Equal(1040, ContextBudget.Size(Full()));
        foreach (var budget in new[] { 0, -1, 1040 })
        {
            var fit = ContextBudget.Fit(Full(), budget);
            Assert.Equal(1040, ContextBudget.Size(fit));
            Assert.NotNull(fit.Recalled);
        }
    }

    [Fact]
    public void Least_Important_Parts_Go_First()
    {
        var noRecall = ContextBudget.Fit(Full(), 1000);
        Assert.Null(noRecall.Recalled);
        Assert.NotEmpty(noRecall.Graph);

        var noGraph = ContextBudget.Fit(Full(), 900);
        Assert.Empty(noGraph.Graph);
        Assert.Equal(2, noGraph.Overviews.Count);

        var oneOverview = ContextBudget.Fit(Full(), 800);
        Assert.Equal("a", Assert.Single(oneOverview.Overviews).DocumentId);
        Assert.Equal(2, oneOverview.Bridged.Count);

        var oneBridge = ContextBudget.Fit(Full(), 500);
        Assert.Empty(oneBridge.Overviews);
        Assert.Equal(0, Assert.Single(oneBridge.Bridged).Chunk.ChunkIndex);
        Assert.Equal(3, oneBridge.Found.Count);

        var twoPassages = ContextBudget.Fit(Full(), 250);
        Assert.Empty(twoPassages.Bridged);
        Assert.Equal([0, 1], twoPassages.Found.Select(h => h.Chunk.ChunkIndex));
    }

    [Fact]
    public void Best_Passage_Stays_And_Facts_Count_Toward_The_Budget()
    {
        var least = ContextBudget.Fit(Full(), 1);
        Assert.Equal(0, Assert.Single(least.Found).Chunk.ChunkIndex);
        Assert.Empty(least.Bridged);
        Assert.Empty(least.Overviews);
        Assert.Null(least.Recalled);
        Assert.Empty(least.Graph);

        // 50 tokens of facts push the material over a budget it would otherwise fit.
        Assert.Null(ContextBudget.Fit(Full(), 1040, fixedTokens: 50).Recalled);
    }

    [Fact]
    public async Task Answer_Over_The_Budget_Cites_Only_What_It_Was_Given()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = new CoreHarness(withChat: true);
        var project = harness.Manager.Create("Budget", settings: CoreHarness.FakeSettings() with { ContextTokens = 1 });
        var folder = harness.NewFolder("budget");
        CoreHarness.WriteFile(folder, "travel.md",
            "# Travel\n\n## Hotels\n\nThe hotel cap is 150 euros.\n\n## Meals\n\nThe meal cap is 40 euros.\n\n## Taxis\n\nThe taxi cap is 30 euros.\n");
        var runtime = harness.Manager.Open(project.Id);
        runtime.AddSource(folder);
        await runtime.WaitForIdleAsync(ct);
        runtime.AddFact("Caps are per day.");

        harness.Chat.Requests.Clear();
        var answer = await runtime.AskAsync(null, "What is the cap for hotels, meals and taxis?", ct);

        var only = Assert.Single(answer.Sources);
        Assert.True(only.ChunkIndex >= 0, "The best passage stays; the overview goes before it.");
        var request = harness.Chat.Requests.Single();
        Assert.Contains(request, m => m.Role == ChatRole.System && m.Text.Contains("Caps are per day.", StringComparison.Ordinal));
        Assert.Contains("[1] travel.md", request[^1].Text, StringComparison.Ordinal);
        Assert.DoesNotContain("[2]", request[^1].Text, StringComparison.Ordinal);
    }
}
