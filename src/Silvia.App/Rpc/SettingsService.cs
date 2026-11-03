using Microsoft.Win32;
using NeoAstra.Rpc;
using Silvia.App.Hosting;
using Silvia.Core.Models;
using Silvia.Core.Projects;
using Silvia.Core.Secrets;

namespace Silvia.App.Rpc;

/// <summary>Per-project settings, app-wide settings, the known models, and appearance.</summary>
[NeoRpcService("settings", Version = 1)]
public sealed class SettingsService(AppHost host)
{

    [NeoRpcMethod("getProject")]
    public ValueTask<ProjectSettingsDto> GetProjectAsync(ProjectIdScoped request, CancellationToken cancellationToken)
    {
        Scope.Runtime(host.Manager, request.ProjectId);
        return ValueTask.FromResult(ToDto(host.Manager.ReadSettings(request.ProjectId)));
    }

    /// <summary>
    /// Saves a project's settings. The project is reopened with them on its next use; changing the
    /// embedder marks its documents for re-embedding.
    /// </summary>
    /// <remarks>
    /// A newly chosen chat model must be one DeepSeek offers to the saved key. When DeepSeek cannot be
    /// asked (no key, no connection), the name is saved as given.
    /// </remarks>
    [NeoRpcMethod("updateProject")]
    public async ValueTask<ProjectSettingsDto> UpdateProjectAsync(UpdateProjectSettingsRequest request, CancellationToken cancellationToken)
    {
        Scope.Runtime(host.Manager, request.ProjectId);
        var current = host.Manager.ReadSettings(request.ProjectId);
        var next = FromDto(request.Settings, current);

        var modelChosen = next.ChatModel != current.ChatModel || (next.CloudPolicy == CloudPolicies.DeepSeek && current.CloudPolicy != CloudPolicies.DeepSeek);
        if (modelChosen && next.CloudPolicy == CloudPolicies.DeepSeek)
        {
            var check = await host.ChatProvider.CheckAsync(null, cancellationToken);
            if (check.Offers(next.ChatModel) == false)
            {
                throw Scope.Invalid($"DeepSeek does not offer the model '{next.ChatModel}'. Choose one of: {string.Join(", ", check.Models)}.");
            }
        }

        if (next.JudgeModel.Length > 0 && next.JudgeModel != current.JudgeModel && next.CloudPolicy == CloudPolicies.DeepSeek)
        {
            var check = await host.ChatProvider.CheckAsync(null, cancellationToken);
            if (check.Offers(next.JudgeModel) == false)
            {
                throw Scope.Invalid($"DeepSeek does not offer the judge model '{next.JudgeModel}'. Choose one of: {string.Join(", ", check.Models)}.");
            }
        }

        host.Manager.UpdateSettings(request.ProjectId, next);
        return ToDto(next);
    }

    [NeoRpcMethod("getGlobal")]
    public ValueTask<GlobalSettingsDto> GetGlobalAsync(EmptyRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new GlobalSettingsDto(
            host.DataRoot,
            host.ModelsRoot,
            !string.IsNullOrWhiteSpace(AppHost.ChatKey(host.Secrets)),
            host.Manager.Options.MaxOpenRuntimes));

    /// <summary>
    /// Checks the key with DeepSeek, then saves it, protected for this Windows user. A key DeepSeek rejects
    /// is not saved; one that cannot be checked (no connection) is saved and the reply says so. The key is
    /// write-only: no call returns it. Open projects are closed so the next use picks it up.
    /// </summary>
    [NeoRpcMethod("setDeepSeekKey")]
    public async ValueTask<ChatProviderDto> SetDeepSeekKeyAsync(SetDeepSeekKeyRequest request, CancellationToken cancellationToken)
    {
        var key = request.Key?.Trim() ?? string.Empty;
        if (key.Length is < 8 or > 512 || key.Any(char.IsControl))
        {
            throw Scope.Invalid("The key does not look like an API key.");
        }

        var check = await host.ChatProvider.CheckAsync(key, cancellationToken);
        if (check.State == ProviderState.Rejected)
        {
            throw Scope.Invalid("DeepSeek rejected this key, so it was not saved. Check it and try again.");
        }

        host.Secrets.Set(SecretStore.DeepSeekKey, key);
        host.ChatProvider.Forget();
        host.Manager.CloseAll();
        return ToDto(check, saved: true);
    }

