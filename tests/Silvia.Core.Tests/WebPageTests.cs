using System.Net;
using System.Text;
using Silvia.Core.Ingestion;
using Silvia.Core.Projects;
using Silvia.Core.Runtime;
using Silvia.Core.Tests.Harness;
using Xunit;

namespace Silvia.Core.Tests;

/// <summary>
/// Web pages as project documents: the fetcher's limits, the conversion of each content type, and a page's life
/// in a project (indexed, cited by its address, refreshed, removed, and carried by an import or a duplicate).
/// Every response comes from a fake handler; nothing here reaches the network.
/// </summary>
public sealed class WebPageTests
{
    private static readonly Uri Pricing = new("https://example.com/plans/pricing");

    [Fact]
    public async Task Html_Becomes_Markdown_Of_Its_Main_Content_Under_A_Source_Line()
    {
        var web = new FakeWeb();
        web.Html(Pricing, "Pricing", "<h2>Plans</h2><p>The Team plan costs 12 euros per seat each month. " + Filler + "</p>" +
            "<table><tr><th>Plan</th><th>Price</th></tr><tr><td>Team</td><td>12</td></tr><tr><td>Studio</td><td>30</td></tr></table>",
            nav: "<nav><a href='/'>Home</a> <a href='/blog'>Blog</a></nav>");
        var fetch = await new WebFetcher(web.Client()).FetchAsync(Pricing, null, null, TestContext.Current.CancellationToken);

        var page = WebPageConverter.Convert(fetch.FinalUrl, fetch.MediaType, fetch.Charset, fetch.Body, new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero));

