namespace Harness.Persistence.Abstractions.Conversations;

/// <summary>
/// A conversa em que o perfil estava, naquele projeto. Ver a migration 0090 para o porquê da chave
/// ser (perfil, projeto) e do estado morar no servidor.
/// </summary>
public sealed record ProfileActiveConversationRecord(
    string TenantId,
    string ProfileId,
    string ProjectId,
    string ConversationId,
    DateTimeOffset UpdatedAt);

public interface IProfileActiveConversationStore
{
    /// <summary>
    /// Registra a conversa aberta. Idempotente por (perfil, projeto): reabrir a mesma conversa só
    /// atualiza o instante, e abrir outra substitui — nunca acumula duas ativas.
    /// </summary>
    Task<ProfileActiveConversationRecord> RememberAsync(
        ProfileActiveConversationRecord record, CancellationToken cancellationToken = default);

    /// <summary>A conversa a restaurar, ou nulo quando o perfil nunca abriu uma nesse projeto.</summary>
    Task<ProfileActiveConversationRecord?> RecallAsync(
        string tenantId, string profileId, string projectId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Esquece a conversa — usado quando ela é arquivada ou apagada. Restaurar uma conversa que já
    /// não existe deixaria o Chat abrindo numa tela vazia, que é o próprio bug de volta.
    /// </summary>
    Task ForgetAsync(
        string tenantId, string profileId, string projectId,
        CancellationToken cancellationToken = default);
}
