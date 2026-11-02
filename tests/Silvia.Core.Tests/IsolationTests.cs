using System.Text;
using Silvia.Core.Projects;
using Silvia.Core.Tests.Harness;
using Xunit;

namespace Silvia.Core.Tests;

/// <summary>
/// The M2 exit gate: projects A and B must not see each other's documents, vectors, conversations or
/// memory, and every operation must hold up when another project is busy.
/// </summary>
public sealed class IsolationTests
{
    [Fact]
    public async Task Documents_Search_Chat_And_Bytes_Of_A_Never_Reach_B()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = new CoreHarness(withChat: true);
        const string marker = "zebraquartz7731";

        var a = harness.Manager.Create("A", settings: CoreHarness.FakeSettings());
        var b = harness.Manager.Create("B", settings: CoreHarness.FakeSettings());
        var folderA = harness.NewFolder("a");
        CoreHarness.WriteFile(folderA, "secret.md", $"Project A keeps the phrase {marker} for its own notes.");

        var runtimeA = harness.Manager.Open(a.Id);
        runtimeA.AddSource(folderA);
        await runtimeA.WaitForIdleAsync(ct);

        var runtimeB = harness.Manager.Open(b.Id);
        await runtimeB.WaitForIdleAsync(ct);

        Assert.Contains(runtimeA.Documents(), d => d.DocumentId.EndsWith("secret.md", StringComparison.Ordinal));
        Assert.Empty(runtimeB.Documents());
        Assert.Empty(await runtimeB.SearchAsync(marker, topK: 5, cancellationToken: ct));

        var answer = await runtimeB.AskAsync(null, "What does the phrase in the secret note refer to?", ct);
        Assert.Empty(answer.Sources);
        Assert.DoesNotContain(
            harness.Chat.Requests.SelectMany(r => r).SelectMany(m => m.Contents).OfType<Microsoft.Extensions.AI.TextContent>(),
            t => t.Text.Contains(marker, StringComparison.Ordinal));

        harness.Manager.Close(b.Id);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var bytesB = ReadAllDatabaseBytes(Path.Combine(harness.Root, "projects", b.Id, "project.db"));
        Assert.DoesNotContain(marker, Encoding.UTF8.GetString(bytesB), StringComparison.Ordinal);

        var bytesA = ReadAllDatabaseBytes(Path.Combine(harness.Root, "projects", a.Id, "project.db"));
        Assert.Contains(marker, Encoding.UTF8.GetString(bytesA), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Same_Folder_In_Two_Projects_Is_Indexed_Twice_And_Follows_The_Files_In_Both()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = new CoreHarness();
        var a = harness.Manager.Create("A", settings: CoreHarness.FakeSettings());
        var b = harness.Manager.Create("B", settings: CoreHarness.FakeSettings());
        var shared = harness.NewFolder("shared");
        var file = CoreHarness.WriteFile(shared, "policy.md", "Quarterly access reviews are signed by the security lead.");

        var runtimeA = harness.Manager.Open(a.Id);
        var runtimeB = harness.Manager.Open(b.Id);
        var sourceA = runtimeA.AddSource(shared);
        runtimeB.AddSource(shared);
        await runtimeA.WaitForIdleAsync(ct);
        await runtimeB.WaitForIdleAsync(ct);
        Assert.Single(runtimeA.Documents());
        Assert.Single(runtimeB.Documents());

        // Deleting the file is seen by both watchers, so both projects forget it.
        File.Delete(file);
        await Waits.UntilAsync(
            () => runtimeA.Documents().Count == 0 && runtimeB.Documents().Count == 0,
            TimeSpan.FromSeconds(10),
            "both projects to forget the deleted file");

        // Restoring it indexes it in both again.
        CoreHarness.WriteFile(shared, "policy.md", "Quarterly access reviews are signed by the security lead.");
        await Waits.UntilAsync(
            () => runtimeA.Documents().Count == 1 && runtimeB.Documents().Count == 1,
            TimeSpan.FromSeconds(10),
            "both projects to index the restored file");

        // Removing the source from A removes only A's copy.
        runtimeA.RemoveSource(sourceA);
        await runtimeA.WaitForIdleAsync(ct);

        Assert.Empty(runtimeA.Documents());
        Assert.Single(runtimeB.Documents());
        Assert.NotEmpty(await runtimeB.SearchAsync("security lead", topK: 3, cancellationToken: ct));
    }


