namespace Harness.Modules.Delivery.Contracts;

// ---------------------------------------------------------------------------------------------------
// DEL-04 — Central de Relatórios / DEL-05 — Encerramento & Valor / DEL-10 — Reporte à coordenação.
//
// Um RELATÓRIO é uma FOTO (snapshot) versionada e IMUTÁVEL de uma entrega, derivada estritamente do
// Projeto 360 (DEL-02) + métricas + previsão honesta (DEL-09) — nada é inventado. O conteúdo é gerado
// em um documento estruturado (format-agnostic) e renderizado para formatos de saída via a costura
// IReportRenderer. Um relatório nasce `draft`, exige APROVAÇÃO HUMANA (`approved`) e só então pode ser
// enviado (`sent`) para uma coordenação sem acesso (DEL-10) por referência OPACA de destinatário.
// ---------------------------------------------------------------------------------------------------

/// <summary>Os tipos de documento de relatório da Central de Relatórios (DEL-04/DEL-05).</summary>
public enum DeliveryReportType
{
    /// <summary>Status executivo semanal.</summary>
    WeeklyExecutiveStatus,

    /// <summary>Relatório de marcos.</summary>
    MilestoneReport,

    /// <summary>Prontidão para homologação.</summary>
    HomologationReadiness,

    /// <summary>Prontidão para produção.</summary>
    ProductionReadiness,

    /// <summary>Dossiê de encerramento (DEL-05).</summary>
    ClosureDossier,
}

/// <summary>
/// Formatos de saída. Markdown/HTML/CSV/JSON têm renderizadores REAIS (BCL, sem dependências pesadas);
/// PDF/PPTX/Word/ZIP são expostos pela costura com um renderizador padrão que devolve
/// `format_not_available` (o binário real é um follow-on).
/// </summary>
public enum DeliveryReportFormat
{
    Markdown,
    Html,
    Csv,
    Json,
    Pdf,
    Pptx,
    Word,
    Zip,
}

/// <summary>Ciclo de vida do relatório: rascunho → aprovado → enviado. Nunca regride.</summary>
public enum DeliveryReportStatus
{
    Draft,
    Approved,
    Sent,
}

// ---------------------------------------------------------------------------------------------------
// Documento estruturado (a "foto dos dados"), independente de formato. É serializado como o snapshot
// imutável do relatório; cada renderizador o transforma em um formato concreto.
// ---------------------------------------------------------------------------------------------------

/// <summary>Um par rótulo/valor de uma seção.</summary>
public sealed record ReportField(string Label, string Value);

/// <summary>Uma tabela de uma seção (cabeçalhos + linhas), derivada de fatos.</summary>
public sealed record ReportTable(
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<string>> Rows);

/// <summary>Uma seção do relatório: campos rótulo/valor e, opcionalmente, uma tabela.</summary>
public sealed record ReportSection(
    string Key,
    string Title,
    IReadOnlyList<ReportField> Fields,
    ReportTable? Table);

/// <summary>
/// O documento estruturado do relatório — a FOTO imutável dos dados. Tudo aqui deriva do 360/métricas/
/// previsão da entrega no instante <see cref="GeneratedAt"/>; renderizadores o convertem em formatos.
/// </summary>
public sealed record ReportDocument(
    string Type,
    string Title,
    string DeliveryId,
    string ProjectKey,
    string Audience,
    string Classification,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<ReportSection> Sections);

// ---------------------------------------------------------------------------------------------------
// Contratos de resposta da API (aditivos — não alteram contratos existentes verificados por drift).
// ---------------------------------------------------------------------------------------------------

/// <summary>Um relatório completo (com conteúdo renderizado e o documento estruturado).</summary>
public sealed record DeliveryReportContract(
    string Id,
    string DeliveryId,
    string ProjectId,
    string Type,
    string Format,
    string Status,
    string Audience,
    string Classification,
    int Version,
    string ContentType,
    bool Available,
    string? Content,
    string? Reason,
    string? ApprovedBy,
    DateTimeOffset? ApprovedAt,
    DateTimeOffset? SentAt,
    DateTimeOffset CreatedAt,
    ReportDocument? Document);

