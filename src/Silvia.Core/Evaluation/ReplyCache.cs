using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;

namespace Silvia.Core.Evaluation;

/// <summary>
/// Model replies for evaluations, kept by a hash of the model and the exact prompt. The same question
/// over the same passages gets the same answer and the same grade on a rerun, and costs no call.
/// </summary>
public sealed class ReplyCache(EvalStore store)
{
    public async Task<string> GetAsync(IChatClient chat, string model, IReadOnlyList<ChatMessage> messages, ChatOptions? options, CancellationToken cancellationToken)
    {
        var key = Key(model, messages, options);
        if (store.Cached(key) is { } cached)
        {
            return cached;
        }

        var reply = await chat.GetResponseAsync(messages, options, cancellationToken);
        return reply.Text;
    }

    /// <summary>Keeps a reply once its caller has accepted it, so a reply that failed to parse is asked for again.</summary>
    public void Keep(string model, IReadOnlyList<ChatMessage> messages, ChatOptions? options, string reply) =>
        store.Cache(Key(model, messages, options), reply);

    private static string Key(string model, IReadOnlyList<ChatMessage> messages, ChatOptions? options)
    {
        var text = new StringBuilder(model).Append('\0').Append(options?.Temperature?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-").Append('\0');
        foreach (var message in messages)
        {
            text.Append(message.Role.Value).Append('\0').Append(message.Text).Append('\0');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}
