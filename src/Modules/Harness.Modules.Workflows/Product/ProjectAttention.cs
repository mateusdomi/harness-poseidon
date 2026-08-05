namespace Harness.Modules.Workflows.Product;

/// <summary>
/// Onde a ATENÇÃO do desenvolvedor deve estar — um estado por projeto, derivado do que já existe.
///
/// A tese de operar vários projetos morre se o operador precisar abrir cada chat para saber se
/// algo espera por ele. Este enum é a resposta de um olhar: a ordem é de urgência decrescente, e o
/// portfólio ordena por ela.
/// </summary>
public enum ProjectAttentionState
{
    /// <summary>Uma decisão humana está pendente: cards escalados ou gate aguardando o dono.</summary>
    NeedsHumanDecision,

    /// <summary>Falhou por dependência externa (quota, provedor, rede) e vai retentar sozinho.</summary>
    FailedRetryable,

    /// <summary>Bloqueado por algo que não é decisão do dono nem retentável: exige investigação.</summary>
    BlockedExternal,

    /// <summary>A entrega passou nos portões e espera o aceite do usuário.</summary>
    ReadyForUat,

    /// <summary>Tudo verde até o fim: pronto para entrega.</summary>
    DeliveryReady,

    /// <summary>Progredindo sozinho. O estado bom — e o único que não pede nada de ninguém.</summary>
    AutonomouslyProgressing,
}

/// <summary>Os fatos derivados dos stores existentes. Nenhuma fonte nova.</summary>
public sealed record ProjectAttentionFacts(
    bool Paused,
    int EscalatedCards,
    bool GateAwaitingHuman,
    int RetryableFailures,
    int BlockedCards,
    bool AllPhasesComplete,
    bool InUatPhase);

/// <summary>
/// Deriva o estado de atenção. PURO e de fonte única: os fatos vêm do quadro e do workflow que já
/// existem, e mudar a regra aqui muda o portfólio inteiro junto — que é o contrário de cada tela
/// inventar a própria semântica de "precisa de mim".
/// </summary>
public static class ProjectAttentionClassifier
{
    public static ProjectAttentionState Classify(ProjectAttentionFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        // Pausado é decisão do dono: não grita por atenção. O que ele tiver de pendente aparece
        // quando for reativado — anunciar pendência de projeto pausado é spam.
        if (facts.Paused)
        {
            return ProjectAttentionState.AutonomouslyProgressing;
        }

        if (facts.EscalatedCards > 0 || facts.GateAwaitingHuman)
        {
            return ProjectAttentionState.NeedsHumanDecision;
        }

        if (facts.AllPhasesComplete)
        {
            return ProjectAttentionState.DeliveryReady;
        }

        if (facts.InUatPhase)
        {
            return ProjectAttentionState.ReadyForUat;
        }

        if (facts.RetryableFailures > 0)
        {
            return ProjectAttentionState.FailedRetryable;
        }

        return facts.BlockedCards > 0
            ? ProjectAttentionState.BlockedExternal
            : ProjectAttentionState.AutonomouslyProgressing;
    }

    /// <summary>
    /// Os estados que merecem NOTIFICAÇÃO — alto sinal, nunca card normal. É a lista que impede o
    /// canal de virar ruído: quem recebe dez avisos por hora para de ler todos.
    /// </summary>
    public static bool IsHighSignal(ProjectAttentionState state) => state is
        ProjectAttentionState.NeedsHumanDecision or
        ProjectAttentionState.ReadyForUat or
        ProjectAttentionState.DeliveryReady;
}
