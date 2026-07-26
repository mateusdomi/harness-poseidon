using Harness.Modules.Tools.Application;
using Harness.Persistence.Abstractions.Governance;
using Harness.SharedKernel.Security;

namespace Harness.Host.Security;

/// <summary>
/// Sink de auditoria das decisões de capability sobre o `audit_ledger` append-only. O
/// <see cref="SecurityPolicyEnforcementPoint"/> decide; este auditor registra: toda decisão —
/// autorizada ou negada — vira entrada imutável e verificável por hash. Sem ele, a decisão do PEP
/// existiria apenas em memória, e o canon trata falha de auditoria como bloqueio, não como
/// detalhe (`governance/rules/security.md`, Default-FAIL).
///
/// O detalhe registrado carrega somente identificadores, código, operação e recurso, já redigidos:
/// nunca entrada de ferramenta, prompt, histórico de agente ou segredo.
///
/// Publicação em canal externo tem gateway próprio (`ChannelOutputGateway`), que audita as
/// próprias negativas; este auditor não duplica esse registro.
/// </summary>
public sealed class LedgerSecurityAuditor(IAuditEventStore ledger) : ICapabilityDecisionAuditSink
{
    private readonly IAuditEventStore _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));

    public async ValueTask RecordAsync(
        CapabilityDecisionAuditRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        _ = await _ledger.AppendAsync(
            new AuditEventAppendCommand(
                record.TenantId,
                ActorKindOf(record.ActorKind),
                record.ActorId,
                record.Allowed ? "capability.allowed" : "capability.denied",
                "work_task",
                record.CardId,
                SecretTextProtector.Redact(string.Join(
                    "; ",
                    $"code={record.Code}",
                    $"operation={record.Operation}",
                    $"resource={record.ResourceId}",
                    $"tool={record.ToolId ?? "-"}",
                    $"path={record.RelativePath ?? "-"}",
                    $"project={record.ProjectId}",
                    $"attempt={record.AttemptId}",
                    $"capability={record.CapabilityId ?? "-"}")),
                record.OccurredAt),
            cancellationToken);
    }

    /// <summary>
    /// Traduz o ator da capability para os tipos aceitos pelo ledger
    /// (<c>user|chief|agent|system</c>): o chefe tem tipo próprio, especialista é agente e worker
    /// entra como componente do sistema.
    /// </summary>
    private static string ActorKindOf(CapabilityActorKind kind) =>
        kind switch
        {
            CapabilityActorKind.Chief => "chief",
            CapabilityActorKind.Specialist => "agent",
            _ => "system",
        };
}
