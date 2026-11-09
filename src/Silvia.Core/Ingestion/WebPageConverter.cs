using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;

namespace Silvia.Core.Ingestion;

/// <summary>
/// Turns a downloaded page into a document Silvia can index. HTML becomes Markdown of its main content, under a
/// heading with the page's title and a line naming its address; Markdown and plain text are kept as they are;
/// PDF and Office files keep their bytes for the existing parsers. Any other type is refused.
/// </summary>
public static partial class WebPageConverter
{
    /// <summary>Below this many characters of text, an HTML page is taken to need a browser: script, or a sign-in.</summary>
    public const int MinimumText = 200;

    public const string RefusedType = "Silvia cannot read this type of page.";

    public const string NeedsBrowserMessage =
        "The page shows almost no text without a browser: it is probably built by script or behind a sign-in. It was not indexed.";

    private static readonly Dictionary<string, string> Binary = new(StringComparer.OrdinalIgnoreCase)
    {
        ["application/pdf"] = ".pdf",
        ["application/vnd.openxmlformats-officedocument.wordprocessingml.document"] = ".docx",
        ["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"] = ".xlsx",
        ["application/vnd.openxmlformats-officedocument.presentationml.presentation"] = ".pptx",
    };

    static WebPageConverter()
    {
        // Pages still served in windows-1252, Shift_JIS and the like.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>
    /// Converts a response. <paramref name="fetched"/> dates the source line. The hash covers the content only,
    /// not that line, so a refresh of an unchanged page compares equal.
    /// </summary>
    public static WebConversion Convert(Uri url, string? mediaType, string? charset, byte[] body, DateTimeOffset fetched)
    {
        var type = string.IsNullOrWhiteSpace(mediaType) || mediaType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase)
            ? TypeFromAddress(url)
            : mediaType.Trim().ToLowerInvariant();

        if (type is null)
        {
            return WebConversion.Refused(RefusedType);
        }

        if (Binary.TryGetValue(type, out var extension))
        {
            return WebConversion.Ok(TitleFromAddress(url), extension, body, Hash(body));
        }

        switch (type)
        {
            case "text/html" or "application/xhtml+xml":
                return FromHtml(url, Decode(body, charset, html: true), fetched);

            case "text/markdown" or "text/x-markdown":
            {
                var text = Decode(body, charset, html: false);
                var title = MarkdownTitle().Match(text) is { Success: true } heading ? heading.Groups[1].Value.Trim() : TitleFromAddress(url);
                return FromText(url, title, ".md", text, fetched, markdown: true);
            }

            case "text/plain":
                return FromText(url, TitleFromAddress(url), ".txt", Decode(body, charset, html: false), fetched, markdown: false);

            default:
                return WebConversion.Refused(RefusedType);
        }
    }

