using NeoAstra.Rpc;
using Silvia.App.Hosting;
using Silvia.App.Rpc;
using Silvia.Core.Models;
using Xunit;

namespace Silvia.App.Tests;

/// <summary>
/// The RPC services exercised directly over real projects: two projects stay isolated through every
/// service, foreign ids resolve to not_found, and invalid input is refused with a safe error code.
/// Needs the local ONNX models (scripts/fetch-models.ps1); the test is skipped when they are absent.
/// </summary>
public sealed class RpcServicesTests
{
    [Fact]
    public async Task Two_Projects_Stay_Isolated_Through_The_Rpc_Services()
    {
        var ct = TestContext.Current.CancellationToken;
        var models = Environment.GetEnvironmentVariable("SILVIA_MODELS")
            ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "models"));
        if (!File.Exists(ModelCatalog.ModelFile(models, ModelCatalog.DefaultEmbedder)))
        {
            Assert.Skip("The local models are not downloaded. Run scripts/fetch-models.ps1 first.");
        }

        var data = Path.Combine(Path.GetTempPath(), "silvia-app-tests", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("SILVIA_DATA", data);
        Environment.SetEnvironmentVariable("SILVIA_MODELS", models);
        using var host = AppHost.Create();
        try
        {
            var projects = new ProjectsService(host);
            var library = new LibraryService(host);
            var chat = new ChatService(host);
            var settings = new SettingsService(host);

            var alpha = await projects.CreateAsync(new CreateProjectRequest("Alpha", null, null), ct);
            var beta = await projects.CreateAsync(new CreateProjectRequest("Beta", null, null), ct);
            Assert.Equal(2, (await projects.ListAsync(new EmptyRequest(), ct)).Length);

            var folder = Path.Combine(data, "alpha-notes");
            Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(Path.Combine(folder, "field.md"), "The Lumen kit carries a 40 watt solar charger.", ct);
            await library.AddSourceAsync(new AddSourceRequest(alpha.Id, folder, null, null), ct);
            await host.Manager.Open(alpha.Id).WaitForIdleAsync(ct);

            Assert.Single(await library.ListDocumentsAsync(new ProjectIdScoped(alpha.Id), ct));
            Assert.Empty(await library.ListDocumentsAsync(new ProjectIdScoped(beta.Id), ct));

            var hits = await chat.SearchAsync(new SearchRequest(alpha.Id, "solar charger", 3), ct);
            Assert.NotEmpty(hits);
            Assert.Empty(await chat.SearchAsync(new SearchRequest(beta.Id, "solar charger", 3), ct));

            var inspect = new InspectService(host);
            var field = Path.Combine(folder, "field.md");
            var detail = await inspect.DocumentAsync(new DocumentRequest(alpha.Id, field), ct);
            Assert.Equal("field.md", detail.FileName);
            Assert.Contains(detail.Passages, p => p.Text.Contains("solar charger", StringComparison.Ordinal));
            var hidden = await Assert.ThrowsAsync<NeoRpcException>(() => inspect.DocumentAsync(new DocumentRequest(beta.Id, field), ct).AsTask());
            Assert.Equal("not_found", hidden.Code);
            var trace = await inspect.TraceAsync(new SearchRequest(alpha.Id, "solar charger", 3), ct);
            Assert.Equal("vector", trace.Mode);
            Assert.Equal(hits.Select(h => (h.DocumentId, h.ChunkIndex)), trace.Hits.Select(h => (h.DocumentId, h.ChunkIndex)));
            Assert.NotNull(trace.Hits[0].Similarity);
            var map = await inspect.MapAsync(new ProjectIdScoped(alpha.Id), ct);
            Assert.True(map.Available);
            Assert.All(map.Points, p => Assert.InRange(p.X, 0, 10_000));
            Assert.Empty((await inspect.MapAsync(new ProjectIdScoped(beta.Id), ct)).Points);
            Assert.Equal("field.md", Assert.Single((await inspect.CoverageAsync(new ProjectIdScoped(alpha.Id), ct)).Documents).FileName);
            Assert.Empty((await inspect.CoverageAsync(new ProjectIdScoped(beta.Id), ct)).Documents);

            var answer = await host.Manager.Open(alpha.Id).AskAsync(null, "What does the kit carry?", ct);
            var mine = await chat.MessagesAsync(new ConversationIdRequest(alpha.Id, answer.ConversationId), ct);
            Assert.NotEmpty(mine);
            var foreign = await Assert.ThrowsAsync<NeoRpcException>(() =>
                chat.MessagesAsync(new ConversationIdRequest(beta.Id, answer.ConversationId), ct).AsTask());
            Assert.Equal("not_found", foreign.Code);

            var facts = new FactsService(host);
            var fact = await facts.AddAsync(new AddFactRequest(alpha.Id, "The kit ships in March."), ct);
            Assert.Equal("The kit ships in March.", (await facts.UpdateAsync(new UpdateFactRequest(alpha.Id, fact.Id, "The kit ships in March."), ct)).Text);
            Assert.Single(await facts.ListAsync(new ProjectIdScoped(alpha.Id), ct));
            Assert.Empty(await facts.ListAsync(new ProjectIdScoped(beta.Id), ct));
            var foreignFact = await Assert.ThrowsAsync<NeoRpcException>(() =>
                facts.DeleteAsync(new FactIdRequest(beta.Id, fact.Id), ct).AsTask());
            Assert.Equal("not_found", foreignFact.Code);
            var emptyFact = await Assert.ThrowsAsync<NeoRpcException>(() =>
                facts.AddAsync(new AddFactRequest(alpha.Id, "  "), ct).AsTask());
            Assert.Equal("invalid_request", emptyFact.Code);
            Assert.Equal("A fact needs some text.", emptyFact.Message);

            var rejected = await Assert.ThrowsAsync<NeoRpcException>(() =>
                settings.UpdateAppearanceAsync(Appearance("Not A Real Font"), ct).AsTask());
            Assert.Equal("invalid_request", rejected.Code);

            var saved = await settings.UpdateAppearanceAsync(Appearance("Inter"), ct);
            Assert.Equal("Inter", saved.InterfaceFont);
            Assert.Equal("Inter", (await settings.GetAppearanceAsync(new EmptyRequest(), ct)).InterfaceFont);

            var projectSettings = await settings.GetProjectAsync(new ProjectIdScoped(alpha.Id), ct);
            Assert.Equal(12_000, projectSettings.ContextTokens);
            await settings.UpdateProjectAsync(new UpdateProjectSettingsRequest(alpha.Id, projectSettings with { ContextTokens = 4_000 }), ct);
            Assert.Equal(4_000, (await settings.GetProjectAsync(new ProjectIdScoped(alpha.Id), ct)).ContextTokens);
            await settings.UpdateProjectAsync(new UpdateProjectSettingsRequest(alpha.Id, projectSettings with { ContextTokens = 0 }), ct);
            Assert.Equal(0, host.Manager.ReadSettings(alpha.Id).ContextTokens);
            var tooSmall = await Assert.ThrowsAsync<NeoRpcException>(() =>
                settings.UpdateProjectAsync(new UpdateProjectSettingsRequest(alpha.Id, projectSettings with { ContextTokens = 500 }), ct).AsTask());
            Assert.Equal("invalid_request", tooSmall.Code);

            await projects.DeleteAsync(new ProjectIdRequest(beta.Id), ct);
            Assert.Single(await projects.ListAsync(new EmptyRequest(), ct));
            Assert.False(Directory.Exists(Path.Combine(data, "projects", beta.Id)));
        }
        finally
        {
            Environment.SetEnvironmentVariable("SILVIA_DATA", null);
            Environment.SetEnvironmentVariable("SILVIA_MODELS", null);
        }
    }

    [Fact]
    public async Task Open_Url_Refuses_An_Address_That_Is_Not_One_Of_The_Projects_Pages()
    {
        var ct = TestContext.Current.CancellationToken;
        var data = Path.Combine(Path.GetTempPath(), "silvia-app-tests", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("SILVIA_DATA", data);
        // No models are needed: the project opens in keyword mode.
        Environment.SetEnvironmentVariable("SILVIA_MODELS", Path.Combine(data, "no-models"));
        using var host = AppHost.Create();
        try
        {
            var projects = new ProjectsService(host);
            var library = new LibraryService(host);
            var alpha = await projects.CreateAsync(new CreateProjectRequest("Alpha", null, null), ct);

            // Nothing here is a web address, so nothing is added and nothing is fetched.
            var result = await library.AddWebPagesAsync(new AddWebPagesRequest(alpha.Id, "notes.txt ftp://example.com/a"), ct);
            Assert.Empty(result.Added);
            Assert.Equal(["notes.txt", "ftp://example.com/a"], result.Rejected);
            Assert.Empty(await library.ListWebPagesAsync(new ProjectIdScoped(alpha.Id), ct));

            foreach (var url in new[] { "https://example.com/", "file:///C:/Windows/notepad.exe", "calc.exe", "" })
            {
                var refused = await Assert.ThrowsAsync<NeoRpcException>(() => library.OpenUrlAsync(new OpenUrlRequest(alpha.Id, url), ct).AsTask());
                Assert.Equal("not_found", refused.Code);
            }

            var missing = await Assert.ThrowsAsync<NeoRpcException>(() => library.RemoveWebPageAsync(new WebPageIdRequest(alpha.Id, "42"), ct).AsTask());
            Assert.Equal("not_found", missing.Code);
            var empty = await Assert.ThrowsAsync<NeoRpcException>(() => library.AddWebPagesAsync(new AddWebPagesRequest(alpha.Id, "  "), ct).AsTask());
            Assert.Equal("invalid_request", empty.Code);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SILVIA_DATA", null);
            Environment.SetEnvironmentVariable("SILVIA_MODELS", null);
        }
    }

    private static AppearanceDto Appearance(string interfaceFont) => new(
        Theme: "dark",
        InterfaceSize: 14,
        ReadingSize: 16,
        LineHeight: "comfortable",
        ReadingWidth: "wide",
        InterfaceFont: interfaceFont,
        ReadingFont: "System default",
        MonoFont: "System default");
}
