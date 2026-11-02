using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Silvia.Storage.Sqlite.Tests;

public sealed class SqliteVectorStoreTests
{
    private const string Model = "test-model";

    private static EmbeddedChunk MakeChunk(
        string docId, int chunkIndex, float[] embedding, IDictionary<string, MetadataValue>? metadata = null) => new()
        {
            Chunk = new TextChunk
            {
                Text = $"{docId}-{chunkIndex}",
                DocumentId = new DocumentId(docId),
                ChunkIndex = chunkIndex,
                Metadata = metadata ?? new Dictionary<string, MetadataValue>(StringComparer.Ordinal),
            },
            Embedding = embedding,
        };

    private static Dictionary<string, MetadataValue> Meta(params (string Key, MetadataValue Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

    [Fact]
    public async Task StoreAndSearch_RanksByCosineSimilarity()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = new TempDatabase();
        using var store = new SqliteVectorStore(db.Path, dimensions: 2, Model);

        await store.StoreAsync(
        [
            MakeChunk("doc-x", 0, [1f, 0f]),
            MakeChunk("doc-y", 0, [0f, 1f]),
            MakeChunk("doc-diag", 0, [1f, 1f]),
        ], ct);

        var results = await store.SearchAsync(new float[] { 1f, 0f }, new SearchOptions { TopK = 2 }, ct);

        Assert.Equal(2, results.Count);
        Assert.Equal("doc-x", results[0].Chunk.DocumentId.Value);
        Assert.Equal(1.0, results[0].Score, precision: 5);
        Assert.Equal("doc-diag", results[1].Chunk.DocumentId.Value);
    }

    [Fact]
    public async Task Search_MinScoreAndMetadataFilter_Respected()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = new TempDatabase();
        using var store = new SqliteVectorStore(db.Path, dimensions: 2, Model);

        await store.StoreAsync(
        [
            MakeChunk("doc-eng", 0, [1f, 0f], Meta(("department", "engineering"))),
            MakeChunk("doc-mkt", 0, [1f, 0f], Meta(("department", "marketing"))),
            MakeChunk("doc-far", 0, [0f, 1f], Meta(("department", "engineering"))),
        ], ct);

        var results = await store.SearchAsync(
            new float[] { 1f, 0f },
            new SearchOptions
            {
                TopK = 10,
                MinScore = 0.5,
                MetadataFilter = Meta(("department", "engineering")),
            },
            ct);

        Assert.Single(results);
        Assert.Equal("doc-eng", results[0].Chunk.DocumentId.Value);
    }

    [Fact]
    public async Task Store_Idempotent_ByDocIdChunkIndex()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = new TempDatabase();
        using var store = new SqliteVectorStore(db.Path, dimensions: 2, Model);

        await store.StoreAsync([MakeChunk("doc-a", 0, [1f, 0f])], ct);
        await store.StoreAsync([MakeChunk("doc-a", 0, [0f, 1f])], ct);

        var results = await store.SearchAsync(new float[] { 0f, 1f }, new SearchOptions { TopK = 10 }, ct);

