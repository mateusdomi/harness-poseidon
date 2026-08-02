namespace Harness.Modules.Operations;

/// <summary>Resultado de um gate: passou, ou não passou com os motivos.</summary>
public sealed record CompletionVerdict(bool Passed, IReadOnlyList<string> Reasons)
{
    public static CompletionVerdict Pass() => new(true, []);
}

/// <summary>
/// Decide, por CÓDIGO, se a Operação Final terminou.
///
/// Existe porque a saída de um agente não é autoritativa (R1 da especificação). Um modelo
/// pode concluir que "chegou a um bom ponto de fechamento" e emitir um relatório final
/// enquanto ainda conhece defeitos abertos, E2E incompleto e cards falhando — foi
/// exatamente o que aconteceu em 2026-08-02, com 8 defeitos em aberto e o E2E na fase 3
/// de 9. Repetir "não pare" no prompt não fecha essa lacuna: reduz probabilidade, não a
/// elimina. Aqui a decisão deixa de ser probabilística.
///
/// A saída da instância Integradora é sempre tratada como YIELD. Só este gate transforma
/// YIELD em DONE.
/// </summary>
public static class CompletionGate
{
    public static CompletionVerdict Evaluate(OperationState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var reasons = new List<string>();

        Require(state.CleanE2E?.Status, "pass", "prova limpa 1-9", reasons);
        Require(state.GeneratedProduct, "pass", "produto gerado iniciado e testado", reasons);
        Require(state.RecoveryTests, "pass", "testes de recuperação", reasons);

        var blocking = state.BlockingFindings;
        if (blocking > 0)
        {
            reasons.Add($"{blocking} finding(s) bloqueante(s) em aberto");
        }

        var executable = state.ExecutableWork;
        if (executable > 0)
        {
            reasons.Add($"{executable} item(ns) de trabalho executável conhecido");
        }

        if (state.MandatoryGatesFailed > 0)
        {
            reasons.Add($"{state.MandatoryGatesFailed} gate(s) obrigatório(s) vermelho(s)");
        }

        if (state.MandatoryTestsPending > 0)
        {
            reasons.Add($"{state.MandatoryTestsPending} teste(s) obrigatório(s) pendente(s)");
        }

        return reasons.Count == 0 ? CompletionVerdict.Pass() : new CompletionVerdict(false, reasons);
    }

    /// <summary>
    /// Um bloqueio externo real (credencial que só o dono gera, serviço fora) NÃO fecha a
    /// operação e também não autoriza abandoná-la: todo trabalho que não depende dele
    /// continua. Serve só para o supervisor saber que precisa chamar o humano em vez de
    /// relançar em vão.
    /// </summary>
    public static bool NeedsHuman(OperationState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.HumanDecisionRequired || state.ExternalBlockers.Count > 0;
    }

    private static void Require(string? actual, string expected, string label, List<string> reasons)
    {
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add($"{label}: {actual ?? "ausente"} (esperado {expected})");
        }
    }
}
