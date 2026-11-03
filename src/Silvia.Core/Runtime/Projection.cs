namespace Silvia.Core.Runtime;

/// <summary>
/// Projects vectors onto their two principal components, so a set of embeddings can be drawn on a page.
/// Power iteration on the centered rows keeps the cost at rows times dimensions per step, without ever
/// building the dimensions-by-dimensions covariance matrix.
/// </summary>
internal static class Projection
{
    private const int Iterations = 60;

    /// <summary>
    /// The coordinates of each row on the first two components, and the share of the total variance each
    /// component explains. Fewer than two rows, or rows with no spread, project to the origin.
    /// </summary>
    public static (float[] X, float[] Y, double FirstShare, double SecondShare) Principal(IReadOnlyList<float[]> rows)
    {
        var count = rows.Count;
        var x = new float[count];
        var y = new float[count];
        if (count < 2)
        {
            return (x, y, 0, 0);
        }

        var dims = rows[0].Length;
        var mean = new double[dims];
        foreach (var row in rows)
        {
            for (var d = 0; d < dims; d++)
            {
                mean[d] += row[d];
            }
        }

        for (var d = 0; d < dims; d++)
        {
            mean[d] /= count;
        }

        var centered = new double[count][];
        var total = 0.0;
        for (var i = 0; i < count; i++)
        {
            var row = new double[dims];
            for (var d = 0; d < dims; d++)
            {
                row[d] = rows[i][d] - mean[d];
                total += row[d] * row[d];
            }

            centered[i] = row;
        }

        if (total <= 0)
        {
            return (x, y, 0, 0);
        }

        var first = Component(centered, dims, null);
        var second = Component(centered, dims, first);
        var firstVariance = 0.0;
        var secondVariance = 0.0;
        for (var i = 0; i < count; i++)
        {
            var a = Dot(centered[i], first);
            var b = Dot(centered[i], second);
            x[i] = (float)a;
            y[i] = (float)b;
            firstVariance += a * a;
            secondVariance += b * b;
        }

        return (x, y, firstVariance / total, secondVariance / total);
    }

    /// <summary>The leading direction of the rows, orthogonal to <paramref name="exclude"/> when given.</summary>
    private static double[] Component(double[][] rows, int dims, double[]? exclude)
    {
        // A fixed start keeps the picture the same from one call to the next.
        var vector = new double[dims];
        for (var d = 0; d < dims; d++)
        {
            vector[d] = 1.0 + (d % 7) * 0.1;
        }

        Orthogonalize(vector, exclude);
        Normalize(vector);
        var scores = new double[rows.Length];
        for (var step = 0; step < Iterations; step++)
        {
            for (var i = 0; i < rows.Length; i++)
            {
                scores[i] = Dot(rows[i], vector);
            }

            var next = new double[dims];
            for (var i = 0; i < rows.Length; i++)
            {
                var row = rows[i];
                var score = scores[i];
                for (var d = 0; d < dims; d++)
                {
                    next[d] += row[d] * score;
                }
            }

            Orthogonalize(next, exclude);
            if (Normalize(next) == 0)
            {
                break;
            }

            vector = next;
        }

        // An eigenvector's sign is arbitrary; pinning it stops the map flipping between calls.
        var largest = 0;
        for (var d = 1; d < dims; d++)
        {
            if (Math.Abs(vector[d]) > Math.Abs(vector[largest]))
            {
                largest = d;
            }
        }

        if (vector[largest] < 0)
        {
            for (var d = 0; d < dims; d++)
            {
                vector[d] = -vector[d];
            }
        }

        return vector;
    }

    private static void Orthogonalize(double[] vector, double[]? exclude)
    {
        if (exclude is null)
        {
            return;
        }

        var along = Dot(vector, exclude);
        for (var d = 0; d < vector.Length; d++)
        {
            vector[d] -= along * exclude[d];
        }
    }

    private static double Normalize(double[] vector)
    {
        var length = Math.Sqrt(Dot(vector, vector));
        if (length > 0)
        {
            for (var d = 0; d < vector.Length; d++)
            {
                vector[d] /= length;
            }
        }

        return length;
    }

    private static double Dot(double[] a, double[] b)
    {
        var sum = 0.0;
        for (var d = 0; d < a.Length; d++)
        {
            sum += a[d] * b[d];
        }

        return sum;
    }
}
