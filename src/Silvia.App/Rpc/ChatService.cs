using System.Runtime.CompilerServices;
using NeoAstra.Rpc;
using Silvia.App.Hosting;
using Silvia.Core.Conversations;
using Silvia.Core.Runtime;

namespace Silvia.App.Rpc;

/// <summary>Chat (streamed answers with citations, saved conversations) and search without a model.</summary>
[NeoRpcService("chat", Version = 1)]
public sealed class ChatService(AppHost host)
{
    [NeoRpcMethod("listConversations")]
    public ValueTask<ConversationDto[]> ListConversationsAsync(ProjectIdScoped request, CancellationToken cancellationToken)
    {
        var runtime = Scope.Runtime(host.Manager, request.ProjectId);
        return ValueTask.FromResult(runtime.ListConversations()
            .Select(c => new ConversationDto(c.Id, c.Title, c.CreatedUtc.ToString("O")))
            .ToArray());
    }

    [NeoRpcMethod("messages")]
    public ValueTask<MessageDto[]> MessagesAsync(ConversationIdRequest request, CancellationToken cancellationToken)
    {
        var runtime = Scope.Runtime(host.Manager, request.ProjectId);
        if (runtime.GetConversation(request.ConversationId) is null)
        {
            throw Scope.NotFound("conversation");
        }

        return ValueTask.FromResult(runtime.Messages(request.ConversationId)
            .Select(m => new MessageDto(m.Role, m.Content, m.CreatedUtc.ToString("O"), m.Sources.Select(ToDto).ToArray()))
            .ToArray());
    }

    [NeoRpcMethod("deleteConversation")]
    public async ValueTask<EmptyRequest> DeleteConversationAsync(ConversationIdRequest request, CancellationToken cancellationToken)
    {
        var runtime = Scope.Runtime(host.Manager, request.ProjectId);
        if (!await runtime.DeleteConversationAsync(request.ConversationId, cancellationToken))
        {
            throw Scope.NotFound("conversation");
        }

        return new EmptyRequest();
    }

    /// <summary>
    /// A streamed answer. Each item is one step: the conversation id, a piece of text, the cited chunks,
    /// or the final <c>done</c>. A failure ends the stream with a safe error.
    /// </summary>
    [NeoRpcMethod("ask")]
    public NeoRpcChannel<AskUpdateDto> Ask(AskRequest request, CancellationToken cancellationToken) =>
        new(AskUpdates(request, cancellationToken), AppJsonContext.Default.AskUpdateDto);

    private async IAsyncEnumerable<AskUpdateDto> AskUpdates(
        AskRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var runtime = Scope.Runtime(host.Manager, request.ProjectId);
        if (string.IsNullOrWhiteSpace(request.Question))
        {
            throw Scope.Invalid("Ask a question first.");
        }

        var updates = runtime.AskStreamAsync(request.ConversationId, request.Question, cancellationToken);
        await foreach (var update in updates.WithCancellation(cancellationToken))
        {
            yield return Map(update);
        }
    }

    [NeoRpcMethod("search")]
    public async ValueTask<HitDto[]> SearchAsync(SearchRequest request, CancellationToken cancellationToken)
    {
        var runtime = Scope.Runtime(host.Manager, request.ProjectId);
        if (string.IsNullOrWhiteSpace(request.Query))
        {
            throw Scope.Invalid("Enter a search first.");
        }

        var hits = await runtime.SearchAsync(request.Query, request.TopK is > 0 and <= 50 ? request.TopK : null, cancellationToken);
        return hits.Select(h =>
        {
            var cited = CitedChunk.From(h);
            return new HitDto(cited.DocumentId, cited.ChunkIndex, cited.FileName, cited.Text, (int)Math.Round(h.Score * 1000));
        }).ToArray();
    }

    private static AskUpdateDto Map(AnswerUpdate update) => new(
        update.Kind,
        update.ConversationId,
        update.Text,
        update.Sources?.Select(ToDto).ToArray());

    private static CitationDto ToDto(CitedChunk chunk) => new(chunk.DocumentId, chunk.ChunkIndex, chunk.FileName, chunk.Text);
}
