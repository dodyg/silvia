using System.Net;
using Silvia.App.Hosting;
using Xunit;

namespace Silvia.App.Tests;

/// <summary>The DeepSeek key and model check, against a stub provider: it never calls the real API.</summary>
public sealed class ChatProviderTests
{
    private static readonly Uri Endpoint = new("https://api.example.test/v1");

    [Fact]
    public async Task A_Working_Key_Lists_The_Models_And_Is_Cached()
    {
        var ct = TestContext.Current.CancellationToken;
        var stub = new StubHandler(HttpStatusCode.OK, """{"object":"list","data":[{"id":"deepseek-v4-pro"},{"id":"deepseek-flash"}]}""");
        var provider = new ChatProvider(new HttpClient(stub), Endpoint, () => "sk-saved-key-123");

        var check = await provider.CheckAsync(null, ct);
        Assert.Equal(ProviderState.Ok, check.State);
        Assert.Equal(["deepseek-flash", "deepseek-v4-pro"], check.Models);
        Assert.True(check.Offers("deepseek-flash"));
        Assert.False(check.Offers("deepseek-chat"));
        Assert.Equal("https://api.example.test/v1/models", stub.LastUri?.AbsoluteUri);
        Assert.Equal("Bearer sk-saved-key-123", stub.LastAuthorization);

        await provider.CheckAsync(null, ct);
        Assert.Equal(1, stub.Calls);
        provider.Forget();
        await provider.CheckAsync(null, ct);
        Assert.Equal(2, stub.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, ProviderState.Rejected)]
    [InlineData(HttpStatusCode.Forbidden, ProviderState.Rejected)]
    [InlineData(HttpStatusCode.InternalServerError, ProviderState.Failed)]
    public async Task Provider_Errors_Map_To_A_State_And_No_Model_Verdict(HttpStatusCode status, ProviderState expected)
    {
        var provider = new ChatProvider(new HttpClient(new StubHandler(status, "{}")), Endpoint, () => "sk-saved-key-123");
        var check = await provider.CheckAsync(null, TestContext.Current.CancellationToken);
        Assert.Equal(expected, check.State);
        Assert.Null(check.Offers("deepseek-flash"));
    }

    [Fact]
    public async Task No_Connection_Is_Unreachable_And_No_Key_Is_Not_Sent()
    {
        var ct = TestContext.Current.CancellationToken;
        var offline = new ChatProvider(new HttpClient(new StubHandler(new HttpRequestException("no route"))), Endpoint, () => "sk-saved-key-123");
        Assert.Equal(ProviderState.Unreachable, (await offline.CheckAsync(null, ct)).State);

        var stub = new StubHandler(HttpStatusCode.OK, "{}");
        var keyless = new ChatProvider(new HttpClient(stub), Endpoint, () => null);
        Assert.Equal(ProviderState.NoKey, (await keyless.CheckAsync(null, ct)).State);
        Assert.Equal(0, stub.Calls);
    }

    [Fact]
    public async Task A_Key_Being_Saved_Is_Checked_Instead_Of_The_Saved_One()
    {
        var stub = new StubHandler(HttpStatusCode.OK, """{"data":[{"id":"deepseek-flash"}]}""");
        var provider = new ChatProvider(new HttpClient(stub), Endpoint, () => "sk-old-key-123");
        await provider.CheckAsync("sk-new-key-456", TestContext.Current.CancellationToken);
        Assert.Equal("Bearer sk-new-key-456", stub.LastAuthorization);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body = string.Empty;
        private readonly Exception? _failure;

        public StubHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        public StubHandler(Exception failure) => _failure = failure;

        public int Calls { get; private set; }

        public Uri? LastUri { get; private set; }

        public string? LastAuthorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastUri = request.RequestUri;
            LastAuthorization = request.Headers.Authorization?.ToString();
            if (_failure is not null)
            {
                throw _failure;
            }

            return Task.FromResult(new HttpResponseMessage(_status) { Content = new StringContent(_body) });
        }
    }
}
