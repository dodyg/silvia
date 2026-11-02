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
}
