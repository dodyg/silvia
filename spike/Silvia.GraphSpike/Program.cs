// GraphRAG spike: what Rag.NET.GraphRag costs on the eval corpus, whether its answers beat plain
// retrieval on questions that join facts across files, and what happens to the graph when a file
// changes or is deleted. Sends the (fictional) corpus to the configured DeepSeek model.
using System.ClientModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using OpenAI;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Embeddings.Onnx;
using Rag.NET.Graph;
using Rag.NET.GraphRag;
using Rag.NET.GraphRag.LocalSearch;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Reranking.Onnx;
using Rag.NET.Retrieval.Behaviors;
using Silvia.Core.Projects;
using Silvia.Core.Secrets;
using Silvia.Storage.Sqlite;

var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
var corpusDir = Path.Combine(repo, "eval", "corpus");
var questionsPath = Path.Combine(repo, "eval", "graph-golden.json");
var modelsDir = Environment.GetEnvironmentVariable("SILVIA_MODELS") ?? Path.Combine(repo, "models");
// --reuse answers from the graph and stores an earlier run left in the work folder, skipping the
// ingest, which is the slow and costly half.
var workArg = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
var workDir = Path.GetFullPath(workArg ?? Path.Combine(Path.GetTempPath(), "silvia-graph-spike"));
var ingestedMarker = Path.Combine(workDir, "ingested.ok");
var reuse = args.Contains("--reuse") && File.Exists(ingestedMarker);
var chatModel = Environment.GetEnvironmentVariable("SILVIA_CHAT_MODEL") ?? new ProjectSettings().ChatModel;
var dataRoot = Environment.GetEnvironmentVariable("SILVIA_DATA")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Silvia");
var apiKey = SecretStore.InDataFolder(dataRoot).Get(SecretStore.DeepSeekKey) ?? Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.WriteLine("No DeepSeek key in the Silvia secret store or DEEPSEEK_API_KEY.");
    return 1;
}

if (!reuse && Directory.Exists(workDir))
{
    Directory.Delete(workDir, recursive: true);
}

var docsDir = Directory.CreateDirectory(Path.Combine(workDir, "corpus")).FullName;
if (!reuse)
{
    foreach (var file in Directory.EnumerateFiles(corpusDir, "*.md"))
    {
        File.Copy(file, Path.Combine(docsDir, Path.GetFileName(file)));
    }
}

var settings = new ProjectSettings();
var graphPath = Path.Combine(workDir, "graph.db");
var storePath = Path.Combine(workDir, "project.db");
var endpoint = new Uri(Environment.GetEnvironmentVariable("DEEPSEEK_BASE_URL") ?? "https://api.deepseek.com/v1");
var baseChat = new OpenAIClient(new ApiKeyCredential(apiKey), new OpenAIClientOptions { Endpoint = endpoint })
    .GetChatClient(chatModel).AsIChatClient();
var meter = new CallMeter();
var extractChat = meter.Wrap(baseChat, "extract");
var reportChat = meter.Wrap(baseChat, "report");
var globalChat = meter.Wrap(baseChat, "global");
var answerChat = meter.Wrap(baseChat, "answer");

// One document store and one graph-chunk store, shared by both containers: the first ingests and
// answers with plain retrieval and local search, the second adds global search to retrieval. Both are
// the app's SQLite store, so a later --reuse run finds them.
using var documents = new SqliteVectorStore(storePath, settings.EmbedDims, settings.EmbedModel);
using var graphChunks = new SqliteVectorStore(storePath, settings.EmbedDims, settings.EmbedModel, "graph_chunks");

