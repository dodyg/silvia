using System.Net;
using System.Text;
using Silvia.Core.Models;
using Silvia.Core.Secrets;
using Silvia.Core.Tests.Harness;
using Xunit;

namespace Silvia.Core.Tests;

public sealed class SecretsAndDownloadsTests
{
    [Fact]
    public void Secret_Round_Trips_And_Never_Appears_In_Plain_Text()
    {
        using var harness = new CoreHarness();
        var path = Path.Combine(harness.Root, "secrets.bin");
        var store = new SecretStore(path);

        store.Set(SecretStore.DeepSeekKey, "sk-test-0123456789");

        Assert.Equal("sk-test-0123456789", store.Get(SecretStore.DeepSeekKey));
        var onDisk = File.ReadAllBytes(path);
        Assert.DoesNotContain("sk-test-0123456789", Encoding.UTF8.GetString(onDisk), StringComparison.Ordinal);
        Assert.Equal("sk-test-0123456789", new SecretStore(path).Get(SecretStore.DeepSeekKey));
    }

    [Fact]
    public void Removing_A_Secret_Forgets_It()
    {
        using var harness = new CoreHarness();
        var store = new SecretStore(Path.Combine(harness.Root, "secrets.bin"));
        store.Set(SecretStore.DeepSeekKey, "sk-test-0123456789");

        Assert.True(store.Remove(SecretStore.DeepSeekKey));
        Assert.Null(store.Get(SecretStore.DeepSeekKey));
        Assert.False(store.Remove(SecretStore.DeepSeekKey));
    }

    [Fact]
    public async Task Model_Download_Writes_Complete_Files_And_Skips_Existing_Ones()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = new CoreHarness();
        var modelsRoot = Path.Combine(harness.Root, "models");
        var served = new Dictionary<string, byte[]>
        {
            ["https://example.test/a.onnx"] = Encoding.UTF8.GetBytes(new string('a', 300_000)),
            ["https://example.test/a.vocab.txt"] = Encoding.UTF8.GetBytes("[PAD]\n[UNK]\n"),
        };
        var requests = new List<string>();
        using var http = new HttpClient(new FakeHandler(served, requests));
        var files = new[]
        {
            new ModelFile("https://example.test/a.onnx", "a.onnx"),
            new ModelFile("https://example.test/a.vocab.txt", "a.vocab.txt"),
        };

        var progress = new List<ModelDownloadProgress>();
        await new ModelDownloader(http, modelsRoot, files).DownloadAsync(new Progress<ModelDownloadProgress>(progress.Add), ct);

        Assert.Equal(served["https://example.test/a.onnx"], File.ReadAllBytes(Path.Combine(modelsRoot, "a.onnx")));
        Assert.False(File.Exists(Path.Combine(modelsRoot, "a.onnx.partial")));
        Assert.Equal(2, requests.Count);

        // A second run finds both files and fetches nothing.
        requests.Clear();
        await new ModelDownloader(http, modelsRoot, files).DownloadAsync(null, ct);
        Assert.Empty(requests);
    }

    private sealed class FakeHandler(Dictionary<string, byte[]> served, List<string> requests) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            requests.Add(url);
            if (!served.TryGetValue(url, out var body))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        }
    }
}
