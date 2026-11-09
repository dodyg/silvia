using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Silvia.Core.Conversations;
using Silvia.Core.Ingestion;
using Silvia.Storage.Sqlite;

namespace Silvia.Core.Runtime;

// Read-only views of what a project has indexed and how retrieval picks from it. Nothing here calls a
// cloud model or changes the index, so every view is safe to open while the queue is working.
public sealed partial class ProjectRuntime
{
    /// <summary>Which index answers searches: <c>vector</c> (hybrid, with an embedder) or <c>keyword</c> (BM25 only).</summary>
    public string RetrievalMode => _keywords is null ? RetrievalModes.Vector : RetrievalModes.Keyword;

    /// <summary>A document's passages exactly as the index holds them, or null if it has none.</summary>
    public DocumentView? Document(string documentId)
    {
        using var connection = SqliteStorage.Open(_databasePath);
        if (!HasKeywordTable(connection))
        {
            return null;
        }

        using var command = connection.CreateCommand();
        if (_keywords is not null)
        {
            command.CommandText = "SELECT chunk_index, chunk_text, metadata_json FROM bm25_docs WHERE document_id = $id ORDER BY chunk_index";
        }
        else
        {
            command.CommandText = "SELECT chunk_index, text, metadata FROM vec_chunks WHERE document_id = $id AND model_id = $model ORDER BY chunk_index";
            command.Parameters.AddWithValue("$model", Settings.EmbedModel);
        }

        command.Parameters.AddWithValue("$id", documentId);
        var passages = new List<PassageView>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                passages.Add(new PassageView((int)reader.GetInt64(0), reader.GetString(1), ReadMetadata(reader.GetString(2))));
            }
        }

        return passages.Count == 0
            ? null
            : new DocumentView(documentId, Overview(documentId), IndexedTimes(connection).GetValueOrDefault(documentId), passages);
    }

    /// <summary>
    /// Runs a search the way <see cref="SearchAsync"/> does and explains the result. With an embedder, each
    /// hit also gets its cosine similarity to the query and its rank among the nearest vectors alone; a hit
    /// with no vector rank came in through the keyword side of hybrid search. Close calls are near vectors
    /// that did not make the final list.
    /// </summary>
    public async Task<RetrievalTrace> TraceAsync(string query, int? topK = null, CancellationToken cancellationToken = default)
    {
        var count = topK ?? Settings.TopK;
        var hits = await SearchAsync(query, count, cancellationToken);
        if (_keywords is not null)
        {
            return new RetrievalTrace(RetrievalModes.Keyword, false, hits.Select((h, i) => Traced(h, i + 1, null, null)).ToList(), []);
        }

        var embedded = await _embedder.Value.GenerateAsync([query], cancellationToken: cancellationToken);
        var vector = embedded[0].Vector;
        var nearest = await _vectors.SearchAsync(
            vector,
            new SearchOptions { TopK = Math.Max(Settings.RerankCandidates, count * 3) },
            cancellationToken);
        var ranks = new Dictionary<(string, int), int>();
        for (var i = 0; i < nearest.Count; i++)
        {
            ranks.TryAdd(Key(nearest[i]), i + 1);
        }

        var normalized = Normalized(vector.Span);
        using var connection = SqliteStorage.Open(_databasePath);
        var traced = hits
            .Select((h, i) => Traced(h, i + 1, StoredSimilarity(connection, Key(h), normalized), ranks.TryGetValue(Key(h), out var rank) ? rank : null))
            .ToList();
        var chosen = traced.Select(t => (t.DocumentId, t.ChunkIndex)).ToHashSet();
        var closeCalls = nearest
            .Select((n, i) => (Hit: n, Rank: i + 1))
            .Where(n => !chosen.Contains(Key(n.Hit)))
            .Take(MaxCloseCalls)
            .Select(n => Traced(n.Hit, 0, n.Hit.Score, n.Rank))
            .ToList();
        return new RetrievalTrace(RetrievalModes.Vector, CanRerank, traced, closeCalls);
    }

    private const int MaxCloseCalls = 5;

    /// <summary>
    /// Every passage's embedding projected onto two dimensions, or null when the project has no vectors to
    /// project. Above <paramref name="maxPoints"/> passages, an even sample across the documents is drawn.
    /// </summary>
    public EmbeddingMap? Map(int maxPoints = 3000)
    {
        if (_keywords is not null)
        {
            return null;
        }

        using var connection = SqliteStorage.Open(_databasePath);
        using var counter = connection.CreateCommand();
        counter.CommandText = "SELECT COUNT(*) FROM vec_chunks WHERE model_id = $model";
        counter.Parameters.AddWithValue("$model", Settings.EmbedModel);
        var total = (int)(long)counter.ExecuteScalar()!;
        var stride = Math.Max(1, (int)Math.Ceiling(total / (double)maxPoints));

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT document_id, chunk_index, substr(text, 1, $preview), embedding FROM vec_chunks
            WHERE model_id = $model ORDER BY document_id, chunk_index
            """;
        command.Parameters.AddWithValue("$model", Settings.EmbedModel);
        command.Parameters.AddWithValue("$preview", PreviewCharacters);
        var keys = new List<(string DocumentId, int ChunkIndex, string Preview)>();
        var vectors = new List<float[]>();
        using (var reader = command.ExecuteReader())
        {
            for (var row = 0; reader.Read(); row++)
            {
                if (row % stride != 0)
                {
                    continue;
                }

                keys.Add((reader.GetString(0), (int)reader.GetInt64(1), reader.GetString(2)));
                vectors.Add(MemoryMarshal.Cast<byte, float>((byte[])reader.GetValue(3)).ToArray());
            }
        }

        var (x, y, firstShare, secondShare) = Projection.Principal(vectors);
        var points = keys.Select((k, i) => new MapPoint(k.DocumentId, k.ChunkIndex, x[i], y[i], k.Preview)).ToList();
        return new EmbeddingMap(total, points, firstShare, secondShare);
    }

    /// <summary>
    /// The knowledge graph laid out for drawing, or null when the project does not build one. Above
    /// <paramref name="maxEntities"/> entities, the highest ranked are drawn with the relationships between them.
    /// </summary>
    public async Task<KnowledgeGraph?> KnowledgeGraphAsync(int maxEntities = 300, CancellationToken cancellationToken = default)
    {
        if (_graph is null)
        {
            return null;
        }

        var snapshot = await _graph.SnapshotAsync(cancellationToken);
        var names = Rag.NET.Graph.GraphNames.Comparer;
        var degree = new Dictionary<string, int>(names);
        foreach (var relationship in snapshot.Relationships)
        {
            degree[relationship.SourceEntity] = degree.GetValueOrDefault(relationship.SourceEntity) + 1;
            degree[relationship.TargetEntity] = degree.GetValueOrDefault(relationship.TargetEntity) + 1;
        }

        var entities = snapshot.Entities
            .OrderByDescending(e => e.PageRankScore)
            .ThenByDescending(e => degree.GetValueOrDefault(e.Name))
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .DistinctBy(e => e.Name, names)
            .Take(maxEntities)
            .ToList();
        var index = new Dictionary<string, int>(names);
        for (var i = 0; i < entities.Count; i++)
        {
            index[entities[i].Name] = i;
        }

        var relationships = snapshot.Relationships
            .Where(r => index.ContainsKey(r.SourceEntity) && index.ContainsKey(r.TargetEntity))
            .ToList();
        var edges = relationships
            .Select(r => new GraphEdge(index[r.SourceEntity], index[r.TargetEntity], r.Description ?? string.Empty, r.Weight))
            .ToList();

        // An entity's documents: the one it was extracted from, and those of the relationships it is part of.
        var documents = entities.Select(_ => new SortedSet<string>(StringComparer.OrdinalIgnoreCase)).ToList();
        for (var i = 0; i < entities.Count; i++)
        {
            if (entities[i].SourceDocumentId is { Length: > 0 } source)
            {
                documents[i].Add(source);
            }
        }

        foreach (var relationship in relationships)
        {
            if (relationship.SourceDocumentId is { Length: > 0 } source)
            {
                documents[index[relationship.SourceEntity]].Add(source);
                documents[index[relationship.TargetEntity]].Add(source);
            }
        }

        // Only communities with a member on the drawing; the broadest level first, so an entity's first
        // community is the widest group it belongs to.
        var communities = snapshot.Communities
            .Where(c => c.MemberEntities.Any(index.ContainsKey))
            .OrderBy(c => c.Level)
            .ThenByDescending(c => c.MemberEntities.Count)
            .ThenBy(c => c.Id)
            .ToList();
        var memberships = entities.Select(_ => new List<int>()).ToList();
        for (var c = 0; c < communities.Count; c++)
        {
            foreach (var member in communities[c].MemberEntities.Where(index.ContainsKey).Distinct(names))
            {
                memberships[index[member]].Add(c);
            }
        }

        var (x, y) = GraphLayout.Arrange(entities.Count, edges.Select(e => (e.Source, e.Target)).ToList());
        var nodes = entities
            .Select((e, i) => new GraphNode(
                e.Name,
                e.Type ?? string.Empty,
                e.Description ?? string.Empty,
                e.PageRankScore,
                degree.GetValueOrDefault(e.Name),
                documents[i].ToList(),
                memberships[i],
                x[i],
                y[i]))
            .ToList();
        return new KnowledgeGraph(
            snapshot.Entities.Count,
            snapshot.Relationships.Count,
            nodes,
            edges,
            communities.Select(c => new GraphCommunity(c.Id, c.Level, c.ReportSummary ?? string.Empty, c.MemberEntities.Count)).ToList());
    }

    private const int PreviewCharacters = 200;

    /// <summary>Each indexed document's size in passages and characters, whether it has an overview, and when it was last indexed.</summary>
    public IReadOnlyList<DocumentCoverage> Coverage()
    {
        using var connection = SqliteStorage.Open(_databasePath);
        if (!HasKeywordTable(connection))
        {
            return [];
        }

        using var command = connection.CreateCommand();
        if (_keywords is not null)
        {
            command.CommandText = "SELECT document_id, COUNT(*), SUM(length(chunk_text)) FROM bm25_docs GROUP BY document_id ORDER BY document_id";
        }
        else
        {
            command.CommandText = """
                SELECT document_id, COUNT(*), SUM(length(text)) FROM vec_chunks
                WHERE model_id = $model GROUP BY document_id ORDER BY document_id
                """;
            command.Parameters.AddWithValue("$model", Settings.EmbedModel);
        }

        var rows = new List<(string DocumentId, int Passages, long Characters)>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                rows.Add((reader.GetString(0), (int)reader.GetInt64(1), reader.IsDBNull(2) ? 0 : reader.GetInt64(2)));
            }
        }

        var overviews = DocumentOverviews.Read(_databasePath, rows.Select(r => r.DocumentId));
        var indexed = IndexedTimes(connection);
        return rows
            .Select(r => new DocumentCoverage(r.DocumentId, r.Passages, r.Characters, overviews.ContainsKey(r.DocumentId), indexed.GetValueOrDefault(r.DocumentId)))
            .ToList();
    }

    /// <summary>
    /// When each document was last indexed: the later of its last finished upsert job and, with an embedder,
    /// the time its vectors were written. A folder scan can index a file without a job of its own, so either
    /// source alone misses some documents.
    /// </summary>
    private Dictionary<string, string> IndexedTimes(SqliteConnection connection)
    {
        var times = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Collect(string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(1))
                {
                    continue;
                }

                var path = reader.GetString(0);
                var when = reader.GetString(1);
                if (!times.TryGetValue(path, out var known) || Later(when, known))
                {
                    times[path] = when;
                }
            }
        }

        Collect("SELECT path, MAX(finished_utc) FROM ingest_jobs WHERE status = 'done' AND kind = 'upsert' GROUP BY path");
        if (_keywords is null && TableExists(connection, "embedding_versions"))
        {
            Collect("SELECT doc_id, embedded_at FROM embedding_versions");
        }

        return times;
    }

    private static bool Later(string candidate, string known) =>
        DateTimeOffset.TryParse(candidate, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var a)
        && (!DateTimeOffset.TryParse(known, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var b) || a > b);

    /// <summary>False only for a keyword project that has indexed nothing yet: its BM25 table is created on first use.</summary>
    private bool HasKeywordTable(SqliteConnection connection) => _keywords is null || TableExists(connection, "bm25_docs");

    private static bool TableExists(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name";
        command.Parameters.AddWithValue("$name", table);
        return command.ExecuteScalar() is not null;
    }

    /// <summary>The cosine similarity between a stored passage vector and the normalized query, or null if the passage has none.</summary>
    private double? StoredSimilarity(SqliteConnection connection, (string DocumentId, int ChunkIndex) key, float[] query)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT embedding FROM vec_chunks WHERE document_id = $id AND chunk_index = $index AND model_id = $model";
        command.Parameters.AddWithValue("$id", key.DocumentId);
        command.Parameters.AddWithValue("$index", key.ChunkIndex);
        command.Parameters.AddWithValue("$model", Settings.EmbedModel);
        if (command.ExecuteScalar() is not byte[] blob)
        {
            return null;
        }

        // Stored vectors are normalized on write, so the dot product is the cosine.
        var stored = MemoryMarshal.Cast<byte, float>(blob);
        if (stored.Length != query.Length)
        {
            return null;
        }

        var sum = 0.0;
        for (var d = 0; d < stored.Length; d++)
        {
            sum += stored[d] * query[d];
        }

        return sum;
    }

    private static float[] Normalized(ReadOnlySpan<float> vector)
    {
        var length = 0.0;
        foreach (var value in vector)
        {
            length += value * value;
        }

        length = Math.Sqrt(length);
        var result = new float[vector.Length];
        for (var d = 0; d < vector.Length; d++)
        {
            result[d] = length > 0 ? (float)(vector[d] / length) : 0;
        }

        return result;
    }

    private static (string, int) Key(SearchResult result) => (result.Chunk.DocumentId.Value, result.Chunk.ChunkIndex);

    private static TracedHit Traced(SearchResult result, int position, double? similarity, int? vectorRank)
    {
        var cited = CitedChunk.From(result);
        return new TracedHit(cited.DocumentId, cited.ChunkIndex, cited.FileName, cited.Text, result.Score, position, similarity, vectorRank);
    }

    /// <summary>
    /// Flattens stored metadata to text. The vector table keeps each value as <c>{"k": kind, "v": value}</c>
    /// and the keyword table as the plain value; both read the same here.
    /// </summary>
    private static IReadOnlyDictionary<string, string> ReadMetadata(string json)
    {
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return values;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                var value = property.Value.ValueKind == JsonValueKind.Object && property.Value.TryGetProperty("v", out var inner)
                    ? inner
                    : property.Value;
                values[property.Name] = value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
            }
        }
        catch (JsonException)
        {
            // Unreadable metadata shows as none rather than failing the whole document.
        }

        return values;
    }
}

public static class RetrievalModes
{
    public const string Vector = "vector";
    public const string Keyword = "keyword";
}

/// <summary>One passage as the index holds it, with its metadata flattened to text.</summary>
public sealed record PassageView(int ChunkIndex, string Text, IReadOnlyDictionary<string, string> Metadata);

/// <summary>A document's passages in order, its overview if one was written, and when it was last indexed.</summary>
public sealed record DocumentView(string DocumentId, string? Overview, string? IndexedUtc, IReadOnlyList<PassageView> Passages);

/// <summary>
/// A retrieved passage and why it ranked where it did. <see cref="Position"/> is its place in the final
/// list (zero for a close call); <see cref="Similarity"/> and <see cref="VectorRank"/> are null without an embedder.
/// </summary>
public sealed record TracedHit(
    string DocumentId,
    int ChunkIndex,
    string FileName,
    string Text,
    double Score,
    int Position,
    double? Similarity,
    int? VectorRank);

/// <summary>How a search was answered: the mode, whether a reranker reordered it, the hits, and the near vectors left out.</summary>
public sealed record RetrievalTrace(string Mode, bool Reranked, IReadOnlyList<TracedHit> Hits, IReadOnlyList<TracedHit> CloseCalls);

public sealed record MapPoint(string DocumentId, int ChunkIndex, float X, float Y, string Preview);

/// <summary>
/// Passage embeddings on their first two principal components. <see cref="Total"/> counts every passage,
/// which can exceed the points drawn; the shares say how much of the spread each axis shows.
/// </summary>
public sealed record EmbeddingMap(int Total, IReadOnlyList<MapPoint> Points, double FirstShare, double SecondShare);

/// <summary>
/// One entity of the knowledge graph on the drawing: its PageRank, how many relationships it has in the
/// whole graph, the documents it comes from, and the communities it belongs to (indexes into
/// <see cref="KnowledgeGraph.Communities"/>, broadest first).
/// </summary>
public sealed record GraphNode(
    string Name,
    string Type,
    string Description,
    double Rank,
    int Degree,
    IReadOnlyList<string> Documents,
    IReadOnlyList<int> Communities,
    float X,
    float Y);

/// <summary>A relationship between two entities on the drawing, by their indexes in <see cref="KnowledgeGraph.Nodes"/>.</summary>
public sealed record GraphEdge(int Source, int Target, string Description, double Weight);

/// <summary>A community of related entities and the report the model wrote about it; members counts all of them.</summary>
public sealed record GraphCommunity(int Id, int Level, string Report, int Members);

/// <summary>
/// The knowledge graph laid out for drawing. The totals count the whole graph, which can hold more than
/// the drawing shows.
/// </summary>
public sealed record KnowledgeGraph(
    int TotalEntities,
    int TotalRelationships,
    IReadOnlyList<GraphNode> Nodes,
    IReadOnlyList<GraphEdge> Edges,
    IReadOnlyList<GraphCommunity> Communities);

public sealed record DocumentCoverage(string DocumentId, int Passages, long Characters, bool HasOverview, string? IndexedUtc);
