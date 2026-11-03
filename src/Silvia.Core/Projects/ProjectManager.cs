using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Silvia.Core.Catalog;
using Silvia.Core.Ingestion;
using Silvia.Core.Models;
using Silvia.Core.Runtime;
using Silvia.Storage.Sqlite;

namespace Silvia.Core.Projects;

/// <summary>
/// Owns the project catalog and the lifecycle of every project: create, rename, delete,
/// duplicate, export, import, and opening runtimes on demand. Long operations write their state
/// to the catalog first, so <see cref="Recover"/> can finish or roll them back after a crash.
/// </summary>
public sealed class ProjectManager : IDisposable
{
    private readonly ProjectManagerOptions _options;
    private readonly ModelPool _models;
    private readonly IngestThrottle _throttle;
    private readonly Dictionary<string, OpenRuntime> _open = [];
    private readonly Lock _gate = new();
    private ProjectCatalog? _catalog;
    private long _clock;

    public ProjectManager(ProjectManagerOptions options, ModelPool models)
    {
        _options = options;
        _models = models;
        _throttle = new IngestThrottle(options.MaxConcurrentIngests);
    }

    private ProjectCatalog Catalog => _catalog ?? throw new InvalidOperationException("Call Initialize() first.");

    /// <summary>Opens the catalog and finishes or rolls back any operation a crash interrupted.</summary>
    public void Initialize()
    {
        Directory.CreateDirectory(ProjectsRoot);
        _catalog = ProjectCatalog.Open(Path.Combine(_options.RootDirectory, "catalog.db"));
        Recover();
    }

    public IReadOnlyList<ProjectRecord> List() => Catalog.List();

    /// <summary>An app-wide setting from the catalog (for example <c>appearance.theme</c>), or null.</summary>
    public string? GetGlobal(string key) => Catalog.GetGlobal(key);

    public void SetGlobal(string key, string value) => Catalog.SetGlobal(key, value);

    /// <summary>The options this manager was created with: the data folder, limits and seams.</summary>
    public ProjectManagerOptions Options => _options;

    /// <summary>How many project runtimes are open right now.</summary>
    public int OpenCount
    {
        get { lock (_gate) return _open.Count; }
    }

    public ProjectRecord Get(string id) => Catalog.Find(id) ?? throw new ProjectNotFoundException(id);

    /// <summary>Creates an empty project, copying the default settings unless others are given.</summary>
    public ProjectRecord Create(string name, string? description = null, ProjectSettings? settings = null)
    {
        EnsureNameFree(name);
        var id = Ulid.New();
        var directory = ProjectDirectory(id);

        Directory.CreateDirectory(directory);
        _options.Faults.Step("create:directory");

        ProjectDatabase.Create(DatabasePath(id), id, name, settings ?? DefaultSettings());
        _options.Faults.Step("create:database");

        var record = new ProjectRecord(id, name, description, DateTimeOffset.UtcNow, null, ProjectStatus.Ready);
        Catalog.Insert(record);
        _options.Faults.Step("create:catalog");
        return record;
    }

    public void Rename(string id, string name)
    {
        Get(id);
        EnsureNameFree(name, except: id);
        Catalog.Rename(id, name);
    }

    /// <summary>Closes the project, then removes its folder and its catalog row.</summary>
    public void Delete(string id)
    {
        Get(id);
        Close(id);

        Catalog.SetStatus(id, ProjectStatus.Deleting);
        _options.Faults.Step("delete:marked");

        RemoveProjectFiles(id);
        _options.Faults.Step("delete:files");

        Catalog.Delete(id);
    }

    /// <summary>Copies a project's file to a new project under <paramref name="newName"/>.</summary>
    public ProjectRecord Duplicate(string id, string newName)
    {
        var source = Get(id);
        EnsureNameFree(newName);
        var newId = Ulid.New();
        Directory.CreateDirectory(ProjectDirectory(newId));

        CopyDatabase(id, DatabasePath(newId));
        _options.Faults.Step("duplicate:copied");
        ProjectDatabase.SetIdentity(DatabasePath(newId), newId, newName);

        var record = new ProjectRecord(newId, newName, source.Description, DateTimeOffset.UtcNow, null, ProjectStatus.Ready);
        Catalog.Insert(record);
        return record;
    }

    /// <summary>Writes a self-contained copy of the project to the export folder and returns its path.</summary>
    public string Export(string id)
    {
        var record = Get(id);
        Directory.CreateDirectory(ExportRoot);
        var safeName = string.Concat(record.Name.Select(c => char.IsLetterOrDigit(c) ? c : '-'));
        var target = Path.Combine(ExportRoot, $"{safeName}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.silvia");

        CopyDatabase(id, target);
        return target;
    }

