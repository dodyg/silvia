using Rag.NET.Models;
using Silvia.Core.Conversations;

namespace Silvia.Core.Runtime;

/// <summary>
/// Keeps what an answer is given within a size. The passages of the search, those of the further (bridge)
/// searches, the document overviews, the knowledge graph, a recalled exchange and the project's facts all add
/// to the prompt, and nothing else bounds their sum. When it is over the budget, the parts that matter least
/// go first: the recalled exchange, then the knowledge graph, then the overviews (the last cited first), then the
/// bridge passages (the last found first), then the weakest passages of the search. The best passage always
/// stays, and so do the facts: the user confirmed them, so they count toward the budget but are never cut.
/// </summary>
public static class ContextBudget
{
    // The "[n] file › section (modified …)" line in front of each passage.
    private const int HeaderTokens = 20;

    /// <summary>What may be cut, in the order each part is given: search passages best first.</summary>
    public sealed record Material(
        IReadOnlyList<SearchResult> Found,
        IReadOnlyList<SearchResult> Bridged,
        IReadOnlyList<CitedChunk> Overviews,
        string? Recalled,
        string Graph);

    /// <summary>
    /// A rough token count, about four characters per token in English prose: the same estimate the chunk size
    /// setting uses.
    /// </summary>
    public static int Tokens(string? text) => string.IsNullOrEmpty(text) ? 0 : (text.Length + 3) / 4;

    /// <summary>The size of the material and the fixed part.</summary>
    public static int Size(Material material, int fixedTokens = 0) =>
        fixedTokens +
        Tokens(material.Recalled) +
        Tokens(material.Graph) +
        material.Overviews.Sum(o => Tokens(o.Text) + HeaderTokens) +
        material.Found.Sum(Cost) +
        material.Bridged.Sum(Cost);

    /// <summary>
    /// The material cut down to <paramref name="budget"/> tokens, together with <paramref name="fixedTokens"/> that
    /// cannot be cut. A budget of zero or less means no limit.
    /// </summary>
    public static Material Fit(Material material, int budget, int fixedTokens = 0)
    {
        if (budget <= 0)
        {
            return material;
        }

        var found = material.Found.ToList();
        var bridged = material.Bridged.ToList();
        var overviews = material.Overviews.ToList();
        var recalled = material.Recalled;
        var graph = material.Graph;

        var total = Size(material, fixedTokens);
        while (total > budget)
        {
            if (recalled is not null)
            {
                total -= Tokens(recalled);
                recalled = null;
            }
            else if (graph.Length > 0)
            {
                total -= Tokens(graph);
                graph = string.Empty;
            }
            else if (overviews.Count > 0)
            {
                total -= Tokens(overviews[^1].Text) + HeaderTokens;
                overviews.RemoveAt(overviews.Count - 1);
            }
            else if (bridged.Count > 0)
            {
                total -= Cost(bridged[^1]);
                bridged.RemoveAt(bridged.Count - 1);
            }
            else if (found.Count > 1)
            {
                total -= Cost(found[^1]);
                found.RemoveAt(found.Count - 1);
            }
            else
            {
                break;
            }
        }

        return new Material(found, bridged, overviews, recalled, graph);
    }

    private static int Cost(SearchResult hit) => Tokens(hit.Chunk.Text) + HeaderTokens;
}