        Assert.Equal(WebPageStatus.Ok, page.Status);
        Assert.Equal(".md", page.Extension);
        Assert.Equal("Pricing", page.Title);
        var markdown = Encoding.UTF8.GetString(page.Content!);
        Assert.StartsWith("# Pricing\n\nSource: https://example.com/plans/pricing · fetched 2026-11-03\n\n", markdown, StringComparison.Ordinal);
        Assert.Contains("## Plans", markdown, StringComparison.Ordinal);
        Assert.Contains("| Team | 12 |", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("Blog", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void The_Hash_Ignores_The_Fetch_Date()
    {
        var body = Encoding.UTF8.GetBytes(FakeWeb.Page("Pricing", $"<p>{Filler}</p>"));
        var monday = WebPageConverter.Convert(Pricing, "text/html", "utf-8", body, DateTimeOffset.UtcNow);
        var tuesday = WebPageConverter.Convert(Pricing, "text/html", "utf-8", body, DateTimeOffset.UtcNow.AddDays(1));

        Assert.Equal(monday.ContentHash, tuesday.ContentHash);
    }

    [Fact]
    public void Pdf_Keeps_Its_Bytes_And_Other_Types_Are_Refused()
    {
        byte[] pdf = [0x25, 0x50, 0x44, 0x46, 0x2D, 0x31];
        var saved = WebPageConverter.Convert(new Uri("https://example.com/report"), "application/pdf", null, pdf, DateTimeOffset.UtcNow);
        Assert.Equal(WebPageStatus.Ok, saved.Status);
        Assert.Equal(".pdf", saved.Extension);
        Assert.Equal(pdf, saved.Content);

        // Served without a useful type, the address's extension decides.
        Assert.Equal(".docx", WebPageConverter.Convert(new Uri("https://example.com/a.docx"), "application/octet-stream", null, pdf, DateTimeOffset.UtcNow).Extension);

        var refused = WebPageConverter.Convert(new Uri("https://example.com/logo"), "image/png", null, pdf, DateTimeOffset.UtcNow);
        Assert.Equal(WebPageStatus.Failed, refused.Status);
        Assert.Equal(WebPageConverter.RefusedType, refused.Error);
    }

    [Fact]
    public void A_Page_Built_By_Script_Needs_A_Browser()
    {
        var body = Encoding.UTF8.GetBytes("<html><head><title>App</title><script>render()</script></head><body><div id='root'></div><noscript>Enable JavaScript.</noscript></body></html>");
        var page = WebPageConverter.Convert(Pricing, "text/html", null, body, DateTimeOffset.UtcNow);

        Assert.Equal(WebPageStatus.NeedsBrowser, page.Status);
        Assert.Null(page.Content);
    }

    [Fact]
    public void File_Names_Are_Readable_And_Unique_Per_Address()
    {
        var name = WebPageConverter.FileName(new Uri("https://www.example.com/plans/pricing.html"), ".md");
        Assert.Matches("^example-com-plans-pricing-[0-9a-f]{8}\\.md$", name);
        Assert.NotEqual(name, WebPageConverter.FileName(new Uri("https://www.example.com/plans/pricing.html?v=2"), ".md"));
    }

    [Fact]
    public async Task A_Refresh_Asks_Conditionally_And_304_Has_No_Body()
    {
        var web = new FakeWeb();
        web.Respond(Pricing, request => request.Headers.IfNoneMatch.ToString() == "\"v1\""
            ? new HttpResponseMessage(HttpStatusCode.NotModified)
            : FakeWeb.Ok(FakeWeb.Page("Pricing", $"<p>{Filler}</p>"), "text/html", etag: "\"v1\""));
        var fetcher = new WebFetcher(web.Client());

        var first = await fetcher.FetchAsync(Pricing, null, null, TestContext.Current.CancellationToken);
        Assert.Equal(200, first.Status);
        Assert.Equal("\"v1\"", first.ETag);

        var again = await fetcher.FetchAsync(Pricing, first.ETag, null, TestContext.Current.CancellationToken);
        Assert.Equal(304, again.Status);
        Assert.Empty(again.Body);
    }

    [Fact]
    public async Task Redirects_Are_Followed_Up_To_The_Limit()
    {
        var web = new FakeWeb();
        for (var i = 0; i < 10; i++)
        {
            var next = new Uri($"https://example.com/hop/{i + 1}");
            web.Respond(new Uri($"https://example.com/hop/{i}"), _ => Redirect(next));
        }

        web.Html(new Uri("https://example.com/hop/5"), "Arrived", $"<p>{Filler}</p>");
        var fetcher = new WebFetcher(web.Client());

        var arrived = await fetcher.FetchAsync(new Uri("https://example.com/hop/0"), null, null, TestContext.Current.CancellationToken);
        Assert.Equal("https://example.com/hop/5", arrived.FinalUrl.AbsoluteUri);

        web.Respond(new Uri("https://example.com/hop/5"), _ => Redirect(new Uri("https://example.com/hop/6")));
        var tooMany = await Assert.ThrowsAsync<WebFetchException>(() => fetcher.FetchAsync(new Uri("https://example.com/hop/0"), null, null, TestContext.Current.CancellationToken));
        Assert.True(tooMany.Permanent);
        Assert.Contains("redirects more than 5", tooMany.Message, StringComparison.Ordinal);

        web.Respond(new Uri("https://example.com/away"), _ => Redirect(new Uri("ftp://example.com/file")));
        await Assert.ThrowsAsync<WebFetchException>(() => fetcher.FetchAsync(new Uri("https://example.com/away"), null, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_Page_Over_The_Size_Limit_Is_Refused()
    {
        var web = new FakeWeb();
        web.Respond(Pricing, _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[WebFetcher.MaxBytes + 1]) });

        var failure = await Assert.ThrowsAsync<WebFetchException>(() => new WebFetcher(web.Client()).FetchAsync(Pricing, null, null, TestContext.Current.CancellationToken));
        Assert.True(failure.Permanent);
        Assert.Contains("larger than 20 MB", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Site_That_Does_Not_Answer_Times_Out()
    {
        var web = new FakeWeb();
        web.Respond(Pricing, _ => throw new InvalidOperationException("never called"), hang: true);

        var failure = await Assert.ThrowsAsync<WebFetchException>(() =>
            new WebFetcher(web.Client(), TimeSpan.FromMilliseconds(200)).FetchAsync(Pricing, null, null, TestContext.Current.CancellationToken));
        Assert.False(failure.Permanent);
        Assert.Contains("did not answer", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://example.com/a#part", true)]
    [InlineData("http://intranet/wiki", true)]
    [InlineData("ftp://example.com/a", false)]
    [InlineData("file:///C:/secret.txt", false)]
    [InlineData("example.com", false)]
    public void Only_Web_Addresses_Are_Accepted(string text, bool accepted)
    {
        Assert.Equal(accepted, WebFetcher.TryParse(text, out var url));
        if (accepted)
        {
            Assert.Empty(url.Fragment);
        }
    }

    [Fact]
    public async Task A_Page_Is_Indexed_Searched_And_Cited_By_Its_Address()
    {
        var ct = TestContext.Current.CancellationToken;
        var web = new FakeWeb();
        web.Html(Pricing, "Pricing", $"<h2>Plans</h2><p>The Heliotrope plan costs 12 euros per seat each month. {Filler}</p>");
        using var harness = Harness(web, withChat: true);
        var project = harness.Manager.Create("Web", settings: CoreHarness.FakeSettings() with { DocumentOverviews = false });
        var runtime = harness.Manager.Open(project.Id);

        var (added, rejected) = runtime.AddWebPages([Pricing.AbsoluteUri, "not a url"]);
        Assert.Single(added);
        Assert.Equal(["not a url"], rejected);
        await runtime.WaitForIdleAsync(ct);

        var page = Assert.Single(runtime.WebPages());
        Assert.Equal(WebPageStatus.Ok, page.Status);
        Assert.Equal("Pricing", page.Title);
        var path = Path.Combine(runtime.WebFolder, page.FileName);
        Assert.True(File.Exists(path));
        Assert.Equal(page, runtime.WebPageOf(path));

        var hits = await runtime.SearchAsync("Heliotrope plan price", cancellationToken: ct);
        Assert.Equal(path, hits[0].Chunk.DocumentId.Value);

        var answer = await runtime.AskAsync(null, "What does the Heliotrope plan cost?", ct);
        var cited = answer.Sources.Where(s => s.DocumentId == path).ToList();
        Assert.Contains(cited, s => s.Text.Contains("Heliotrope", StringComparison.Ordinal));
        Assert.All(cited, s => Assert.Equal(Pricing.AbsoluteUri, s.Url));
        Assert.All(cited, s => Assert.Equal("Pricing", s.FileName));
        var context = harness.Chat.Requests.Last().Last().Text;
        Assert.Contains("Pricing — example.com", context, StringComparison.Ordinal);
        Assert.Contains("(web page, fetched ", context, StringComparison.Ordinal);

        // The citation keeps its address when the conversation is read back.
        var stored = runtime.Messages(answer.ConversationId).Last().Sources;
        Assert.Contains(stored, s => s.Url == Pricing.AbsoluteUri);
    }

    [Fact]
    public async Task An_Unchanged_Refresh_Rewrites_Nothing_And_Builds_No_Overview()
    {
        var ct = TestContext.Current.CancellationToken;
        var web = new FakeWeb();
        web.Html(Pricing, "Pricing", $"<p>The Heliotrope plan costs 12 euros. {Filler}</p>");
        using var harness = Harness(web, withChat: true);
        var project = harness.Manager.Create("Web", settings: CoreHarness.FakeSettings() with { DocumentOverviews = true });
        var runtime = harness.Manager.Open(project.Id);
        runtime.AddWebPages([Pricing.AbsoluteUri]);
        await runtime.WaitForIdleAsync(ct);

        var page = Assert.Single(runtime.WebPages());
        var path = Path.Combine(runtime.WebFolder, page.FileName);
        var written = File.GetLastWriteTimeUtc(path);
        Assert.NotNull(runtime.Overview(path));
        var calls = harness.Chat.Requests.Count;

        // Same content, no validators: the hash decides. Then a 304.
        runtime.RefreshWebPage(page.Id);
        await runtime.WaitForIdleAsync(ct);
        web.Respond(Pricing, _ => new HttpResponseMessage(HttpStatusCode.NotModified));
        runtime.RefreshAllWebPages();
        await runtime.WaitForIdleAsync(ct);

        Assert.Equal(written, File.GetLastWriteTimeUtc(path));
        Assert.Equal(calls, harness.Chat.Requests.Count);
        Assert.Equal(WebPageStatus.Ok, runtime.WebPages().Single().Status);
        Assert.True(runtime.WebPages().Single().FetchedUtc > page.FetchedUtc);
    }

    [Fact]
    public async Task A_Changed_Refresh_Reindexes_And_Memory_Stops_Recalling_The_Old_Answer()
    {
        var ct = TestContext.Current.CancellationToken;
        var web = new FakeWeb();
        web.Html(Pricing, "Pricing", $"<p>The hotel cap is 150 euros a night. {Filler}</p>");
        using var harness = Harness(web, withChat: true);
        var project = harness.Manager.Create("Web", settings: CoreHarness.FakeSettings() with { MemoryMinScore = 0.1, DocumentOverviews = false });
        var runtime = harness.Manager.Open(project.Id);
        runtime.AddWebPages([Pricing.AbsoluteUri]);
        await runtime.WaitForIdleAsync(ct);

        await runtime.AskAsync(null, "What is the hotel cap?", ct);
        harness.Chat.Requests.Clear();
        await runtime.AskAsync(null, "What is the hotel cap?", ct);
        Assert.True(Recalled(harness.Chat.Requests.Single()));

        web.Html(Pricing, "Pricing", $"<p>The hotel cap is 180 euros a night. {Filler}</p>");
        runtime.RefreshWebPage(runtime.WebPages().Single().Id);
        await runtime.WaitForIdleAsync(ct);

        var hits = await runtime.SearchAsync("hotel cap euros", cancellationToken: ct);
        Assert.Contains("180 euros", hits[0].Chunk.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(hits, h => h.Chunk.Text.Contains("150 euros", StringComparison.Ordinal));

        harness.Chat.Requests.Clear();
        await runtime.AskAsync(null, "What is the hotel cap?", ct);
        Assert.False(Recalled(harness.Chat.Requests.Single()));
    }

    [Fact]
    public async Task Pages_Older_Than_The_Refresh_Interval_Are_Downloaded_Again()
    {
        var ct = TestContext.Current.CancellationToken;
        var web = new FakeWeb();
        web.Html(Pricing, "Pricing", $"<p>The Heliotrope plan costs 12 euros. {Filler}</p>");
        using var harness = Harness(web);
        var project = harness.Manager.Create("Web", settings: CoreHarness.FakeSettings() with { WebRefreshHours = 24 });
        var runtime = harness.Manager.Open(project.Id);
        runtime.AddWebPages([Pricing.AbsoluteUri]);
        await runtime.WaitForIdleAsync(ct);
        Assert.Equal(1, web.Requests(Pricing));

        // Fetched just now: not due.
        Assert.Equal(0, runtime.RefreshStaleWebPages());
        Assert.Equal(0, runtime.RefreshStaleWebPages(DateTimeOffset.UtcNow.AddHours(23)));

        Assert.Equal(1, runtime.RefreshStaleWebPages(DateTimeOffset.UtcNow.AddHours(25)));
        await runtime.WaitForIdleAsync(ct);
        Assert.Equal(2, web.Requests(Pricing));
    }

    [Fact]
    public async Task Zero_Hours_Refreshes_Only_On_Demand()
    {
        var ct = TestContext.Current.CancellationToken;
        var web = new FakeWeb();
        web.Html(Pricing, "Pricing", $"<p>The Heliotrope plan costs 12 euros. {Filler}</p>");
        using var harness = Harness(web);
        var project = harness.Manager.Create("Web", settings: CoreHarness.FakeSettings() with { WebRefreshHours = 0 });
        var runtime = harness.Manager.Open(project.Id);
        runtime.AddWebPages([Pricing.AbsoluteUri]);
        await runtime.WaitForIdleAsync(ct);

        Assert.Equal(0, runtime.RefreshStaleWebPages(DateTimeOffset.UtcNow.AddYears(1)));
    }

    [Fact]
    public async Task Remove_Clears_The_Document_And_The_File()
    {
        var ct = TestContext.Current.CancellationToken;
        var web = new FakeWeb();
        web.Html(Pricing, "Pricing", $"<p>The Heliotrope plan costs 12 euros. {Filler}</p>");
        using var harness = Harness(web);
        var project = harness.Manager.Create("Web", settings: CoreHarness.FakeSettings());
        var runtime = harness.Manager.Open(project.Id);
        runtime.AddWebPages([Pricing.AbsoluteUri]);
        await runtime.WaitForIdleAsync(ct);
        var page = runtime.WebPages().Single();
        var path = Path.Combine(runtime.WebFolder, page.FileName);
        Assert.Single(runtime.Documents());

        runtime.RemoveWebPage(page.Id);
        await runtime.WaitForIdleAsync(ct);

        Assert.Empty(runtime.WebPages());
        Assert.Empty(runtime.Documents());
        Assert.False(File.Exists(path));
        Assert.Throws<KeyNotFoundException>(() => runtime.RemoveWebPage(page.Id));
    }

    [Fact]
    public async Task A_Failed_Fetch_Shows_In_Activity_With_The_Address()
    {
        var ct = TestContext.Current.CancellationToken;
        var web = new FakeWeb();
        var gone = new Uri("https://example.com/gone");
        web.Respond(gone, _ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var app = new Uri("https://example.com/app");
        web.Respond(app, _ => FakeWeb.Ok("<html><body><div id='root'></div></body></html>", "text/html"));
        using var harness = Harness(web);
        var project = harness.Manager.Create("Web", settings: CoreHarness.FakeSettings());
        var runtime = harness.Manager.Open(project.Id);

        runtime.AddWebPages([gone.AbsoluteUri, app.AbsoluteUri]);
        await runtime.WaitForIdleAsync(ct);

        var failures = runtime.RecentFailures();
        Assert.Contains(failures, f => f.Path == gone.AbsoluteUri && f.Error!.Contains("404", StringComparison.Ordinal));
        Assert.Contains(failures, f => f.Path == app.AbsoluteUri);
        Assert.Equal(WebPageStatus.Failed, runtime.WebPages().Single(p => p.Url == gone.AbsoluteUri).Status);
        Assert.Equal(WebPageStatus.NeedsBrowser, runtime.WebPages().Single(p => p.Url == app.AbsoluteUri).Status);
        Assert.Empty(runtime.Documents());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_Imported_Or_Duplicated_Project_Indexes_Its_Pages_Under_Its_Own_Folder(bool duplicate)
    {
        var ct = TestContext.Current.CancellationToken;
        var web = new FakeWeb();
        web.Html(Pricing, "Pricing", $"<p>The Heliotrope plan costs 12 euros. {Filler}</p>");
        using var harness = Harness(web);
        var original = harness.Manager.Create("Original", settings: CoreHarness.FakeSettings());
        var runtime = harness.Manager.Open(original.Id);
        runtime.AddWebPages([Pricing.AbsoluteUri]);
        await runtime.WaitForIdleAsync(ct);
        var originalFolder = runtime.WebFolder;

        var copy = duplicate
            ? harness.Manager.Duplicate(original.Id, "Copy")
            : harness.Manager.Import(harness.Manager.Export(original.Id), "Copy");

        // The copy must not depend on the original's folder: it is gone before the copy first opens.
        harness.Manager.Delete(original.Id);
        Assert.False(Directory.Exists(originalFolder));

        var copyRuntime = harness.Manager.Open(copy.Id);
        await copyRuntime.WaitForIdleAsync(ct);

        var page = Assert.Single(copyRuntime.WebPages());
        var path = Path.Combine(copyRuntime.WebFolder, page.FileName);
        Assert.True(File.Exists(path));
        Assert.Equal([path], copyRuntime.Documents().Select(d => d.DocumentId));
        var hits = await copyRuntime.SearchAsync("Heliotrope plan", cancellationToken: ct);
        Assert.Equal(path, hits[0].Chunk.DocumentId.Value);
        Assert.Equal(1, web.Requests(Pricing));
    }

    private const string Filler =
        "Prices are listed before tax and are billed monthly or yearly. A yearly plan is billed once, at the start of the " +
        "year, and seats added during the year are billed for the months left. Every plan includes support by email.";

    private static CoreHarness Harness(FakeWeb web, bool withChat = false) =>
        new(withChat: withChat, runtimeOptions: new ProjectRuntime.RuntimeOptions
        {
            WatchDebounce = TimeSpan.FromMilliseconds(200),
            WebClient = web.Client(),
        });

    private static HttpResponseMessage Redirect(Uri to)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = to;
        return response;
    }

    private static bool Recalled(IReadOnlyList<Microsoft.Extensions.AI.ChatMessage> request) =>
        request.Any(m => m.Role == Microsoft.Extensions.AI.ChatRole.System && m.Text.StartsWith("From a previous conversation", StringComparison.Ordinal));

    /// <summary>A web of fixed responses by address, which counts the requests each address receives.</summary>
    private sealed class FakeWeb : HttpMessageHandler
    {
        private readonly Dictionary<string, (Func<HttpRequestMessage, HttpResponseMessage> Respond, bool Hang)> _pages = [];
        private readonly Dictionary<string, int> _requests = [];

        public HttpClient Client() => new(this, disposeHandler: false);

        public void Respond(Uri url, Func<HttpRequestMessage, HttpResponseMessage> respond, bool hang = false)
        {
            lock (_pages)
            {
                _pages[url.AbsoluteUri] = (respond, hang);
            }
        }

        public void Html(Uri url, string title, string body, string nav = "") =>
            Respond(url, _ => Ok(Page(title, body, nav), "text/html"));

        public int Requests(Uri url)
        {
            lock (_pages)
            {
                return _requests.GetValueOrDefault(url.AbsoluteUri);
            }
        }

        public static string Page(string title, string body, string nav = "") =>
            $"<!doctype html><html><head><title>{title}</title></head><body><header>{nav}</header>" +
            $"<main><article><h1>{title}</h1>{body}</article></main><footer>© Example Ltd. All rights reserved.</footer></body></html>";

        public static HttpResponseMessage Ok(string body, string type, string? etag = null)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, type) };
            if (etag is not null)
            {
                response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag);
            }

            return response;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            (Func<HttpRequestMessage, HttpResponseMessage> Respond, bool Hang) page;
            lock (_pages)
            {
                var key = request.RequestUri!.AbsoluteUri;
                _requests[key] = _requests.GetValueOrDefault(key) + 1;
                if (!_pages.TryGetValue(key, out page))
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }
            }

            if (page.Hang)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return page.Respond(request);
        }
    }
}