    /// <summary>Imports an exported file as a new project. Source folders are kept as paths, not copied.</summary>
    public ProjectRecord Import(string filePath, string name)
    {
        EnsureNameFree(name);
        var id = Ulid.New();
        var directory = ProjectDirectory(id);

        Catalog.Insert(new ProjectRecord(id, name, null, DateTimeOffset.UtcNow, null, ProjectStatus.Importing));
        _options.Faults.Step("import:row");

        Directory.CreateDirectory(directory);
        File.Copy(filePath, DatabasePath(id), overwrite: false);
        _options.Faults.Step("import:copied");

        ProjectDatabase.SetIdentity(DatabasePath(id), id, name);
        Catalog.SetStatus(id, ProjectStatus.Ready);
        return Get(id);
    }

    /// <summary>
    /// Returns the open runtime for a project, building it on first use. When more than the limit are
    /// open, the least recently used idle runtime is closed first.
    /// </summary>
    public ProjectRuntime Open(string id)
    {
        lock (_gate)
        {
            if (_open.TryGetValue(id, out var open))
            {
                open.LastUsed = ++_clock;
                return open.Runtime;
            }

            var record = Get(id);
            if (record.Status != ProjectStatus.Ready)
            {
                throw new InvalidOperationException($"Project '{record.Name}' is {ProjectStatusNames.ToDb(record.Status)} and cannot be opened.");
            }

            EvictIdleRuntimes(_options.MaxOpenRuntimes - 1);

            var settings = ReadSettings(id);
            var runtime = ProjectRuntime.Open(
                id,
                ProjectDirectory(id),
                settings,
                _models,
                _throttle,
                ChatClientFor(settings),
                _options.RuntimeOptions,
                JudgeClientFor(settings));
            _open[id] = new OpenRuntime(runtime, ++_clock);
            Catalog.MarkOpened(id, DateTimeOffset.UtcNow);
            return runtime;
        }
    }

    /// <summary>Closes a project's runtime if it is open. Its files and queue stay as they are.</summary>
    public async Task CloseAsync(string id)
    {
        OpenRuntime? open;
        lock (_gate)
        {
            if (!_open.Remove(id, out open))
            {
                return;
            }
        }

        await open.Runtime.DisposeAsync();
    }

    public void Close(string id) => CloseAsync(id).GetAwaiter().GetResult();

    /// <summary>Closes every open runtime. The next open rebuilds each one with current credentials and settings.</summary>
    public void CloseAll()
    {
        List<string> ids;
        lock (_gate)
        {
            ids = _open.Keys.ToList();
        }

        foreach (var id in ids)
        {
            Close(id);
        }
    }

    /// <summary>Reads a project's settings without opening its runtime.</summary>
    public ProjectSettings ReadSettings(string id)
    {
        using var connection = SqliteStorage.Open(DatabasePath(id));
        return ProjectDatabase.ReadSettings(connection);
    }

    /// <summary>
    /// Saves new settings. The runtime is rebuilt on its next open, so the new embedder or chunking
    /// takes effect then. Changing the embedder marks the old vectors stale for re-indexing.
    /// </summary>
    public void UpdateSettings(string id, ProjectSettings settings)
    {
        Get(id);
        Close(id);
        ProjectDatabase.WriteSettings(settings, DatabasePath(id));
    }

