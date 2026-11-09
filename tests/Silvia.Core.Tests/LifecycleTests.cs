using Silvia.Core.Catalog;
using Silvia.Core.Projects;
using Silvia.Core.Tests.Harness;
using Xunit;

namespace Silvia.Core.Tests;

/// <summary>Catalog lifecycle: names, crash recovery at each step, and the open-runtime limit.</summary>
public sealed class LifecycleTests
{
    [Fact]
    public void Names_Must_Be_Present_And_Unique()
    {
        using var harness = new CoreHarness();
        harness.Manager.Create("Lumen");

        Assert.Throws<ProjectNameTakenException>(() => harness.Manager.Create("Lumen"));
        Assert.Throws<ArgumentException>(() => harness.Manager.Create("  "));

        var other = harness.Manager.Create("Other");
        Assert.Throws<ProjectNameTakenException>(() => harness.Manager.Rename(other.Id, "Lumen"));
        harness.Manager.Rename(other.Id, "Renamed");
        Assert.Equal("Renamed", harness.Manager.Get(other.Id).Name);
    }

    [Fact]
    public void Open_Count_Stays_At_The_Limit_And_Reopened_Projects_Still_Work()
    {
        using var harness = new CoreHarness();
        var ids = new[] { "One", "Two", "Three", "Four" }
            .Select(name => harness.Manager.Create(name, settings: CoreHarness.FakeSettings()).Id)
            .ToList();

        foreach (var id in ids)
        {
            harness.Manager.Open(id);
        }

        Assert.Equal(3, harness.Manager.OpenCount);

        // Reopening an evicted project builds it again and returns a working runtime.
        var reopened = harness.Manager.Open(ids[0]);
        Assert.Equal(ids[0], reopened.ProjectId);
        Assert.Equal(3, harness.Manager.OpenCount);
    }

    [Theory]
    [InlineData("create:directory")]
    [InlineData("create:database")]
    public void Crash_During_Create_Leaves_No_Project_After_Restart(string step)
    {
        using var harness = new CoreHarness();
        harness.Restart(harness.Options with { Faults = new CrashAt(step) });

        Assert.Throws<SimulatedCrash>(() => harness.Manager.Create("Crashed"));

        harness.Restart();
        Assert.Empty(harness.Manager.List());
        Assert.Empty(Directory.Exists(Path.Combine(harness.Root, "projects"))
            ? Directory.GetDirectories(Path.Combine(harness.Root, "projects"))
            : []);
    }

    [Fact]
    public void Crash_After_Catalog_Insert_Keeps_The_Completed_Project()
    {
        using var harness = new CoreHarness();
        harness.Restart(harness.Options with { Faults = new CrashAt("create:catalog") });

        Assert.Throws<SimulatedCrash>(() => harness.Manager.Create("Finished"));

        harness.Restart();
        var project = Assert.Single(harness.Manager.List());
        Assert.Equal("Finished", project.Name);
        Assert.Equal(ProjectStatus.Ready, project.Status);
        Assert.True(File.Exists(Path.Combine(harness.Root, "projects", project.Id, "project.db")));
    }

    [Theory]
    [InlineData("delete:marked")]
    [InlineData("delete:files")]
    public void Crash_During_Delete_Is_Finished_On_Restart(string step)
    {
        using var harness = new CoreHarness();
        var project = harness.Manager.Create("Doomed", settings: CoreHarness.FakeSettings());

        harness.Restart(harness.Options with { Faults = new CrashAt(step) });
        Assert.Throws<SimulatedCrash>(() => harness.Manager.Delete(project.Id));

        harness.Restart();
        Assert.Empty(harness.Manager.List());
        Assert.False(Directory.Exists(Path.Combine(harness.Root, "projects", project.Id)));
    }

    [Theory]
    [InlineData("duplicate:copied")]
    public void Crash_During_Duplicate_Rolls_Back_The_Copy(string step)
    {
        using var harness = new CoreHarness();
        var original = harness.Manager.Create("Original", settings: CoreHarness.FakeSettings());

        harness.Restart(harness.Options with { Faults = new CrashAt(step) });
        Assert.Throws<SimulatedCrash>(() => harness.Manager.Duplicate(original.Id, "Copy"));

        harness.Restart();
        var project = Assert.Single(harness.Manager.List());
        Assert.Equal(original.Id, project.Id);
        Assert.Single(Directory.GetDirectories(Path.Combine(harness.Root, "projects")));
    }

    [Theory]
    [InlineData("import:row")]
    [InlineData("import:copied")]
    public void Crash_During_Import_Is_Rolled_Back_On_Restart(string step)
    {
        using var harness = new CoreHarness();
        var original = harness.Manager.Create("Source", settings: CoreHarness.FakeSettings());
        var exported = harness.Manager.Export(original.Id);

        harness.Restart(harness.Options with { Faults = new CrashAt(step) });
        Assert.Throws<SimulatedCrash>(() => harness.Manager.Import(exported, "Imported"));

        harness.Restart();
        var project = Assert.Single(harness.Manager.List());
        Assert.Equal(original.Id, project.Id);
        Assert.Single(Directory.GetDirectories(Path.Combine(harness.Root, "projects")));
    }

    [Fact]
    public void Orphan_Folders_Are_Removed_On_Restart()
    {
        using var harness = new CoreHarness();
        var orphan = Path.Combine(harness.Root, "projects", "01ORPHANFOLDER000000000000");
        Directory.CreateDirectory(orphan);
        File.WriteAllText(Path.Combine(orphan, "project.db"), "stale");

        harness.Restart();

        Assert.False(Directory.Exists(orphan));
    }

    [Fact]
    public void Settings_Round_Trip_Through_Project_File()
    {
        using var harness = new CoreHarness();
        var project = harness.Manager.Create("Tuned", settings: CoreHarness.FakeSettings() with
        {
            ChunkTokens = 300,
            TopK = 4,
            CloudPolicy = CloudPolicies.SearchOnly,
        });

        var read = harness.Manager.ReadSettings(project.Id);
        Assert.Equal(300, read.ChunkTokens);
        Assert.Equal(4, read.TopK);
        Assert.Equal(CloudPolicies.SearchOnly, read.CloudPolicy);
    }
}
