using Silvia.Core.Conversations;
using Silvia.Core.Ingestion;
using Silvia.Core.Projects;

namespace Silvia.Core.Runtime;

// Web pages as project documents. Each page is saved as a file under the project's own web folder and indexed
// from there by the queue's per-file jobs, so search, overviews, the graph and conversation memory treat it as
// any other file. Its content also lives in the web_pages table, which travels with project.db: the folder is
// a copy the runtime writes again where it is missing.
public sealed partial class ProjectRuntime
{
    /// <summary>The folder the project's web pages are saved in. It is not a watched folder.</summary>
    public string WebFolder { get; }

    /// <summary>The project's web pages, in the order they were added.</summary>
    public IReadOnlyList<WebPage> WebPages() => _webPages.List();

    /// <summary>
    /// Adds web pages and queues their download. Each text is one address; one that is not an absolute
    /// <c>http</c> or <c>https</c> address is returned as rejected. An address already added is fetched again.
    /// </summary>
    public (IReadOnlyList<WebPage> Added, IReadOnlyList<string> Rejected) AddWebPages(IEnumerable<string> urls)
    {
        var added = new List<WebPage>();
        var rejected = new List<string>();
        foreach (var text in urls.Select(u => u.Trim()).Where(u => u.Length > 0).Distinct(StringComparer.Ordinal))
        {
            if (!WebFetcher.TryParse(text, out var url))
            {
                rejected.Add(text);
                continue;
            }

            var (page, _) = _webPages.Add(url);
            QueueFetch(page);
            if (added.All(a => a.Id != page.Id))
            {
                added.Add(page);
            }
        }

        return (added, rejected);
    }

    /// <summary>Queues a new download of one page. Throws <see cref="KeyNotFoundException"/> when the project has no such page.</summary>
    public void RefreshWebPage(long id) =>
        QueueFetch(_webPages.Get(id) ?? throw new KeyNotFoundException($"Web page {id} is not part of project '{ProjectId}'."));

    /// <summary>Queues a new download of every page.</summary>
    public void RefreshAllWebPages()
    {
        foreach (var page in _webPages.List())
        {
            QueueFetch(page);
        }
    }

    /// <summary>
    /// Removes a page: its row, its saved copy and its document. Throws <see cref="KeyNotFoundException"/> when
    /// the project has no such page.
    /// </summary>
    public void RemoveWebPage(long id)
    {
        var page = _webPages.Get(id) ?? throw new KeyNotFoundException($"Web page {id} is not part of project '{ProjectId}'.");
        _webPages.Remove(id);
        var path = WebPath(page.FileName);
        TryDelete(path);
        _queue.Enqueue(JobKind.Delete, path);
    }

    /// <summary>The page a document is the saved copy of, or null for any other document.</summary>
    public WebPage? WebPageOf(string documentId) => IsWebDocument(documentId)
        ? _webPages.GetByFileName(Path.GetFileName(documentId))
        : null;

    /// <summary>True when the address is one of this project's pages, as added or as reached after redirects.</summary>
    public bool IsWebPageAddress(string url) =>
        _webPages.List().Any(p => string.Equals(p.Url, url, StringComparison.Ordinal) || string.Equals(p.FinalUrl, url, StringComparison.Ordinal));

    private bool IsWebDocument(string documentId) =>
        string.Equals(Path.GetDirectoryName(documentId), WebFolder, StringComparison.OrdinalIgnoreCase);

    private string WebPath(string fileName) => DocumentIds.For(Path.Combine(WebFolder, fileName));

    private void QueueFetch(WebPage page)
    {
        // One download per page at a time: a second click on Refresh while one waits changes nothing.
        if (!_queue.Waiting(JobKind.Fetch).Contains(page.Url))
        {
            _queue.Enqueue(JobKind.Fetch, page.Url);
        }
    }

