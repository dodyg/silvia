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
        createEmbedder: id => new OnnxEmbeddingGenerator(new OnnxEmbeddingOptions
        {
            ModelPath = ModelCatalog.ModelFile(modelsRoot, id),
            TokenizerVocabPath = ModelCatalog.VocabularyFile(modelsRoot, id),
        }),
        createReranker: id => new OnnxReranker(new OnnxRerankerOptions
        {
            ModelPath = ModelCatalog.ModelFile(modelsRoot, id),
            VocabPath = ModelCatalog.VocabularyFile(modelsRoot, id),
        }),
        createChatClient: (endpoint, apiKey, model) => new OpenAIClient(
                new ApiKeyCredential(apiKey),
                new OpenAIClientOptions { Endpoint = endpoint })
            .GetChatClient(model)
            .AsIChatClient());
}
