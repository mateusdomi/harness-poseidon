namespace Harness.Modules.Coordination.Application;

/// <summary>O que a reconciliação deve fazer com uma tentativa que o banco diz estar viva.</summary>
public enum AttemptReconciliationAction
{
    /// <summary>Nada: a execução ainda está em voo, ou quem registra é a colheita rica.</summary>
    None,

    /// <summary>Sem workspace e passada a tolerância: órfã, devolve o card à fila.</summary>
    ExpireOrphan,

    /// <summary>Execução morta (falha ou cancelamento): devolve o card à fila com motivo.</summary>
    ExpireDead,

    /// <summary>Execução concluída que a colheita rica não vai registrar neste ciclo.</summary>
    RecordCompleted,
}

/// <summary>Fatos observáveis sobre a tentativa, sem nada que dependa de juízo.</summary>
public readonly record struct AttemptReconciliationFacts(
    bool HasWorkspace,
    bool RunCompleted,
    bool RunDead,
    TimeSpan Age,
    bool RichHarvestWillRun);

/// <summary>
/// Decide o destino de uma tentativa marcada como viva.
///
/// É código, e não juízo de modelo, porque cada ramo aqui já custou um card travado: tentativa
/// órfã que ninguém fechava, projeto pausado que perdia entrega pronta, e run em voo expirado
/// cedo demais por falta de janela de tolerância. Separar a decisão da execução torna cada um
/// desses casos verificável sem subir Host, banco e Git.
/// </summary>
public static class AttemptReconciliationPolicy
{
    /// <summary>
    /// Tolerância antes de declarar órfã uma tentativa sem workspace. Existe porque o
    /// orquestrador aceita o run DEPOIS de a cadeia iniciá-lo: sem a janela, um lançamento do
    /// próprio ciclo seria expirado no berço.
    /// </summary>
    public static readonly TimeSpan OrphanTolerance = TimeSpan.FromMinutes(2);

    public static AttemptReconciliationAction Decide(AttemptReconciliationFacts facts)
    {
        if (!facts.HasWorkspace)
        {
            return facts.Age > OrphanTolerance
                ? AttemptReconciliationAction.ExpireOrphan
                : AttemptReconciliationAction.None;
        }

        if (facts.RunCompleted)
        {
            // Trabalho pronto nunca se perde: se a colheita rica não vai rodar (projeto pausado,
            // manual ou fora da raiz controlada), o registro acontece aqui.
            return facts.RichHarvestWillRun
                ? AttemptReconciliationAction.None
                : AttemptReconciliationAction.RecordCompleted;
        }

        return facts.RunDead
            ? AttemptReconciliationAction.ExpireDead
            : AttemptReconciliationAction.None;
    }
}