    /// <summary>
    /// Runs a fetch job. A page whose content is unchanged (a <c>304</c>, or the same content hash) rewrites
    /// nothing, so its overview and part of the graph are not built again. Changed content is written to the
    /// page's file and queued for indexing. A failure keeps the saved copy and records why.
    /// </summary>
    private async Task FetchWebPageAsync(string url, CancellationToken cancellationToken)
    {
        if (_webPages.GetByUrl(url) is not { } page)
        {
            return;
        }

        var path = WebPath(page.FileName);
        var saved = page.HasContent && File.Exists(path);
        var now = DateTimeOffset.UtcNow;
        WebFetch fetch;
        try
        {
            fetch = await _fetcher.FetchAsync(new Uri(url), saved ? page.ETag : null, saved ? page.LastModified : null, cancellationToken);
        }
        catch (WebFetchException ex)
        {
            _webPages.SaveCheck(page.Id, WebPageStatus.Failed, ex.Message, now);
            throw ex.Permanent ? new PermanentJobFailure(ex.Message) : new InvalidOperationException(ex.Message);
        }

        if (fetch.Status == 304)
        {
            _webPages.SaveCheck(page.Id, WebPageStatus.Ok, null, now, fetch);
            return;
        }

        var conversion = WebPageConverter.Convert(fetch.FinalUrl, fetch.MediaType, fetch.Charset, fetch.Body, now);
        if (conversion.Status != WebPageStatus.Ok)
        {
            _webPages.SaveCheck(page.Id, conversion.Status, conversion.Error, now, fetch, conversion.Title);
            throw new PermanentJobFailure(conversion.Error!);
        }

        if (saved && conversion.ContentHash == page.ContentHash)
        {
            _webPages.SaveCheck(page.Id, WebPageStatus.Ok, null, now, fetch);
            return;
        }

        // A page whose type changed (an article that became a PDF) is saved under a new extension.
        var fileName = Path.GetExtension(page.FileName).Equals(conversion.Extension, StringComparison.OrdinalIgnoreCase)
            ? page.FileName
            : WebPageConverter.FileName(new Uri(page.Url), conversion.Extension!);
        if (!_webPages.SaveContent(page.Id, fetch, conversion, fileName, now))
        {
            return;
        }

        var target = WebPath(fileName);
        Directory.CreateDirectory(WebFolder);
        await File.WriteAllBytesAsync(target, conversion.Content!, cancellationToken);
        if (_webPages.Get(page.Id) is null)
        {
            // Removed while it downloaded.
            TryDelete(target);
            return;
        }

        if (!string.Equals(target, path, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(path);
            _queue.Enqueue(JobKind.Delete, path);
        }

        _queue.Enqueue(JobKind.Upsert, target);
    }

    /// <summary>
    /// Brings the web folder in line with the table when the runtime opens. After an import or a duplicate, the
    /// pages' files are written under this project's folder and indexed there, and the documents still named
    /// after the original project's folder are removed. A file whose page is gone is deleted.
    /// </summary>
    private void RestoreWebPages()
    {
        var pages = _webPages.List();
        var indexed = Documents().Select(d => d.DocumentId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var names = pages.Select(p => p.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Saved copies of these pages indexed under another project's folder: an import or duplicate copied them.
        foreach (var stale in indexed.Where(id =>
                     !IsWebDocument(id) &&
                     string.Equals(Path.GetFileName(Path.GetDirectoryName(id)), "web", StringComparison.OrdinalIgnoreCase) &&
                     names.Contains(Path.GetFileName(id))))
        {
            _queue.Enqueue(JobKind.Delete, stale);
        }

        if (Directory.Exists(WebFolder))
        {
            foreach (var file in Directory.EnumerateFiles(WebFolder).Where(f => !names.Contains(Path.GetFileName(f))))
            {
                TryDelete(file);
                if (indexed.Contains(DocumentIds.For(file)))
                {
                    _queue.Enqueue(JobKind.Delete, DocumentIds.For(file));
                }
            }
        }

        foreach (var page in pages.Where(p => p.HasContent))
        {
            var path = WebPath(page.FileName);
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(WebFolder);
                File.WriteAllBytes(path, _webPages.Content(page.Id)!);
            }

            if (!indexed.Contains(path))
            {
                _queue.Enqueue(JobKind.Upsert, path);
            }
        }

        // A page added just before the app closed, never fetched.
        foreach (var page in pages.Where(p => p.Status == WebPageStatus.Pending))
        {
            QueueFetch(page);
        }
    }

    /// <summary>The citations with each web page's title as its name and its address to open.</summary>
    private List<CitedChunk> WithWebPages(IEnumerable<CitedChunk> cited)
    {
        var pages = WebPagesByPath();
        return cited.Select(c => pages.TryGetValue(c.DocumentId, out var page)
                ? c with { FileName = page.Title ?? c.FileName, Url = page.Address }
                : c)
            .ToList();
    }

    /// <summary>The project's saved pages by the document id of their copy.</summary>
    private Dictionary<string, WebPage> WebPagesByPath() =>
        _webPages.List()
            .GroupBy(p => WebPath(p.FileName), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Held by another program for a moment; the next open of the project removes it.
        }
    }
}
