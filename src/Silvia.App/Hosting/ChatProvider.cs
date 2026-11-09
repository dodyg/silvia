using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Silvia.App.Hosting;

/// <summary>
/// Asks the chat provider (DeepSeek's OpenAI-compatible API) whether a key works and which chat models it
/// offers. One <c>GET /models</c> call answers both, and it costs nothing. The model list is kept for a few
/// minutes, so settings screens can check a model name without calling the provider on every change.
/// </summary>
public sealed class ChatProvider(HttpClient http, Uri endpoint, Func<string?> savedKey)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(15);

    private readonly Lock _gate = new();
    private (string Key, DateTimeOffset At, ProviderCheck Check)? _cached;

    /// <summary>Checks <paramref name="key"/>, or the saved key when it is null. A successful check of the saved key is cached.</summary>
    public async Task<ProviderCheck> CheckAsync(string? key, CancellationToken cancellationToken)
    {
        var candidate = key ?? savedKey();
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return new ProviderCheck(ProviderState.NoKey, []);
        }

        lock (_gate)
        {
            if (_cached is { } hit && hit.Key == candidate && DateTimeOffset.UtcNow - hit.At < CacheFor)
            {
                return hit.Check;
            }
        }

        var check = await CallAsync(candidate, cancellationToken);
        if (check.State == ProviderState.Ok)
        {
            lock (_gate)
            {
                _cached = (candidate, DateTimeOffset.UtcNow, check);
            }
        }

        return check;
    }

    /// <summary>Forgets the cached answer, for example after the key is replaced or removed.</summary>
    public void Forget()
    {
        lock (_gate)
        {
            _cached = null;
        }
    }

    private async Task<ProviderCheck> CallAsync(string key, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CallTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint.AbsoluteUri.TrimEnd('/') + "/models"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        try
        {
            using var response = await http.SendAsync(request, timeout.Token);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return new ProviderCheck(ProviderState.Rejected, []);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new ProviderCheck(ProviderState.Failed, []);
            }

            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var json = await JsonDocument.ParseAsync(body, cancellationToken: timeout.Token);
            var models = json.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
                ? data.EnumerateArray()
                    .Select(m => m.TryGetProperty("id", out var id) ? id.GetString() : null)
                    .OfType<string>()
                    .Order(StringComparer.Ordinal)
                    .ToList()
                : [];
            return new ProviderCheck(ProviderState.Ok, models);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // No connection, a timeout, or a reply that is not the expected JSON.
            return new ProviderCheck(ProviderState.Unreachable, []);
        }
    }
}

public enum ProviderState
{
    Ok,
    NoKey,
    Rejected,
    Unreachable,
    Failed,
}

/// <summary>What the provider said: the state, and the chat models it offers when the key works.</summary>
public sealed record ProviderCheck(ProviderState State, IReadOnlyList<string> Models)
{
    /// <summary>Whether the provider offers <paramref name="model"/>. Unknown (null) unless the check succeeded.</summary>
    public bool? Offers(string model) => State == ProviderState.Ok ? Models.Contains(model, StringComparer.Ordinal) : null;
}
