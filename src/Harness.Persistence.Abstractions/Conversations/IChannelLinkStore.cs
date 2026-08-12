namespace Harness.Persistence.Abstractions.Conversations;

public interface IChannelLinkStore
{
    Task<ChannelLinkRecord> GetOrCreateAsync(
        ChannelLinkCreateCommand command,
        CancellationToken cancellationToken = default);

    Task<ChannelLinkRecord?> GetAsync(
        string tenantId,
        string linkId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ChannelLinkRecord>> ListAsync(
        string tenantId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Registra o instante da última mensagem de entrada recebida neste vínculo.
    /// Insumo durável do roteamento de saída para o último canal ativo da conversa
    /// (contrato: `docs/contracts/channels.md`). Não falha se o vínculo não existir
    /// mais: a marcação é best-effort e nunca deve derrubar o processamento inbound.
    /// </summary>
    Task MarkInboundAsync(
        string tenantId,
        string linkId,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atualiza o nome de exibição humanizado do vínculo quando o adapter
    /// descobre metadados do canal (ex.: título/username do chat Telegram).
    /// Não falha se o vínculo não existir mais.
    /// </summary>
    Task UpdateDisplayNameAsync(
        string tenantId,
        string linkId,
        string displayName,
        CancellationToken cancellationToken = default);
}

public sealed record ChannelLinkRecord(
    string TenantId,
    string Id,
    string Kind,
    string ExternalIdentity,
    string ProfileId,
    string ProjectId,
    string ConversationId,
    DateTimeOffset LinkedAt,
    DateTimeOffset? LastInboundAt = null,
    string? DisplayName = null);

public sealed record ChannelLinkCreateCommand(
    string TenantId,
    string Id,
    string Kind,
    string ExternalIdentity,
    string ProfileId,
    string ProjectId,
    string ConversationId,
    DateTimeOffset OccurredAt,
    string? DisplayName = null);
