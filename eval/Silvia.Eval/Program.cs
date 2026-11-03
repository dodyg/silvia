// Retrieval quality over the golden sets. Each configuration ingests the corpus into a fresh project and
// scores every question by whether the top-K passages hold all of its evidence: for each piece, a passage
// from the expected file that contains every expected term.
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
var resultsRoot = Path.Combine(evalRoot, "results");
var modelsRoot = Path.GetFullPath(Environment.GetEnvironmentVariable("SILVIA_MODELS") ?? "models");
var json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.Never };

if (!File.Exists(ModelCatalog.ModelFile(modelsRoot, ModelCatalog.DefaultEmbedder)))
{
    Console.Error.WriteLine($"Models not found in {modelsRoot}. Run scripts/fetch-models.ps1 first.");
    return 2;
}

// golden.json holds the plain lookups; hard.json the kinds that plain lookups don't exercise.
var basic = JsonSerializer.Deserialize<List<GoldenQuestion>>(File.ReadAllText(Path.Combine(evalRoot, "golden.json")), json)
    ?? throw new InvalidDataException("golden.json is empty.");
var hard = JsonSerializer.Deserialize<List<EvalCase>>(File.ReadAllText(Path.Combine(evalRoot, "hard.json")), json)
    ?? throw new InvalidDataException("hard.json is empty.");
var cases = basic.Select(q => new EvalCase(q.Id, Kinds.Basic, null, q.QuestionText, [new Evidence(q.File, [q.Phrase])])).Concat(hard).ToList();
if (cases.FirstOrDefault(c => !Kinds.All.Contains(c.Kind)) is { } unknown)
{
    Console.Error.WriteLine($"Question '{unknown.Id}' has unknown kind '{unknown.Kind}'.");
    return 2;
}

var corpus = PrepareCorpus(Path.Combine(evalRoot, "corpus"));
// The default setting runs first, so the further searches a missing bridge needs are planned from its passages.
var configs = (from chunk in new[] { 200, 400, 800 }
               from topK in new[] { 3, 6 }
               from rerank in new[] { false, true }
               select new Config(chunk, topK, rerank)).OrderByDescending(IsDefaultConfig).ToList();

var results = new List<ConfigResult>();
var rewritesPath = Path.Combine(resultsRoot, "rewrites.json");
string rewriteNote;
using (var models = ModelPoolFactory.Create(modelsRoot))
{
    var apiKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
    var chat = string.IsNullOrWhiteSpace(apiKey)
        ? null
        : models.GetChatClient(new ProjectManagerOptions { RootDirectory = string.Empty }.DeepSeekEndpoint, apiKey, new ProjectSettings().ChatModel);
    (cases, rewriteNote) = await RewriteFollowUpsAsync(cases, rewritesPath, chat, json);
    Console.WriteLine(rewriteNote);
    var bridges = new BridgePlans(Path.Combine(resultsRoot, "bridges.json"), chat, json);
    foreach (var config in configs)
    {
        results.Add(await RunAsync(config, cases, corpus, models, bridges));
    }

    Console.WriteLine(bridges.Save());
}

TryDelete(corpus);

var table = Table(results);
var missed = Missed(results.Single(IsDefault), cases);
Console.WriteLine(table);
Console.WriteLine(missed);

var record = args.Contains("--record");
var check = args.Contains("--check");
var baselinePath = Path.Combine(resultsRoot, "baseline.json");

if (record)
{
    Directory.CreateDirectory(resultsRoot);
    File.WriteAllText(baselinePath, JsonSerializer.Serialize(new Baseline(DateTimeOffset.UtcNow.ToString("O"), cases.Count, results), json));
    File.WriteAllText(Path.Combine(resultsRoot, "baseline.md"), Report(table, missed, cases));
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
    var before = baseline.Results.Single(IsDefault);
    var now = results.Single(IsDefault);
    Console.WriteLine($"Default setting: hit@{DefaultTopK} {now.HitRate:P1} (baseline {before.HitRate:P1}), MRR {now.Mrr:F3} (baseline {before.Mrr:F3}).");

    // Retrieval is deterministic, so any drop within a kind is a real change; only the overall rate fails the check.
    foreach (var kind in Kinds.Answerable)
    {
        if (before.ByKind.GetValueOrDefault(kind) - now.ByKind.GetValueOrDefault(kind) > RegressionTolerance)
        {
            Console.WriteLine($"  {kind}: {now.ByKind.GetValueOrDefault(kind):P1} (baseline {before.ByKind.GetValueOrDefault(kind):P1})");
        }
    }

    if (before.HitRate - now.HitRate > RegressionTolerance)
    {
        Console.Error.WriteLine($"Regression: hit rate fell by {before.HitRate - now.HitRate:P1}, more than the {RegressionTolerance:P0} tolerance.");
        return 1;
    }

    Console.WriteLine("No regression against the baseline.");
}

