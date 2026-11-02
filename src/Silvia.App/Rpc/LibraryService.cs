using System.Diagnostics;
using NeoAstra.Rpc;
using Silvia.App.Hosting;
using Silvia.Core.Ingestion;
using Silvia.Core.Projects;

namespace Silvia.App.Rpc;

/// <summary>Library, activity, and opening documents. Every call names a project and resolves through it.</summary>
[NeoRpcService("library", Version = 1)]
public sealed class LibraryService(AppHost host)
{
    [NeoRpcMethod("listSources")]
    public ValueTask<SourceDto[]> ListSourcesAsync(ProjectIdScoped request, CancellationToken cancellationToken)
    {
        var runtime = Scope.Runtime(host.Manager, request.ProjectId);
        return ValueTask.FromResult(runtime.Sources().Select(ToDto).ToArray());
    }

    [NeoRpcMethod("addSource")]
    public ValueTask<SourceDto> AddSourceAsync(AddSourceRequest request, CancellationToken cancellationToken)
    {
        var runtime = Scope.Runtime(host.Manager, request.ProjectId);
        try
        {
            var id = runtime.AddSource(request.Path, request.IncludeGlobs ?? "**/*", request.ExcludeGlobs ?? string.Empty);
            return ValueTask.FromResult(ToDto(runtime.Sources().Single(s => s.Id == id)));
        }
        catch (DirectoryNotFoundException)
        {
            throw Scope.Invalid("That folder does not exist.");
        }
    }

    /// <summary>
    /// Shows the native folder dialog and returns the chosen folder. It does not add the folder: the page
    /// calls <c>addSource</c> with it, so a typed path and a picked one go through the same checks.
    /// </summary>
    [NeoRpcMethod("pickFolder", TimeoutMilliseconds = 600_000)]
    public async ValueTask<FolderPickDto> PickFolderAsync(EmptyRequest request, CancellationToken cancellationToken)
    {
        var (status, path) = await host.FolderPicker.PickAsync("Choose a folder to index", cancellationToken);
        return new FolderPickDto(status, path);
    }

    [NeoRpcMethod("removeSource")]
    public ValueTask<EmptyRequest> RemoveSourceAsync(SourceIdRequest request, CancellationToken cancellationToken)
    {
        var runtime = Scope.Runtime(host.Manager, request.ProjectId);
        try
        {
            runtime.RemoveSource(Scope.ParseId(request.SourceId));
        }
        catch (KeyNotFoundException)
        {
            throw Scope.NotFound("folder");
        }

        return ValueTask.FromResult(new EmptyRequest());
    }

    [NeoRpcMethod("rescan")]
    public ValueTask<EmptyRequest> RescanAsync(RescanRequest request, CancellationToken cancellationToken)
    {
        var runtime = Scope.Runtime(host.Manager, request.ProjectId);
        runtime.Rescan(request.SourceId is null ? null : Scope.ParseId(request.SourceId));
        return ValueTask.FromResult(new EmptyRequest());
    }

    [NeoRpcMethod("listDocuments")]
    public ValueTask<DocumentDto[]> ListDocumentsAsync(ProjectIdScoped request, CancellationToken cancellationToken)
    {
        var runtime = Scope.Runtime(host.Manager, request.ProjectId);
        return ValueTask.FromResult(runtime.Documents()
            .Select(d => new DocumentDto(d.DocumentId, Path.GetFileName(d.DocumentId), Path.GetDirectoryName(d.DocumentId) ?? string.Empty, d.Chunks))
            .ToArray());
    }

    /// <summary>Which local models this project uses now. Both are optional; without the embedder, search is by keyword.</summary>
    [NeoRpcMethod("modelStatus")]
    public ValueTask<ModelStatusDto> ModelStatusAsync(ProjectIdScoped request, CancellationToken cancellationToken)
    {
        var runtime = Scope.Runtime(host.Manager, request.ProjectId);
        return ValueTask.FromResult(new ModelStatusDto(runtime.CanEmbed, runtime.CanRerank));
    }

    [NeoRpcMethod("activity")]
    public ValueTask<JobSummaryDto> ActivityAsync(ProjectIdScoped request, CancellationToken cancellationToken)
    {
        var runtime = Scope.Runtime(host.Manager, request.ProjectId);
        var counts = runtime.JobCounts();
        var failures = runtime.RecentFailures(20)
            .Select(f => new FailureDto(f.Path, f.Error, f.FinishedUtc))
            .ToArray();
        return ValueTask.FromResult(new JobSummaryDto(
            Pending: counts.GetValueOrDefault("pending"),
            Running: counts.GetValueOrDefault("running"),
            Done: counts.GetValueOrDefault("done"),
            Failed: counts.GetValueOrDefault("failed"),
            RecentFailures: failures));
    }

    /// <summary>Opens a document's file, but only a file this project indexed, under a folder it watches.</summary>
    [NeoRpcMethod("openDocument")]
    public ValueTask<ShellResult> OpenDocumentAsync(OpenDocumentRequest request, CancellationToken cancellationToken)
    {
        var runtime = Scope.Runtime(host.Manager, request.ProjectId);
        var path = AllowedPath(runtime, request.DocumentId);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        return ValueTask.FromResult(new ShellResult(true, "Opened."));
    }

    [NeoRpcMethod("revealDocument")]
    public ValueTask<ShellResult> RevealDocumentAsync(OpenDocumentRequest request, CancellationToken cancellationToken)
    {
        var runtime = Scope.Runtime(host.Manager, request.ProjectId);
        var path = AllowedPath(runtime, request.DocumentId);
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        return ValueTask.FromResult(new ShellResult(true, "Revealed."));
    }

    private static SourceDto ToDto(SourceRow s) =>
        new(s.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), s.Path, s.IncludeGlobs, s.ExcludeGlobs, s.Enabled);

    private static string AllowedPath(Silvia.Core.Runtime.ProjectRuntime runtime, string documentId)
    {
        var indexed = runtime.Documents().Any(d => string.Equals(d.DocumentId, documentId, StringComparison.OrdinalIgnoreCase));
        var underSource = runtime.Sources().Any(s =>
            documentId.StartsWith(s.Path.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        if (!indexed || !underSource || !File.Exists(documentId) || !ContentTypes.IsSupported(documentId))
        {
            throw Scope.NotFound("document");
        }

        return documentId;
    }
}