    /// <summary>Asks DeepSeek whether the saved key works, and which chat models it offers.</summary>
    [NeoRpcMethod("verifyDeepSeekKey")]
    public async ValueTask<ChatProviderDto> VerifyDeepSeekKeyAsync(EmptyRequest request, CancellationToken cancellationToken)
    {
        host.ChatProvider.Forget();
        return ToDto(await host.ChatProvider.CheckAsync(null, cancellationToken), saved: false);
    }

    /// <summary>The chat models DeepSeek offers to the saved key, from a recent check when there is one.</summary>
    [NeoRpcMethod("listChatModels")]
    public async ValueTask<ChatProviderDto> ListChatModelsAsync(EmptyRequest request, CancellationToken cancellationToken) =>
        ToDto(await host.ChatProvider.CheckAsync(null, cancellationToken), saved: false);

    [NeoRpcMethod("removeDeepSeekKey")]
    public ValueTask<EmptyRequest> RemoveDeepSeekKeyAsync(EmptyRequest request, CancellationToken cancellationToken)
    {
        host.Secrets.Remove(SecretStore.DeepSeekKey);
        host.ChatProvider.Forget();
        host.Manager.CloseAll();
        return ValueTask.FromResult(new EmptyRequest());
    }

    private static ChatProviderDto ToDto(ProviderCheck check, bool saved)
    {
        var (state, message) = check.State switch
        {
            ProviderState.Ok => ("ok", saved
                ? $"Saved. DeepSeek accepted the key and offers {Count(check.Models.Count)}."
                : $"The key works. DeepSeek offers {Count(check.Models.Count)}."),
            ProviderState.NoKey => ("no_key", "No key is saved."),
            ProviderState.Rejected => ("rejected", "DeepSeek rejected the saved key. Save a new one."),
            ProviderState.Unreachable => ("unreachable", saved
                ? "Saved, but DeepSeek could not be reached to check it. Verify it once you are online."
                : "DeepSeek could not be reached. Check the connection and try again."),
            _ => ("failed", saved
                ? "Saved, but DeepSeek answered with an error, so the key was not checked."
                : "DeepSeek answered with an error. Try again in a moment."),
        };
        return new ChatProviderDto(state, message, check.Models);

        static string Count(int n) => n == 1 ? "1 chat model" : $"{n} chat models";
    }

    [NeoRpcMethod("startModelDownload")]
    public ValueTask<ModelDownloadStatusDto> StartModelDownloadAsync(EmptyRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(host.Downloads.Start());

    [NeoRpcMethod("modelDownloadStatus")]
    public ValueTask<ModelDownloadStatusDto> ModelDownloadStatusAsync(EmptyRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(host.Downloads.Status());

    [NeoRpcMethod("listModels")]
    public ValueTask<ModelDto[]> ListModelsAsync(EmptyRequest request, CancellationToken cancellationToken)
    {
        var models = ModelCatalog.Known.Select(m => new ModelDto(
            m.Id,
            m.Kind,
            m.Dimensions,
            File.Exists(ModelCatalog.ModelFile(host.ModelsRoot, m.Id)) && File.Exists(ModelCatalog.VocabularyFile(host.ModelsRoot, m.Id)),
            m.Id == ModelCatalog.DefaultEmbedder || m.Id == ModelCatalog.DefaultReranker))
            .ToArray();
        return ValueTask.FromResult(models);
    }

    [NeoRpcMethod("getAppearance")]
    public ValueTask<AppearanceDto> GetAppearanceAsync(EmptyRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Appearance.Load(host.Manager));

    [NeoRpcMethod("updateAppearance")]
    public ValueTask<AppearanceDto> UpdateAppearanceAsync(AppearanceDto request, CancellationToken cancellationToken)
    {
        var valid = Appearance.Validate(request, SystemFonts());
        Appearance.Save(host.Manager, valid);
        return ValueTask.FromResult(valid);
    }

    [NeoRpcMethod("listFonts")]
    public ValueTask<FontListDto> ListFontsAsync(EmptyRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new FontListDto(Appearance.BundledFonts, SystemFonts()));

    private static IReadOnlyList<string> SystemFonts()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        // Installed fonts are listed under these registry keys, as "Family Name (TrueType)" values.
        var names = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts");
            if (key is null)
            {
                continue;
            }

            foreach (var value in key.GetValueNames())
            {
                var family = value.Replace(" (TrueType)", string.Empty, StringComparison.OrdinalIgnoreCase)
                    .Replace(" (OpenType)", string.Empty, StringComparison.OrdinalIgnoreCase)
                    .Replace(" (All res)", string.Empty, StringComparison.OrdinalIgnoreCase)
                    .Trim();
                if (family.Length > 0 && !family.Contains('&') && !family.Contains('\\'))
                {
                    names.Add(family);
                }
            }
        }

        return names.ToList();
    }