        Assert.Single(results);
        Assert.Equal(1.0, results[0].Score, precision: 5);
    }

    [Fact]
    public async Task Store_Upsert_ReplacesTextAndMetadata()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = new TempDatabase();
        using var store = new SqliteVectorStore(db.Path, dimensions: 2, Model);

        await store.StoreAsync([MakeChunk("doc-a", 0, [1f, 0f], Meta(("version", 1)))], ct);
        await store.StoreAsync([MakeChunk("doc-a", 0, [1f, 0f], Meta(("version", 2)))], ct);

        var chunk = Assert.Single(await store.GetChunksAsync([new ChunkKey("doc-a", 0)], ct));
        Assert.Equal(2, chunk.Metadata["version"].NumberValue);
    }

    [Fact]
    public async Task Dimensions_MismatchThrows()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = new TempDatabase();
        using var store = new SqliteVectorStore(db.Path, dimensions: 2, Model);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.StoreAsync([MakeChunk("doc-3d", 0, [1f, 0f, 0f])], ct));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.SearchAsync(new float[] { 1f, 0f, 0f }, new SearchOptions { TopK = 1 }, ct));
    }

    [Fact]
    public async Task DeleteByDocumentId_RemovesAllChunksForDocument()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = new TempDatabase();
        using var store = new SqliteVectorStore(db.Path, dimensions: 2, Model);

        await store.StoreAsync(
        [
            MakeChunk("doc-a", 0, [1f, 0f]),
            MakeChunk("doc-a", 1, [1f, 0f]),
            MakeChunk("doc-b", 0, [1f, 0f]),
        ], ct);

        await store.DeleteByDocumentIdAsync("doc-a", ct);

        var results = await store.SearchAsync(new float[] { 1f, 0f }, new SearchOptions { TopK = 10 }, ct);
        Assert.Single(results);
        Assert.Equal("doc-b", results[0].Chunk.DocumentId.Value);
    }

    [Fact]
    public async Task Delete_KeepsRemainingRowsScoredCorrectly()
    {
        // Deleting a row swaps the last matrix row into its place; later searches must still
        // return each surviving chunk with its own score.
        var ct = TestContext.Current.CancellationToken;
        using var db = new TempDatabase();
        using var store = new SqliteVectorStore(db.Path, dimensions: 2, Model);

        await store.StoreAsync(
        [
            MakeChunk("first", 0, [1f, 0f]),
            MakeChunk("middle", 0, [0f, 1f]),
            MakeChunk("last", 0, [1f, 1f]),
        ], ct);

        await store.DeleteByDocumentIdAsync("first", ct);

        var results = await store.SearchAsync(new float[] { 0f, 1f }, new SearchOptions { TopK = 10 }, ct);
        Assert.Equal(["middle", "last"], results.Select(r => r.Chunk.DocumentId.Value));
        Assert.Equal(1.0, results[0].Score, precision: 5);
        Assert.Equal(Math.Sqrt(0.5), results[1].Score, precision: 5);
    }

    [Fact]
    public async Task Restart_PersistsVectorsAndMetadata()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = new TempDatabase();

        using (var first = new SqliteVectorStore(db.Path, dimensions: 3, Model))
        {
            await first.StoreAsync(
            [
                MakeChunk("doc-a", 0, [1f, 0f, 0f], Meta(("file_name", "a.md"), ("page", 3))),
                MakeChunk("doc-b", 0, [0f, 1f, 0f]),
            ], ct);
        }

        using var second = new SqliteVectorStore(db.Path, dimensions: 3, Model);
        var results = await second.SearchAsync(new float[] { 1f, 0f, 0f }, new SearchOptions { TopK = 1 }, ct);

        var top = Assert.Single(results);
        Assert.Equal("doc-a", top.Chunk.DocumentId.Value);
        Assert.Equal("a.md", top.Chunk.Metadata["file_name"].StringValue);
        Assert.Equal(3, top.Chunk.Metadata["page"].NumberValue);
    }

    [Fact]
    public async Task Metadata_RoundTripsEachKindAndFiltersOnIt()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = new TempDatabase();
        using var store = new SqliteVectorStore(db.Path, dimensions: 2, Model);
        var published = new DateTimeOffset(2027, 3, 14, 9, 30, 0, TimeSpan.FromHours(1));

        await store.StoreAsync(
        [
            MakeChunk("doc-a", 0, [1f, 0f], Meta(("page", 3), ("draft", true), ("published", published), ("tag", "3"))),
            MakeChunk("doc-b", 0, [1f, 0f], Meta(("page", 4), ("draft", false), ("published", published.AddDays(1)), ("tag", "4"))),
        ], ct);

        var chunk = Assert.Single(await store.GetChunksAsync([new ChunkKey("doc-a", 0)], ct));
        Assert.Equal(MetadataValueKind.Number, chunk.Metadata["page"].Kind);
        Assert.Equal(MetadataValueKind.Boolean, chunk.Metadata["draft"].Kind);
        Assert.Equal(MetadataValueKind.DateTimeOffset, chunk.Metadata["published"].Kind);
        Assert.Equal(MetadataValueKind.String, chunk.Metadata["tag"].Kind);
        Assert.Equal(published, chunk.Metadata["published"].DateTimeOffsetValue);

        async Task<string[]> Find(string key, MetadataValue value) =>
            (await store.SearchAsync(new float[] { 1f, 0f }, new SearchOptions
            {
                TopK = 10,
                MetadataFilter = Meta((key, value)),
            }, ct)).Select(r => r.Chunk.DocumentId.Value).ToArray();

        Assert.Equal(["doc-a"], await Find("page", 3));          // numeric, not the text "3"
        Assert.Empty(await Find("page", "3"));
        Assert.Equal(["doc-a"], await Find("draft", true));
        Assert.Equal(["doc-b"], await Find("draft", false));
        Assert.Equal(["doc-a"], await Find("published", published));
        Assert.Equal(["doc-a"], await Find("tag", "3"));
    }

    [Fact]
    public async Task Filter_RejectsKeysThatCouldChangeTheQuery()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = new TempDatabase();
        using var store = new SqliteVectorStore(db.Path, dimensions: 2, Model);

        await Assert.ThrowsAsync<ArgumentException>(() => store.SearchAsync(
            new float[] { 1f, 0f },
            new SearchOptions { TopK = 1, MetadataFilter = Meta(("bad\"key", "x")) },
            ct));
    }

    [Fact]
    public async Task GetChunks_ReturnsFoundKeysAndSkipsMissingOnes()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = new TempDatabase();
        using var store = new SqliteVectorStore(db.Path, dimensions: 2, Model);

        await store.StoreAsync([MakeChunk("doc-a", 0, [1f, 0f]), MakeChunk("doc-a", 1, [0f, 1f])], ct);

        var chunks = await store.GetChunksAsync(
            [new ChunkKey("doc-a", 1), new ChunkKey("doc-gone", 0)], ct);

        var chunk = Assert.Single(chunks);
        Assert.Equal("doc-a-1", chunk.Text);
        Assert.Equal(1, chunk.ChunkIndex);
    }

    [Fact]
    public async Task Models_AreIsolated_ButDeleteReachesEveryModel()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = new TempDatabase();
        using var old = new SqliteVectorStore(db.Path, dimensions: 2, "old-model");
        using var current = new SqliteVectorStore(db.Path, dimensions: 2, "new-model");

        await old.StoreAsync([MakeChunk("doc-a", 0, [1f, 0f])], ct);
        await current.StoreAsync([MakeChunk("doc-b", 0, [1f, 0f])], ct);

        var seen = await current.SearchAsync(new float[] { 1f, 0f }, new SearchOptions { TopK = 10 }, ct);
        Assert.Equal(["doc-b"], seen.Select(r => r.Chunk.DocumentId.Value));

        await current.DeleteByDocumentIdAsync("doc-a", ct);
        Assert.Empty(await old.GetChunksAsync([new ChunkKey("doc-a", 0)], ct));
    }

    [Fact]
    public async Task Collections_CreateExistsDelete()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = new TempDatabase();
        using var store = new SqliteVectorStore(db.Path, dimensions: 2, Model);

        Assert.False(await store.CollectionExistsAsync("old-model", ct));
        await store.CreateCollectionAsync("old-model", 2, ct);
        Assert.True(await store.CollectionExistsAsync("old-model", ct));

        await store.StoreAsync([MakeChunk("doc-a", 0, [1f, 0f])], ct);
        Assert.True(await store.CollectionExistsAsync(Model, ct));

        await store.DeleteCollectionAsync(Model, ct);
        Assert.False(await store.CollectionExistsAsync(Model, ct));
        Assert.Empty(await store.SearchAsync(new float[] { 1f, 0f }, new SearchOptions { TopK = 10 }, ct));
    }

    [Fact]
    public async Task Collections_RefuseDifferentDimensions()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = new TempDatabase();
        using var store = new SqliteVectorStore(db.Path, dimensions: 2, Model);

        await store.CreateCollectionAsync(Model, 2, ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateCollectionAsync(Model, 3, ct));
    }
}