    [Fact]
    public async Task Deleting_A_While_B_Is_Ingesting_Lets_B_Finish_And_Frees_A_Folder()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = new CoreHarness();
        var a = harness.Manager.Create("A", settings: CoreHarness.FakeSettings());
        var b = harness.Manager.Create("B", settings: CoreHarness.FakeSettings());

        var folderA = harness.NewFolder("a");
        CoreHarness.WriteFile(folderA, "small.md", "A small document in project A.");
        var runtimeA = harness.Manager.Open(a.Id);
        runtimeA.AddSource(folderA);
        await runtimeA.WaitForIdleAsync(ct);

        var folderB = harness.NewFolder("b");
        for (var i = 0; i < 40; i++)
        {
            CoreHarness.WriteFile(folderB, $"doc-{i:D2}.md", $"Document {i} in project B about topic {i % 7}.");
        }

        var runtimeB = harness.Manager.Open(b.Id);
        runtimeB.AddSource(folderB);

        harness.Manager.Delete(a.Id);

        await runtimeB.WaitForIdleAsync(ct);
        Assert.Equal(40, runtimeB.Documents().Count);
        Assert.False(Directory.Exists(Path.Combine(harness.Root, "projects", a.Id)));
        Assert.Null(harness.Manager.List().SingleOrDefault(p => p.Id == a.Id));
    }

    [Fact]
    public async Task Two_Embedders_Load_Side_By_Side_And_Pool_Holds_Two_Not_Three()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = new CoreHarness();
        var small = harness.Manager.Create("Small", settings: CoreHarness.FakeSettings());
        var wide = harness.Manager.Create("Wide", settings: CoreHarness.FakeSettings("hashing-768", 768));
        var another = harness.Manager.Create("Another", settings: CoreHarness.FakeSettings());

        var folders = new Dictionary<string, string>();
        foreach (var (record, marker) in new[] { (small, "alpha"), (wide, "bravo"), (another, "charlie") })
        {
            var folder = harness.NewFolder(record.Name);
            CoreHarness.WriteFile(folder, "doc.md", $"The codeword in {record.Name} is {marker}.");
            folders[record.Id] = folder;
        }

        foreach (var record in new[] { small, wide, another })
        {
            var runtime = harness.Manager.Open(record.Id);
            runtime.AddSource(folders[record.Id]);
            await runtime.WaitForIdleAsync(ct);
        }

        Assert.Equal(2, harness.Pool.LoadedEmbedderCount);

        foreach (var (record, marker) in new[] { (small, "alpha"), (wide, "bravo"), (another, "charlie") })
        {
            var hits = await harness.Manager.Open(record.Id).SearchAsync($"codeword {marker}", topK: 1, cancellationToken: ct);
            Assert.Equal($"The codeword in {record.Name} is {marker}.", hits[0].Chunk.Text.TrimEnd('\n'));
        }
    }

    [Fact]
    public async Task Projects_On_One_Embedder_Share_It_And_Release_It_With_The_Last_Close()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = new CoreHarness();
        var a = harness.Manager.Create("A", settings: CoreHarness.FakeSettings());
        var b = harness.Manager.Create("B", settings: CoreHarness.FakeSettings());
        var runtimeA = harness.Manager.Open(a.Id);
        var runtimeB = harness.Manager.Open(b.Id);
        await runtimeA.WaitForIdleAsync(ct);
        await runtimeB.WaitForIdleAsync(ct);

        Assert.Equal(1, harness.Pool.LoadedEmbedderCount);
        var embedder = Assert.Single(harness.Embedders);

        harness.Manager.Close(a.Id);
        Assert.False(embedder.Disposed);
        Assert.Equal(1, harness.Pool.LoadedEmbedderCount);

        var hits = await runtimeB.SearchAsync("anything", topK: 1, cancellationToken: ct);
        Assert.Empty(hits);

        harness.Manager.Close(b.Id);
        Assert.True(embedder.Disposed);
        Assert.Equal(0, harness.Pool.LoadedEmbedderCount);
    }

    [Fact]
    public async Task Conversation_Memory_From_A_Is_Never_Recalled_In_B()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = new CoreHarness(withChat: true);
        var a = harness.Manager.Create("A", settings: CoreHarness.FakeSettings());
        var b = harness.Manager.Create("B", settings: CoreHarness.FakeSettings());
        var runtimeA = harness.Manager.Open(a.Id);
        var runtimeB = harness.Manager.Open(b.Id);

        var first = await runtimeA.AskAsync(null, "My codename is Heliotrope. Please remember it.", ct);
        await runtimeA.AskAsync(first.ConversationId, "What did I say my codename was?", ct);

        // The second request in A carries A's own history; B's requests must never carry it.
        var inA = harness.Chat.Requests.Last().Select(m => m.Text).ToList();
        Assert.Contains(inA, t => t.Contains("Heliotrope", StringComparison.Ordinal));

        harness.Chat.Requests.Clear();
        await runtimeB.AskAsync(null, "What is my codename?", ct);
        var inB = harness.Chat.Requests.SelectMany(r => r).Select(m => m.Text).ToList();
        Assert.DoesNotContain(inB, t => t.Contains("Heliotrope", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Exported_Copy_Matches_The_Original_And_Survives_Deleting_It()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = new CoreHarness();
        var original = harness.Manager.Create("Original", settings: CoreHarness.FakeSettings());
        var folder = harness.NewFolder("docs");
        CoreHarness.WriteFile(folder, "field.md", "The Lumen kit carries a 40 watt solar charger.");
        CoreHarness.WriteFile(folder, "other.md", "Expense reports are due on the fifth working day.");

        var runtime = harness.Manager.Open(original.Id);
        runtime.AddSource(folder);
        await runtime.WaitForIdleAsync(ct);
        var before = await runtime.SearchAsync("solar charger watt", topK: 2, cancellationToken: ct);

        var exported = harness.Manager.Export(original.Id);
        var copy = harness.Manager.Import(exported, "Original copy");
        var copyRuntime = harness.Manager.Open(copy.Id);
        var after = await copyRuntime.SearchAsync("solar charger watt", topK: 2, cancellationToken: ct);

        Assert.Equal(before.Select(r => r.Chunk.DocumentId.Value), after.Select(r => r.Chunk.DocumentId.Value));
        Assert.Equal(before.Select(r => r.Score), after.Select(r => r.Score));

        harness.Manager.Delete(original.Id);
        var stillWorking = await copyRuntime.SearchAsync("solar charger watt", topK: 1, cancellationToken: ct);
        Assert.Equal(before[0].Chunk.DocumentId.Value, stillWorking[0].Chunk.DocumentId.Value);
    }

    [Fact]
    public async Task Ids_From_Another_Project_Resolve_To_Nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = new CoreHarness(withChat: true);
        var a = harness.Manager.Create("A", settings: CoreHarness.FakeSettings());
        var b = harness.Manager.Create("B", settings: CoreHarness.FakeSettings());
        var runtimeA = harness.Manager.Open(a.Id);
        var runtimeB = harness.Manager.Open(b.Id);

        var answer = await runtimeA.AskAsync(null, "A private question.", ct);

        Assert.Null(runtimeB.GetConversation(answer.ConversationId));
        Assert.Empty(runtimeB.Messages(answer.ConversationId));
        Assert.False(await runtimeB.DeleteConversationAsync(answer.ConversationId, ct));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => runtimeB.AskAsync(answer.ConversationId, "Follow-up.", ct));
        Assert.Throws<ProjectNotFoundException>(() => harness.Manager.Get("01NOTAPROJECT0000000000000"));
    }

    private static byte[] ReadAllDatabaseBytes(string databasePath)
    {
        var bytes = new List<byte>();
        foreach (var path in new[] { databasePath, databasePath + "-wal" })
        {
            if (File.Exists(path))
            {
                bytes.AddRange(File.ReadAllBytes(path));
            }
        }

        return bytes.ToArray();
    }
}