    // Below about a thousand tokens an answer keeps little more than its best passage; above the largest
    // context a chat model takes, the budget would never cut anything.
    private const int MinContextTokens = 1_000;
    private const int MaxContextTokens = 200_000;

    private static ProjectSettingsDto ToDto(ProjectSettings s) => new(
        s.EmbedModel,
        s.EmbedDims,
        s.RerankModel,
        s.ChatModel,
        s.CloudPolicy,
        s.ChunkTokens,
        s.ChunkOverlap,
        s.TopK,
        s.MemoryTopK,
        (int)Math.Round(s.MemoryMinScore * 100),
        s.SystemPrompt,
        s.DocumentOverviews,
        s.GraphRag,
        s.JudgeModel,
        s.BridgeSearch,
        s.ContextTokens);

    /// <summary>Applies the editable fields to the stored settings. The embedder keeps its dimension.</summary>
    private static ProjectSettings FromDto(ProjectSettingsDto dto, ProjectSettings current)
    {
        if (!CloudPolicies.IsKnown(dto.CloudPolicy))
        {
            throw Scope.Invalid("Choose a known cloud policy.");
        }

        if (dto.ChunkTokens is < 50 or > 2000 || dto.ChunkOverlap is < 0 or > 500 || dto.ChunkOverlap >= dto.ChunkTokens)
        {
            throw Scope.Invalid("Chunks must be between 50 and 2000 tokens, with a smaller overlap.");
        }

        if (dto.TopK is < 1 or > 30 || dto.MemoryTopK is < 0 or > 10 || dto.MemoryMinPercent is < 0 or > 100)
        {
            throw Scope.Invalid("Retrieval numbers are out of range.");
        }

        if (dto.ContextTokens is not 0 and (< MinContextTokens or > MaxContextTokens))
        {
            throw Scope.Invalid("The context budget is 0 for no limit, or between 1,000 and 200,000 tokens.");
        }

        var embedderChanged = dto.EmbedModel != current.EmbedModel;
        if (embedderChanged && !ModelCatalog.Known.Any(m => m.Id == dto.EmbedModel && m.Kind == "embedder"))
        {
            throw Scope.Invalid("That embedder is not available in this build.");
        }

        var dims = embedderChanged
            ? ModelCatalog.Known.Single(m => m.Id == dto.EmbedModel).Dimensions
            : current.EmbedDims;

        return current with
        {
            EmbedModel = dto.EmbedModel,
            EmbedDims = dims,
            RerankModel = dto.RerankModel,
            ChatModel = dto.ChatModel,
            CloudPolicy = dto.CloudPolicy,
            ChunkTokens = dto.ChunkTokens,
            ChunkOverlap = dto.ChunkOverlap,
            TopK = dto.TopK,
            MemoryTopK = dto.MemoryTopK,
            MemoryMinScore = dto.MemoryMinPercent / 100.0,
            SystemPrompt = dto.SystemPrompt,
            DocumentOverviews = dto.DocumentOverviews,
            GraphRag = dto.GraphRag,
            BridgeSearch = dto.BridgeSearch,
            ContextTokens = dto.ContextTokens,
            JudgeModel = dto.JudgeModel?.Trim() ?? string.Empty,
        };
    }
}
