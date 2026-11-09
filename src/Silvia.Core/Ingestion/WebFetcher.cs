using System.Net;
using System.Net.Http.Headers;
using System.Reflection;

namespace Silvia.Core.Ingestion;

/// <summary>
/// Downloads one web page within fixed limits: <c>http</c> and <c>https</c> only, a timeout, at most
/// <see cref="MaxRedirects"/> redirects, at most <see cref="MaxBytes"/>, and no cookies. Local and intranet
/// addresses are allowed: reaching them is the point for a desktop app. A refresh sends the page's
/// <c>ETag</c> and <c>Last-Modified</c>, so an unchanged page answers <c>304</c> with no body.
/// </summary>
public sealed class WebFetcher(HttpClient http, TimeSpan? timeout = null)
{
    public const int MaxRedirects = 5;

    public const long MaxBytes = 20L * 1024 * 1024;

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private static readonly Lazy<HttpClient> Shared = new(CreateClient);

    /// <summary>The client the app uses: redirects are followed here, one by one, so each hop is checked.</summary>
    public static HttpClient SharedClient => Shared.Value;

    private readonly TimeSpan _timeout = timeout ?? DefaultTimeout;

    public static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>
    /// Fetches <paramref name="url"/>. Returns a <c>304</c> result with no body when the page has not changed
    /// since <paramref name="etag"/> or <paramref name="lastModified"/>. Throws <see cref="WebFetchException"/>
    /// with a sentence for the user when the page cannot be fetched.
    /// </summary>
    public async Task<WebFetch> FetchAsync(Uri url, string? etag, string? lastModified, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        try
        {
            return await FetchWithinAsync(url, etag, lastModified, deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new WebFetchException($"The site did not answer within {_timeout.TotalSeconds:0} seconds.", permanent: false);
        }
        catch (HttpRequestException ex)
        {
            throw new WebFetchException($"The site could not be reached: {ex.Message}", permanent: false);
        }
    }

    private async Task<WebFetch> FetchWithinAsync(Uri url, string? etag, string? lastModified, CancellationToken cancellationToken)
    {
        var current = url;
        for (var hop = 0; ; hop++)
        {
            EnsureWebAddress(current);
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Silvia", Version));
            // A browser's Accept: some sites answer 404 when text/markdown is named, looking for a variant they lack.
            request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,*/*;q=0.8");
            if (!string.IsNullOrEmpty(etag))
            {
                request.Headers.TryAddWithoutValidation("If-None-Match", etag);
            }

            if (!string.IsNullOrEmpty(lastModified))
            {
                request.Headers.TryAddWithoutValidation("If-Modified-Since", lastModified);
            }

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var status = (int)response.StatusCode;
            if (status is 301 or 302 or 303 or 307 or 308)
            {
                if (hop >= MaxRedirects)
                {
                    throw new WebFetchException($"The page redirects more than {MaxRedirects} times.", permanent: true);
                }

                var location = response.Headers.Location
                    ?? throw new WebFetchException("The page redirects without saying where.", permanent: true);
                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                continue;
            }

            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                return new WebFetch(304, current, null, null, [], etag, lastModified);
            }

            if (!response.IsSuccessStatusCode)
            {
                // A client error will not change by asking again; a server error might.
                throw new WebFetchException(
                    $"The site answered {status} {response.ReasonPhrase}.".Replace(" .", ".", StringComparison.Ordinal),
                    permanent: status is >= 400 and < 500 and not 408 and not 429);
            }

            if (response.Content.Headers.ContentLength > MaxBytes)
            {
                throw new WebFetchException(TooLarge, permanent: true);
            }

            var body = await ReadCappedAsync(response.Content, cancellationToken);
            return new WebFetch(
                status,
                current,
                response.Content.Headers.ContentType?.MediaType,
                response.Content.Headers.ContentType?.CharSet,
                body,
                response.Headers.ETag?.ToString(),
                response.Content.Headers.LastModified?.ToString("R"));
        }
    }

    private static readonly string TooLarge = $"The page is larger than {MaxBytes / (1024 * 1024)} MB.";

    private static async Task<byte[]> ReadCappedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxBytes)
            {
                throw new WebFetchException(TooLarge, permanent: true);
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>Parses an address the user typed. Only absolute <c>http</c> and <c>https</c> addresses are accepted.</summary>
    public static bool TryParse(string text, out Uri url)
    {
        url = null!;
        var trimmed = text.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed) || !IsWebAddress(parsed))
        {
            return false;
        }

        // The fragment only scrolls the page; two addresses that differ in it are the same page.
        url = new UriBuilder(parsed) { Fragment = string.Empty }.Uri;
        return true;
    }

    public static bool IsWebAddress(Uri url) =>
        url.IsAbsoluteUri && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps) && !string.IsNullOrEmpty(url.Host);

    private static void EnsureWebAddress(Uri url)
    {
        if (!IsWebAddress(url))
        {
            throw new WebFetchException("Only http and https addresses can be added.", permanent: true);
        }
    }

    private static readonly string Version =
        typeof(WebFetcher).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(WebFetcher).Assembly.GetName().Version?.ToString(3)
        ?? "1.0";
}

/// <summary>
/// One response: <c>200</c> with the body, or <c>304</c> when the page has not changed. <see cref="FinalUrl"/> is
/// the address after redirects.
/// </summary>
public sealed record WebFetch(
    int Status,
    Uri FinalUrl,
    string? MediaType,
    string? Charset,
    byte[] Body,
    string? ETag,
    string? LastModified);

/// <summary>
/// A page that cannot be fetched or read, with a sentence for the user. A permanent failure is not retried:
/// asking again would give the same answer.
/// </summary>
public sealed class WebFetchException(string message, bool permanent) : Exception(message)
{
    public bool Permanent { get; } = permanent;
}
