using Silvia.Core.Projects;
using Silvia.Core.Runtime;
using Silvia.Core.Tests.Harness;
using Silvia.Storage.Sqlite;
using Xunit;

namespace Silvia.Core.Tests;

/// <summary>The ingest queue and folder watchers: timing, edits, and recovery after a crash.</summary>
public sealed class IngestTests
{
    [Fact]
    public async Task New_Edited_And_Deleted_Files_Reach_Search_Within_Five_Seconds()
    {
        var ct = TestContext.Current.CancellationToken;

        // Production debounce (2 s), so the five-second budget is measured as the user would see it.
        using var harness = new CoreHarness(runtimeOptions: new ProjectRuntime.RuntimeOptions());
        var project = harness.Manager.Create("Watched", settings: CoreHarness.FakeSettings());
        var folder = harness.NewFolder("watched");
        var runtime = harness.Manager.Open(project.Id);
        runtime.AddSource(folder);
        await runtime.WaitForIdleAsync(ct);

        var created = CoreHarness.WriteFile(folder, "new.md", "The Lumen pilot launches in Lisbon.");
        await WaitForHitAsync(runtime, "Lumen pilot launches Lisbon", "new.md", TimeSpan.FromSeconds(5), ct);

        CoreHarness.WriteFile(folder, "new.md", "The Lumen pilot moved to Nairobi.");
        await WaitForHitAsync(runtime, "Lumen pilot Nairobi", "new.md", TimeSpan.FromSeconds(5), ct);

        File.Delete(created);
        await Waits.UntilAsync(
            () => runtime.Documents().All(d => !d.DocumentId.EndsWith("new.md", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(5),
            "the deleted file to leave the index");
    }

    [Fact]
    public async Task A_Folder_Removed_And_Added_Again_Is_Indexed_Again()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = new CoreHarness();
        var project = harness.Manager.Create("Re-added", settings: CoreHarness.FakeSettings());
        var folder = harness.NewFolder("readded");
        for (var i = 0; i < 3; i++)
        {
            CoreHarness.WriteFile(folder, $"note-{i}.md", $"Note {i} about the Lumen pilot.");
        }

        var runtime = harness.Manager.Open(project.Id);
        var sourceId = runtime.AddSource(folder);
        await runtime.WaitForIdleAsync(ct);
        Assert.Equal(3, runtime.Documents().Count);

        runtime.RemoveSource(sourceId);
        await runtime.WaitForIdleAsync(ct);
        Assert.Empty(runtime.Documents());

        // The files did not change, so only forgetting their ingest records brings them back.
        runtime.AddSource(folder);
        await runtime.WaitForIdleAsync(ct);
        Assert.Equal(3, runtime.Documents().Count);
    }

    [Fact]
    public async Task Jobs_Queued_Before_A_Crash_Run_After_Restart()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = new CoreHarness(runtimeOptions: new ProjectRuntime.RuntimeOptions { StartWorkers = false });
        var project = harness.Manager.Create("Interrupted", settings: CoreHarness.FakeSettings());
        var folder = harness.NewFolder("interrupted");
        for (var i = 0; i < 5; i++)
        {
            CoreHarness.WriteFile(folder, $"part-{i}.md", $"Part {i} of the interrupted project.");
        }

        // The worker never starts, so the scan stays queued: that is the state a crash leaves behind.
        var runtime = harness.Manager.Open(project.Id);
        runtime.AddSource(folder);
        Assert.Equal(1, runtime.JobCounts().GetValueOrDefault("pending"));

        // Simulate the process dying while a job was running: its row says "running".
        MarkJobsRunning(Path.Combine(harness.Root, "projects", project.Id, "project.db"));

        harness.Restart(harness.Options with { RuntimeOptions = new ProjectRuntime.RuntimeOptions() });
        var restarted = harness.Manager.Open(project.Id);
        await restarted.WaitForIdleAsync(ct);

        Assert.Equal(5, restarted.Documents().Count);
        var counts = restarted.JobCounts();
        Assert.False(counts.ContainsKey("pending"));
        Assert.False(counts.ContainsKey("running"));
        Assert.True(counts.GetValueOrDefault("done") >= 1);
    }

    [Fact]
    public async Task Files_Changed_While_A_Project_Was_Closed_Are_Picked_Up_On_Open()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = new CoreHarness();
        var project = harness.Manager.Create("Closed", settings: CoreHarness.FakeSettings());
        var folder = harness.NewFolder("closed");
        var stale = CoreHarness.WriteFile(folder, "stale.md", "Old handbook text about kettles.");
        var runtime = harness.Manager.Open(project.Id);
        runtime.AddSource(folder);
        await runtime.WaitForIdleAsync(ct);
        harness.Manager.Close(project.Id);

        File.Delete(stale);
        CoreHarness.WriteFile(folder, "fresh.md", "New handbook text about pilots.");

        var reopened = harness.Manager.Open(project.Id);
        await reopened.WaitForIdleAsync(ct);

