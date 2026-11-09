using Microsoft.Extensions.AI;
using Silvia.Core.Runtime;
using Silvia.Core.Tests.Harness;
using Xunit;

namespace Silvia.Core.Tests;

/// <summary>
/// A follow-up such as "When is she away?" names nothing an index can find, so chat rewrites it to stand
/// on its own before it searches. The answer still sees the question as asked.
/// </summary>
public sealed class FollowUpTests
{
    private static bool IsRewriteRequest(IReadOnlyList<ChatMessage> request) =>
        request.Any(m => m.Role == ChatRole.System && m.Text.Contains("rewrite the latest question", StringComparison.Ordinal));

    private static async Task<ProjectRuntime> OpenAsync(CoreHarness harness, CancellationToken ct)
    {
        var project = harness.Manager.Create("Follow-ups", settings: CoreHarness.FakeSettings() with { TopK = 1 });
        var folder = harness.NewFolder("docs");
        // The question as asked matches this file best; only its rewrite finds leave.md.
        CoreHarness.WriteFile(folder, "vendors.md", "# Vendors\n\nMira Okafor approves new vendors. When she is away, Tom Reyes approves them.");
        CoreHarness.WriteFile(folder, "leave.md", "# Leave\n\nMira Okafor is on leave from 3 to 14 August.");
        var runtime = harness.Manager.Open(project.Id);
        runtime.AddSource(folder);
        await runtime.WaitForIdleAsync(ct);
        return runtime;
    }

    [Fact]
    public async Task A_Follow_Up_Is_Searched_By_Its_Standalone_Rewrite()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = new CoreHarness(withChat: true);
        var runtime = await OpenAsync(harness, ct);
        harness.Chat.Respond = request => IsRewriteRequest(request) ? "When is Mira Okafor on leave?" : null;
        harness.Chat.Requests.Clear();

        var first = await runtime.AskAsync(null, "Who approves new vendors?", ct);
        Assert.DoesNotContain(harness.Chat.Requests, IsRewriteRequest);

        var answer = await runtime.AskAsync(first.ConversationId, "When is she away?", ct);

        var rewrite = Assert.Single(harness.Chat.Requests, IsRewriteRequest);
        Assert.Contains("User: Who approves new vendors?", rewrite[^1].Text, StringComparison.Ordinal);
        Assert.Contains("Latest question: When is she away?", rewrite[^1].Text, StringComparison.Ordinal);
        Assert.Equal("leave.md", Assert.Single(answer.Sources, s => s.ChunkIndex >= 0).FileName);
        Assert.EndsWith("Question: When is she away?", harness.Chat.Requests[^1][^1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Failed_Rewrite_Searches_The_Question_As_Asked()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = new CoreHarness(withChat: true);
        var runtime = await OpenAsync(harness, ct);
        harness.Chat.Respond = request => IsRewriteRequest(request) ? throw new HttpRequestException("offline") : null;

        var first = await runtime.AskAsync(null, "Who approves new vendors?", ct);
        var answer = await runtime.AskAsync(first.ConversationId, "When is Mira Okafor on leave?", ct);

        Assert.Equal("Answer [1].", answer.Answer);
        Assert.Contains(answer.Sources, s => s.FileName == "leave.md" && s.ChunkIndex >= 0);
    }

    [Theory]
    [InlineData("When is Mira Okafor on leave?", "When is Mira Okafor on leave?")]
    [InlineData("\"When is Mira Okafor on leave?\"\n", "When is Mira Okafor on leave?")]
    [InlineData("Latest question: When is Mira Okafor on leave?", "When is Mira Okafor on leave?")]
    [InlineData("  \n", "When is she away?")]
    public void The_Rewrite_Is_The_First_Line_Of_The_Reply(string reply, string expected) =>
        Assert.Equal(expected, StandaloneQuestion.Parse(reply, "When is she away?"));
}