ServiceProvider Build(bool global)
{
    var services = new ServiceCollection();

    services.AddSingleton<IVectorStore>(documents);
    services.AddSingleton(answerChat);
    services.AddRagNet(
        configure: rag =>
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
            // The app's chunking (ProjectRuntime): extraction runs once per chunk, so this sets the call count.
            rag.UseChunkingStrategy<Rag.NET.Chunking.RecursiveChunkingStrategy>(c =>
            {
                c.MaxChunkSize = settings.ChunkTokens * 4;
                c.Overlap = settings.ChunkOverlap * 4;
            });
            rag.UseSqlitePersistence(storePath, collectionName: "silvia-project");
            rag.UseGraphRag(
                configure: o =>
                {
                    o.GleaningPasses = 1;
                    // The library default (0.10) re-detects communities and rewrites every report whenever
                    // the entity count grows 10%, which on a small corpus is nearly every file. A bulk load
                    // skips that and rebuilds once at the end; SPIKE_GROWTH=0.1 restores the default.
                    o.CommunityDetectionGrowthThreshold = double.Parse(Environment.GetEnvironmentVariable("SPIKE_GROWTH") ?? "100");
                    o.ExtractionChatClient = extractChat;
                    o.SummarizationChatClient = reportChat;
                },
                retrieval: o => o.GlobalChatClient = globalChat,
                graph: g => g.UseSqlite(graphPath),
                chunks: c => c.Use(graphChunks));
        },
        retrieval: global ? p => p.Add<GraphGlobalSearchBehavior>(before: typeof(RerankingBehavior)) : null);
    return services.BuildServiceProvider();
}

await using var main = Build(global: false);
await using var withGlobal = Build(global: true);
var pipeline = main.GetRequiredService<IRagPipeline>();
var globalPipeline = withGlobal.GetRequiredService<IRagPipeline>();
var local = main.GetRequiredService<IGraphRagSearch>();
var graph = main.GetRequiredService<IGraphStore>();
var rebuilder = main.GetRequiredService<GraphProjectionRebuilder>();

// 1. Ingest
var clock = Stopwatch.StartNew();
var communities = 0;
if (!reuse)
{
Console.WriteLine($"## Ingest ({chatModel}, {Directory.GetFiles(docsDir).Length} files, {Directory.GetFiles(docsDir).Sum(f => new FileInfo(f).Length):N0} bytes)\n");
clock.Restart();
foreach (var file in Directory.EnumerateFiles(docsDir, "*.md").Order())
{
    await IngestAsync(file);
}

Console.WriteLine($"ingest: {clock.Elapsed.TotalSeconds:F1} s, {meter.Summary()}");
meter.Reset();
clock.Restart();
communities = await rebuilder.RebuildAsync(CancellationToken.None);
Console.WriteLine($"rebuild: {clock.Elapsed.TotalSeconds:F1} s, {meter.Summary()}");
meter.Reset();
File.WriteAllText(ingestedMarker, DateTime.Now.ToString("O"));
}
else
{
    Console.WriteLine($"## Reusing the graph and stores in {workDir}");
}

var snapshot = await graph.GetFullGraphAsync(CancellationToken.None);
Console.WriteLine($"graph: {snapshot.Entities.Count} entities, {snapshot.Relationships.Count} relationships, {snapshot.Communities.Count} communities (rebuild returned {communities})");
Console.WriteLine("top entities: " + string.Join(", ", snapshot.Entities.OrderByDescending(e => e.PageRankScore).Take(15).Select(e => $"{e.Name} ({e.Type})")));

