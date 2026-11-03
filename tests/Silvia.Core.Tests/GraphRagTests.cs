using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Silvia.Core.Projects;
using Silvia.Core.Tests.Harness;
using Xunit;

namespace Silvia.Core.Tests;

/// <summary>
/// The knowledge graph (GraphRAG): off unless the project turns it on, built from each document after it
/// is indexed, kept in step with the files, and given to answers beside the retrieved passages.
/// </summary>
public sealed partial class GraphRagTests
{
    private static bool IsExtraction(IReadOnlyList<ChatMessage> request) =>
        request.Any(m => m.Text.Contains("You build a knowledge graph", StringComparison.Ordinal));

    private static bool IsReport(IReadOnlyList<ChatMessage> request) =>
        request.Any(m => m.Text.Contains("You summarise one community", StringComparison.Ordinal));

    /// <summary>
    /// A stand-in for the model: every capitalised two-word name in the passage is a person, and every two
    /// people in one passage work together. Reports name their members.
    /// </summary>
    private static string? Respond(IReadOnlyList<ChatMessage> request)
    {
        if (IsExtraction(request))
        {
            var prompt = request[^1].Text;
            var passage = prompt[(prompt.LastIndexOf("Passage:", StringComparison.Ordinal) + "Passage:".Length)..];
            var names = Name().Matches(passage).Select(m => m.Value).Distinct().ToList();
            var entities = names.Select(n => new { name = n, type = "Person", description = $"{n} appears in the passage." });
            var relationships = names.Zip(names.Skip(1), (a, b) => new { source = a, target = b, description = "works with", weight = 1.0 });
            return JsonSerializer.Serialize(new { entities, relationships });
        }

        return IsReport(request) ? "A community of colleagues." : null;
    }

    [GeneratedRegex(@"\b[A-Z][a-z]+ [A-Z][a-z]+\b")]
    private static partial Regex Name();

    private static async Task<(CoreHarness Harness, Runtime.ProjectRuntime Runtime, string Folder, long SourceId)> OpenAsync(
        bool graphRag = true, Func<IReadOnlyList<ChatMessage>, string?>? respond = null)
    {
        var harness = new CoreHarness(withChat: true);
        harness.Chat.Respond = respond ?? Respond;
        var settings = CoreHarness.FakeSettings() with { DocumentOverviews = false, GraphRag = graphRag };
        var project = harness.Manager.Create("Graph", settings: settings);
        var folder = harness.NewFolder("graph");
        CoreHarness.WriteFile(folder, "security.md", "The access reviews are signed off by Tomas Berg and checked by Mira Holm.");
        CoreHarness.WriteFile(folder, "meeting.md", "Tomas Berg will rotate the database keys with Dana Okafor.");
        var runtime = harness.Manager.Open(project.Id);
        var sourceId = runtime.AddSource(folder);
        await runtime.WaitForIdleAsync(TestContext.Current.CancellationToken);
        return (harness, runtime, folder, sourceId);
    }

