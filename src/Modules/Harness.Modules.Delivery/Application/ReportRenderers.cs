using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Harness.Modules.Delivery.Contracts;

namespace Harness.Modules.Delivery.Application;

/// <summary>
/// Resultado tipado de uma renderização. Formatos indisponíveis (PDF/PPTX/Word/ZIP hoje) devolvem
/// <see cref="Available"/>=false com <see cref="Reason"/>=`format_not_available` — nunca uma exceção —
/// mantendo a foto de dados intacta para uma renderização binária futura (follow-on).
/// </summary>
public sealed record ReportRenderResult(
    bool Available, string Format, string? ContentType, string? Content, string? Reason)
{
    public const string NotAvailableReason = "format_not_available";

    public static ReportRenderResult Rendered(DeliveryReportFormat format, string contentType, string content) =>
        new(true, DeliveryReportTokens.ToToken(format), contentType, content, null);

    public static ReportRenderResult Unavailable(DeliveryReportFormat format) =>
        new(false, DeliveryReportTokens.ToToken(format), null, null, NotAvailableReason);
}

/// <summary>
/// A costura de renderização (DEL-04): transforma o documento estruturado (foto imutável) em um
/// formato de saída. Renderizadores reais existem para markdown/html/csv/json; a costura fica exposta
/// para os formatos binários, cujo padrão é `format_not_available`.
/// </summary>
public interface IReportRenderer
{
    DeliveryReportFormat Format { get; }

    ReportRenderResult Render(ReportDocument document);
}

/// <summary>
/// Registro de renderizadores por formato. Formatos sem renderizador registrado (PDF/PPTX/Word/ZIP)
/// caem no padrão tipado `format_not_available` em vez de falhar.
/// </summary>
public sealed class ReportRendererRegistry
{
    private readonly Dictionary<DeliveryReportFormat, IReportRenderer> _renderers;

    public ReportRendererRegistry(IEnumerable<IReportRenderer> renderers)
    {
        ArgumentNullException.ThrowIfNull(renderers);
        _renderers = renderers.ToDictionary(r => r.Format, r => r);
    }

    /// <summary>A bateria padrão de renderizadores reais (somente BCL, sem dependências pesadas).</summary>
    public static ReportRendererRegistry Default() => new(
    [
        new MarkdownReportRenderer(),
        new HtmlReportRenderer(),
        new CsvReportRenderer(),
        new JsonReportRenderer(),
    ]);

    public ReportRenderResult Render(DeliveryReportFormat format, ReportDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return _renderers.TryGetValue(format, out var renderer)
            ? renderer.Render(document)
            : ReportRenderResult.Unavailable(format);
    }
}

/// <summary>Renderizador Markdown: títulos, listas rótulo/valor e tabelas GFM.</summary>
public sealed class MarkdownReportRenderer : IReportRenderer
{
    public DeliveryReportFormat Format => DeliveryReportFormat.Markdown;

    public ReportRenderResult Render(ReportDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var sb = new StringBuilder();
        sb.Append("# ").Append(document.Title).Append("\n\n");
        sb.Append("- **Entrega:** ").Append(document.DeliveryId).Append('\n');
        sb.Append("- **Chave:** ").Append(document.ProjectKey).Append('\n');
        sb.Append("- **Público:** ").Append(document.Audience).Append('\n');
        sb.Append("- **Classificação:** ").Append(document.Classification).Append('\n');
        sb.Append("- **Gerado em:** ").Append(Iso(document.GeneratedAt)).Append("\n\n");

        foreach (var section in document.Sections)
        {
            sb.Append("## ").Append(section.Title).Append("\n\n");
            foreach (var field in section.Fields)
            {
                sb.Append("- **").Append(field.Label).Append(":** ").Append(field.Value).Append('\n');
            }

            if (section.Fields.Count > 0)
            {
                sb.Append('\n');
            }

            if (section.Table is { } table && table.Columns.Count > 0)
            {
                sb.Append("| ").Append(string.Join(" | ", table.Columns.Select(Cell))).Append(" |\n");
                sb.Append("| ").Append(string.Join(" | ", table.Columns.Select(_ => "---"))).Append(" |\n");
                foreach (var row in table.Rows)
                {
                    sb.Append("| ").Append(string.Join(" | ", row.Select(Cell))).Append(" |\n");
                }

                sb.Append('\n');
            }
        }

        return ReportRenderResult.Rendered(Format, "text/markdown; charset=utf-8", sb.ToString().TrimEnd() + "\n");
    }

    // Escapa o pipe para não quebrar a célula da tabela GFM.
    private static string Cell(string value) =>
        (value ?? string.Empty).Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

