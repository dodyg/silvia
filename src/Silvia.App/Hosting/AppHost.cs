using Silvia.Core.Models;
using Silvia.Core.Projects;
using Silvia.Core.Secrets;

namespace Silvia.App.Hosting;

/// <summary>
/// The composition root: where the data lives, which models load, where secrets are kept, and the one
/// project manager the RPC services share. Everything here is created once at startup and disposed at exit.
/// </summary>
public sealed class AppHost : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(30) };

    private AppHost(ProjectManager manager, ModelPool models, SecretStore secrets, string dataRoot, string modelsRoot)
    {
        Manager = manager;
        Models = models;
        Secrets = secrets;
        DataRoot = dataRoot;
        ModelsRoot = modelsRoot;
        Downloads = new ModelDownloadService(modelsRoot, _http);
    }

    public ProjectManager Manager { get; }

    public ModelPool Models { get; }

    public SecretStore Secrets { get; }

    public ModelDownloadService Downloads { get; }

    /// <summary>The native folder dialog. It reports <c>unavailable</c> until the main window attaches it.</summary>
    public FolderPicker FolderPicker { get; } = new();

    public string DataRoot { get; }

    public string ModelsRoot { get; }

    /// <summary>
    /// Opens the data folder (<c>%LOCALAPPDATA%\Silvia</c>, or <c>SILVIA_DATA</c> when set) and starts
    /// the project manager, which recovers any operation a previous run left unfinished.
    /// </summary>
    public static AppHost Create()
    {
        var dataRoot = Environment.GetEnvironmentVariable("SILVIA_DATA")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Silvia");
        var modelsRoot = Environment.GetEnvironmentVariable("SILVIA_MODELS") ?? Path.Combine(dataRoot, "models");

        var secrets = SecretStore.InDataFolder(dataRoot);
        var models = ModelPoolFactory.Create(modelsRoot);
        var manager = new ProjectManager(new ProjectManagerOptions
        {
            RootDirectory = dataRoot,
            ApiKeyProvider = () => ChatKey(secrets),
        }, models);
        manager.Initialize();
        return new AppHost(manager, models, secrets, dataRoot, modelsRoot);
    }

    /// <summary>The DeepSeek key: the saved one first, then the environment variable for scripted setups.</summary>
    public static string? ChatKey(SecretStore secrets) =>
        secrets.Get(SecretStore.DeepSeekKey) ?? Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");

    public void Dispose()
    {
        Manager.Dispose();
        Models.Dispose();
        _http.Dispose();
    }
}
