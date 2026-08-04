using Harness.Modules.Operations;

namespace Harness.UnitTests.Operations;

/// <summary>
/// O gate deriva o contador de trabalho do `FINDINGS.jsonl`. Uma linha que não desserializa
/// não some do radar — vira `PARSE-ERROR` — mas leva junto o CONTEÚDO do defeito real:
/// um `OPS-070` já corrigido virou trabalho aberto sintético só porque `fix` fora escrito
/// como array. Estes testes cobrem a tolerância e o seu limite.
/// </summary>
public sealed class OperationFindingParseTests
{
    [Fact]
    public void AnArrayOfTextKeepsTheFindingInsteadOfBecomingAParseError()
    {
        const string line = """
            {"id":"OPS-070","severity":"critical","status":"fixed","blocking":true,
             "title":"t","fix":["primeira metade","segunda metade"],"owner":"agent"}
            """;

        var findings = OperationFinding.ParseLines([line]);

        var finding = Assert.Single(findings);
        Assert.Equal("OPS-070", finding.Id);
        Assert.Equal("fixed", finding.Status);
        Assert.Contains("primeira metade", finding.Fix);
        Assert.Contains("segunda metade", finding.Fix);
        Assert.False(finding.IsExecutable);
    }

    [Fact]
    public void PlainTextStillParses()
    {
        const string line = """
            {"id":"OPS-001","status":"open","nextAction":"consertar","fix":"um texto so"}
            """;

        var finding = Assert.Single(OperationFinding.ParseLines([line]));

        Assert.Equal("um texto so", finding.Fix);
        Assert.True(finding.IsAgentExecutable);
    }

    [Fact]
    public void AGenuinelyUnreadableShapeStillBecomesAParseError()
    {
        // A tolerância é estreita de propósito: um conversor que aceita qualquer coisa
        // devolveria silêncio no lugar do defeito.
        const string line = """{"id":"OPS-999","fix":{"qualquer":"objeto"}}""";

        var finding = Assert.Single(OperationFinding.ParseLines([line]));

        Assert.Equal("PARSE-ERROR", finding.Id);
        Assert.True(finding.IsAgentExecutable);
    }

    [Fact]
    public void TheRealOperationFileHasNoUnreadableLine()
    {
        var path = ResolveFindingsPath();
        var findings = OperationFinding.ParseLines(File.ReadAllLines(path));

        Assert.NotEmpty(findings);
        Assert.DoesNotContain(findings, f => f.Id == "PARSE-ERROR");
    }

    private static string ResolveFindingsPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "coordination", "final-operation", "FINDINGS.jsonl");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("FINDINGS.jsonl não encontrado a partir de " + AppContext.BaseDirectory);
    }
}
