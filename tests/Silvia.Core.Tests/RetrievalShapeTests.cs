using Microsoft.Extensions.AI;
using Silvia.Core.Ingestion;
using Silvia.Core.Runtime;
using Silvia.Core.Tests.Harness;
using Xunit;

namespace Silvia.Core.Tests;

/// <summary>
/// What the passages of an answer look like: no document taking every place, the reranker's relevance as the
/// score, and a context that names each passage's section, dates it, and admits a weak match.
/// </summary>
public sealed class RetrievalShapeTests
{
    private static async Task<ProjectRuntime> OpenAsync(CoreHarness harness, Projects.ProjectSettings settings, params (string Name, string Text)[] files)
    {
        var project = harness.Manager.Create("Shape", settings: settings);
        var folder = harness.NewFolder("docs");
        foreach (var (name, text) in files)
        {
            CoreHarness.WriteFile(folder, name, text);
        }

        var runtime = harness.Manager.Open(project.Id);
        runtime.AddSource(folder);
        await runtime.WaitForIdleAsync(TestContext.Current.CancellationToken);
        return runtime;
    }

    [Fact]
    public async Task The_Context_Names_Each_Passages_Title_And_Section_Without_Changing_Its_Text()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = new CoreHarness(withChat: true);
        var runtime = await OpenAsync(harness, CoreHarness.FakeSettings() with { DocumentOverviews = false },
            ("travel-2025.md", "# Travel policy (2025 edition)\n\nKept for reference.\n\n## Hotels\n\nThe nightly cap is 150 euros."));

        var hits = await runtime.SearchAsync("nightly cap euros", cancellationToken: ct);
        var hotel = Assert.Single(hits, h => h.Chunk.Text.Contains("150 euros", StringComparison.Ordinal));
        Assert.Equal("Travel policy (2025 edition) › Hotels", SectionChunkingStrategy.Of(hotel));
        Assert.Equal("## Hotels\n\nThe nightly cap is 150 euros.", hotel.Chunk.Text.Trim());

        await runtime.AskAsync(null, "What is the nightly cap in euros?", ct);

