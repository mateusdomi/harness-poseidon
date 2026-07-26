using Harness.Persistence.Abstractions.Conversations;
using Harness.Persistence.Abstractions.Governance;
using Harness.Persistence.Abstractions.Projects;

namespace Harness.Host.Conversations;

/// <summary>
/// Motivo pelo qual uma publicação foi autorizada ou bloqueada. Códigos estáveis: entram em
/// span, ledger e teste, então mudá-los é mudar contrato observável.
/// </summary>
public enum ChannelOutputDecision
{
    /// <summary>Mensagem da Bruna, coerente e no canal ativo: pode publicar.</summary>
    Allowed,

    /// <summary>Autor não é a Bruna — agente, ferramenta ou usuário nunca publica.</summary>
    DeniedNotChief,

    /// <summary>O id do autor não corresponde ao Chief persistido do projeto.</summary>
    DeniedChiefIdentityMismatch,

    /// <summary>Mensagem de outra conversa, projeto ou tenant que não o do vínculo.</summary>
    DeniedCorrelationMismatch,

    /// <summary>O vínculo não é o último canal ativo da conversa.</summary>
    SkippedInactiveChannel,
}

/// <summary>
/// Veredito do gateway. <see cref="Allowed"/> é a única condição em que um adapter publica.
/// </summary>
public sealed record ChannelOutputAuthorization(ChannelOutputDecision Decision)
{
    public bool Allowed => Decision == ChannelOutputDecision.Allowed;

    /// <summary>Código curto para tag de span e resultado de telemetria.</summary>
    public string ResultCode => Decision switch
    {
        ChannelOutputDecision.Allowed => "allowed",
        ChannelOutputDecision.DeniedNotChief => "denied_not_chief",
        ChannelOutputDecision.DeniedChiefIdentityMismatch => "denied_chief_identity_mismatch",
        ChannelOutputDecision.DeniedCorrelationMismatch => "denied_correlation_mismatch",
        ChannelOutputDecision.SkippedInactiveChannel => "skipped_inactive_channel",
        _ => "denied",
    };
}

/// <summary>
/// Output Gateway do canon (`governance/rules/authority.md`): TODA saída externa passa por aqui.
/// Valida identidade da Bruna, correlação (tenant, projeto, conversa), canal ativo, e registra
/// no `audit_ledger` qualquer tentativa de publicação que não seja dela — é o que torna o
/// critério "agentes não publicam" verificável em vez de confiado.
///
/// Dedup e ordenação continuam onde já são fatos duráveis: o cursor por vínculo de cada adapter
/// avança monotonicamente por id de mensagem (ULID léxico), e a entrada é deduplicada pelo inbox
/// durável. O gateway não os reimplementa; ele barra o que aqueles mecanismos não cobrem —
/// autoria e correlação.
///
/// Publicação autorizada NÃO gera linha de ledger: ela já é um span
/// `poseidon.channel.outbound` correlacionado (Fase 1), e duplicar cada mensagem entregue no
/// ledger inflaria um registro append-only imutável sem acrescentar fato. Violação é rara e
/// relevante — essa vai para o ledger.
/// </summary>
public sealed partial class ChannelOutputGateway(
    ActiveChannelRouter router,
    IProjectStore projects,
    IAuditEventStore audit,
    ILogger<ChannelOutputGateway> logger)
{
    private readonly ActiveChannelRouter _router =
        router ?? throw new ArgumentNullException(nameof(router));
    private readonly IAuditEventStore _audit =
        audit ?? throw new ArgumentNullException(nameof(audit));
    private readonly IProjectStore _projects =
        projects ?? throw new ArgumentNullException(nameof(projects));

    /// <summary>
    /// Autoriza (ou nega) a publicação de <paramref name="message"/> no canal
    /// <paramref name="link"/>. Nega e audita quando o autor não é a Bruna ou quando a
    /// correlação não fecha; apenas pula, sem auditar, quando o canal não é o ativo — não é
    /// violação, é roteamento.
    /// </summary>
    public async Task<ChannelOutputAuthorization> AuthorizeAsync(
        string tenantId,
        ChannelLinkRecord link,
        MessageRecord message,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(message);

        if (!string.Equals(message.AuthorRole, "chief", StringComparison.Ordinal))
        {
            await DenyAsync(
                tenantId,
                link,
                message,
                ChannelOutputDecision.DeniedNotChief,
                $"Publicação bloqueada no canal {link.Kind}: autor '{message.AuthorRole}' não é a Bruna.",
                occurredAt,
                cancellationToken);
            return new ChannelOutputAuthorization(ChannelOutputDecision.DeniedNotChief);
        }

        if (!string.Equals(message.TenantId, tenantId, StringComparison.Ordinal) ||
            !string.Equals(message.ConversationId, link.ConversationId, StringComparison.Ordinal) ||
            !string.Equals(message.ProjectId, link.ProjectId, StringComparison.Ordinal))
        {
            await DenyAsync(
                tenantId,
                link,
                message,
                ChannelOutputDecision.DeniedCorrelationMismatch,
                $"Publicação bloqueada no canal {link.Kind}: correlação de tenant, projeto ou conversa divergente.",
                occurredAt,
                cancellationToken);
            return new ChannelOutputAuthorization(ChannelOutputDecision.DeniedCorrelationMismatch);
        }

        var project = await _projects.GetAsync(tenantId, link.ProjectId, cancellationToken);
        if (project is null ||
            string.IsNullOrWhiteSpace(message.AuthorAgentId) ||
            !string.Equals(
                message.AuthorAgentId,
                project.ChiefAgentId,
                StringComparison.Ordinal))
        {
            await DenyAsync(
                tenantId,
                link,
                message,
                ChannelOutputDecision.DeniedChiefIdentityMismatch,
                $"Publicação bloqueada no canal {link.Kind}: identidade do Chief divergente.",
                occurredAt,
                cancellationToken);
            return new ChannelOutputAuthorization(
                ChannelOutputDecision.DeniedChiefIdentityMismatch);
        }

        return await _router.IsActiveAsync(tenantId, link, cancellationToken)
            ? new ChannelOutputAuthorization(ChannelOutputDecision.Allowed)
            : new ChannelOutputAuthorization(ChannelOutputDecision.SkippedInactiveChannel);
    }

    private async Task DenyAsync(
        string tenantId,
        ChannelLinkRecord link,
        MessageRecord message,
        ChannelOutputDecision decision,
        string description,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        LogPublicationDenied(logger, link.Kind, decision.ToString());
        // O conteúdo da mensagem NUNCA entra no ledger: só identificadores e o motivo.
        await _audit.AppendAsync(
            new AuditEventAppendCommand(
                tenantId,
                "system",
                "channel-output-gateway",
                "channel.publication.denied",
                "channel_link",
                link.Id,
                description,
                occurredAt),
            cancellationToken);
    }

    [LoggerMessage(
        EventId = 5601,
        Level = LogLevel.Warning,
        Message = "Output Gateway bloqueou publicação no canal {Channel}: {Decision}.")]
    private static partial void LogPublicationDenied(ILogger logger, string channel, string decision);
}
