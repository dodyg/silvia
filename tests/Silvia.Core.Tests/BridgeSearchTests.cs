using Microsoft.Extensions.AI;
using Silvia.Core.Runtime;
using Silvia.Core.Tests.Harness;
using Xunit;

namespace Silvia.Core.Tests;

/// <summary>
/// "When did the person who signs off access reviews rotate the keys?" first finds who signs them off, but
/// the passage about the keys never mentions access reviews. The chat model reads the first passages and
/// writes a further search that names the person, whose best new passages join the context.
/// </summary>
public sealed class BridgeSearchTests
{
    private static bool IsBridgeRequest(IReadOnlyList<ChatMessage> request) =>
        request.Any(m => m.Role == ChatRole.System && m.Text.Contains("You plan searches", StringComparison.Ordinal));

    private static async Task<ProjectRuntime> OpenAsync(CoreHarness harness, bool bridgeSearch, CancellationToken ct)
    {
        var project = harness.Manager.Create("Bridges", settings: CoreHarness.FakeSettings() with { TopK = 1, DocumentOverviews = false, BridgeSearch = bridgeSearch });
        var folder = harness.NewFolder("docs");
        // The question matches security.md; only a search that names Tomas Berg finds keys.md.
        CoreHarness.WriteFile(folder, "security.md", "# Access reviews\n\nTomas Berg signs off the quarterly access reviews.");
        CoreHarness.WriteFile(folder, "keys.md", "# Credentials\n\nBerg rotated the production credentials on 25 February.");
        var runtime = harness.Manager.Open(project.Id);
        runtime.AddSource(folder);
        await runtime.WaitForIdleAsync(ct);
        return runtime;
    }

    private const string Question = "Who signs off the quarterly access reviews, and when did that person rotate anything?";

    [Fact]
    public async Task A_Further_Search_Adds_The_Passage_The_First_One_Points_To()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = new CoreHarness(withChat: true);
        var runtime = await OpenAsync(harness, bridgeSearch: true, ct);
        harness.Chat.Respond = request => IsBridgeRequest(request) ? "Berg rotated production credentials" : null;
        harness.Chat.Requests.Clear();

        var answer = await runtime.AskAsync(null, Question, ct);

        var bridge = Assert.Single(harness.Chat.Requests, IsBridgeRequest);
        Assert.Contains($"Question: {Question}", bridge[^1].Text, StringComparison.Ordinal);
        Assert.Contains("[1] security.md", bridge[^1].Text, StringComparison.Ordinal);
        Assert.Equal(["keys.md", "security.md"], answer.Sources.Where(s => s.ChunkIndex >= 0).Select(s => s.FileName).Order());
        Assert.Contains("25 February", harness.Chat.Requests[^1][^1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task None_Keeps_The_First_Passages()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = new CoreHarness(withChat: true);
        var runtime = await OpenAsync(harness, bridgeSearch: true, ct);
        harness.Chat.Respond = request => IsBridgeRequest(request) ? "NONE" : null;

        var answer = await runtime.AskAsync(null, Question, ct);

        Assert.Equal("security.md", Assert.Single(answer.Sources, s => s.ChunkIndex >= 0).FileName);
    }

    [Fact]
    public async Task A_Failed_Planning_Call_Keeps_The_First_Passages()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = new CoreHarness(withChat: true);
        var runtime = await OpenAsync(harness, bridgeSearch: true, ct);
        harness.Chat.Respond = request => IsBridgeRequest(request) ? throw new HttpRequestException("offline") : null;

        var answer = await runtime.AskAsync(null, Question, ct);

        Assert.Equal("Answer [1].", answer.Answer);
        Assert.Equal("security.md", Assert.Single(answer.Sources, s => s.ChunkIndex >= 0).FileName);
    }

    [Fact]
    public async Task With_Bridge_Search_Off_No_Planning_Call_Is_Made()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = new CoreHarness(withChat: true);
        var runtime = await OpenAsync(harness, bridgeSearch: false, ct);
        harness.Chat.Requests.Clear();

        await runtime.AskAsync(null, Question, ct);

        Assert.DoesNotContain(harness.Chat.Requests, IsBridgeRequest);
    }

    [Fact]
    public async Task Versions_That_Differ_In_One_Number_Are_Both_Kept_And_Exact_Copies_Once()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = new CoreHarness();
        var project = harness.Manager.Create("Versions", settings: CoreHarness.FakeSettings() with { TopK = 3 });
        var folder = harness.NewFolder("docs");
        CoreHarness.WriteFile(folder, "travel-2025.md", "# Hotels\n\nHotel bookings must stay under the nightly cap of 150 euros.");
        CoreHarness.WriteFile(folder, "travel.md", "# Hotels\n\nHotel bookings must stay under the nightly cap of 180 euros.");
        CoreHarness.WriteFile(folder, "travel-copy.md", "# Hotels\n\nHotel bookings must stay under the nightly cap of 180 euros.");
        var runtime = harness.Manager.Open(project.Id);
        runtime.AddSource(folder);
        await runtime.WaitForIdleAsync(ct);

        var hits = await runtime.SearchAsync("What is the nightly hotel cap?", cancellationToken: ct);

        Assert.Equal(2, hits.Count);
        Assert.Contains(hits, h => h.Chunk.Text.Contains("150 euros", StringComparison.Ordinal));
        Assert.Contains(hits, h => h.Chunk.Text.Contains("180 euros", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("NONE", new string[0])]
    [InlineData("none.", new string[0])]
    [InlineData("  \n", new string[0])]
    [InlineData("Tomas Berg database keys", new[] { "Tomas Berg database keys" })]
    [InlineData("1. Who is Ines Carvalho's manager?\n2) travel exceptions the operations director approves", new[] { "Who is Ines Carvalho's manager?", "travel exceptions the operations director approves" })]
    [InlineData("- \"Tomas Berg database keys\"\n- Tomas Berg database keys\n- third\n- fourth", new[] { "Tomas Berg database keys", "third" })]
    [InlineData("Who signs off access reviews?", new string[0])]
    public void The_Searches_Are_The_Lines_Of_The_Reply(string reply, string[] expected) =>
        Assert.Equal(expected, BridgeQuestion.Parse(reply, "Who signs off access reviews?"));
}
