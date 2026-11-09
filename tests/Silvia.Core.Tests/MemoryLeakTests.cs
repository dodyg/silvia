using Silvia.Core.Tests.Harness;
using Xunit;

namespace Silvia.Core.Tests;

/// <summary>
/// Conversation memory lives in its own table, so it must never come back as a document chunk, and it
/// must still recall earlier conversations within the project that stored it.
/// </summary>
public sealed class MemoryLeakTests
{
    [Fact]
    public async Task Memory_Entries_Are_Not_Returned_As_Document_Chunks()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = new CoreHarness(withChat: true);
        var project = harness.Manager.Create("Memory", settings: CoreHarness.FakeSettings());
        var runtime = harness.Manager.Open(project.Id);

        var answer = await runtime.AskAsync(null, "My codename is Heliotrope. Please remember it.", ct);

        var hits = await runtime.SearchAsync("codename Heliotrope", topK: 10, cancellationToken: ct);
        Assert.DoesNotContain(hits, h => h.Chunk.DocumentId.Value == answer.ConversationId);
        Assert.Empty(runtime.Documents());
    }

    [Fact]
    public async Task Earlier_Conversation_Is_Recalled_Within_The_Same_Project()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = new CoreHarness(withChat: true);
        var project = harness.Manager.Create("Recall", settings: CoreHarness.FakeSettings() with { MemoryMinScore = 0.1 });
        var runtime = harness.Manager.Open(project.Id);

        await runtime.AskAsync(null, "My codename is Heliotrope. Please remember it.", ct);
        harness.Chat.Requests.Clear();
        await runtime.AskAsync(null, "What is my codename?", ct);

        var system = harness.Chat.Requests.Single().Where(m => m.Role == Microsoft.Extensions.AI.ChatRole.System).Select(m => m.Text);
        Assert.Contains(system, t => t.Contains("Heliotrope", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Answer_From_A_Document_That_Changed_Or_Went_Is_Not_Recalled(bool delete)
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = new CoreHarness(withChat: true);
        var settings = CoreHarness.FakeSettings() with { MemoryMinScore = 0.1, DocumentOverviews = false };
        var project = harness.Manager.Create("Stale", settings: settings);
        var folder = harness.NewFolder("policy");
        var file = CoreHarness.WriteFile(folder, "travel.md", "The hotel cap is 150 euros a night.");
        var runtime = harness.Manager.Open(project.Id);
        runtime.AddSource(folder);
        await runtime.WaitForIdleAsync(ct);

        var first = await runtime.AskAsync(null, "What is the hotel cap?", ct);
        Assert.Contains(first.Sources, s => s.DocumentId == file);

        // While the document is as it was, the earlier answer is recalled.
        harness.Chat.Requests.Clear();
        await runtime.AskAsync(null, "What is the hotel cap?", ct);
        Assert.True(Recalled(harness.Chat.Requests.Single()));

        if (delete)
        {
            File.Delete(file);
        }
        else
        {
            // Same length: only the write time tells the versions apart.
            File.WriteAllText(file, "The hotel cap is 180 euros a night.");
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(1));
        }

        harness.Chat.Requests.Clear();
        await runtime.AskAsync(null, "What is the hotel cap?", ct);
        Assert.False(Recalled(harness.Chat.Requests.Single()));
    }

    private static bool Recalled(IReadOnlyList<Microsoft.Extensions.AI.ChatMessage> request) =>
        request.Any(m => m.Role == Microsoft.Extensions.AI.ChatRole.System && m.Text.StartsWith("From a previous conversation", StringComparison.Ordinal));
}
