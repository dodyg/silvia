// Step 1 of .alta/plans/2026-11-03-web-pages.md: how well Silvia's WebFetcher and WebPageConverter (SmartReader,
// a main-region fallback, ReverseMarkdown) turn real pages into documents.
// Usage: dotnet run -- [url ...]. Prints, per page, the status, title, size, headings and tables, and the start of
// the text, and writes each saved file to ./out.
using System.Text;
using System.Text.RegularExpressions;
using Silvia.Core.Ingestion;

string[] urls = args.Length > 0 ? args :
[
    "https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/statements/yield",
    "https://docs.python.org/3/library/functools.html",
    "https://en.wikipedia.org/wiki/Okapi_BM25",
    "https://en.wikipedia.org/wiki/List_of_countries_by_population_(United_Nations)",
    "https://www.bbc.com/news",
    "https://www.theguardian.com/international",
    "https://sqlite.org/lang_vacuum.html",
    "https://github.com/strumenta/SmartReader",
    "https://react.dev/",
    "https://www.rfc-editor.org/rfc/rfc9110.html",
];

var fetcher = new WebFetcher(WebFetcher.SharedClient);
Directory.CreateDirectory("out");
foreach (var text in urls)
{
    try
    {
        WebFetcher.TryParse(text, out var url);
        var fetch = await fetcher.FetchAsync(url, null, null, CancellationToken.None);
        var page = WebPageConverter.Convert(fetch.FinalUrl, fetch.MediaType, fetch.Charset, fetch.Body, DateTimeOffset.UtcNow);
        var markdown = page.Content is null ? string.Empty : Encoding.UTF8.GetString(page.Content);
        var plain = Regex.Replace(markdown, @"\s+", " ").Trim();
        Console.WriteLine($"== {url}");
        Console.WriteLine($"   status={page.Status} title='{page.Title}' html={fetch.Body.Length} md={markdown.Length} " +
            $"headings={Regex.Matches(markdown, @"^#{1,6} ", RegexOptions.Multiline).Count} tables={Regex.Matches(markdown, @"^\|(\s*:?-+:?\s*\|)+\s*$", RegexOptions.Multiline).Count}");
        Console.WriteLine("   " + (plain.Length > 400 ? plain[..400] : plain));
        if (page.Content is not null)
        {
            File.WriteAllBytes(Path.Combine("out", WebPageConverter.FileName(url, page.Extension!)), page.Content);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"== {text}\n   FAILED {ex.GetType().Name}: {ex.Message}");
    }
}