return 0;

static bool IsDefault(ConfigResult r) => IsDefaultConfig(r.Config);

static bool IsDefaultConfig(Config c) => c.Chunk == DefaultChunkTokens && c.TopK == DefaultTopK && c.Rerank;

// A temporary copy of the Markdown corpus plus the generated PDF and Office documents.
static string PrepareCorpus(string source)
{
    var folder = Path.Combine(Path.GetTempPath(), "silvia-eval", "corpus-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(folder);
    foreach (var file in Directory.EnumerateFiles(source))
    {
        File.Copy(file, Path.Combine(folder, Path.GetFileName(file)));
    }

    Fixtures.Write(folder);
    return folder;
}

static async Task<ConfigResult> RunAsync(Config config, List<EvalCase> cases, string corpus, ModelPool models, BridgePlans bridges)
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

        // A file the parsers skip would otherwise show up only as missed questions.
        var indexed = runtime.Documents().Select(d => Path.GetFileName(d.DocumentId)).ToHashSet(StringComparer.Ordinal);
        var skipped = Directory.EnumerateFiles(corpus).Select(Path.GetFileName).Where(f => !indexed.Contains(f!)).ToList();
        if (skipped.Count > 0)
        {
            throw new InvalidOperationException($"Not indexed under {config.Label}: {string.Join(", ", skipped)}");
        }

        var outcomes = new List<Outcome>();
        foreach (var item in cases)
        {
            // A follow-up is searched by its standalone rewrite, as chat does; every other question as asked. Then
            // the further searches that chat's bridge step plans from the first passages add theirs.
            var query = item.Query ?? item.Question;
            // The best score is the first search's: a bridge search's passages match its own query, not the question.
            var best = 0.0;
            var hits = await runtime.SearchAsync(query, config.TopK, found =>
            {
                best = found.Max(h => h.Score);
                return bridges.ForAsync(query, found);
            }, ct);
            var ranks = item.Evidence.Select(e => Rank(hits, e)).ToList();
            var found = ranks.Count(r => r > 0);
            var rank = item.Evidence.Count > 0 && found == item.Evidence.Count ? ranks.Max() : 0;
            outcomes.Add(new Outcome(item.Id, item.Kind, rank, found, item.Evidence.Count, best));
        }

        var answerable = outcomes.Where(o => o.Kind != Kinds.Unanswerable).ToList();
        var byKind = answerable.GroupBy(o => o.Kind).ToDictionary(g => g.Key, g => g.Count(o => o.Rank > 0) / (double)g.Count());
        return new ConfigResult(
            config,
            HitRate: answerable.Count(o => o.Rank > 0) / (double)answerable.Count,
            Mrr: answerable.Average(o => o.Rank > 0 ? 1.0 / o.Rank : 0.0),
            Recall: answerable.Sum(o => o.Found) / (double)answerable.Sum(o => o.Of),
            Abstain: AbstainAccuracy(outcomes),
            byKind,
            outcomes);
    }
    finally
    {
        manager.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        TryDelete(root);
    }
}