        var documents = reopened.Documents().Select(d => Path.GetFileName(d.DocumentId)).ToList();
        Assert.Equal(["fresh.md"], documents);
    }

    [Fact]
    public async Task Changing_The_Embedder_Re_Embeds_Existing_Documents_On_Next_Open()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = new CoreHarness();
        var project = harness.Manager.Create("Switch", settings: CoreHarness.FakeSettings());
        var folder = harness.NewFolder("switch");
        CoreHarness.WriteFile(folder, "guide.md", "The Lumen kit ships with a solar charger.");
        var runtime = harness.Manager.Open(project.Id);
        runtime.AddSource(folder);
        await runtime.WaitForIdleAsync(ct);
        harness.Manager.Close(project.Id);

        harness.Manager.UpdateSettings(project.Id, CoreHarness.FakeSettings("hashing-768", 768));
        var reopened = harness.Manager.Open(project.Id);
        await reopened.WaitForIdleAsync(ct);

        // The old vectors were stale under the new model; the reopened project re-embedded them.
        var document = Assert.Single(reopened.Documents());
        Assert.Equal("guide.md", Path.GetFileName(document.DocumentId));
        var hits = await reopened.SearchAsync("solar charger", topK: 1, cancellationToken: ct);
        Assert.Equal("guide.md", hits[0].Chunk.Metadata["file_name"].StringValue);
    }

    [Fact]
    public void Pruning_Removes_Old_Done_Jobs_But_Keeps_The_Latest_Upsert_Of_Each_File()
    {
        using var harness = new CoreHarness(runtimeOptions: new ProjectRuntime.RuntimeOptions { StartWorkers = false });
        var project = harness.Manager.Create("Pruned", settings: CoreHarness.FakeSettings());
        var runtime = harness.Manager.Open(project.Id);
        var databasePath = Path.Combine(harness.Root, "projects", project.Id, "project.db");

        var now = DateTimeOffset.UtcNow;
        var old = now - TimeSpan.FromDays(30);
        var recent = now - TimeSpan.FromDays(1);
        using (var connection = SqliteStorage.Open(databasePath))
        {
            void Insert(string path, string kind, string status, DateTimeOffset? finished)
            {
                using var insert = connection.CreateCommand();
                insert.CommandText = """
                    INSERT INTO ingest_jobs (path, kind, status, attempts, enqueued_utc, finished_utc)
                    VALUES ($path, $kind, $status, 0, $enqueued, $finished)
                    """;
                insert.Parameters.AddWithValue("$path", path);
                insert.Parameters.AddWithValue("$kind", kind);
                insert.Parameters.AddWithValue("$status", status);
                insert.Parameters.AddWithValue("$enqueued", old.ToString("O"));
                insert.Parameters.AddWithValue("$finished", (object?)finished?.ToString("O") ?? DBNull.Value);
                insert.ExecuteNonQuery();
            }

            Insert("a.md", "upsert", "done", old - TimeSpan.FromDays(1)); // superseded: removed
            Insert("a.md", "upsert", "done", old);                        // latest of a.md, old: kept
            Insert("b.md", "upsert", "done", old);                        // latest of b.md: kept
            Insert("c.md", "upsert", "done", old);                        // superseded by a recent one: removed
            Insert("c.md", "upsert", "done", recent);                     // kept
            Insert("", "scan", "done", old);                              // removed
            Insert("a.md", "overview", "done", old);                      // removed
            Insert("", "scan", "done", recent);                           // kept
            Insert("d.md", "upsert", "failed", old);                      // failures stay
            Insert("e.md", "upsert", "pending", null);                    // the queue stays
        }

        Assert.Equal(4, runtime.PruneFinishedJobs(now));

        using var check = SqliteStorage.Open(databasePath);
        using var read = check.CreateCommand();
        read.CommandText = "SELECT path, kind, status, finished_utc FROM ingest_jobs ORDER BY id";
        using var reader = read.ExecuteReader();
        var left = new List<string>();
        while (reader.Read())
        {
            var finished = reader.IsDBNull(3) ? "-" : reader.GetString(3) == old.ToString("O") ? "old" : "recent";
            left.Add($"{reader.GetString(0)} {reader.GetString(1)} {reader.GetString(2)} {finished}");
        }

        Assert.Equal(
            ["a.md upsert done old", "b.md upsert done old", "c.md upsert done recent", " scan done recent", "d.md upsert failed old", "e.md upsert pending -"],
            left);

        // A second run finds nothing more to remove.
        Assert.Equal(0, runtime.PruneFinishedJobs(now));
    }

    private static async Task WaitForHitAsync(ProjectRuntime runtime, string query, string fileName, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var hits = await runtime.SearchAsync(query, topK: 1, cancellationToken: ct);
            if (hits.Count > 0 && hits[0].Chunk.Metadata["file_name"].StringValue == fileName)
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"'{fileName}' did not show in search for '{query}' within {timeout.TotalSeconds:F0} s.");
            }

            await Task.Delay(100, ct);
        }
    }

    private static void MarkJobsRunning(string databasePath)
    {
        using var connection = SqliteStorage.Open(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE ingest_jobs SET status = 'running' WHERE status = 'pending'";
        command.ExecuteNonQuery();
    }
}
