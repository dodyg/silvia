namespace Silvia.Core.Catalog;

/// <summary>A row of <c>catalog.db</c>: one project, as the catalog knows it.</summary>
public sealed record ProjectRecord(
    string Id,
    string Name,
    string? Description,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? LastOpenedUtc,
    ProjectStatus Status);

/// <summary>
/// Lifecycle state written before a long operation and cleared after it. A row left in
/// <see cref="Importing"/> or <see cref="Deleting"/> after a crash is finished or rolled back at startup.
/// </summary>
public enum ProjectStatus
{
    Ready,
    Importing,
    Deleting,
}

public static class ProjectStatusNames
{
    public static string ToDb(ProjectStatus status) => status switch
    {
        ProjectStatus.Ready => "ready",
        ProjectStatus.Importing => "importing",
        ProjectStatus.Deleting => "deleting",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    public static ProjectStatus FromDb(string value) => value switch
    {
        "ready" => ProjectStatus.Ready,
        "importing" => ProjectStatus.Importing,
        "deleting" => ProjectStatus.Deleting,
        _ => throw new InvalidDataException($"Unknown project status '{value}'."),
    };
}
