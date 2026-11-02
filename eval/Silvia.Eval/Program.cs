// Retrieval quality over the golden set. Each configuration ingests the corpus into a fresh project and
// scores every question by whether a top-K passage from the expected file contains the expected phrase.
//
//   dotnet run --project eval/Silvia.Eval                 # print the grid
//   dotnet run --project eval/Silvia.Eval -- --record     # also write eval/results/baseline.*
//   dotnet run --project eval/Silvia.Eval -- --check      # exit 1 if the default setting regressed
//
// Run from the repository root. Needs the local models (scripts/fetch-models.ps1).

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Silvia.Core.Models;
using Silvia.Core.Projects;
using Silvia.Core.Runtime;

const double RegressionTolerance = 0.02;
const int DefaultChunkTokens = 400;
const int DefaultTopK = 6;

var evalRoot = Path.GetFullPath("eval");
var corpus = Path.Combine(evalRoot, "corpus");
var resultsRoot = Path.Combine(evalRoot, "results");
var modelsRoot = Path.GetFullPath(Environment.GetEnvironmentVariable("SILVIA_MODELS") ?? "models");
var json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.Never };

if (!File.Exists(ModelCatalog.ModelFile(modelsRoot, ModelCatalog.DefaultEmbedder)))
{
    Console.Error.WriteLine($"Models not found in {modelsRoot}. Run scripts/fetch-models.ps1 first.");
    return 2;
}

var questions = JsonSerializer.Deserialize<List<GoldenQuestion>>(File.ReadAllText(Path.Combine(evalRoot, "golden.json")), json)
    ?? throw new InvalidDataException("golden.json is empty.");

var configs = (from chunk in new[] { 200, 400, 800 }
               from topK in new[] { 3, 6 }
               from rerank in new[] { false, true }
               select new Config(chunk, topK, rerank)).ToList();

using var models = ModelPoolFactory.Create(modelsRoot);
var results = new List<ConfigResult>();
foreach (var config in configs)
{
    results.Add(await RunAsync(config, questions, corpus, models));
}

var table = Table(results);
Console.WriteLine(table);

var record = args.Contains("--record");
var check = args.Contains("--check");
var baselinePath = Path.Combine(resultsRoot, "baseline.json");

if (record)
{
    Directory.CreateDirectory(resultsRoot);
    File.WriteAllText(baselinePath, JsonSerializer.Serialize(new Baseline(DateTimeOffset.UtcNow.ToString("O"), questions.Count, results), json));
    File.WriteAllText(Path.Combine(resultsRoot, "baseline.md"), Report(table, questions.Count));
    Console.WriteLine($"Recorded {baselinePath}");
}

if (check)
{
    if (!File.Exists(baselinePath))
    {
        Console.Error.WriteLine("No baseline to check against. Record one first with --record.");
        return 2;
    }

    var baseline = JsonSerializer.Deserialize<Baseline>(File.ReadAllText(baselinePath), json)!;
    var before = baseline.Results.Single(r => r.Config.Chunk == DefaultChunkTokens && r.Config.TopK == DefaultTopK && r.Config.Rerank);
    var now = results.Single(r => r.Config.Chunk == DefaultChunkTokens && r.Config.TopK == DefaultTopK && r.Config.Rerank);
    var drop = before.HitRate - now.HitRate;
    Console.WriteLine($"Default setting: hit@{DefaultTopK} {now.HitRate:P1} (baseline {before.HitRate:P1}), MRR {now.Mrr:F3} (baseline {before.Mrr:F3}).");
    if (drop > RegressionTolerance)
    {
        Console.Error.WriteLine($"Regression: hit rate fell by {drop:P1}, more than the {RegressionTolerance:P0} tolerance.");
        return 1;
    }

    Console.WriteLine("No regression against the baseline.");
}

return 0;