/// <summary>Resumo de um relatório para listagem (sem o corpo renderizado nem o documento).</summary>
public sealed record DeliveryReportSummaryContract(
    string Id,
    string DeliveryId,
    string Type,
    string Format,
    string Status,
    string Audience,
    string Classification,
    int Version,
    bool Available,
    string? ApprovedBy,
    DateTimeOffset? ApprovedAt,
    DateTimeOffset? SentAt,
    DateTimeOffset CreatedAt);

public sealed record DeliveryReportListContract(
    string DeliveryId,
    int Total,
    IReadOnlyList<DeliveryReportSummaryContract> Reports);

/// <summary>
/// Recibo de envio (DEL-10): registra QUEM/QUANDO/VERSÃO e o canal. O destinatário permanece uma
/// referência OPACA — nunca um endereço literal — e por isso não é ecoado aqui.
/// </summary>
public sealed record DeliveryReportSendReceiptContract(
    string ReportId,
    int Version,
    string Channel,
    string SentBy,
    DateTimeOffset SentAt,
    string Result,
    DeliveryReportContract Report);

// ---------------------------------------------------------------------------------------------------
// Tokens canônicos (persistidos como TEXT com CHECK). Um único ponto de verdade para (de)serialização.
// ---------------------------------------------------------------------------------------------------

/// <summary>Mapeia os enums do relatório para/dos tokens canônicos usados na API e na persistência.</summary>
public static class DeliveryReportTokens
{
    public static string ToToken(DeliveryReportType type) => type switch
    {
        DeliveryReportType.WeeklyExecutiveStatus => "weekly_executive_status",
        DeliveryReportType.MilestoneReport => "milestone_report",
        DeliveryReportType.HomologationReadiness => "homologation_readiness",
        DeliveryReportType.ProductionReadiness => "production_readiness",
        DeliveryReportType.ClosureDossier => "closure_dossier",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public static bool TryParseType(string? token, out DeliveryReportType type)
    {
        switch (token)
        {
            case "weekly_executive_status": type = DeliveryReportType.WeeklyExecutiveStatus; return true;
            case "milestone_report": type = DeliveryReportType.MilestoneReport; return true;
            case "homologation_readiness": type = DeliveryReportType.HomologationReadiness; return true;
            case "production_readiness": type = DeliveryReportType.ProductionReadiness; return true;
            case "closure_dossier": type = DeliveryReportType.ClosureDossier; return true;
            default: type = default; return false;
        }
    }

    public static string ToToken(DeliveryReportFormat format) => format switch
    {
        DeliveryReportFormat.Markdown => "markdown",
        DeliveryReportFormat.Html => "html",
        DeliveryReportFormat.Csv => "csv",
        DeliveryReportFormat.Json => "json",
        DeliveryReportFormat.Pdf => "pdf",
        DeliveryReportFormat.Pptx => "pptx",
        DeliveryReportFormat.Word => "word",
        DeliveryReportFormat.Zip => "zip",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    public static bool TryParseFormat(string? token, out DeliveryReportFormat format)
    {
        switch (token)
        {
            case "markdown": format = DeliveryReportFormat.Markdown; return true;
            case "html": format = DeliveryReportFormat.Html; return true;
            case "csv": format = DeliveryReportFormat.Csv; return true;
            case "json": format = DeliveryReportFormat.Json; return true;
            case "pdf": format = DeliveryReportFormat.Pdf; return true;
            case "pptx": format = DeliveryReportFormat.Pptx; return true;
            case "word": format = DeliveryReportFormat.Word; return true;
            case "zip": format = DeliveryReportFormat.Zip; return true;
            default: format = default; return false;
        }
    }

    public static string ToToken(DeliveryReportStatus status) => status switch
    {
        DeliveryReportStatus.Draft => "draft",
        DeliveryReportStatus.Approved => "approved",
        DeliveryReportStatus.Sent => "sent",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    public static bool TryParseStatus(string? token, out DeliveryReportStatus status)
    {
        switch (token)
        {
            case "draft": status = DeliveryReportStatus.Draft; return true;
            case "approved": status = DeliveryReportStatus.Approved; return true;
            case "sent": status = DeliveryReportStatus.Sent; return true;
            default: status = default; return false;
        }
    }
}
