using Silvia.Core.Tests.Harness;
using Xunit;

namespace Silvia.Core.Tests;

public sealed class SmokeTests
{
    [Fact]
    public async Task Indexes_A_Folder_And_Finds_It()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = new CoreHarness();
        var record = harness.Manager.Create("Smoke", settings: CoreHarness.FakeSettings());
        var folder = harness.NewFolder("smoke");
        CoreHarness.WriteFile(folder, "notes.md", "The launch owner is Ines Carvalho in Lisbon.");

        var runtime = harness.Manager.Open(record.Id);
        runtime.AddSource(folder);
        await runtime.WaitForIdleAsync(ct);

        var hits = await runtime.SearchAsync("launch owner Lisbon", topK: 3, cancellationToken: ct);
        Assert.Single(hits);
        Assert.Equal("notes.md", hits[0].Chunk.Metadata["file_name"].StringValue);
    }
}
