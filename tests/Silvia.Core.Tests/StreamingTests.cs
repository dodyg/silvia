using Silvia.Core.Tests.Harness;
using Xunit;

namespace Silvia.Core.Tests;

/// <summary>The streamed ask: text arrives as steps, the citations follow, and the saved answer matches what was shown.</summary>
public sealed class StreamingTests
{
    [Fact]
    public async Task AskStream_Yields_Text_Then_Sources_Then_Done_And_Saves_The_Answer()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = new CoreHarness(withChat: true);
        var project = harness.Manager.Create("Streaming", settings: CoreHarness.FakeSettings());
        var folder = harness.NewFolder("streaming");
        CoreHarness.WriteFile(folder, "kit.md", "The Lumen kit carries a 40 watt solar charger.");
        var runtime = harness.Manager.Open(project.Id);
        runtime.AddSource(folder);
        await runtime.WaitForIdleAsync(ct);

        var steps = new List<string>();
        var text = new System.Text.StringBuilder();
        string? conversationId = null;
        await foreach (var update in runtime.AskStreamAsync(null, "What does the kit carry?", ct))
        {
            steps.Add(update.Kind);
            if (update.Kind == "conversation")
            {
                conversationId = update.ConversationId;
            }

            if (update.Kind == "delta")
            {
                text.Append(update.Text);
            }
        }

        Assert.Equal("conversation", steps.First());
        Assert.Equal("done", steps.Last());
        Assert.Contains("delta", steps);
        Assert.Contains("sources", steps);
        Assert.NotNull(conversationId);

        var saved = runtime.Messages(conversationId!);
        Assert.Equal(["user", "assistant"], saved.Select(m => m.Role));
        Assert.Equal(text.ToString(), saved[1].Content);
        Assert.NotEmpty(saved[1].Sources);
    }
}
