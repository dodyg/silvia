using System.Numerics.Tensors;
using Rag.NET.Models;

namespace Silvia.Storage.Sqlite;

/// <summary>
/// In-memory copy of the vectors for one model: a contiguous, L2-normalized <c>float</c> matrix
/// with one row per chunk, plus the key of each row. SQLite stays the source of truth; this
/// class is only ever written under the store's write lock.
/// </summary>
internal sealed class VectorMatrix(int dimensions)
{
    private readonly List<ChunkKey> _keys = [];
    private readonly Dictionary<ChunkKey, int> _rows = [];
    private float[] _data = [];

    public int Count => _keys.Count;

    public bool TryGetRow(ChunkKey key, out int row) => _rows.TryGetValue(key, out row);

    public ChunkKey KeyOf(int row) => _keys[row];

    public ReadOnlySpan<float> Vector(int row) => _data.AsSpan(row * dimensions, dimensions);

    /// <summary>Adds a row for <paramref name="key"/>, or overwrites the existing one.</summary>
    public void Upsert(ChunkKey key, ReadOnlySpan<float> normalizedVector)
    {
        if (!_rows.TryGetValue(key, out var row))
        {
            row = _keys.Count;
            EnsureCapacity(row + 1);
            _keys.Add(key);
            _rows[key] = row;
        }

        normalizedVector.CopyTo(_data.AsSpan(row * dimensions, dimensions));
    }

    /// <summary>
    /// Removes the given rows. Rows are swapped with the last row, so the caller must pass them
    /// in descending order for the indices to stay valid.
    /// </summary>
    public void RemoveRowsDescending(IReadOnlyList<int> rowsDescending)
    {
        foreach (var row in rowsDescending)
        {
            var last = _keys.Count - 1;
            var removed = _keys[row];
            if (row != last)
            {
                _data.AsSpan(last * dimensions, dimensions).CopyTo(_data.AsSpan(row * dimensions, dimensions));
                _keys[row] = _keys[last];
                _rows[_keys[row]] = row;
            }

            _keys.RemoveAt(last);
            _rows.Remove(removed);
        }
    }

    public void Clear()
    {
        _keys.Clear();
        _rows.Clear();
        _data = [];
    }

    /// <summary>
    /// Scores the query against every row (or only <paramref name="candidates"/> when given) and
    /// keeps the best <paramref name="k"/> at or above <paramref name="minScore"/>, best first.
    /// </summary>
    public List<(int Row, double Score)> TopK(
        ReadOnlySpan<float> normalizedQuery,
        int k,
        double minScore,
        IReadOnlyList<int>? candidates)
    {
        var heap = new PriorityQueue<int, double>(k + 1);

        if (candidates is null)
        {
            for (var row = 0; row < _keys.Count; row++)
            {
                Offer(heap, k, row, normalizedQuery, minScore);
            }
        }
        else
        {
            foreach (var row in candidates)
            {
                Offer(heap, k, row, normalizedQuery, minScore);
            }
        }

        var results = new List<(int Row, double Score)>(heap.Count);
        while (heap.TryDequeue(out var row, out var score))
        {
            results.Add((row, score));
        }

        results.Reverse();
        return results;
    }

    private void Offer(PriorityQueue<int, double> heap, int k, int row, ReadOnlySpan<float> query, double minScore)
    {
        var score = TensorPrimitives.Dot(query, Vector(row));
        if (score < minScore)
        {
            return;
        }

        if (heap.Count < k)
        {
            heap.Enqueue(row, score);
        }
        else if (heap.TryPeek(out _, out var worst) && score > worst)
        {
            heap.Dequeue();
            heap.Enqueue(row, score);
        }
    }

    private void EnsureCapacity(int rows)
    {
        var needed = rows * dimensions;
        if (_data.Length >= needed)
        {
            return;
        }

        var capacity = Math.Max(needed, Math.Max(1024 * dimensions, _data.Length * 2));
        Array.Resize(ref _data, capacity);
    }
}