// Each follow-up rewritten to stand on its own, the way chat rewrites one before it searches. Rewrites are
// kept in results/rewrites.json, keyed by the history and question, so a rerun searches the same text and
// needs no API key. A follow-up with no kept rewrite and no DEEPSEEK_API_KEY is searched as asked.
static async Task<(List<EvalCase> Cases, string Note)> RewriteFollowUpsAsync(
    List<EvalCase> cases, string path, Microsoft.Extensions.AI.IChatClient? chat, JsonSerializerOptions json)
{
    var kept = File.Exists(path)
        ? JsonSerializer.Deserialize<List<Rewrite>>(File.ReadAllText(path), json) ?? []
        : [];

    var rewritten = new List<EvalCase>();
    var used = new List<Rewrite>();
    var asAsked = 0;
    foreach (var item in cases)
    {
        if (item.History is not { Count: > 0 } history)
        {
            rewritten.Add(item);
            continue;
        }

        var query = kept.FirstOrDefault(r => r.Question == item.Question && r.History.SequenceEqual(history))?.Query;
        if (query is null && chat is not null)
        {
            // The history alternates user and assistant turns, starting with the user.
            var turns = history.Select((h, i) => new Microsoft.Extensions.AI.ChatMessage(
                i % 2 == 0 ? Microsoft.Extensions.AI.ChatRole.User : Microsoft.Extensions.AI.ChatRole.Assistant, h)).ToList();
            var reply = await chat.GetResponseAsync(StandaloneQuestion.Prompt(turns, item.Question));
            query = StandaloneQuestion.Parse(reply.Text, item.Question);
        }

        if (query is null)
        {
            asAsked++;
        }
        else
        {
            used.Add(new Rewrite(history, item.Question, query));
        }

        rewritten.Add(item with { Query = query });
    }

    if (used.Count > 0)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(used, json));
    }

    var note = asAsked == 0
        ? $"Follow-ups: {used.Count} searched by their standalone rewrite ({path})."
        : $"Follow-ups: {asAsked} of {asAsked + used.Count} searched as asked: no kept rewrite and no DEEPSEEK_API_KEY.";
    return (rewritten, note);
}

// The 1-based position of the first hit from the evidence's file that holds every term, or 0.
static int Rank(IReadOnlyList<Rag.NET.Models.SearchResult> hits, Evidence evidence)
{
    for (var i = 0; i < hits.Count; i++)
    {
        var chunk = hits[i].Chunk;
        var fileName = chunk.Metadata.TryGetValue("file_name", out var name) ? name.ToString() : string.Empty;
        var text = Normalize(chunk.Text);
        if (string.Equals(fileName, evidence.File, StringComparison.Ordinal) && evidence.Terms.All(t => text.Contains(Normalize(t), StringComparison.Ordinal)))
        {
            return i + 1;
        }
    }

    return 0;
}

// How well one threshold on the best hit's score tells unanswerable questions from the rest: the balanced
// accuracy (mean of both classes' recall) at the best threshold. 0.5 means the score says nothing.
static double AbstainAccuracy(List<Outcome> outcomes) => AbstainThreshold(outcomes).Accuracy;

// The threshold behind AbstainAccuracy: a best hit scoring below it counts as "the documents don't say".
static (double Threshold, double Accuracy) AbstainThreshold(List<Outcome> outcomes)
{
    var none = outcomes.Where(o => o.Kind == Kinds.Unanswerable).Select(o => o.TopScore).ToList();
    var some = outcomes.Where(o => o.Kind != Kinds.Unanswerable).Select(o => o.TopScore).ToList();
    if (none.Count == 0 || some.Count == 0)
    {
        return (0, 0);
    }

    return outcomes.Select(o => o.TopScore).Append(double.MaxValue)
        .Select(t => (Threshold: t, Accuracy: (none.Count(s => s < t) / (double)none.Count + some.Count(s => s >= t) / (double)some.Count) / 2))
        .MaxBy(x => x.Accuracy);
}

static string Normalize(string text) => Regex.Replace(text, @"\s+", " ").Trim().ToLowerInvariant();

static void TryDelete(string folder)
{
    try
    {
        Directory.Delete(folder, recursive: true);
    }
    catch (IOException)
    {
        // A temp file held by the indexer; the next run uses a new folder anyway.
    }
}

static string Table(List<ConfigResult> results)
{
    var builder = new StringBuilder();
    builder.AppendLine($"| Chunk | K | Rerank | All | {string.Join(" | ", Kinds.Answerable)} | Evidence recall | MRR | Abstain |");
    builder.AppendLine($"|---:|---:|:---:|---:|{string.Concat(Kinds.Answerable.Select(_ => "---:|"))}---:|---:|---:|");
    foreach (var result in results.OrderBy(r => r.Config.Chunk).ThenBy(r => r.Config.TopK).ThenBy(r => r.Config.Rerank))
    {
        var kinds = string.Join(" | ", Kinds.Answerable.Select(k => Percent(result.ByKind.GetValueOrDefault(k))));
        builder.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"| {result.Config.Chunk} | {result.Config.TopK} | {(result.Config.Rerank ? "on" : "off")} | **{Percent(result.HitRate)}** | {kinds} | {Percent(result.Recall)} | {result.Mrr:F3} | {result.Abstain:F2} |"));
    }

    return builder.ToString();
}

