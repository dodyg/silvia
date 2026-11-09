// M0 spike: Rag.NET over a sample folder with local ONNX embeddings,
// an in-memory vector store, local reranking and optional DeepSeek chat.
using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using OpenAI;
using Rag.NET.Abstractions;
using Rag.NET.Storage;
using Rag.NET.DependencyInjection;
using Rag.NET.Embeddings.Onnx;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Reranking.Onnx;

var docsDir = Path.GetFullPath(args.Length > 0 ? args[0] : "../sample-docs");
var modelsDir = Path.GetFullPath(Environment.GetEnvironmentVariable("SILVIA_MODELS") ?? "../../models");
var apiKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
var question = args.Length > 1 ? args[1] : "Who is the security lead and how often are access reviews run?";

var services = new ServiceCollection();

services.AddSingleton<IVectorStore, InMemoryVectorStore>();
services.AddRagNet(rag =>
{
    rag.UseOnnxEmbeddings(o =>
    {
        o.ModelPath = Path.Combine(modelsDir, "bge-small-en-v1.5.onnx");
        o.TokenizerVocabPath = Path.Combine(modelsDir, "bge-small-en-v1.5.vocab.txt");
    });
    rag.UseOnnxReranking(o =>
    {
        o.ModelPath = Path.Combine(modelsDir, "ms-marco-MiniLM-L-6-v2.onnx");
        o.VocabPath = Path.Combine(modelsDir, "ms-marco-MiniLM-L-6-v2.vocab.txt");
    });
});

if (!string.IsNullOrWhiteSpace(apiKey))
{
    var deepSeek = new OpenAIClient(
        new ApiKeyCredential(apiKey),
        new OpenAIClientOptions { Endpoint = new Uri(Environment.GetEnvironmentVariable("DEEPSEEK_BASE_URL") ?? "https://api.deepseek.com/v1") });
    services.AddSingleton<IChatClient>(deepSeek.GetChatClient("deepseek-chat").AsIChatClient());
}

await using var provider = services.BuildServiceProvider();
var pipeline = provider.GetRequiredService<IRagPipeline>();

foreach (var file in Directory.EnumerateFiles(docsDir, "*.md"))
{
    var id = Path.GetFileName(file);
    await using var stream = File.OpenRead(file);
    var result = await pipeline.IngestAsync(stream, new DocumentMetadata
    {
        DocumentId = new DocumentId(id),
        FileName = id,
        ContentType = "text/markdown",
    });
    Console.WriteLine($"ingested {id}: {(result.IsSuccess ? "ok" : result.Error.ToString())}");
}

var retrieved = await pipeline.RetrieveAsync(question, new RetrievalOptions { TopK = 3, UseReranking = true });
Console.WriteLine($"\nQ: {question}");
if (!retrieved.IsSuccess)
{
    Console.WriteLine($"retrieve failed: {retrieved.Error}");
    return 1;
}
foreach (var r in retrieved.Value)
{
    Console.WriteLine($"  [{r.Score:F3}] {r.Chunk.Metadata["file_name"]}: {Truncate(r.Chunk.Text)}");
}

if (provider.GetService<IChatClient>() is null)
{
    Console.WriteLine("\nDEEPSEEK_API_KEY not set: skipping AskAsync (retrieval-only run).");
    return 0;
}

var answer = await pipeline.AskAsync(question);
Console.WriteLine($"\nA: {answer.Answer}");
foreach (var source in answer.Sources)
{
    Console.WriteLine($"  source: {source.Chunk.Metadata["file_name"]}");
}
return 0;

static string Truncate(string text) =>
    text.Length <= 90 ? text.ReplaceLineEndings(" ") : text[..90].ReplaceLineEndings(" ") + "...";
