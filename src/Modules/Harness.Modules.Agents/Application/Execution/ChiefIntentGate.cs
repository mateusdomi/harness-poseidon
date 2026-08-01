namespace Harness.Modules.Agents.Application.Execution;

/// <summary>
/// B14 (Fase 2B) — o portão que faz a tabela de despacho valer.
///
/// A classificação sozinha não muda nada: se o modelo dissesse "conversa_geral" e mesmo assim
/// emitisse demandas, e o worker as aplicasse, a taxonomia seria decoração. Aqui as ações do turno
/// são cortadas ao que a rota da intenção permite — e o corte é registrado, nunca silencioso.
///
/// O corte não é desconfiança do modelo: é a separação entre o que ele decide (o quê) e o que o
/// sistema decide (se pode). Um turno de conversa que gera trabalho não é criatividade útil; é
/// trabalho que ninguém pediu entrando no board do dono.
/// </summary>
public static class ChiefIntentGate
{
    /// <summary>O que sobrou do turno depois do portão, e o que foi cortado.</summary>
    public sealed record Result(
        ChiefTurnOutput Output,
        ChiefIntentRoute Route,
        int DemandsDropped,
        int TeamActionsDropped)
    {
        public bool AnythingDropped => DemandsDropped > 0 || TeamActionsDropped > 0;

        /// <summary>Motivo auditável do corte, para o ledger. Vazio quando nada foi cortado.</summary>
        public string DropReason => AnythingDropped
            ? $"intent={ChiefIntentDispatchTable.Name(Route.Intent)};" +
              $"demandsDropped={DemandsDropped};teamActionsDropped={TeamActionsDropped}"
            : string.Empty;
    }

    /// <summary>
    /// Aplica a rota da intenção sobre a saída do turno.
    ///
    /// A resposta ao usuário NUNCA é cortada — cortar texto deixaria o dono com meia frase e
    /// nenhuma explicação. O que o portão remove são as AÇÕES que a intenção não autoriza.
    /// </summary>
    public static Result Apply(ChiefTurnOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var route = ChiefIntentDispatchTable.For(output.Intent);

        var mayCreateDemands = route.AllowedActions.HasFlag(ChiefTurnAction.CreateDemands);
        var mayManageTeam = route.AllowedActions.HasFlag(ChiefTurnAction.ManageTeam);

        var demands = mayCreateDemands ? output.Demands : [];
        var teamActions = mayManageTeam ? output.TeamActions : null;

        return new Result(
            output with { Demands = demands, TeamActions = teamActions },
            route,
            mayCreateDemands ? 0 : output.Demands.Count,
            mayManageTeam ? 0 : output.TeamActions?.Count ?? 0);
    }
}
