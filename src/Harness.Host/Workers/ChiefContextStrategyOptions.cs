using Harness.Modules.Governance.Context;

namespace Harness.Host.Workers;

/// <summary>
/// Configuração da estratégia de contexto do Chief (PLAT-02). Trocável por deployment via
/// <c>Harness:Governance:Features</c>. Default seguro: DESLIGADA — quando desligada, o Chief mantém
/// exatamente o comportamento anterior (nenhuma janela é montada nem injetada).
/// </summary>
public sealed record ChiefContextStrategyOptions(bool Enabled, ContextStrategyBudget Budget, int HistoryScanLimit)
{
    public static ChiefContextStrategyOptions Disabled { get; } =
        new(false, ContextStrategyBudget.Default, 200);
}