static async Task<ConfigResult> RunAsync(Config config, List<GoldenQuestion> questions, string corpus, ModelPool models)
{
    var ct = CancellationToken.None;
    var root = Path.Combine(Path.GetTempPath(), "silvia-eval", Guid.NewGuid().ToString("N"));
    using var manager = new ProjectManager(new ProjectManagerOptions
    {
        RootDirectory = root,
        ApiKeyProvider = () => null,
    }, models);
    manager.Initialize();

    try
    {
        var project = manager.Create($"eval-{config.Label}", settings: new ProjectSettings
        {
            ChunkTokens = config.Chunk,
            ChunkOverlap = config.Chunk * 15 / 100,
            TopK = config.TopK,
            RerankModel = config.Rerank ? ModelCatalog.DefaultReranker : string.Empty,
            CloudPolicy = CloudPolicies.SearchOnly,
        });
        var runtime = manager.Open(project.Id);
        runtime.AddSource(corpus);
        await runtime.WaitForIdleAsync(ct);

        var outcomes = new List<Outcome>();
        foreach (var question in questions)
        {
            var hits = await runtime.SearchAsync(question.QuestionText, config.TopK, ct);
            var rank = 0;
            for (var i = 0; i < hits.Count && rank == 0; i++)
            {
                var chunk = hits[i].Chunk;
                var fileName = chunk.Metadata.TryGetValue("file_name", out var name) ? name.ToString() : string.Empty;
                if (string.Equals(fileName, question.File, StringComparison.Ordinal) && Normalize(chunk.Text).Contains(Normalize(question.Phrase), StringComparison.Ordinal))
                {
                    rank = i + 1;
                }
            }

            outcomes.Add(new Outcome(question.Id, rank));
        }

        var hitRate = outcomes.Count(o => o.Rank > 0) / (double)outcomes.Count;
        var mrr = outcomes.Average(o => o.Rank > 0 ? 1.0 / o.Rank : 0.0);
        return new ConfigResult(config, hitRate, mrr, outcomes);
    }
    finally
    {
        manager.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // A temp file held by the indexer; the next run uses a new folder anyway.
        }
    }
}

static string Normalize(string text) => Regex.Replace(text, @"\s+", " ").Trim().ToLowerInvariant();

static string Table(List<ConfigResult> results)
{
    var builder = new StringBuilder();
    builder.AppendLine("| Chunk (tokens) | Passages (top K) | Reranker | hit@K | MRR | Missed |");
    builder.AppendLine("|---:|---:|:---:|---:|---:|---|");
    foreach (var result in results.OrderBy(r => r.Config.Chunk).ThenBy(r => r.Config.TopK).ThenBy(r => r.Config.Rerank))
    {
        var missed = string.Join(", ", result.Outcomes.Where(o => o.Rank == 0).Select(o => o.Id));
        builder.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"| {result.Config.Chunk} | {result.Config.TopK} | {(result.Config.Rerank ? "on" : "off")} | {result.HitRate:P1} | {result.Mrr:F3} | {(missed.Length == 0 ? "none" : missed)} |"));
    }

    return builder.ToString();
}

static string Report(string table, int questionCount) => $"""
    # Retrieval baseline

    Golden set: {questionCount} questions over the synthetic corpus in `eval/corpus`. A question is a hit when a
    top-K passage from the expected file contains the expected phrase. Scores come from the real ONNX embedder
    (bge-small-en-v1.5, 384 dimensions) and, where marked, the ms-marco MiniLM reranker. No chat model is involved.

    Recorded on the default settings of the app: 400-token chunks, 6 passages, reranker on. The regression check
    compares that row only.

    {table}
    The corpus is fictional. Replace it with your own documents, add your own questions to `eval/golden.json`,
    and record a new baseline before you change the defaults.
    """;

internal sealed record GoldenQuestion(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("question")] string QuestionText,
    [property: JsonPropertyName("file")] string File,
    [property: JsonPropertyName("phrase")] string Phrase);

internal sealed record Config(int Chunk, int TopK, bool Rerank)
{
    public string Label => $"c{Chunk}-k{TopK}-{(Rerank ? "rr" : "norr")}";
}

internal sealed record Outcome(string Id, int Rank);

internal sealed record ConfigResult(Config Config, double HitRate, double Mrr, List<Outcome> Outcomes);

internal sealed record Baseline(string RecordedUtc, int Questions, List<ConfigResult> Results);
