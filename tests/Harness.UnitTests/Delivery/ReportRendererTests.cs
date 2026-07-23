using System.Text.Json;
using Harness.Modules.Delivery.Application;
using Harness.Modules.Delivery.Contracts;

namespace Harness.UnitTests.Delivery;

/// <summary>
/// DEL-04 — a costura de renderização: markdown/html/csv/json produzem a forma correta e determinística;
/// pdf/pptx/word/zip caem no padrão tipado `format_not_available` (nunca exceção).
/// </summary>
public sealed class ReportRendererTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private static ReportDocument Sample() => new(
        "weekly_executive_status",
        "Status executivo — Pagamentos",
        "01H0000000000000000000DLV1",
        "PAY",
        "coordination",
        "internal",
        new DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.Zero),
        [
            new ReportSection("executive_summary", "Resumo executivo",
                [new ReportField("Saúde", "red"), new ReportField("Responsável", "agent, com vírgula")],
                Table: null),
            new ReportSection("attention", "Precisa de atenção",
                [new ReportField("Total", "1")],
                new ReportTable(
                    ["Código", "Severidade", "Detalhe"],
                    [["pending_db_access", "critical", "Aguardando banco | homolog"]])),
        ]);

    [Fact]
    public void MarkdownRendererProducesTitleSectionsAndTables()
    {
        var result = new MarkdownReportRenderer().Render(Sample());

        Assert.True(result.Available);
        Assert.Equal("markdown", result.Format);
        Assert.StartsWith("text/markdown", result.ContentType);
        Assert.Contains("# Status executivo — Pagamentos", result.Content);
        Assert.Contains("## Precisa de atenção", result.Content);
        Assert.Contains("| Código | Severidade | Detalhe |", result.Content);
        // O pipe do conteúdo é escapado para não quebrar a célula GFM.
        Assert.Contains("Aguardando banco \\| homolog", result.Content);
    }

    [Fact]
    public void HtmlRendererEscapesAndEmitsSemanticStructure()
    {
        var doc = Sample() with { Title = "Título <script>alert(1)</script>" };
        var result = new HtmlReportRenderer().Render(doc);

        Assert.True(result.Available);
        Assert.StartsWith("text/html", result.ContentType);
        Assert.Contains("<section data-key=\"attention\">", result.Content);
        Assert.Contains("<th>Severidade</th>", result.Content);
        // O HTML malicioso é escapado — nunca injetado como marcação.
        Assert.DoesNotContain("<script>alert(1)</script>", result.Content);
        Assert.Contains("&lt;script&gt;", result.Content);
    }

    [Fact]
    public void CsvRendererQuotesFieldsAndFlattensTables()
    {
        var result = new CsvReportRenderer().Render(Sample());

        Assert.True(result.Available);
        Assert.StartsWith("text/csv", result.ContentType);
        var lines = result.Content!.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("section,label,value", lines[0]);
        // Valor com vírgula é aspado (RFC 4180).
        Assert.Contains(lines, l => l.Contains("\"agent, com vírgula\""));
        // A tabela é achatada em linhas section,column,value.
        Assert.Contains(lines, l => l.Contains("pending_db_access"));
    }

    [Fact]
    public void JsonRendererRoundTripsTheDocument()
    {
        var result = new JsonReportRenderer().Render(Sample());

        Assert.True(result.Available);
        Assert.StartsWith("application/json", result.ContentType);
        var roundTrip = JsonSerializer.Deserialize<ReportDocument>(result.Content!, WebJson);
        Assert.NotNull(roundTrip);
        Assert.Equal("PAY", roundTrip!.ProjectKey);
        Assert.Equal(2, roundTrip.Sections.Count);
    }

    [Theory]
    [InlineData(DeliveryReportFormat.Pdf)]
    [InlineData(DeliveryReportFormat.Pptx)]
    [InlineData(DeliveryReportFormat.Word)]
    [InlineData(DeliveryReportFormat.Zip)]
    public void BinaryFormatsFallBackToFormatNotAvailable(DeliveryReportFormat format)
    {
        var registry = ReportRendererRegistry.Default();
        var result = registry.Render(format, Sample());

        Assert.False(result.Available);
        Assert.Null(result.Content);
        Assert.Equal("format_not_available", result.Reason);
        Assert.Equal(DeliveryReportTokens.ToToken(format), result.Format);
    }

    [Theory]
    [InlineData(DeliveryReportFormat.Markdown)]
    [InlineData(DeliveryReportFormat.Html)]
    [InlineData(DeliveryReportFormat.Csv)]
    [InlineData(DeliveryReportFormat.Json)]
    public void DefaultRegistryRendersEveryTextFormat(DeliveryReportFormat format)
    {
        var result = ReportRendererRegistry.Default().Render(format, Sample());

        Assert.True(result.Available);
        Assert.NotNull(result.Content);
        Assert.Null(result.Reason);
    }
}