static string Percent(double value) => (value * 100).ToString("F0", CultureInfo.InvariantCulture) + " %";

static string Missed(ConfigResult result, List<EvalCase> cases)
{
    var builder = new StringBuilder();
    builder.AppendLine($"Missed at the default setting ({result.Config.Chunk} tokens, top {result.Config.TopK}, reranker on):");
    builder.AppendLine();
    foreach (var kind in Kinds.Answerable)
    {
        var misses = result.Outcomes.Where(o => o.Kind == kind && o.Rank == 0).ToList();
        if (misses.Count > 0)
        {
            builder.AppendLine($"- **{kind}**: {string.Join(", ", misses.Select(o => o.Of > 1 ? $"{o.Id} ({o.Found}/{o.Of})" : o.Id))}");
        }
    }

    var scores = result.Outcomes.Where(o => o.Kind == Kinds.Unanswerable).Select(o => o.TopScore).ToList();
    var answered = result.Outcomes.Where(o => o.Kind != Kinds.Unanswerable).Select(o => o.TopScore).Order().ToList();
    if (scores.Count > 0)
    {
        builder.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"- **unanswerable**: best-hit score {scores.Min():F3} to {scores.Max():F3}; answerable questions {answered[0]:F3} to {answered[^1]:F3} (median {answered[answered.Count / 2]:F3}); best threshold {AbstainThreshold(result.Outcomes).Threshold:F3}"));
        var unanswerable = result.Outcomes.Where(o => o.Kind == Kinds.Unanswerable).OrderBy(o => o.TopScore);
        builder.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"- **unanswerable, best-hit score each**: {string.Join(", ", unanswerable.Select(o => $"{o.Id} ({o.TopScore:F3})"))}"));
        foreach (var cut in new[] { 0.01, 0.05, 0.1, 0.2 })
        {
            var weak = result.Outcomes.Where(o => o.TopScore < cut).ToList();
            builder.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"- **below {cut:F2}**: {weak.Count(o => o.Kind == Kinds.Unanswerable)} of {scores.Count} unanswerable, {weak.Count(o => o.Kind != Kinds.Unanswerable)} of {answered.Count} answerable ({string.Join(", ", weak.Where(o => o.Kind != Kinds.Unanswerable).Select(o => o.Id))})"));
        }
    }

    return builder.ToString();
}

static string Report(string table, string missed, List<EvalCase> cases)
{
    var counts = string.Join(", ", Kinds.All.Select(k => $"{k} {cases.Count(c => c.Kind == k)}"));
    return $"""
        # Retrieval baseline

        {cases.Count} questions over the synthetic corpus in `eval/corpus` plus the PDF and Office documents that
        `Fixtures.cs` writes at the start of each run ({counts}). Scores come from the real ONNX embedder
        (bge-small-en-v1.5, 384 dimensions) and, where marked, the ms-marco MiniLM reranker. The only chat model calls are the rewrite of each
        follow-up and the bridge searches: further searches for what the first passages point to, which add up to two
        passages each, as chat does. Both are kept in `results/`, so a rerun needs no API key; without a kept plan or a
        key, a question is searched once.

        A question is a hit when the top-K passages hold every piece of its evidence: a passage from the expected
        file that contains all the expected terms. Kinds:

        - **basic**: `golden.json`, one fact per question, worded much like the document.
        - **paraphrase**: the same kind of fact, asked in other words than the document uses.
        - **stale**: the current answer, when an older or more general document says something else.
        - **long-doc**: a fact buried in the middle of a long document.
        - **format**: facts from the generated PDF, Word, Excel and PowerPoint files.
        - **multi-hop**: an answer that needs passages from two or three files.
        - **follow-up**: a question that only makes sense after the turns in `history`. Retrieval searches its
          standalone rewrite, as chat does, kept in `results/rewrites.json`.
        - **aggregation**: a question whose full answer is spread across three or four files.
        - **unanswerable**: nothing in the corpus answers it. Not part of the hit rate.

        *All* is the hit rate over every answerable question. *Evidence recall* counts the pieces of evidence
        found, so a multi-hop question with one of two pieces counts half. *Abstain* is the balanced accuracy
        of the best single threshold on the top hit's score at telling unanswerable questions from the rest
        (0.5 means the score says nothing). With the reranker on, that score is its relevance from 0 to 1, which
        the app's `WeakMatchBelow` setting is chosen from; with it off, it is a fused rank score that says little
        about relevance. MRR uses the final order of the hits, which the lost-in-the-middle
        reordering changes, so read it as a rough signal only.

        Recorded on the default settings of the app: 400-token chunks, 6 passages, reranker on. The regression
        check compares the *All* column of that row.

        {table}
        {missed}
        The corpus is fictional. Replace it with your own documents, add your own questions to `eval/golden.json`
        and `eval/hard.json`, and record a new baseline before you change the defaults.
        """;
}

