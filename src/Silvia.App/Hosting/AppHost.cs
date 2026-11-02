using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;
using Silvia.Core.Models;
using Silvia.Core.Projects;

namespace Silvia.App.Hosting;

/// <summary>
/// The composition root: where the data lives, which models load, and the one project manager the
/// RPC services share. Everything here is created once at startup and disposed at exit.
/// </summary>
public sealed class AppHost : IDisposable
{
    private AppHost(ProjectManager manager, ModelPool models, string dataRoot, string modelsRoot)
    {
        Manager = manager;
        Models = models;
        DataRoot = dataRoot;
        ModelsRoot = modelsRoot;
    }

    public ProjectManager Manager { get; }

    public ModelPool Models { get; }

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

        var models = new ModelPool(
            createEmbedder: id =>
            {
                var options = new Rag.NET.Embeddings.Onnx.OnnxEmbeddingOptions
                {
                    ModelPath = ModelCatalog.ModelFile(modelsRoot, id),
                    TokenizerVocabPath = ModelCatalog.VocabularyFile(modelsRoot, id),
                };
                return new Rag.NET.Embeddings.Onnx.OnnxEmbeddingGenerator(options);
            },
            createReranker: id =>
            {
                var options = new Rag.NET.Reranking.Onnx.OnnxRerankerOptions
                {
                    ModelPath = ModelCatalog.ModelFile(modelsRoot, id),
                    VocabPath = ModelCatalog.VocabularyFile(modelsRoot, id),
                };
                return new Rag.NET.Reranking.Onnx.OnnxReranker(options);
            },
            createChatClient: (endpoint, apiKey, model) => new OpenAIClient(
                    new ApiKeyCredential(apiKey),
                    new OpenAIClientOptions { Endpoint = endpoint })
                .GetChatClient(model)
                .AsIChatClient());

        var manager = new ProjectManager(
            new ProjectManagerOptions { RootDirectory = dataRoot },
            models);
        manager.Initialize();
        return new AppHost(manager, models, dataRoot, modelsRoot);
    }

    public void Dispose()
    {
        Manager.Dispose();
        Models.Dispose();
    }
}
