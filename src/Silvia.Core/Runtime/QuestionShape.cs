using System.Text;
using System.Text.RegularExpressions;

namespace Silvia.Core.Runtime;

/// <summary>
/// What the wording of a question says about how to search for it and whether the passages found can answer it,
/// without calling a model.
/// </summary>
public static partial class QuestionShape
{
    /// <summary>
    /// True when the question asks for every item of a kind ("List everything Dana is responsible for", "Which rules
    /// set a deadline in working days?"). Such an answer is spread over several documents, so it is given more
    /// passages than a question about one fact.
    /// </summary>
    public static bool AsksForAll(string question)
    {
        var text = question.Trim();
        return ListVerb().IsMatch(text) || AllWord().IsMatch(text) || WhichPlural().IsMatch(text);
    }

    /// <summary>
    /// The terms of a question that name something particular: words written with a capital inside the sentence
    /// (names of people, places, products), words with a digit (years, versions, codes), acronyms, and quoted
    /// phrases. A passage that answers the question nearly always names them too.
    /// </summary>
    public static IReadOnlyList<string> SalientTerms(string question)
    {
        var terms = new List<string>();
        foreach (Match quoted in Quoted().Matches(question))
        {
            terms.Add(quoted.Groups[1].Value.Trim());
        }

        var unquoted = Quoted().Replace(question, " ");
        var first = true;
        foreach (Match word in Word().Matches(unquoted))
        {
            var token = Possessive().Replace(word.Value, string.Empty).Trim('.', '-', '\'', '’');
            var isFirst = first;
            first = false;
            if (token.Length < 2 || Common.Contains(token))
            {
                continue;
            }

            var hasDigit = token.Any(char.IsDigit);
            var isAcronym = token.Count(char.IsUpper) >= 2 && token.All(c => !char.IsLetter(c) || char.IsUpper(c) || c == 's');
            var isName = !isFirst && char.IsUpper(token[0]);
            if (hasDigit || isAcronym || isName)
            {
                terms.Add(token);
            }
        }

        return terms.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// The salient terms of the question (see <see cref="SalientTerms"/>) that none of the texts mentions. Words are
    /// compared whole, ignoring case, hyphens and possessives, so "Wi-Fi" matches "wifi" and "Dana's" matches "Dana".
    /// </summary>
    public static IReadOnlyList<string> Unmentioned(string question, IEnumerable<string?> texts)
    {
        var terms = SalientTerms(question);
        if (terms.Count == 0)
        {
            return [];
        }

        var haystack = new StringBuilder(" ");
        foreach (var text in texts)
        {
            if (!string.IsNullOrEmpty(text))
            {
                haystack.Append(Squash(text)).Append(' ');
            }
        }

        var all = haystack.ToString();
        return terms.Where(t => Squash(t) is { Length: > 0 } squashed && !all.Contains($" {squashed} ", StringComparison.Ordinal)).ToList();
    }

    /// <summary>Lower-case words separated by single spaces, with hyphens, dots inside words and possessives removed.</summary>
    private static string Squash(string text)
    {
        var withoutPossessives = Possessive().Replace(text, string.Empty);
        var builder = new StringBuilder(withoutPossessives.Length);
        var space = false;
        for (var i = 0; i < withoutPossessives.Length; i++)
        {
            var c = withoutPossessives[i];
            if (char.IsLetterOrDigit(c))
            {
                if (space && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                space = false;
                builder.Append(char.ToLowerInvariant(c));
            }
            else if ((c is '-' or '.' or '\'' or '’' or '­') && i > 0 && i + 1 < withoutPossessives.Length &&
                     char.IsLetterOrDigit(withoutPossessives[i - 1]) && char.IsLetterOrDigit(withoutPossessives[i + 1]))
            {
                // Joined: "Wi-Fi" and "wifi", "v1.2" and "v12" compare equal.
            }
            else
            {
                space = true;
            }
        }

        return builder.ToString();
    }

    // Capitalised words that do not name anything particular.
    private static readonly HashSet<string> Common = new(StringComparer.Ordinal)
    {
        "I", "I'm", "I've", "I'd", "I'll", "OK", "Ok",
    };

    [GeneratedRegex(@"^(list|name|enumerate|summari[sz]e all|give me all|show all)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ListVerb();

    [GeneratedRegex(@"\b(all|every|everything|each|everyone|everybody)\b", RegexOptions.IgnoreCase)]
    private static partial Regex AllWord();

    // "Which rules…", "What documents…": a plural noun right after the question word. Verbs that end in s are not nouns.
    [GeneratedRegex(@"^(which|what)\s+(?!is\b|was\b|does\b|has\b|says\b|happens\b|means\b|this\b)[a-z]+[^s\s]s\b", RegexOptions.IgnoreCase)]
    private static partial Regex WhichPlural();

    [GeneratedRegex("[\"“”]([^\"“”]{2,80})[\"“”]")]
    private static partial Regex Quoted();

    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}'’.\-]*")]
    private static partial Regex Word();

    [GeneratedRegex(@"['’]s\b", RegexOptions.IgnoreCase)]
    private static partial Regex Possessive();
}