internal static class Kinds
{
    public const string Basic = "basic";
    public const string Unanswerable = "unanswerable";

    public static readonly string[] Answerable = [Basic, "paraphrase", "stale", "long-doc", "format", "multi-hop", "follow-up", "aggregation"];

    public static readonly string[] All = [.. Answerable, Unanswerable];
}

internal sealed record GoldenQuestion(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("question")] string QuestionText,
    [property: JsonPropertyName("file")] string File,
    [property: JsonPropertyName("phrase")] string Phrase);

/// <summary>
/// One question of either set. An unanswerable question has no evidence. <see cref="Query"/> is a
/// follow-up's standalone rewrite, which retrieval searches instead of the question.
/// </summary>
internal sealed record EvalCase(string Id, string Kind, List<string>? History, string Question, List<Evidence> Evidence, string? Query = null);

/// <summary>A follow-up's standalone rewrite, kept so a rerun searches the same text.</summary>
internal sealed record Rewrite(List<string> History, string Question, string Query);

/// <summary>The further searches chat's bridge step planned for a query, kept so a rerun searches the same text.</summary>
internal sealed record Bridge(string Query, List<string> Searches);

/// <summary>
/// The further searches for each query, as chat's bridge step plans them from the first passages. They are kept in
/// <c>results/bridges.json</c> by query, planned from the passages of the first setting that runs (the default), so
/// every setting and every rerun searches the same text and needs no API key. A query with no kept plan and no chat
/// model gets no further search.
/// </summary>
internal sealed class BridgePlans(string path, Microsoft.Extensions.AI.IChatClient? chat, JsonSerializerOptions json)
{
    private readonly Dictionary<string, List<string>> _kept = File.Exists(path)
        ? (JsonSerializer.Deserialize<List<Bridge>>(File.ReadAllText(path), json) ?? []).ToDictionary(b => b.Query, b => b.Searches)
        : [];

    private readonly Dictionary<string, List<string>> _used = [];
    private readonly HashSet<string> _unplanned = [];

    public async Task<IReadOnlyList<string>> ForAsync(string query, IReadOnlyList<Rag.NET.Models.SearchResult> found)
    {
        if (_used.TryGetValue(query, out var searches) || _kept.TryGetValue(query, out searches))
        {
            return _used[query] = searches;
        }

        if (chat is null)
        {
            _unplanned.Add(query);
            return [];
        }

        var reply = await chat.GetResponseAsync(BridgeQuestion.Prompt(query, found));
        return _used[query] = [.. BridgeQuestion.Parse(reply.Text, query)];
    }

    /// <summary>Writes the plans this run used, and says how many queries had one.</summary>
    public string Save()
    {
        if (_used.Count > 0)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(_used.Select(u => new Bridge(u.Key, u.Value)).OrderBy(b => b.Query, StringComparer.Ordinal).ToList(), json));
        }

        return _unplanned.Count == 0
            ? $"Bridge searches: {_used.Count(u => u.Value.Count > 0)} of {_used.Count} queries searched further ({path})."
            : $"Bridge searches: {_unplanned.Count} queries had no kept plan and no DEEPSEEK_API_KEY, so they searched only once.";
    }
}

/// <summary>A passage a full answer needs: from <see cref="File"/>, containing every one of <see cref="Terms"/>.</summary>
internal sealed record Evidence(string File, List<string> Terms);

internal sealed record Config(int Chunk, int TopK, bool Rerank)
{
    public string Label => $"c{Chunk}-k{TopK}-{(Rerank ? "rr" : "norr")}";
}

internal sealed record Outcome(string Id, string Kind, int Rank, int Found, int Of, double TopScore);

internal sealed record ConfigResult(
    Config Config,
    double HitRate,
    double Mrr,
    double Recall,
    double Abstain,
    Dictionary<string, double> ByKind,
    List<Outcome> Outcomes);

internal sealed record Baseline(string RecordedUtc, int Questions, List<ConfigResult> Results);