    [Fact]
    public async Task Graph_Is_Off_By_Default_And_Sends_Nothing()
    {
        Assert.False(new ProjectSettings().GraphRag);
        var (harness, runtime, _, _) = await OpenAsync(graphRag: false);
        using var _ = harness;

        Assert.False(runtime.UsesGraph);
        Assert.DoesNotContain(harness.Chat.Requests, IsExtraction);
        await runtime.AskAsync(null, "What is Tomas Berg responsible for?", TestContext.Current.CancellationToken);
        Assert.DoesNotContain("Knowledge graph", harness.Chat.Requests[^1][^1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Indexed_Documents_Build_A_Graph_That_Joins_Them_In_Answers()
    {
        var ct = TestContext.Current.CancellationToken;
        var (harness, runtime, _, _) = await OpenAsync();
        using var _ = harness;

        Assert.True(runtime.UsesGraph);
        Assert.Equal(2, harness.Chat.Requests.Count(IsExtraction));
        Assert.Contains(harness.Chat.Requests, IsReport);
        var size = await runtime.GraphSizeAsync(ct);
        Assert.Equal(2, size!.Value.Documents);
        Assert.Equal(3, size.Value.Entities);
        Assert.True(size.Value.Communities > 0);

        harness.Chat.Requests.Clear();
        await runtime.AskAsync(null, "What is Tomas Berg responsible for?", ct);
        var prompt = harness.Chat.Requests[^1][^1].Text;
        Assert.Contains("Knowledge graph of the project", prompt, StringComparison.Ordinal);
        Assert.Contains("Dana Okafor", prompt, StringComparison.Ordinal);

        // Search stays passages only: the graph's chunks never reach the document index.
        var hits = await runtime.SearchAsync("Tomas Berg", 10, ct);
        Assert.All(hits, h => Assert.True(h.Chunk.Metadata.ContainsKey("file_name")));
    }

    [Fact]
    public async Task Unchanged_Files_Are_Not_Sent_Again_And_Changed_Ones_Replace_Their_Part()
    {
        var ct = TestContext.Current.CancellationToken;
        var (harness, runtime, folder, sourceId) = await OpenAsync();
        using var _ = harness;

        harness.Chat.Requests.Clear();
        runtime.Rescan(sourceId);
        await runtime.WaitForIdleAsync(ct);
        Assert.DoesNotContain(harness.Chat.Requests, IsExtraction);

        var security = Path.Combine(folder, "security.md");
        File.WriteAllText(security, "The access reviews are signed off by Ines Carvalho alone.");
        File.SetLastWriteTimeUtc(security, DateTime.UtcNow.AddMinutes(1));
        runtime.Rescan(sourceId);
        await runtime.WaitForIdleAsync(ct);

        var extraction = Assert.Single(harness.Chat.Requests, IsExtraction);
        Assert.Contains("Ines Carvalho", extraction[^1].Text, StringComparison.Ordinal);
        Assert.Contains(harness.Chat.Requests, IsReport);

        harness.Chat.Requests.Clear();
        await runtime.AskAsync(null, "Who signs off the access reviews?", ct);
        var prompt = harness.Chat.Requests[^1][^1].Text;
        Assert.DoesNotContain("Mira Holm", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resume_Picks_Up_The_Documents_Whose_Extraction_Failed()
    {
        var ct = TestContext.Current.CancellationToken;
        var (harness, runtime, _, _) = await OpenAsync(respond: request =>
            IsExtraction(request) && request[^1].Text.Contains("Dana Okafor", StringComparison.Ordinal)
                ? throw new HttpRequestException("The model is unavailable.")
                : Respond(request));
        using var _ = harness;

        Assert.Equal(1, (await runtime.GraphSizeAsync(ct))!.Value.Documents);
        Assert.True(runtime.JobCounts().GetValueOrDefault("failed") > 0);

        harness.Chat.Respond = Respond;
        harness.Chat.Requests.Clear();
        runtime.Resume();
        await runtime.WaitForIdleAsync(ct);

        // Only the missing document is read again, and the communities take it in.
        var extraction = Assert.Single(harness.Chat.Requests, IsExtraction);
        Assert.Contains("Dana Okafor", extraction[^1].Text, StringComparison.Ordinal);
        Assert.Contains(harness.Chat.Requests, IsReport);
        Assert.Equal(2, (await runtime.GraphSizeAsync(ct))!.Value.Documents);

        harness.Chat.Requests.Clear();
        runtime.Resume();
        await runtime.WaitForIdleAsync(ct);
        Assert.Empty(harness.Chat.Requests);
    }

    [Fact]
    public async Task The_Graph_View_Draws_Every_Entity_With_Its_Relationships_And_Documents()
    {
        var ct = TestContext.Current.CancellationToken;
        var (harness, runtime, _, _) = await OpenAsync();
        using var _ = harness;

        var graph = await runtime.KnowledgeGraphAsync(cancellationToken: ct);
        Assert.NotNull(graph);
        Assert.Equal(3, graph.TotalEntities);
        Assert.Equal(["Dana Okafor", "Mira Holm", "Tomas Berg"], graph.Nodes.Select(n => n.Name).Order());

        // Tomas Berg is in both documents, so he joins the other two.
        var tomas = graph.Nodes.Single(n => n.Name == "Tomas Berg");
        Assert.Equal(2, tomas.Documents.Count);
        Assert.Equal(2, tomas.Degree);
        Assert.Equal(graph.TotalRelationships, graph.Edges.Count);
        var at = graph.Nodes.ToList().IndexOf(tomas);
        Assert.All(graph.Edges, e => Assert.Contains(at, new[] { e.Source, e.Target }));
        Assert.NotEmpty(graph.Communities);
        Assert.All(graph.Nodes, n => Assert.NotEmpty(n.Communities));

        // Same graph, same drawing; and no two entities on one spot.
        var again = await runtime.KnowledgeGraphAsync(cancellationToken: ct);
        Assert.Equal(graph.Nodes.Select(n => (n.X, n.Y)), again!.Nodes.Select(n => (n.X, n.Y)));
        Assert.Equal(3, graph.Nodes.Select(n => (n.X, n.Y)).Distinct().Count());

        var top = await runtime.KnowledgeGraphAsync(maxEntities: 1, cancellationToken: ct);
        Assert.Single(top!.Nodes);
        Assert.Empty(top.Edges);
        Assert.Equal(3, top.TotalEntities);
    }

    [Fact]
    public async Task The_Graph_View_Is_Empty_When_The_Project_Builds_No_Graph()
    {
        var (harness, runtime, _, _) = await OpenAsync(graphRag: false);
        using var _ = harness;

        Assert.Null(await runtime.KnowledgeGraphAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_Deleted_File_Leaves_The_Graph()
    {
        var ct = TestContext.Current.CancellationToken;
        var (harness, runtime, folder, sourceId) = await OpenAsync();
        using var _ = harness;

        File.Delete(Path.Combine(folder, "meeting.md"));
        runtime.Rescan(sourceId);
        await runtime.WaitForIdleAsync(ct);

        var size = await runtime.GraphSizeAsync(ct);
        Assert.Equal(1, size!.Value.Documents);
        harness.Chat.Requests.Clear();
        await runtime.AskAsync(null, "Who works with Dana Okafor?", ct);
        Assert.DoesNotContain("Dana Okafor appears", harness.Chat.Requests[^1][^1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Turning_The_Graph_Off_Deletes_It_From_The_Project_File()
    {
        var ct = TestContext.Current.CancellationToken;
        var (harness, runtime, _, _) = await OpenAsync();
        using var _ = harness;
        var id = runtime.ProjectId;

        harness.Manager.UpdateSettings(id, runtime.Settings with { GraphRag = false });
        var reopened = harness.Manager.Open(id);
        await reopened.WaitForIdleAsync(ct);

        Assert.False(reopened.UsesGraph);
        harness.Manager.Close(id);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var database = Path.Combine(harness.Root, "projects", id, "project.db");
        var text = string.Concat(new[] { database, database + "-wal" }.Where(File.Exists).Select(p => System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(p))));
        Assert.DoesNotContain("Mira Holm appears", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_Graph_Is_Built_Under_Search_Only()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = new CoreHarness(withChat: true);
        var settings = CoreHarness.FakeSettings(cloudPolicy: CloudPolicies.SearchOnly) with { GraphRag = true };
        var project = harness.Manager.Create("Private", settings: settings);
        var folder = harness.NewFolder("private");
        CoreHarness.WriteFile(folder, "notes.md", "Tomas Berg and Mira Holm meet on Monday.");
        var runtime = harness.Manager.Open(project.Id);
        runtime.AddSource(folder);
        await runtime.WaitForIdleAsync(ct);

        Assert.False(runtime.UsesGraph);
        Assert.Empty(harness.Chat.Requests);
    }
}