    /// <summary>
    /// Finishes deletes and imports a crash interrupted, removes folders no catalog row points to,
    /// and leaves every ready project as it was.
    /// </summary>
    public void Recover()
    {
        foreach (var record in Catalog.List())
        {
            if (record.Status is ProjectStatus.Deleting or ProjectStatus.Importing)
            {
                RemoveProjectFiles(record.Id);
                Catalog.Delete(record.Id);
            }
        }

        var known = Catalog.List().Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        if (!Directory.Exists(ProjectsRoot))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(ProjectsRoot))
        {
            if (!known.Contains(Path.GetFileName(directory)))
            {
                RemoveDirectory(directory);
            }
        }
    }

    public void Dispose()
    {
        CloseAll();
        _catalog?.Dispose();
        SqliteConnection.ClearAllPools();
    }

    private string ProjectsRoot => Path.Combine(_options.RootDirectory, "projects");

    private string ExportRoot => Path.Combine(_options.RootDirectory, "export");

    private string ProjectDirectory(string id) => Path.Combine(ProjectsRoot, id);

    private string DatabasePath(string id) => Path.Combine(ProjectDirectory(id), "project.db");

    private ProjectSettings DefaultSettings() => new();

    /// <summary>Writes a compacted copy of a project's file to <paramref name="target"/>.</summary>
    private void CopyDatabase(string id, string target)
    {
        using var source = SqliteStorage.Open(DatabasePath(id));
        using var vacuum = source.CreateCommand();
        vacuum.CommandText = "VACUUM INTO $target";
        vacuum.Parameters.AddWithValue("$target", target);
        vacuum.ExecuteNonQuery();
    }

    private void EnsureNameFree(string name, string? except = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A project needs a name.", nameof(name));
        }

        if (Catalog.List().Any(r => string.Equals(r.Name, name, StringComparison.Ordinal) && r.Id != except))
        {
            throw new ProjectNameTakenException(name);
        }
    }

    private IChatClient? ChatClientFor(ProjectSettings settings)
    {
        if (settings.CloudPolicy != CloudPolicies.DeepSeek)
        {
            return null;
        }

        var apiKey = _options.ApiKeyProvider?.Invoke();
        return string.IsNullOrWhiteSpace(apiKey)
            ? null
            : _models.GetChatClient(_options.DeepSeekEndpoint, apiKey, settings.ChatModel);
    }

    /// <summary>The chat model that grades evaluations: the judge model when one is set, else the chat model.</summary>
    private IChatClient? JudgeClientFor(ProjectSettings settings) =>
        string.IsNullOrWhiteSpace(settings.JudgeModel)
            ? ChatClientFor(settings)
            : ChatClientFor(settings with { ChatModel = settings.JudgeModel });

    private void EvictIdleRuntimes(int keep)
    {
        while (_open.Count > keep)
        {
            var candidate = _open
                .Where(pair => !pair.Value.Runtime.IsBusy)
                .OrderBy(pair => pair.Value.LastUsed)
                .Select(pair => (KeyValuePair<string, OpenRuntime>?)pair)
                .FirstOrDefault();
            if (candidate is not { } victim)
            {
                return;
            }

            _open.Remove(victim.Key);
            victim.Value.Runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private void RemoveProjectFiles(string id)
    {
        Close(id);
        SqliteConnection.ClearAllPools();
        RemoveDirectory(ProjectDirectory(id));
    }

    private static void RemoveDirectory(string directory)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }

                return;
            }
            catch (IOException) when (attempt < 5)
            {
                // Antivirus or the search indexer can hold a file for a moment.
                Thread.Sleep(200 * attempt);
            }
        }
    }

    private sealed class OpenRuntime(ProjectRuntime runtime, long lastUsed)
    {
        public ProjectRuntime Runtime { get; } = runtime;

        public long LastUsed { get; set; } = lastUsed;
    }
}

/// <summary>Settings for a <see cref="ProjectManager"/>.</summary>
public sealed record ProjectManagerOptions
{
    /// <summary>The data folder: holds <c>catalog.db</c>, <c>projects/</c> and <c>export/</c>.</summary>
    public required string RootDirectory { get; init; }

    /// <summary>How many runtimes may be open at once.</summary>
    public int MaxOpenRuntimes { get; init; } = 3;

    /// <summary>How many projects may embed at once.</summary>
    public int MaxConcurrentIngests { get; init; } = 1;

    public Uri DeepSeekEndpoint { get; init; } = new("https://api.deepseek.com/v1");

    /// <summary>Supplies the chat API key. The app passes its secret store; this environment default serves tests and scripts.</summary>
    public Func<string?>? ApiKeyProvider { get; init; } = () => Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");

    public ProjectRuntime.RuntimeOptions RuntimeOptions { get; init; } = new();

    /// <summary>Test seam: lets a test stop an operation partway through, as a crash would.</summary>
    public IProjectFaults Faults { get; init; } = NoFaults.Instance;
}

/// <summary>Called at each step of a long project operation. Tests throw here to simulate a crash.</summary>
public interface IProjectFaults
{
    void Step(string name);
}

internal sealed class NoFaults : IProjectFaults
{
    public static readonly NoFaults Instance = new();

    public void Step(string name)
    {
    }
}

public sealed class ProjectNotFoundException(string id) : Exception($"Project '{id}' was not found.")
{
    public string ProjectId { get; } = id;
}

public sealed class ProjectNameTakenException(string name) : Exception($"A project named '{name}' already exists.");
