using NeoAstra.Rpc;
using Silvia.App.Hosting;
using Silvia.Core.Projects;

namespace Silvia.App.Rpc;

/// <summary>
/// A project's confirmed facts: short statements the user knows to be true, given to every answer in
/// the project. They correct what the documents leave out or only imply.
/// </summary>
[NeoRpcService("facts", Version = 1)]
public sealed class FactsService(AppHost host)
{
    [NeoRpcMethod("list")]
    public ValueTask<FactDto[]> ListAsync(ProjectIdScoped request, CancellationToken cancellationToken)
    {
        var runtime = Scope.Runtime(host.Manager, request.ProjectId);
        return ValueTask.FromResult(runtime.Facts().Select(ToDto).ToArray());
    }

    [NeoRpcMethod("add")]
    public ValueTask<FactDto> AddAsync(AddFactRequest request, CancellationToken cancellationToken)
    {
        var runtime = Scope.Runtime(host.Manager, request.ProjectId);
        return ValueTask.FromResult(ToDto(Validated(() => runtime.AddFact(request.Text ?? string.Empty))));
    }

    [NeoRpcMethod("update")]
    public ValueTask<FactDto> UpdateAsync(UpdateFactRequest request, CancellationToken cancellationToken)
    {
        var runtime = Scope.Runtime(host.Manager, request.ProjectId);
        var id = Scope.ParseId(request.Id);
        if (!Validated(() => runtime.UpdateFact(id, request.Text ?? string.Empty)))
        {
            throw Scope.NotFound("fact");
        }

        return ValueTask.FromResult(ToDto(runtime.Facts().Single(f => f.Id == id)));
    }

    [NeoRpcMethod("delete")]
    public ValueTask<EmptyRequest> DeleteAsync(FactIdRequest request, CancellationToken cancellationToken)
    {
        var runtime = Scope.Runtime(host.Manager, request.ProjectId);
        if (!runtime.DeleteFact(Scope.ParseId(request.Id)))
        {
            throw Scope.NotFound("fact");
        }

        return ValueTask.FromResult(new EmptyRequest());
    }

    /// <summary>Runs a store call, turning its validation message into an <c>invalid_request</c>.</summary>
    private static T Validated<T>(Func<T> call)
    {
        try
        {
            return call();
        }
        catch (ArgumentException invalid)
        {
            throw Scope.Invalid(invalid.Message.Split(" (Parameter", 2)[0]);
        }
    }

    private static FactDto ToDto(Fact fact) =>
        new(fact.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), fact.Text, fact.CreatedUtc.ToString("O"));
}