    /// <summary>
    /// The file a page is saved as: <c>&lt;host&gt;-&lt;slug&gt;-&lt;hash&gt;.&lt;ext&gt;</c>, readable in Activity and
    /// unique per address.
    /// </summary>
    public static string FileName(Uri url, string extension)
    {
        var host = Slug(url.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? url.Host[4..] : url.Host, 40);
        var path = Slug(Path.ChangeExtension(url.AbsolutePath, null) ?? string.Empty, 50);
        var hash = System.Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(url.AbsoluteUri)))[..8];
        return string.Join('-', new[] { host, path, hash }.Where(p => p.Length > 0)) + extension;
    }

    private static WebConversion FromText(Uri url, string title, string extension, string text, DateTimeOffset fetched, bool markdown)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return WebConversion.Failed("The page is empty.");
        }

        var content = text.ReplaceLineEndings("\n").Trim() + "\n";
        var header = markdown ? Header(title, url, fetched) : $"{title}\nSource: {url.AbsoluteUri} · fetched {Day(fetched)}\n\n";
        return WebConversion.Ok(title, extension, Encoding.UTF8.GetBytes(header + content), Hash(Encoding.UTF8.GetBytes(content)));
    }

    private static WebConversion FromHtml(Uri url, string html, DateTimeOffset fetched)
    {
        var document = new HtmlParser().ParseDocument(html);
        var title = PageTitle(document, url);
        var markdown = MainContent(url, document, html);
        if (Plain(markdown).Length < MinimumText)
        {
            return WebConversion.NeedsBrowser(title);
        }

        var content = markdown + "\n";
        return WebConversion.Ok(title, ".md", Encoding.UTF8.GetBytes(Header(title, url, fetched) + content), Hash(Encoding.UTF8.GetBytes(content)));
    }

    /// <summary>
    /// The page's main content as Markdown. A readability extract (SmartReader) is preferred: it leaves out menus,
    /// footers and banners. The page's own main region (<c>main</c>, <c>article</c>) is used instead when the
    /// extract is too short or has no table where the region has one, since readability removes data tables. The
    /// whole cleaned body is the last resort.
    /// </summary>
    private static string MainContent(Uri url, IHtmlDocument document, string html)
    {
        var extract = string.Empty;
        try
        {
            var article = new SmartReader.Reader(url.AbsoluteUri, html).GetArticle();
            if (article.IsReadable && !string.IsNullOrWhiteSpace(article.Content))
            {
                extract = ToMarkdown(new HtmlParser().ParseDocument($"<html><body>{article.Content}</body></html>").Body!);
            }
        }
        catch (Exception)
        {
            // An extractor failure leaves the page's own structure to go by.
        }

        Clean(document);
        var region = MainRegion(document);
        var own = region is null ? string.Empty : ToMarkdown(region);
        if (Plain(extract).Length >= MinimumText && !(Tables(extract) == 0 && Tables(own) > 0))
        {
            return extract;
        }

        if (Plain(own).Length >= MinimumText)
        {
            return own;
        }

        if (Plain(extract).Length >= MinimumText)
        {
            return extract;
        }

        return document.Body is { } body ? ToMarkdown(body) : string.Empty;
    }

    private static readonly string[] Boilerplate =
    [
        "script", "style", "noscript", "template", "svg", "canvas", "iframe", "object", "embed", "form", "button",
        "select", "input", "textarea", "nav", "aside", "dialog", "[hidden]", "[aria-hidden='true']",
        "[role='navigation']", "[role='banner']", "[role='contentinfo']", "[role='search']", "[role='complementary']",
        "[role='dialog']", ".mw-editsection", ".navbox", ".reference", ".noprint", "a.headerlink",
        "[id*='cookie' i]", "[class*='cookie' i]", "[id*='consent' i]", "[class*='consent' i]",
    ];

    /// <summary>Removes what is never content. A header or footer goes unless it belongs to the article itself.</summary>
    private static void Clean(IHtmlDocument document)
    {
        foreach (var element in document.QuerySelectorAll(string.Join(',', Boilerplate)).ToList())
        {
            element.Remove();
        }

        foreach (var element in document.QuerySelectorAll("header, footer").ToList())
        {
            if (element.Closest("main, article, [role='main']") is null)
            {
                element.Remove();
            }
        }
    }

    private static IElement? MainRegion(IHtmlDocument document)
    {
        foreach (var selector in new[] { "#mw-content-text", "main", "[role='main']", "#content", "#main-content" })
        {
            if (document.QuerySelector(selector) is { } found)
            {
                return found;
            }
        }

        var articles = document.QuerySelectorAll("article");
        return articles.Length == 1 ? articles[0] : null;
    }

    private static readonly ReverseMarkdown.Converter Markdown = new(new ReverseMarkdown.Config
    {
        GithubFlavored = true,
        Tags = { Unknown = ReverseMarkdown.Config.UnknownTagsOption.Bypass },
        Formatting = { RemoveComments = true },
        Tables = { WithoutHeaderRow = ReverseMarkdown.Config.TableWithoutHeaderRowHandlingOption.EmptyRow },
    });

    /// <summary>
    /// The element as Markdown with headings, lists and tables, and without links or images: link targets and image
    /// addresses add nothing to search or to an answer, and would fill the passages. An image keeps its short alt text.
    /// </summary>
    private static string ToMarkdown(IElement root)
    {
        var copy = (IElement)root.Clone(deep: true);
        foreach (var image in copy.QuerySelectorAll("img").ToList())
        {
            var alt = image.GetAttribute("alt")?.Trim();
            if (string.IsNullOrEmpty(alt) || alt.Length > 200)
            {
                image.Remove();
            }
            else
            {
                image.Replace(copy.Owner!.CreateTextNode(alt));
            }
        }

        foreach (var link in copy.QuerySelectorAll("a").ToList())
        {
            var text = link.TextContent.Trim();
            if (text.Length == 0 || text == "¶" || text == "#")
            {
                link.Remove();
            }
            else
            {
                link.Replace([.. link.ChildNodes]);
            }
        }

        var markdown = Markdown.Convert(copy.InnerHtml).ReplaceLineEndings("\n");
        return BlankLines().Replace(markdown, "\n\n").Trim();
    }

    private static string PageTitle(IHtmlDocument document, Uri url)
    {
        var og = document.QuerySelector("meta[property='og:title']")?.GetAttribute("content");
        var title = !string.IsNullOrWhiteSpace(og) ? og : document.Title;
        title = Whitespace().Replace(title ?? string.Empty, " ").Trim();
        return title.Length > 0 ? title : TitleFromAddress(url);
    }

    private static string TitleFromAddress(Uri url)
    {
        var last = url.Segments.LastOrDefault()?.Trim('/');
        return string.IsNullOrEmpty(last) ? url.Host : $"{Uri.UnescapeDataString(last)} — {url.Host}";
    }

    /// <summary>The level-1 heading and the source line that open every saved Markdown page.</summary>
    private static string Header(string title, Uri url, DateTimeOffset fetched) =>
        $"# {title}\n\nSource: {url.AbsoluteUri} · fetched {Day(fetched)}\n\n";

    private static string Day(DateTimeOffset when) => when.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string? TypeFromAddress(Uri url) => Path.GetExtension(url.AbsolutePath).ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        ".md" or ".markdown" => "text/markdown",
        ".txt" => "text/plain",
        ".html" or ".htm" or "" => "text/html",
        _ => null,
    };

    private static string Decode(byte[] body, string? charset, bool html)
    {
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try
            {
                return Encoding.GetEncoding(charset.Trim('"', ' ')).GetString(body);
            }
            catch (ArgumentException)
            {
                // An unknown charset name: fall through to detection.
            }
        }

        if (html)
        {
            // The parser finds the encoding from a byte-order mark or a <meta charset>.
            using var stream = new MemoryStream(body);
            return new HtmlParser().ParseDocument(stream).DocumentElement.OuterHtml;
        }

        return new UTF8Encoding(false).GetString(body).TrimStart('﻿');
    }

    private static string Hash(byte[] content) => System.Convert.ToHexStringLower(SHA256.HashData(content));

    private static string Plain(string markdown) => Whitespace().Replace(MarkdownMarks().Replace(markdown, " "), " ").Trim();

    private static int Tables(string markdown) => TableRule().Count(markdown);

    private static string Slug(string text, int max)
    {
        var slug = NonWord().Replace(text.ToLowerInvariant(), "-").Trim('-');
        return slug.Length <= max ? slug : slug[..max].TrimEnd('-');
    }

    [GeneratedRegex(@"^#\s+(.+)$", RegexOptions.Multiline)]
    private static partial Regex MarkdownTitle();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLines();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"[#*_|`>\-]+")]
    private static partial Regex MarkdownMarks();

    [GeneratedRegex(@"^\|(\s*:?-+:?\s*\|)+\s*$", RegexOptions.Multiline)]
    private static partial Regex TableRule();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonWord();
}

/// <summary>
/// A converted page: <c>ok</c> with its title, file extension, file bytes and content hash; <c>needs-browser</c>
/// when it shows almost no text; or <c>failed</c> with the reason.
/// </summary>
public sealed record WebConversion(string Status, string? Title, string? Extension, byte[]? Content, string? ContentHash, string? Error)
{
    public static WebConversion Ok(string title, string extension, byte[] content, string hash) => new(WebPageStatus.Ok, title, extension, content, hash, null);

    public static WebConversion NeedsBrowser(string title) => new(WebPageStatus.NeedsBrowser, title, null, null, null, WebPageConverter.NeedsBrowserMessage);

    public static WebConversion Refused(string error) => new(WebPageStatus.Failed, null, null, null, null, error);

    public static WebConversion Failed(string error) => new(WebPageStatus.Failed, null, null, null, null, error);
}

/// <summary>Where a web page stands after its last fetch.</summary>
public static class WebPageStatus
{
    /// <summary>Added, not fetched yet.</summary>
    public const string Pending = "pending";

    public const string Ok = "ok";

    public const string Failed = "failed";

    /// <summary>Fetched, but it shows almost no text without a browser. Not indexed.</summary>
    public const string NeedsBrowser = "needs-browser";
}