    private static string Iso(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}

/// <summary>Renderizador HTML: documento semântico com &lt;section&gt;/&lt;dl&gt;/&lt;table&gt; e escape.</summary>
public sealed class HtmlReportRenderer : IReportRenderer
{
    public DeliveryReportFormat Format => DeliveryReportFormat.Html;

    public ReportRenderResult Render(ReportDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var sb = new StringBuilder();
        sb.Append("<!doctype html>\n<html lang=\"pt-BR\">\n<head>\n<meta charset=\"utf-8\">\n<title>");
        sb.Append(Enc(document.Title));
        sb.Append("</title>\n</head>\n<body>\n");
        sb.Append("<h1>").Append(Enc(document.Title)).Append("</h1>\n");
        sb.Append("<dl class=\"report-meta\">");
        AppendItem(sb, "Entrega", document.DeliveryId);
        AppendItem(sb, "Chave", document.ProjectKey);
        AppendItem(sb, "Público", document.Audience);
        AppendItem(sb, "Classificação", document.Classification);
        AppendItem(sb, "Gerado em", Iso(document.GeneratedAt));
        sb.Append("</dl>\n");

        foreach (var section in document.Sections)
        {
            sb.Append("<section data-key=\"").Append(Enc(section.Key)).Append("\">\n");
            sb.Append("<h2>").Append(Enc(section.Title)).Append("</h2>\n");
            if (section.Fields.Count > 0)
            {
                sb.Append("<dl>");
                foreach (var field in section.Fields)
                {
                    AppendItem(sb, field.Label, field.Value);
                }

                sb.Append("</dl>\n");
            }

            if (section.Table is { } table && table.Columns.Count > 0)
            {
                sb.Append("<table>\n<thead>\n<tr>");
                foreach (var column in table.Columns)
                {
                    sb.Append("<th>").Append(Enc(column)).Append("</th>");
                }

                sb.Append("</tr>\n</thead>\n<tbody>\n");
                foreach (var row in table.Rows)
                {
                    sb.Append("<tr>");
                    foreach (var cell in row)
                    {
                        sb.Append("<td>").Append(Enc(cell)).Append("</td>");
                    }

                    sb.Append("</tr>\n");
                }

                sb.Append("</tbody>\n</table>\n");
            }

            sb.Append("</section>\n");
        }

        sb.Append("</body>\n</html>\n");
        return ReportRenderResult.Rendered(Format, "text/html; charset=utf-8", sb.ToString());
    }

    private static void AppendItem(StringBuilder sb, string label, string value) =>
        sb.Append("<dt>").Append(Enc(label)).Append("</dt><dd>").Append(Enc(value)).Append("</dd>");

    private static string Enc(string? value) => HtmlEncoder.Default.Encode(value ?? string.Empty);

    private static string Iso(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}

/// <summary>Renderizador CSV (RFC 4180): uma linha por campo/célula, achatando seções e tabelas.</summary>
public sealed class CsvReportRenderer : IReportRenderer
{
    public DeliveryReportFormat Format => DeliveryReportFormat.Csv;

    public ReportRenderResult Render(ReportDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var sb = new StringBuilder();
        sb.Append("section,label,value\r\n");
        foreach (var section in document.Sections)
        {
            foreach (var field in section.Fields)
            {
                AppendRow(sb, section.Title, field.Label, field.Value);
            }

            if (section.Table is { } table && table.Columns.Count > 0)
            {
                foreach (var row in table.Rows)
                {
                    for (var i = 0; i < row.Count && i < table.Columns.Count; i++)
                    {
                        AppendRow(sb, section.Title, table.Columns[i], row[i]);
                    }
                }
            }
        }

        return ReportRenderResult.Rendered(Format, "text/csv; charset=utf-8", sb.ToString());
    }

    private static void AppendRow(StringBuilder sb, string section, string label, string value) =>
        sb.Append(Field(section)).Append(',').Append(Field(label)).Append(',').Append(Field(value)).Append("\r\n");

    private static string Field(string? value)
    {
        var v = value ?? string.Empty;
        if (v.IndexOfAny(['"', ',', '\n', '\r']) < 0)
        {
            return v;
        }

        return "\"" + v.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}

/// <summary>Renderizador JSON: serializa o documento estruturado como está (System.Text.Json, BCL).</summary>
public sealed class JsonReportRenderer : IReportRenderer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public DeliveryReportFormat Format => DeliveryReportFormat.Json;

    public ReportRenderResult Render(ReportDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var json = JsonSerializer.Serialize(document, Options);
        return ReportRenderResult.Rendered(Format, "application/json; charset=utf-8", json);
    }
}
