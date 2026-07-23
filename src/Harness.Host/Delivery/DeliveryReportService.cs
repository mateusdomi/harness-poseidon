using System.Text.Json;
using Harness.Host.Notifications;
using Harness.Modules.Delivery.Application;
using Harness.Modules.Delivery.Contracts;
using Harness.Persistence.Abstractions.Delivery;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;

namespace Harness.Host.Delivery;

/// <summary>
/// DEL-04/DEL-05/DEL-10 — orquestra a Central de Relatórios. NÃO possui dados próprios nem lógica de
/// domínio: materializa o input da entrega pelo <see cref="DeliveryReadModelService"/> (reuso), compõe
/// o documento com o compositor PURO, renderiza pela costura de renderizadores, e persiste a foto
/// imutável. A APROVAÇÃO é humana e obrigatória antes do envio; o ENVIO externo (DEL-10) roteia pelo
/// <see cref="ExternalNotificationGateway"/> (EXT-07) a uma coordenação sem acesso, por referência
/// OPACA de destinatário. Toda recusa é TIPADA — nunca uma exceção — e nada é enviado por padrão.
/// </summary>
public sealed class DeliveryReportService(
    DeliveryReadModelService readModel,
    IDeliveryReportStore reports,
    ReportRendererRegistry renderers,
    ExternalNotificationGateway gateway,
    IClock clock)
{
    private static readonly JsonSerializerOptions SnapshotOptions = new(JsonSerializerDefaults.Web);

    private readonly DeliveryReadModelService _readModel = readModel;
    private readonly IDeliveryReportStore _reports = reports;
    private readonly ReportRendererRegistry _renderers = renderers;
    private readonly ExternalNotificationGateway _gateway = gateway;
    private readonly IClock _clock = clock;

    public async Task<ReportResult<DeliveryReportContract>> GenerateAsync(
        string tenantId, string deliveryId, string typeToken, string formatToken,
        string? audience, string? classification, CancellationToken token)
    {
        if (!DeliveryReportTokens.TryParseType(typeToken, out var type))
        {
            return Fail<DeliveryReportContract>(400, "invalid_report_type",
                "type must be one of weekly_executive_status, milestone_report, homologation_readiness, production_readiness, closure_dossier.");
        }

        if (!DeliveryReportTokens.TryParseFormat(formatToken, out var format))
        {
            return Fail<DeliveryReportContract>(400, "invalid_report_format",
                "format must be one of markdown, html, csv, json, pdf, pptx, word, zip.");
        }

        var input = await _readModel.BuildForProjectAsync(tenantId, deliveryId, token);
        if (input is null)
        {
            return Fail<DeliveryReportContract>(404, "delivery_not_found", "The delivery does not exist.");
        }

        var now = _clock.UtcNow;
        var spec = new DeliveryReportSpec(
            type,
            string.IsNullOrWhiteSpace(audience) ? "coordination" : audience.Trim(),
            string.IsNullOrWhiteSpace(classification) ? "internal" : classification.Trim(),
            now);
        var document = DeliveryReportComposer.Compose(spec, input);
        var render = _renderers.Render(format, document);

        var version = await _reports.CountByTypeAsync(tenantId, deliveryId, typeToken, token) + 1;
        var command = new DeliveryReportCreateCommand(
            tenantId,
            UlidValue.New(now).ToString(),
            deliveryId,
            typeToken,
            formatToken,
            spec.Audience,
            spec.Classification,
            version,
            render.ContentType ?? ReportRenderResult.NotAvailableReason,
            render.Content,
            JsonSerializer.Serialize(document, SnapshotOptions),
            now);
        var record = await _reports.CreateAsync(command, token);
        return Ok(ToContract(record, document));
    }

    public async Task<ReportResult<DeliveryReportListContract>> ListAsync(
        string tenantId, string deliveryId, CancellationToken token)
    {
        var input = await _readModel.BuildForProjectAsync(tenantId, deliveryId, token);
        if (input is null)
        {
            return Fail<DeliveryReportListContract>(404, "delivery_not_found", "The delivery does not exist.");
        }

        var records = await _reports.ListByProjectAsync(tenantId, deliveryId, 200, token);
        var summaries = records.Select(ToSummary).ToArray();
        return Ok(new DeliveryReportListContract(deliveryId, summaries.Length, summaries));
    }

    public async Task<ReportResult<DeliveryReportContract>> GetAsync(
        string tenantId, string deliveryId, string reportId, CancellationToken token)
    {
        var record = await _reports.GetAsync(tenantId, deliveryId, reportId, token);
        if (record is null)
        {
            return Fail<DeliveryReportContract>(404, "report_not_found", "The report does not exist.");
        }

        return Ok(ToContract(record, Deserialize(record.DataSnapshotJson)));
    }

    public async Task<ReportResult<DeliveryReportContract>> ApproveAsync(
        string tenantId, string deliveryId, string reportId, string approvedBy, CancellationToken token)
    {
        var record = await _reports.GetAsync(tenantId, deliveryId, reportId, token);
        if (record is null)
        {
            return Fail<DeliveryReportContract>(404, "report_not_found", "The report does not exist.");
        }

        if (!DeliveryReportTokens.TryParseStatus(record.Status, out var status))
        {
            return Fail<DeliveryReportContract>(409, "report_invalid_state", "Unknown report state.");
        }

        var transition = DeliveryReportStateMachine.CanApprove(status);
        if (!transition.Allowed)
        {
            return Fail<DeliveryReportContract>(409, transition.Code!, transition.Detail!);
        }

        var now = _clock.UtcNow;
        var applied = await _reports.ApproveAsync(
            new DeliveryReportApproveCommand(tenantId, deliveryId, reportId, approvedBy, now), token);
        if (!applied)
        {
            return Fail<DeliveryReportContract>(409, "report_not_draft",
                "The report was no longer in draft when approval was applied.");
        }

        var updated = record with { Status = "approved", ApprovedBy = approvedBy, ApprovedAt = now };
        return Ok(ToContract(updated, Deserialize(updated.DataSnapshotJson)));
    }

    public async Task<ReportResult<DeliveryReportSendReceiptContract>> SendAsync(
        string tenantId, string deliveryId, string reportId, string channel,
        string recipientReference, string sentBy, CancellationToken token)
    {
        var record = await _reports.GetAsync(tenantId, deliveryId, reportId, token);
        if (record is null)
        {
            return Fail<DeliveryReportSendReceiptContract>(404, "report_not_found", "The report does not exist.");
        }

        if (!DeliveryReportTokens.TryParseStatus(record.Status, out var status))
        {
            return Fail<DeliveryReportSendReceiptContract>(409, "report_invalid_state", "Unknown report state.");
        }

        // Gate 1: aprovação humana. Enviar antes de aprovar é uma recusa tipada (409), nunca exceção.
        var transition = DeliveryReportStateMachine.CanSend(status);
        if (!transition.Allowed)
        {
            return Fail<DeliveryReportSendReceiptContract>(409, transition.Code!, transition.Detail!);
        }

        // Gate 2: destinatário OPACO. Um endereço literal é recusado na fronteira (400), sem despachar.
        try
        {
            ExternalNotificationGateway.EnsureOpaqueRecipient(recipientReference);
        }
        catch (ArgumentException)
        {
            return Fail<DeliveryReportSendReceiptContract>(400, "recipient_not_opaque",
                "The recipient must be an opaque secret reference (env://, secret:// or keychain://).");
        }

        if (string.IsNullOrWhiteSpace(channel))
        {
            return Fail<DeliveryReportSendReceiptContract>(400, "invalid_channel", "A channel is required.");
        }

        // Gate 3: canal configurado. Sem canal, é uma recusa tipada — nada sai por padrão.
        if (!_gateway.ConfiguredChannels.Any(c => string.Equals(c, channel, StringComparison.OrdinalIgnoreCase)))
        {
            return Fail<DeliveryReportSendReceiptContract>(409, "channel_not_configured",
                "The requested notification channel is not configured for this deployment.");
        }

        // Corpo human-readable, independente do formato pedido: renderiza markdown a partir da FOTO.
        var document = Deserialize(record.DataSnapshotJson);
        var subject = document?.Title ?? $"Relatório {record.Type} v{record.Version}";
        var body = document is null
            ? record.Content ?? subject
            : new MarkdownReportRenderer().Render(document).Content ?? subject;

        var dispatch = await _gateway.DispatchAsync(
            new NotificationDispatch(channel, recipientReference, subject, body), token);
        if (!dispatch.Delivered)
        {
            // Recusa tipada com motivo NÃO sensível; nenhuma transição de estado, nada gravado.
            return Fail<DeliveryReportSendReceiptContract>(409, "send_not_delivered",
                dispatch.FailureReason ?? "channel_unavailable");
        }

        var now = _clock.UtcNow;
        var sendId = UlidValue.New(now).ToString();
        var marked = await _reports.MarkSentAsync(
            new DeliveryReportSendCommand(
                tenantId, deliveryId, reportId, sendId, record.Version, channel,
                recipientReference, sentBy, "delivered", now),
            token);
        if (!marked)
        {
            return Fail<DeliveryReportSendReceiptContract>(409, "report_not_approved",
                "The report was no longer approved when the send was recorded.");
        }

        var sent = record with { Status = "sent", SentAt = now };
        var receipt = new DeliveryReportSendReceiptContract(
            reportId, record.Version, channel, sentBy, now, "delivered",
            ToContract(sent, document));
        return Ok(receipt);
    }

    private static ReportDocument? Deserialize(string? snapshot) =>
        string.IsNullOrWhiteSpace(snapshot)
            ? null
            : JsonSerializer.Deserialize<ReportDocument>(snapshot, SnapshotOptions);

    private static DeliveryReportContract ToContract(DeliveryReportRecord r, ReportDocument? document)
    {
        var available = r.Content is not null;
        return new DeliveryReportContract(
            r.Id, r.ProjectId, r.ProjectId, r.Type, r.Format, r.Status, r.Audience, r.Classification,
            r.Version, r.ContentType, available, r.Content,
            available ? null : ReportRenderResult.NotAvailableReason,
            r.ApprovedBy, r.ApprovedAt, r.SentAt, r.CreatedAt, document);
    }

    private static DeliveryReportSummaryContract ToSummary(DeliveryReportRecord r) => new(
        r.Id, r.ProjectId, r.Type, r.Format, r.Status, r.Audience, r.Classification, r.Version,
        r.Content is not null, r.ApprovedBy, r.ApprovedAt, r.SentAt, r.CreatedAt);

    private static ReportResult<T> Ok<T>(T value) => new(value, null);

    private static ReportResult<T> Fail<T>(int status, string title, string detail) =>
        new(default, new ReportError(status, title, detail));
}

/// <summary>Resultado tipado do serviço: um valor de sucesso OU um erro tipado (nunca ambos).</summary>
public sealed record ReportResult<T>(T? Value, ReportError? Error)
{
    public bool IsError => Error is not null;
}

public sealed record ReportError(int Status, string Title, string Detail);
