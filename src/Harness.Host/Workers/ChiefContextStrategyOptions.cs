using Harness.Modules.Governance.Context;

namespace Harness.Host.Workers;

/// <summary>
/// Configuração da estratégia de contexto do Chief (PLAT-02). Trocável por deployment via
/// <c>Harness:Governance:Features</c>. Default seguro: DESLIGADA — quando desligada, o Chief mantém
/// exatamente o comportamento anterior (nenhuma janela é montada nem injetada).
/// </summary>
public sealed record ChiefContextStrategyOptions(bool Enabled, ContextStrategyBudget Budget, int HistoryScanLimit)
{
    /// <summary>
    /// Quantas notas externalizadas são reinjetadas no contexto. Fase 0A2 (BR-006): antes disso as
    /// notas eram write-only — a estratégia gravava o que era crítico e ninguém lia de volta, o que
    /// fazia a compactação virar esquecimento. O orçamento de tokens continua sendo a trava final.
    /// </summary>
    public int NoteRetrievalLimit { get; init; } = 50;

    public static ChiefContextStrategyOptions Disabled { get; } =
        new(false, ContextStrategyBudget.Default, 200);
}
