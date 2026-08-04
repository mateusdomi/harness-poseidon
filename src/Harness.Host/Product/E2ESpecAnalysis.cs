using System.Text.RegularExpressions;

namespace Harness.Host.Product;

/// <summary>O que a inspeção estrutural das especificações concluiu.</summary>
/// <param name="Usable">A jornada declarada tem o mínimo para ser uma jornada.</param>
/// <param name="Reason">Por que não tem. Nulo quando tem.</param>
/// <param name="Tests">Quantos casos declarados (excluídos os pulados).</param>
/// <param name="Skipped">Quantos casos declarados como pulados.</param>
/// <param name="Navigations">Quantas navegações reais na aplicação alvo.</param>
/// <param name="Assertions">Quantas asserções não triviais.</param>
public sealed record E2ESpecVerdict(
    bool Usable, string? Reason, int Tests, int Skipped, int Navigations, int Assertions);

/// <summary>
/// Inspeção ESTRUTURAL das especificações de jornada entregues.
///
/// A fronteira, explicitada no §10: isto não é análise semântica e não tenta ser. Não há como um
/// verificador decidir se a jornada escrita corresponde ao critério de aceite — quem escreve o
/// significado de "emprestar" é o produto, não a plataforma. O que dá para fazer, e é o que se faz
/// aqui, é <b>rejeitar prova evidentemente vazia</b>:
///
/// <code>test("ok", async () =&gt; { expect(true).toBe(true); });</code>
///
/// roda, sai com zero e não navegou em lugar nenhum. Sem esta inspeção, ele satisfaria
/// `E2EJourneyPassed` com a mesma força de uma jornada de verdade — e o requisito mais caro de
/// provar viraria o mais barato de forjar.
/// </summary>
public static partial class E2ESpecAnalysis
{
    public static E2ESpecVerdict Analyze(IReadOnlyList<(string File, string Content)> specs)
    {
        ArgumentNullException.ThrowIfNull(specs);

        if (specs.Count == 0)
        {
            return new E2ESpecVerdict(
                false, "A entrega não declara nenhuma especificação de jornada.", 0, 0, 0, 0);
        }

        var tests = 0;
        var skipped = 0;
        var navigations = 0;
        var assertions = 0;

        foreach (var (_, content) in specs)
        {
            var stripped = StripComments(content);
            skipped += SkippedTestPattern().Count(stripped);
            tests += TestPattern().Count(stripped) - SkippedTestPattern().Count(stripped);
            navigations += NavigationPattern().Count(stripped);
            assertions += CountRealAssertions(stripped);
        }

        if (tests <= 0)
        {
            return new E2ESpecVerdict(
                false,
                skipped > 0
                    ? $"Todos os {skipped} casos de jornada estão marcados como pulados."
                    : "Nenhum caso de jornada declarado nas especificações.",
                0, skipped, navigations, assertions);
        }

        if (navigations == 0)
        {
            return new E2ESpecVerdict(
                false,
                "Nenhuma navegação na aplicação: as especificações não abrem nenhuma página. " +
                "Um teste que não visita o produto não exercita jornada nenhuma.",
                tests, skipped, 0, assertions);
        }

        return assertions == 0
            ? new E2ESpecVerdict(
                false,
                "Nenhuma asserção com conteúdo: só há verificações trivialmente verdadeiras " +
                "(`expect(true)`, `expect(1)`), que passam sem olhar para o produto.",
                tests, skipped, navigations, 0)
            : new E2ESpecVerdict(true, null, tests, skipped, navigations, assertions);
    }

    /// <summary>
    /// Asserções que olham para alguma coisa. <c>expect(true)</c> e <c>expect(1)</c> ficam de fora
    /// porque são verdadeiras independentemente do produto — contá-las seria aceitar como prova
    /// exatamente a fraude que este método existe para reconhecer.
    /// </summary>
    private static int CountRealAssertions(string content) =>
        AssertionPattern().Matches(content)
            .Count(match => !TrivialAssertionPattern().IsMatch(match.Value));

    /// <summary>
    /// Remove comentários antes de contar. Jornada comentada não roda, e um bloco comentado com
    /// dez `page.goto` tornaria uma spec vazia estatisticamente rica.
    /// </summary>
    private static string StripComments(string content) =>
        LineCommentPattern().Replace(BlockCommentPattern().Replace(content, " "), " ");

    [GeneratedRegex(@"\btest\s*(\.\w+)?\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex TestPattern();

    [GeneratedRegex(@"\btest\s*\.\s*(skip|fixme)\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex SkippedTestPattern();

    [GeneratedRegex(
        @"\bpage\s*\.\s*(goto|reload)\s*\(|\bpage\s*\.\s*waitForURL\s*\(",
        RegexOptions.CultureInvariant)]
    private static partial Regex NavigationPattern();

    [GeneratedRegex(@"\bexpect\s*\([^)]{0,200}?\)", RegexOptions.CultureInvariant)]
    private static partial Regex AssertionPattern();

    [GeneratedRegex(@"^expect\s*\(\s*(true|false|1|0|""""|''|\d+)\s*\)$", RegexOptions.CultureInvariant)]
    private static partial Regex TrivialAssertionPattern();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex BlockCommentPattern();

    [GeneratedRegex(@"//[^\n]*", RegexOptions.CultureInvariant)]
    private static partial Regex LineCommentPattern();
}
