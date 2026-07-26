using Harness.Persistence.Abstractions.Conversations;

namespace Harness.Host.Conversations;

/// <summary>
/// Notification Router do contrato de canais (`docs/contracts/channels.md`): quando mais de um
/// vínculo compartilha a MESMA conversa, a saída da Bruna vai apenas para o último canal ativo,
/// evitando publicar a mesma resposta em todos os canais do usuário.
///
/// "Ativo" é um fato durável, não heurística: o vínculo com <c>last_inbound_at</c> mais recente
/// — gravado por cada adapter ao aceitar uma entrada. Desempate determinístico por
/// <c>linked_at</c> e, por fim, pelo id do vínculo, para que dois processos cheguem sempre à
/// mesma decisão sem coordenação.
///
/// Vínculo único na conversa (o caso normal, já que cada linking cria a própria conversa) é
/// sempre o ativo, então o comportamento pré-existente dos adapters não muda.
/// </summary>
public sealed class ActiveChannelRouter(IChannelLinkStore links)
{
    private readonly IChannelLinkStore _links =
        links ?? throw new ArgumentNullException(nameof(links));

    /// <summary>
    /// Verdadeiro quando <paramref name="link"/> é o canal de saída eleito para a sua conversa.
    /// </summary>
    public async Task<bool> IsActiveAsync(
        string tenantId,
        ChannelLinkRecord link,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(link);
        var active = await SelectActiveAsync(tenantId, link.ConversationId, cancellationToken);
        return active is null || string.Equals(active.Id, link.Id, StringComparison.Ordinal);
    }

    /// <summary>
    /// Vínculo eleito para receber a saída da conversa, ou <c>null</c> quando não há nenhum.
    /// </summary>
    public async Task<ChannelLinkRecord?> SelectActiveAsync(
        string tenantId,
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        var candidates = (await _links.ListAsync(tenantId, cancellationToken))
            .Where(candidate => string.Equals(
                candidate.ConversationId, conversationId, StringComparison.Ordinal))
            .ToArray();
        if (candidates.Length <= 1)
        {
            return candidates.Length == 1 ? candidates[0] : null;
        }

        return candidates
            .OrderByDescending(candidate => candidate.LastInboundAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(candidate => candidate.LinkedAt)
            .ThenByDescending(candidate => candidate.Id, StringComparer.Ordinal)
            .First();
    }
}
