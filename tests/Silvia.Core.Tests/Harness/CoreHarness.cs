using Microsoft.Extensions.AI;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Silvia.Core.Models;
using Silvia.Core.Projects;
using Silvia.Core.Runtime;

namespace Silvia.Core.Tests.Harness;

/// <summary>
/// A data folder, a model pool of fakes, and a project manager over them. Each test gets its own,
/// so nothing leaks between tests. Timers are shortened so watcher tests stay quick.
/// </summary>
public sealed class CoreHarness : IDisposable
{
    public const string EmbedderId = "hashing-384";
    public const int Dimensions = 384;

    /// <summary>The fake reranker; settings name it in <c>RerankModel</c> to use it.</summary>
    public const string RerankerId = "word-overlap";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "silvia-core-tests", Guid.NewGuid().ToString("N"));
    private readonly List<HashingEmbedder> _embedders = [];

    public CoreHarness(
        ProjectManagerOptions? overrides = null,
        bool withChat = false,
        ProjectRuntime.RuntimeOptions? runtimeOptions = null,
        bool localModels = true)
    {
        Chat = new RecordingChatClient();
        Pool = new ModelPool(
            createEmbedder: id =>
            {
                if (!localModels)
                {
                    throw new ModelFilesMissingException(id, Path.Combine(_root, "models"));
                }

                var dims = id == EmbedderId ? Dimensions : int.Parse(id.Split('-')[1]);
                var embedder = new HashingEmbedder(dims);
                _embedders.Add(embedder);
                return embedder;
            },
            createReranker: id => id == RerankerId ? new WordOverlapReranker() : throw new InvalidOperationException($"No fake reranker '{id}'."),
            createChatClient: (_, _, _) => Chat);

        var options = overrides ?? new ProjectManagerOptions { RootDirectory = _root };
        Options = options with
        {
            RootDirectory = _root,
            RuntimeOptions = runtimeOptions ?? new ProjectRuntime.RuntimeOptions
            {
                WatchDebounce = TimeSpan.FromMilliseconds(200),
                RescanInterval = TimeSpan.FromHours(1),
            },
            ApiKeyProvider = withChat ? () => "test-key" : () => null,
        };

        Manager = new ProjectManager(Options, Pool);
        Manager.Initialize();
    }

    public string Root => _root;

    public ProjectManagerOptions Options { get; }

    public ModelPool Pool { get; }

    public ProjectManager Manager { get; private set; }

    public RecordingChatClient Chat { get; }

    /// <summary>Every embedder the pool has loaded so far, including disposed ones.</summary>
    public IReadOnlyList<HashingEmbedder> Embedders => _embedders;

    /// <summary>Settings that use the fake 384-dimension embedder and no reranker.</summary>
    public static ProjectSettings FakeSettings(string embedder = EmbedderId, int dims = Dimensions, string cloudPolicy = CloudPolicies.DeepSeek) => new()
    {
        EmbedModel = embedder,
        EmbedDims = dims,
        RerankModel = string.Empty,
        CloudPolicy = cloudPolicy,
        // The fake chat answers every request alike; BridgeSearchTests turn this on with replies of their own.
        BridgeSearch = false,
    };

    /// <summary>Makes a new manager over the same data folder, as a process restart would.</summary>
    public ProjectManager Restart(ProjectManagerOptions? options = null)
    {
        Manager.Dispose();
        Manager = new ProjectManager(options ?? Options, Pool);
        Manager.Initialize();
        return Manager;
    }

    /// <summary>Writes a file under a folder, creating directories as needed, and returns its full path.</summary>
    public static string WriteFile(string folder, string name, string text)
    {
        var path = Path.Combine(folder, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return Path.GetFullPath(path);
    }

    /// <summary>A fresh folder inside the test root.</summary>
    public string NewFolder(string name)
    {
        var path = Path.Combine(_root, "sources", name);
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        Manager.Dispose();
        Pool.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A file still held by antivirus or the indexer: leave the temp folder behind.
        }
    }
}
