namespace Harness.Persistence.Abstractions.Delivery;

/// <summary>
/// DEL-04/DEL-05/DEL-10 — store durável dos relatórios de entrega. Cada relatório é um SNAPSHOT
/// versionado cujo CONTEÚDO/foto de dados é IMUTÁVEL: só as colunas de ciclo de vida avançam
/// (draft → approved → sent) e o histórico de ENVIOS é append-only. O destinatário de um envio é uma
/// REFERÊNCIA OPACA (`env://`, `secret://`, `keychain://`) — nunca um endereço literal. Deriva a
/// identidade da entrega do projeto; não duplica dados de outras áreas.
/// </summary>
public interface IDeliveryReportStore
{
    /// <summary>Cria um novo relatório em rascunho (snapshot imutável). Retorna a linha persistida.</summary>
    Task<DeliveryReportRecord> CreateAsync(
        DeliveryReportCreateCommand command, CancellationToken cancellationToken = default);

    /// <summary>Retorna o relatório completo (com conteúdo e foto), ou nulo se não existir no escopo.</summary>
    Task<DeliveryReportRecord?> GetAsync(
        string tenantId, string projectId, string reportId, CancellationToken cancellationToken = default);

    /// <summary>Lista os relatórios da entrega (metadados, SEM o corpo nem a foto), mais recentes primeiro.</summary>
    Task<IReadOnlyList<DeliveryReportRecord>> ListByProjectAsync(
        string tenantId, string projectId, int limit, CancellationToken cancellationToken = default);

    /// <summary>Conta os relatórios já existentes de um tipo (para derivar a próxima versão).</summary>
    Task<int> CountByTypeAsync(
        string tenantId, string projectId, string type, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marca como aprovado, registrando o aprovador humano. UPDATE condicional a estar em <c>draft</c>:
    /// devolve <c>true</c> se transicionou, <c>false</c> caso já não estivesse em rascunho (corrida).
    /// </summary>
    Task<bool> ApproveAsync(
        DeliveryReportApproveCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marca como enviado e ANEXA a linha de auditoria de envio, atomicamente. UPDATE condicional a
    /// estar em <c>approved</c>: devolve <c>true</c> se transicionou, <c>false</c> caso contrário. O
    /// destinatário gravado é a referência OPACA — jamais um endereço resolvido.
    /// </summary>
    Task<bool> MarkSentAsync(
        DeliveryReportSendCommand command, CancellationToken cancellationToken = default);

    /// <summary>Lista a auditoria de envios de um relatório (append-only), mais recentes primeiro.</summary>
    Task<IReadOnlyList<DeliveryReportSendRecord>> ListSendsAsync(
        string tenantId, string reportId, int limit, CancellationToken cancellationToken = default);
}

public sealed record DeliveryReportCreateCommand(
    string TenantId,
    string Id,
    string ProjectId,
    string Type,
    string Format,
    string Audience,
    string Classification,
    int Version,
    string ContentType,
    string? Content,
    string DataSnapshotJson,
    DateTimeOffset CreatedAt);

public sealed record DeliveryReportRecord(
    string TenantId,
    string Id,
    string ProjectId,
    string Type,
    string Format,
    string Status,
    string Audience,
    string Classification,
    int Version,
    string ContentType,
    string? Content,
    string? DataSnapshotJson,
    string? ApprovedBy,
    DateTimeOffset? ApprovedAt,
    DateTimeOffset? SentAt,
    DateTimeOffset CreatedAt);

public sealed record DeliveryReportApproveCommand(
    string TenantId,
    string ProjectId,
    string ReportId,
    string ApprovedBy,
    DateTimeOffset ApprovedAt);

public sealed record DeliveryReportSendCommand(
    string TenantId,
    string ProjectId,
    string ReportId,
    string SendId,
    int Version,
    string Channel,
    string RecipientReference,
    string SentBy,
    string Result,
    DateTimeOffset SentAt);

public sealed record DeliveryReportSendRecord(
    string TenantId,
    string Id,
    string ReportId,
    int Version,
    string Channel,
    string RecipientReference,
    string SentBy,
    string Result,
    DateTimeOffset SentAt);
