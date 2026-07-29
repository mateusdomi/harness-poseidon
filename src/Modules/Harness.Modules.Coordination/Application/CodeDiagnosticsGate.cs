using System.Globalization;
using Harness.SharedKernel.CodeGraph;

namespace Harness.Modules.Coordination.Application;

/// <summary>
/// O que os diagnósticos do índice permitem afirmar sobre a rodada.
/// <paramref name="Definitive"/> distingue "não compila, ponto" de "esta passada não tem autoridade
/// para dizer".
/// </summary>
public sealed record CodeDiagnosticsVerdict(
    bool Blocks,
    bool Definitive,
    int ErrorCount,
    string ReasonCode,
    string Detail);

/// <summary>
/// Diagnósticos de compilação como gate PRÉ-REVIEW (B6 ligando-se à camada (i) da F14).
///
/// A F14 deixou a camada determinística pronta e disse que os diagnósticos do Roslyn plugariam
/// depois "sem mudar contrato". É exatamente o que esta classe faz: ela não redefine a camada nem
/// substitui build e testes — ela acrescenta um fato que o índice apura de graça, na mesma passada em
/// que monta o grafo.
///
/// <b>A regra que torna o plug seguro: ele só piora o veredito, nunca melhora.</b>
/// <see cref="ApplyTo"/> pode transformar um Pass em Fail quando há erro definitivo, e jamais
/// transforma Fail ou NotRun em Pass. O motivo é que estes diagnósticos são UM insumo da camada
/// determinística, não a camada inteira — build, testes e varredura de segredo continuam sendo os
/// outros. Se "sem erro de sintaxe" pudesse aprovar a camada, o sistema teria aprendido a chamar de
/// verificado o que ninguém compilou, que é o defeito que a F14 nasceu para impedir.
/// </summary>
public static class CodeDiagnosticsGate
{
    public const string ReasonBlocked = "code_diagnostics.compilation_error";
    public const string ReasonClean = "code_diagnostics.no_compilation_error";
    public const string ReasonNotAuthoritative = "code_diagnostics.scope_not_authoritative";

    /// <summary>
    /// Lê os diagnósticos de uma derivação. Só erro de alcance
    /// <see cref="CodeGraphDiagnosticScope.SyntaxOnly"/> ou <see cref="CodeGraphDiagnosticScope.Semantic"/>
    /// bloqueia — e o alcance entra no veredito, porque reprovar card com base em erro semântico de
    /// uma compilação sintética seria inventar reprovação.
    /// </summary>
    public static CodeDiagnosticsVerdict Inspect(
        IReadOnlyList<CodeGraphDiagnostic> diagnostics,
        CodeGraphDiagnosticScope scope)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        if (!Enum.IsDefined(scope))
        {
            throw new ArgumentOutOfRangeException(nameof(scope));
        }

        var errors = diagnostics
            .Where(diagnostic => diagnostic.Severity == CodeGraphDiagnosticSeverity.Error)
            .ToArray();
        if (errors.Length == 0)
        {
            return new CodeDiagnosticsVerdict(
                Blocks: false,
                Definitive: true,
                0,
                ReasonClean,
                "Nenhum erro de compilação na derivação do índice.");
        }

        // Arquivo que não parseia não compila em build nenhum: definitivo em qualquer alcance.
        var where = errors
            .Select(error => error.FilePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Take(5)
            .ToArray();

        return new CodeDiagnosticsVerdict(
            Blocks: true,
            Definitive: true,
            errors.Length,
            ReasonBlocked,
            string.Format(
                CultureInfo.InvariantCulture,
                "{0} erro(s) de compilação em: {1} (alcance {2}).",
                errors.Length,
                string.Join(", ", where),
                scope));
    }

    /// <summary>
    /// Aplica o veredito à camada determinística. Só rebaixa: nenhuma ausência de erro de compilação
    /// aprova uma camada cujo build ou cujos testes não rodaram.
    /// </summary>
    public static LayerResult ApplyTo(LayerResult deterministic, CodeDiagnosticsVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(deterministic);
        ArgumentNullException.ThrowIfNull(verdict);
        if (deterministic.Layer != VerificationLayer.Deterministic)
        {
            throw new ArgumentException(
                "Diagnósticos de compilação pertencem à camada determinística.",
                nameof(deterministic));
        }

        if (!verdict.Blocks || !verdict.Definitive)
        {
            return deterministic;
        }

        return deterministic with
        {
            Verdict = LayerVerdict.Fail,
            ReasonCode = ReasonBlocked,
            Detail = verdict.Detail
        };
    }
}
