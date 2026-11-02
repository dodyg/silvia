using NeoAstra.Rpc;
using Silvia.App.Hosting;
using Silvia.Core.Catalog;
using Silvia.Core.Models;
using Silvia.Core.Projects;

namespace Silvia.App.Rpc;

/// <summary>Projects: the catalog, lifecycle, and each project's own folders and activity.</summary>
[NeoRpcService("projects", Version = 1)]
public sealed class ProjectsService(AppHost host)
{
    [NeoRpcMethod("list")]
    public ValueTask<ProjectDto[]> ListAsync(EmptyRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(host.Manager.List().Select(ToDto).ToArray());

    [NeoRpcMethod("create")]
    public ValueTask<ProjectDto> CreateAsync(CreateProjectRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var settings = Settings.WithEmbedder(request.EmbedModel);
            return ValueTask.FromResult(ToDto(host.Manager.Create(request.Name, request.Description, settings)));
        }
        catch (Exception ex) when (ex is ProjectNameTakenException or ArgumentException or NotSupportedException)
        {
            throw Scope.Invalid(ex.Message);
        }
    }

    [NeoRpcMethod("rename")]
    public ValueTask<ProjectDto> RenameAsync(RenameProjectRequest request, CancellationToken cancellationToken)
    {
        try
        {
            host.Manager.Rename(request.Id, request.Name);
            return ValueTask.FromResult(ToDto(host.Manager.Get(request.Id)));
        }
        catch (ProjectNotFoundException)
        {
            throw Scope.NotFound("project");
        }
        catch (Exception ex) when (ex is ProjectNameTakenException or ArgumentException)
        {
            throw Scope.Invalid(ex.Message);
        }
    }

    [NeoRpcMethod("delete")]
    public ValueTask<EmptyRequest> DeleteAsync(ProjectIdRequest request, CancellationToken cancellationToken)
    {
        try
        {
            host.Manager.Delete(request.Id);
        }
        catch (ProjectNotFoundException)
        {
            throw Scope.NotFound("project");
        }

        return ValueTask.FromResult(new EmptyRequest());
    }

    [NeoRpcMethod("duplicate")]
    public ValueTask<ProjectDto> DuplicateAsync(RenameProjectRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return ValueTask.FromResult(ToDto(host.Manager.Duplicate(request.Id, request.Name)));
        }
        catch (ProjectNotFoundException)
        {
            throw Scope.NotFound("project");
        }
        catch (ProjectNameTakenException ex)
        {
            throw Scope.Invalid(ex.Message);
        }
    }

    [NeoRpcMethod("export")]
    public ValueTask<ProjectFileDto> ExportAsync(ProjectIdRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return ValueTask.FromResult(new ProjectFileDto(host.Manager.Export(request.Id)));
        }
        catch (ProjectNotFoundException)
        {
            throw Scope.NotFound("project");
        }
    }

    [NeoRpcMethod("import")]
    public ValueTask<ProjectDto> ImportAsync(ImportProjectRequest request, CancellationToken cancellationToken)
    {
        if (!File.Exists(request.FilePath) || !request.FilePath.EndsWith(".silvia", StringComparison.OrdinalIgnoreCase))
        {
            throw Scope.Invalid("Choose a .silvia file exported from Silvia.");
        }

        try
        {
            return ValueTask.FromResult(ToDto(host.Manager.Import(request.FilePath, request.Name)));
        }
        catch (ProjectNameTakenException ex)
        {
            throw Scope.Invalid(ex.Message);
        }
    }

    [NeoRpcMethod("open")]
    public ValueTask<ProjectDto> OpenAsync(ProjectIdRequest request, CancellationToken cancellationToken)
    {
        Scope.Runtime(host.Manager, request.Id);
        return ValueTask.FromResult(ToDto(host.Manager.Get(request.Id)));
    }

    private static ProjectDto ToDto(ProjectRecord record) => new(
        record.Id,
        record.Name,
        record.Description,
        record.CreatedUtc.ToString("O"),
        record.LastOpenedUtc?.ToString("O"),
        ProjectStatusNames.ToDb(record.Status));

    private static class Settings
    {
        public static ProjectSettings WithEmbedder(string? model) =>
            string.IsNullOrWhiteSpace(model) || model == ModelCatalog.DefaultEmbedder
                ? new ProjectSettings()
                : throw new NotSupportedException($"The embedder '{model}' is not available in this build.");
    }
}
