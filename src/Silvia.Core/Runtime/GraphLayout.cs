namespace Silvia.Core.Runtime;

/// <summary>
/// Places the nodes of a graph on a plane so related ones sit together (Fruchterman-Reingold): every pair
/// pushes apart, every edge pulls its two ends together, and a weak pull to the centre keeps the pieces
/// of a graph that are not joined from drifting off. The start is fixed, so one graph always lands the same.
/// </summary>
internal static class GraphLayout
{
    private const int Iterations = 300;
    private const double Gravity = 0.08;

    /// <summary>
    /// Coordinates for <paramref name="count"/> nodes joined by <paramref name="edges"/> (indexes into the
    /// nodes). Nodes earlier in the list start nearer the centre, so put the most important first.
    /// </summary>
    public static (float[] X, float[] Y) Arrange(int count, IReadOnlyList<(int Source, int Target)> edges)
    {
        var x = new double[count];
        var y = new double[count];

        // A sunflower spiral: an even spread with no two nodes on top of each other.
        var golden = Math.PI * (3 - Math.Sqrt(5));
        for (var i = 0; i < count; i++)
        {
            var radius = Math.Sqrt((i + 0.5) / Math.Max(1, count));
            x[i] = radius * Math.Cos(i * golden);
            y[i] = radius * Math.Sin(i * golden);
        }

        if (count < 2)
        {
            return (x.Select(v => (float)v).ToArray(), y.Select(v => (float)v).ToArray());
        }

        // The ideal distance between neighbours, for a unit-sized drawing.
        var k = Math.Sqrt(4.0 / count);
        var dx = new double[count];
        var dy = new double[count];
        for (var step = 0; step < Iterations; step++)
        {
            Array.Clear(dx);
            Array.Clear(dy);
            for (var i = 0; i < count; i++)
            {
                for (var j = i + 1; j < count; j++)
                {
                    var ex = x[i] - x[j];
                    var ey = y[i] - y[j];
                    var distance = Math.Max(1e-4, Math.Sqrt(ex * ex + ey * ey));
                    var push = k * k / distance / distance;
                    dx[i] += ex * push;
                    dy[i] += ey * push;
                    dx[j] -= ex * push;
                    dy[j] -= ey * push;
                }
            }

            foreach (var (source, target) in edges)
            {
                if (source == target)
                {
                    continue;
                }

                var ex = x[source] - x[target];
                var ey = y[source] - y[target];
                var distance = Math.Sqrt(ex * ex + ey * ey);
                var pull = distance / k;
                dx[source] -= ex * pull;
                dy[source] -= ey * pull;
                dx[target] += ex * pull;
                dy[target] += ey * pull;
            }

            // The temperature caps how far a node moves in one step, and cools to nothing.
            var temperature = 0.1 * (1 - step / (double)Iterations);
            for (var i = 0; i < count; i++)
            {
                dx[i] -= x[i] * Gravity / k;
                dy[i] -= y[i] * Gravity / k;
                var length = Math.Sqrt(dx[i] * dx[i] + dy[i] * dy[i]);
                if (length > 0)
                {
                    var move = Math.Min(length, temperature) / length;
                    x[i] += dx[i] * move;
                    y[i] += dy[i] * move;
                }
            }
        }

        return (x.Select(v => (float)v).ToArray(), y.Select(v => (float)v).ToArray());
    }
}
