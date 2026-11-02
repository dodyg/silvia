using Microsoft.Extensions.AI;
using Silvia.Core.Projects;
using Silvia.Core.Runtime;
using Silvia.Core.Tests.Harness;
using Xunit;

namespace Silvia.Core.Tests;

/// <summary>
/// Document overviews and confirmed facts: the two ways an answer sees more than the passages retrieval
/// returns. Overviews follow the files; facts stay inside their project.
/// </summary>
public sealed class OverviewAndFactsTests
{
    private static bool IsOverviewRequest(IReadOnlyList<ChatMessage> request) =>
        request.Any(m => m.Role == ChatRole.System && m.Text.Contains("overview of one document", StringComparison.Ordinal));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Indexed_Document_Gets_An_Overview_That_Answers_Citing_It_Include_First(bool localModels)
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = new CoreHarness(withChat: true, localModels: localModels);
        var project = harness.Manager.Create("Overviews", settings: CoreHarness.FakeSettings());
        var folder = harness.NewFolder("profiles");
        var file = CoreHarness.WriteFile(folder, "profiles.md", "Amperon Technologies is in Tunisia. Dameg is in Egypt.");
        var runtime = harness.Manager.Open(project.Id);
        runtime.AddSource(folder);
        await runtime.WaitForIdleAsync(ct);

        var overviewRequest = Assert.Single(harness.Chat.Requests, IsOverviewRequest);
        Assert.Contains(overviewRequest, m => m.Text.Contains("Amperon Technologies", StringComparison.Ordinal));
        Assert.Equal("Answer [1].", runtime.Overview(file));

        harness.Chat.Requests.Clear();
        var answer = await runtime.AskAsync(null, "Where is Dameg?", ct);

        Assert.Equal(ProjectRuntime.OverviewChunkIndex, answer.Sources[0].ChunkIndex);
        Assert.Equal("profiles.md", answer.Sources[0].FileName);
        Assert.Contains(answer.Sources.Skip(1), s => s.ChunkIndex >= 0 && s.DocumentId == file);
        var prompt = harness.Chat.Requests[^1][^1].Text;
        Assert.Contains("[1] profiles.md (overview of the whole document)", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Overview_Is_Rewritten_When_The_File_Changes_And_Dropped_When_It_Goes()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = new CoreHarness(withChat: true);
        var project = harness.Manager.Create("Follow", settings: CoreHarness.FakeSettings());
        var folder = harness.NewFolder("follow");
        var file = CoreHarness.WriteFile(folder, "notes.md", "The first version names Edubolt.");
        var runtime = harness.Manager.Open(project.Id);
        var sourceId = runtime.AddSource(folder);
        await runtime.WaitForIdleAsync(ct);

        // A rescan of an unchanged file sends nothing again.
        runtime.Rescan(sourceId);
        await runtime.WaitForIdleAsync(ct);
        Assert.Single(harness.Chat.Requests, IsOverviewRequest);

        File.WriteAllText(file, "The second version names Edutek instead.");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(1));
        runtime.Rescan(sourceId);
        await runtime.WaitForIdleAsync(ct);
        var second = harness.Chat.Requests.Where(IsOverviewRequest).Last();
        Assert.Contains(second, m => m.Text.Contains("Edutek", StringComparison.Ordinal));

        File.Delete(file);
        runtime.Rescan(sourceId);
        await runtime.WaitForIdleAsync(ct);
        Assert.Null(runtime.Overview(file));
    }

    [Theory]
    [InlineData(CloudPolicies.SearchOnly, true)]
    [InlineData(CloudPolicies.DeepSeek, false)]
    public async Task No_Overview_Is_Written_Under_Search_Only_Or_When_Turned_Off(string policy, bool overviewsOn)
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = new CoreHarness(withChat: true);
        var settings = CoreHarness.FakeSettings(cloudPolicy: policy) with { DocumentOverviews = overviewsOn };
        var project = harness.Manager.Create("Private", settings: settings);
        var folder = harness.NewFolder("private");
        var file = CoreHarness.WriteFile(folder, "private.md", "Nothing here leaves the machine at indexing time.");
        var runtime = harness.Manager.Open(project.Id);
        runtime.AddSource(folder);
        await runtime.WaitForIdleAsync(ct);

        Assert.Empty(harness.Chat.Requests);
        Assert.Null(runtime.Overview(file));
    }

    [Fact]
    public async Task Facts_Reach_Every_Answer_In_Their_Project_Only()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = new CoreHarness(withChat: true);
        var a = harness.Manager.Create("A", settings: CoreHarness.FakeSettings());
        var b = harness.Manager.Create("B", settings: CoreHarness.FakeSettings());
        var runtimeA = harness.Manager.Open(a.Id);
        var runtimeB = harness.Manager.Open(b.Id);

        var fact = runtimeA.AddFact("  The ten companies in the startup profiles are approved by SILA.  ");
        Assert.Equal("The ten companies in the startup profiles are approved by SILA.", fact.Text);

        await runtimeA.AskAsync(null, "Which companies are approved by SILA?", ct);
        var system = harness.Chat.Requests[^1].Where(m => m.Role == ChatRole.System).Select(m => m.Text);
        Assert.Contains(system, t => t.Contains("startup profiles", StringComparison.Ordinal));

        harness.Chat.Requests.Clear();
        await runtimeB.AskAsync(null, "Which companies are approved by SILA?", ct);
        Assert.DoesNotContain(harness.Chat.Requests.SelectMany(r => r), m => m.Text.Contains("startup profiles", StringComparison.Ordinal));
        Assert.Empty(runtimeB.Facts());
        Assert.False(runtimeB.DeleteFact(fact.Id));
    }

    [Fact]
    public void Facts_Can_Be_Edited_And_Removed_And_Survive_A_Restart()
    {
        using var harness = new CoreHarness();
        var project = harness.Manager.Create("Facts", settings: CoreHarness.FakeSettings());
        var runtime = harness.Manager.Open(project.Id);

        var first = runtime.AddFact("SILA runs from November 2026 to May 2027.");
        var second = runtime.AddFact("Expertise France leads the programme.");
        Assert.True(runtime.UpdateFact(first.Id, "SILA runs for seven months, November 2026 to May 2027."));
        Assert.True(runtime.DeleteFact(second.Id));
        Assert.False(runtime.UpdateFact(999, "No such fact."));
        Assert.Throws<ArgumentException>(() => runtime.AddFact("   "));
        Assert.Throws<ArgumentException>(() => runtime.AddFact(new string('x', FactStore.MaxLength + 1)));

        harness.Restart();
        var facts = harness.Manager.Open(project.Id).Facts();
        var only = Assert.Single(facts);
        Assert.Equal("SILA runs for seven months, November 2026 to May 2027.", only.Text);
    }
}
