using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;
using Rag.NET.Embeddings.Onnx;
using Rag.NET.Reranking.Onnx;

namespace Silvia.Core.Models;

/// <summary>The production model pool: ONNX models from a folder, and the OpenAI-compatible chat endpoint.</summary>
public static class ModelPoolFactory
{
    public static ModelPool Create(string modelsRoot) => new(
        createEmbedder: id =>
        {
            EnsureFiles(modelsRoot, id);
            return new OnnxEmbeddingGenerator(new OnnxEmbeddingOptions
            {
                ModelPath = ModelCatalog.ModelFile(modelsRoot, id),
                TokenizerVocabPath = ModelCatalog.VocabularyFile(modelsRoot, id),
            });
        },
        createReranker: id =>
        {
            EnsureFiles(modelsRoot, id);
            return new OnnxReranker(new OnnxRerankerOptions
            {
                ModelPath = ModelCatalog.ModelFile(modelsRoot, id),
                VocabPath = ModelCatalog.VocabularyFile(modelsRoot, id),
            });
        },
        createChatClient: (endpoint, apiKey, model) => new OpenAIClient(
                new ApiKeyCredential(apiKey),
                new OpenAIClientOptions { Endpoint = endpoint })
            .GetChatClient(model)
            .AsIChatClient());

    private static void EnsureFiles(string modelsRoot, string modelId)
    {
        if (!File.Exists(ModelCatalog.ModelFile(modelsRoot, modelId)) || !File.Exists(ModelCatalog.VocabularyFile(modelsRoot, modelId)))
        {
            throw new ModelFilesMissingException(modelId, modelsRoot);
        }
    }
}

/// <summary>A model's ONNX graph or vocabulary is not in the models folder, so nothing that needs it can open.</summary>
public sealed class ModelFilesMissingException(string modelId, string modelsDirectory)
    : Exception($"The files of the model '{modelId}' are not in '{modelsDirectory}'.")
{
    public string ModelId { get; } = modelId;

    public string ModelsDirectory { get; } = modelsDirectory;
}