        Assert.Contains("travel-2025.md › Travel policy (2025 edition) › Hotels (modified", harness.Chat.Requests[^1][^1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_Document_Takes_At_Most_Half_The_Places_While_Others_Match()
    {
        using var harness = new CoreHarness();
        var rates = string.Join("\n\n", Enumerable.Range(1, 6).Select(i => $"## City {i}\n\nThe hotel cap in city {i} is {100 + i} euros."));
        var runtime = await OpenAsync(harness, CoreHarness.FakeSettings() with { TopK = 4 },
            ("rates.md", "# Rates\n\n" + rates),
            ("policy.md", "# Policy\n\nThe standard hotel cap is 180 euros."));

        var hits = await runtime.SearchAsync("What is the hotel cap in city?", cancellationToken: TestContext.Current.CancellationToken);

        // Two from rates.md, policy.md's one, then a third from rates.md for the place nothing else matches.
        Assert.Equal(4, hits.Count);
        Assert.Contains(hits, h => h.Chunk.Text.Contains("180 euros", StringComparison.Ordinal));
        Assert.Equal(3, hits.Count(h => h.Chunk.DocumentId.Value.EndsWith("rates.md", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task With_A_Reranker_The_Score_Is_Its_Relevance()
    {
        using var harness = new CoreHarness();
        var runtime = await OpenAsync(harness, CoreHarness.FakeSettings() with { RerankModel = CoreHarness.RerankerId, TopK = 2 },
            ("leave.md", "# Leave\n\nMira Okafor is on leave from 3 to 14 August."),
            ("vendors.md", "# Vendors\n\nTom Reyes approves new vendors."));

        var hits = await runtime.SearchAsync("When is Mira Okafor on leave?", cancellationToken: TestContext.Current.CancellationToken);

        // Three of the question's four words: "when" is not in the passage.
        Assert.Equal(0.75, hits.Single(h => h.Chunk.Text.Contains("Mira", StringComparison.Ordinal)).Score);
        Assert.Equal(0.0, hits.Single(h => h.Chunk.Text.Contains("Reyes", StringComparison.Ordinal)).Score);
    }

    [Theory]
    [InlineData("When is Mira Okafor on leave?", false)]
    [InlineData("What colour is the lobby carpet?", true)]
    public async Task A_Weak_Best_Match_Tells_The_Answer_To_Say_So(string question, bool weak)
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = new CoreHarness(withChat: true);
        var runtime = await OpenAsync(harness, CoreHarness.FakeSettings() with { RerankModel = CoreHarness.RerankerId, DocumentOverviews = false },
            ("leave.md", "# Leave\n\nMira Okafor is on leave from 3 to 14 August."));

        await runtime.AskAsync(null, question, ct);

        var context = harness.Chat.Requests[^1][^1].Text;
        Assert.Equal(weak, context.Contains("No passage matches the question closely", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("List everything Dana Okafor is responsible for.", true)]
    [InlineData("Which rules set a deadline counted in working days?", true)]
    [InlineData("What are all the deadlines?", true)]
    [InlineData("What is the hotel cap?", false)]
    [InlineData("What does the travel policy say about trains?", false)]
    [InlineData("Which process applies to refunds?", false)]
    public void A_Question_For_Every_Item_Of_A_Kind_Is_Recognised(string question, bool all) =>
        Assert.Equal(all, QuestionShape.AsksForAll(question));

    [Fact]
    public void Salient_Terms_Are_Names_Numbers_Acronyms_And_Quotes()
    {
        Assert.Equal(["Lumen", "Accra"], QuestionShape.SalientTerms("When does the Lumen pilot launch in Accra?"));
        Assert.Equal(["night shift", "Dana", "2027", "VPN"],QuestionShape.SalientTerms("Did Dana's 2027 VPN request cover the \"night shift\"?"));
        Assert.Empty(QuestionShape.SalientTerms("How many days of leave do I get?"));
    }

    [Fact]
    public void Terms_Match_Whole_Words_Ignoring_Case_Hyphens_And_Possessives()
    {
        string[] passages = ["The wifi password at the lisbon office is on the fridge.", "Dana's team owns the vendor list."];

        Assert.Empty(QuestionShape.Unmentioned("What is the Wi-Fi password at the Lisbon office?", passages));
        Assert.Empty(QuestionShape.Unmentioned("What does Dana own?", passages));
        // "Ann" is not mentioned by "annual".
        Assert.Equal(["Ann"], QuestionShape.Unmentioned("Is Ann on annual leave?", ["The annual leave policy."]));
    }

    [Fact]
    public async Task A_Name_No_Passage_Mentions_Tells_The_Answer_To_Say_So()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = new CoreHarness(withChat: true);
        var runtime = await OpenAsync(harness, CoreHarness.FakeSettings() with { DocumentOverviews = false, BridgeSearch = false },
            ("pilot.md", "# Pilot\n\nThe Lumen pilot launches in Lisbon in March."));

        await runtime.AskAsync(null, "When does the Lumen pilot launch in Accra?", ct);
        var context = harness.Chat.Requests[^1][^1].Text;
        Assert.Contains("No passage mentions \"Accra\"", context, StringComparison.Ordinal);

        await runtime.AskAsync(null, "When does the Lumen pilot launch in Lisbon?", ct);
        Assert.DoesNotContain("No passage mentions", harness.Chat.Requests[^1][^1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Question_For_Every_Item_Gets_Twice_The_Passages_When_Searched_As_Chat_Does()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = new CoreHarness();
        var files = Enumerable.Range(1, 6)
            .Select(i => ($"rule-{i}.md", $"# Rule {i}\n\nForm {i} is due within {i + 2} working days."))
            .ToArray();
        var runtime = await OpenAsync(harness, CoreHarness.FakeSettings() with { TopK = 2 }, files);
        Task<IReadOnlyList<string>> NoBridges(IReadOnlyList<Rag.NET.Models.SearchResult> _) => Task.FromResult<IReadOnlyList<string>>([]);

        Assert.Equal(4, (await runtime.SearchAsync("List every form due within working days", null, NoBridges, ct)).Count);
        Assert.Equal(2, (await runtime.SearchAsync("When is form 3 due?", null, NoBridges, ct)).Count);
        // The Search screen asks for an exact number of passages, and gets it.
        Assert.Equal(2, (await runtime.SearchAsync("List every form due within working days", cancellationToken: ct)).Count);
    }

    [Fact]
    public async Task The_Context_Dates_Each_Passage_And_Says_The_Current_Version_Wins()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = new CoreHarness(withChat: true);
        var runtime = await OpenAsync(harness, CoreHarness.FakeSettings() with { DocumentOverviews = false },
            ("leave.md", "# Leave\n\nMira Okafor is on leave from 3 to 14 August."));
        var path = runtime.Documents().Single().DocumentId;
        var modified = File.GetLastWriteTimeUtc(path).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

        await runtime.AskAsync(null, "When is Mira Okafor on leave?", ct);

        var context = harness.Chat.Requests[^1][^1].Text;
        Assert.Contains($"[1] leave.md › Leave (modified {modified})", context, StringComparison.Ordinal);
        Assert.Contains("superseded document gives way to the current one", context, StringComparison.Ordinal);
    }
}