// 2. Answers
var questions = JsonSerializer.Deserialize<List<GoldenQuestion>>(File.ReadAllText(questionsPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
var totals = new Dictionary<string, (int Hit, int Of)>();
Console.WriteLine("\n## Answers (facts found / facts expected)\n");
foreach (var q in questions)
{
    Console.WriteLine($"### {q.Id} [{q.Kind}] {q.Question}");
    var dense = await DenseAsync(q.Question);
    Score("dense", q, dense.Answer, $"files in top {settings.TopK}: {string.Join(", ", dense.Files)}");
    var localAnswer = await local.LocalSearchAsync(q.Question, CancellationToken.None);
    var ctx = localAnswer.Context;
    Score("local", q, localAnswer.Answer,
        $"context {ctx.TokenCount} tokens: reports {ctx.Reports.Rendered}, entities {ctx.Entities.Rendered}, relationships {ctx.Relationships.Rendered}, sources {ctx.Sources.Rendered}");
    var globalAnswer = await GlobalAsync(q.Question);
    Score("global", q, globalAnswer, string.Empty);
    Console.WriteLine();
}

Console.WriteLine("totals: " + string.Join(", ", totals.Select(t => $"{t.Key} {t.Value.Hit}/{t.Value.Of}")));
Console.WriteLine($"answer calls: {meter.Summary()}");
meter.Reset();

// 3. Delete a file whose people also appear elsewhere
Console.WriteLine("\n## Delete meeting-2027-02.md\n");
var meeting = Path.Combine(docsDir, "meeting-2027-02.md");
await DescribeAsync(["Tomas Berg", "Dana Okafor", "Rua da Prata", "Ines Carvalho"]);
await pipeline.DeleteAsync(DocId(meeting), CancellationToken.None);
File.Delete(meeting);
Console.WriteLine("-- after delete");
await DescribeAsync(["Tomas Berg", "Dana Okafor", "Rua da Prata", "Ines Carvalho"]);
await ProbeAsync("Where is the Lisbon office moving?", "Rua da Prata");
await ProbeAsync("Who will rotate the production database keys?", "database keys");
Console.WriteLine($"delete calls: {meter.Summary()}");
meter.Reset();

// 4. Change a file: the security lead is now someone else
Console.WriteLine("\n## Edit security-policy.md: the security lead becomes Mira Holm\n");
var security = Path.Combine(docsDir, "security-policy.md");
File.WriteAllText(security, File.ReadAllText(security).Replace("Tomas Berg", "Mira Holm"));
clock.Restart();
await IngestAsync(security);
Console.WriteLine($"re-ingest: {clock.Elapsed.TotalSeconds:F1} s, {meter.Summary()}");
meter.Reset();
await DescribeAsync(["Tomas Berg", "Mira Holm"]);
await ProbeAsync("Who is the security lead?", "Mira Holm");
Console.WriteLine($"probe calls: {meter.Summary()}");
return 0;

async Task IngestAsync(string path)
{
    await using var stream = File.OpenRead(path);
    var result = await pipeline.IngestAsync(stream, new DocumentMetadata
    {
        DocumentId = new DocumentId(DocId(path)),
        FileName = Path.GetFileName(path),
        ContentType = "text/markdown",
    }, new IngestionOptions { Overwrite = true });
    Console.WriteLine($"  {Path.GetFileName(path)}: {(result.IsSuccess ? "ok" : result.Error.ToString())}");
}

static string DocId(string path) => Path.GetFullPath(path);

// The app's answer path: reranked top-K passages, numbered, answered with the project's system prompt.
async Task<(string Answer, IReadOnlyList<string> Files)> DenseAsync(string question)
{
    var retrieved = await pipeline.RetrieveAsync(question, new RetrievalOptions
    {
        TopK = settings.TopK,
        UseReranking = true,
        CandidateCount = settings.RerankCandidates,
    });
    var hits = retrieved.Value;
    var context = new StringBuilder();
    for (var i = 0; i < hits.Count; i++)
    {
        context.AppendLine($"[{i + 1}] {FileOf(hits[i].Chunk)}\n{hits[i].Chunk.Text}\n");
    }

    var reply = await answerChat.GetResponseAsync(
    [
        new ChatMessage(ChatRole.System, settings.SystemPrompt),
        new ChatMessage(ChatRole.User, $"Context:\n{context}\nQuestion: {question}"),
    ]);
    return (reply.Text, hits.Select(h => FileOf(h.Chunk)).Distinct().ToList());
}

async Task<string> GlobalAsync(string question)
{
    var retrieved = await globalPipeline.RetrieveAsync(question, new RetrievalOptions { TopK = settings.TopK, UseReranking = true });
    return retrieved.IsSuccess && retrieved.Value.Count > 0 ? retrieved.Value[0].Chunk.Text : $"(failed: {retrieved.Error})";
}

void Score(string arm, GoldenQuestion q, string answer, string note)
{
    var found = q.Facts.Count(f => f.Terms.All(t => answer.Contains(t, StringComparison.OrdinalIgnoreCase)));
    var (hit, of) = totals.GetValueOrDefault(arm);
    totals[arm] = (hit + found, of + q.Facts.Count);
    Console.WriteLine($"- {arm}: {found}/{q.Facts.Count} {note}");
    Console.WriteLine($"  > {Flatten(answer, 400)}");
}

async Task DescribeAsync(string[] names)
{
    var found = await graph.GetEntitiesAsync(names, CancellationToken.None);
    var relationships = await graph.GetRelationshipsAsync(names, CancellationToken.None);
    foreach (var name in names)
    {
        var e = found.FirstOrDefault(x => GraphNames.Comparer.Equals(x.Name, name));
        var rels = relationships.Count(r => GraphNames.Comparer.Equals(r.SourceEntity, name) || GraphNames.Comparer.Equals(r.TargetEntity, name));
        Console.WriteLine(e is null
            ? $"  {name}: not in graph"
            : $"  {name}: from {Path.GetFileName(e.SourceDocumentId ?? "?")}, {rels} relationships, \"{Flatten(e.Description, 160)}\"");
    }
}

async Task ProbeAsync(string question, string term)
{
    var context = await local.BuildLocalContextAsync(question, CancellationToken.None);
    var answer = await local.LocalSearchAsync(question, CancellationToken.None);
    var dense = await DenseAsync(question);
    Console.WriteLine($"  probe \"{question}\": local context mentions '{term}': {context.Text.Contains(term, StringComparison.OrdinalIgnoreCase)}");
    Console.WriteLine($"    local > {Flatten(answer.Answer, 240)}");
    Console.WriteLine($"    dense > {Flatten(dense.Answer, 240)}");
}

// A passage's file, or what kind of graph chunk it is when one reached the document store.
static string FileOf(TextChunk chunk) =>
    chunk.Metadata.TryGetValue("file_name", out var name) ? name.ToString()
    : "graph:" + (chunk.Metadata.TryGetValue("graph_type", out var kind) ? kind.ToString() : string.Join("/", chunk.Metadata.Keys));

static string Flatten(string text, int max)
{
    var flat = text.ReplaceLineEndings(" ");
    return flat.Length <= max ? flat : flat[..max] + "…";
}

record GoldenFact(string File, string[] Terms);

record GoldenQuestion(string Id, string Kind, string Question, List<GoldenFact> Facts);

/// <summary>Counts chat calls and tokens per purpose, so each phase's cost can be read off.</summary>
sealed class CallMeter
{
    private readonly Dictionary<string, (int Calls, long In, long Out, TimeSpan Time)> _byPurpose = new();
    private readonly Lock _gate = new();

    public IChatClient Wrap(IChatClient inner, string purpose) => new Counting(inner, purpose, this);

    public void Record(string purpose, ChatResponse response, TimeSpan elapsed)
    {
        lock (_gate)
        {
            var (calls, input, output, time) = _byPurpose.GetValueOrDefault(purpose);
            _byPurpose[purpose] = (calls + 1, input + (response.Usage?.InputTokenCount ?? 0), output + (response.Usage?.OutputTokenCount ?? 0), time + elapsed);
        }
    }

    public string Summary()
    {
        lock (_gate)
        {
            return _byPurpose.Count == 0
                ? "0 chat calls"
                : string.Join("; ", _byPurpose.Select(p => $"{p.Key} {p.Value.Calls} calls, {p.Value.In:N0} in / {p.Value.Out:N0} out tokens, {p.Value.Time.TotalSeconds:F0} s"));
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _byPurpose.Clear();
        }
    }

    private sealed class Counting(IChatClient inner, string purpose, CallMeter meter) : DelegatingChatClient(inner)
    {
        public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var clock = Stopwatch.StartNew();
            var response = await base.GetResponseAsync(messages, options, cancellationToken);
            meter.Record(purpose, response, clock.Elapsed);
            Console.Error.WriteLine($"    [{DateTime.Now:HH:mm:ss}] {purpose} call: {clock.Elapsed.TotalSeconds:F1} s, {response.Usage?.InputTokenCount} in / {response.Usage?.OutputTokenCount} out");
            return response;
        }
    }
}
