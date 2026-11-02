using Microsoft.Win32;
using NeoAstra.Rpc;
using Silvia.App.Hosting;
using Silvia.Core.Models;
using Silvia.Core.Projects;

namespace Silvia.App.Rpc;

/// <summary>Per-project settings, app-wide settings, the known models, and appearance.</summary>
[NeoRpcService("settings", Version = 1)]
public sealed class SettingsService(AppHost host)
{
    private const string DeepSeekKeyVariable = "DEEPSEEK_API_KEY";

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
    [NeoRpcMethod("updateProject")]
    public ValueTask<ProjectSettingsDto> UpdateProjectAsync(UpdateProjectSettingsRequest request, CancellationToken cancellationToken)
    {
        Scope.Runtime(host.Manager, request.ProjectId);
        var current = host.Manager.ReadSettings(request.ProjectId);
        var next = FromDto(request.Settings, current);
        host.Manager.UpdateSettings(request.ProjectId, next);
        return ValueTask.FromResult(ToDto(next));
    }

    [NeoRpcMethod("getGlobal")]
    public ValueTask<GlobalSettingsDto> GetGlobalAsync(EmptyRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new GlobalSettingsDto(
            host.DataRoot,
            host.ModelsRoot,
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(DeepSeekKeyVariable)),
            host.Manager.Options.MaxOpenRuntimes));

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
        s.SystemPrompt);

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
        };
    }
}
