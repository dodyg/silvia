using NeoAstra.Rpc;
using Silvia.Core.Projects;
using Silvia.Core.Runtime;

namespace Silvia.App.Rpc;

/// <summary>
/// Resolves the project a call names. Every call except the project list and the app-wide settings
/// goes through here, so a stale or foreign id fails with <c>not_found</c> before any data is touched.
/// </summary>
internal static class Scope
{
    public static ProjectRuntime Runtime(ProjectManager manager, string projectId)
    {
        try
        {
            return manager.Open(projectId);
        }
        catch (ProjectNotFoundException)
        {
            throw NotFound("project");
        }
    }

    public static NeoRpcException NotFound(string what) =>
        new("not_found", $"That {what} does not exist in this project.");

    public static NeoRpcException Invalid(string message) =>
        new("invalid_request", message);

    /// <summary>Parses a numeric id from the wire, failing with <c>invalid_request</c> when it is not one.</summary>
    public static long ParseId(string value) =>
        long.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id)
            ? id
            : throw Invalid("The id is not valid.");
}
