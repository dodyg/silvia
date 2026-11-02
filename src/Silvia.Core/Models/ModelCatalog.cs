namespace Silvia.Core.Models;

/// <summary>
/// The local models Silvia knows how to load, and where their files live under the models folder.
/// Model ids are stable names stored in project settings, so they never change once released.
/// </summary>
public static class ModelCatalog
{
    public const string DefaultEmbedder = "bge-small-en-v1.5";
    public const int DefaultEmbedderDims = 384;
    public const string DefaultReranker = "ms-marco-MiniLM-L-6-v2";

    /// <summary>Path of the ONNX graph for an embedder or reranker.</summary>
    public static string ModelFile(string modelsDirectory, string modelId) =>
        Path.Combine(modelsDirectory, modelId + ".onnx");

    /// <summary>Path of the WordPiece vocabulary that goes with a model.</summary>
    public static string VocabularyFile(string modelsDirectory, string modelId) =>
        Path.Combine(modelsDirectory, modelId + ".vocab.txt");
}
