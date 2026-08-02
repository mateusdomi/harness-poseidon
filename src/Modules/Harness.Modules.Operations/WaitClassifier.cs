namespace Harness.Modules.Operations;

/// <summary>Estado de espera. `WAITING` genérico não existe aqui, de propósito.</summary>
public enum WaitState
{
    /// <summary>Atividade comprovável: PID, CPU, heartbeat, output ou chamada de modelo.</summary>
    Running,

    /// <summary>Existe um evento futuro CONCRETO, identificado por ID, legitimamente em voo.</summary>
    WaitingObservable,

    /// <summary>Espera deliberada por recurso: slot HEAVY, lease de repositório, cooldown.</summary>
    WaitingResource,

    /// <summary>O estado diz que trabalha, mas não há atividade real. Nunca se espera: diagnostica-se.</summary>
    Stalled,
}

/// <summary>Fatos observáveis sobre uma execução. Sem ID concreto não há observação.</summary>
public sealed record WaitFacts(
    /// <summary>ID do que está sendo esperado: turn, card, attempt ou run. Nunca uma contagem.</summary>
    string? SubjectId,
    bool ProcessAlive,
    bool ModelCallInFlight,
    bool HeartbeatFresh,
    bool OutputAdvanced,
    bool WaitingOnResource,
    TimeSpan Age);

/// <summary>
/// Traduz fatos em um estado de espera.
///
/// Existe por causa de uma falha concreta (R2 da especificação): a Integradora ficou nove
/// minutos bloqueada num laço que perguntava "existem ao menos duas mensagens?" enquanto a
/// resposta que ela esperava já existia havia vinte e quatro segundos. A pergunta estava
/// errada — contagem genérica não identifica NADA. A pergunta certa é sempre sobre um
/// sujeito: "o turno ABC que eu enviei já foi respondido?".
///
/// E enquanto aquele laço bloqueava, nada era investigado: nem o turno, nem a invocação de
/// modelo, nem worker, banco, cards ou logs. Por isso toda espera aqui é curta e devolve o
/// controle.
/// </summary>
public static class WaitClassifier
{
    /// <summary>
    /// Teto de bloqueio para espera de OBSERVABILIDADE. Não se aplica a inferência
    /// comprovadamente ativa nem a build real — só a ficar olhando para ver se mudou.
    /// </summary>
    public static readonly TimeSpan MaximumObservabilityBlock = TimeSpan.FromSeconds(30);

    /// <summary>Idade a partir da qual a ausência de sinal deixa de ser tolerável.</summary>
    public static readonly TimeSpan DefaultStallThreshold = TimeSpan.FromMinutes(10);

    public static WaitState Classify(WaitFacts facts, TimeSpan? stallThreshold = null)
    {
        ArgumentNullException.ThrowIfNull(facts);

        // Sem sujeito não existe espera legítima: é impossível provar que algo está para
        // acontecer quando não se sabe apontar o quê.
        if (string.IsNullOrWhiteSpace(facts.SubjectId))
        {
            return WaitState.Stalled;
        }

        if (facts.ProcessAlive || facts.OutputAdvanced)
        {
            return WaitState.Running;
        }

        if (facts.ModelCallInFlight || facts.HeartbeatFresh)
        {
            return WaitState.WaitingObservable;
        }

        if (facts.WaitingOnResource)
        {
            return WaitState.WaitingResource;
        }

        return facts.Age > (stallThreshold ?? DefaultStallThreshold)
            ? WaitState.Stalled
            : WaitState.WaitingObservable;
    }

    /// <summary>
    /// Um bloqueio de observabilidade é aceitável? Serve para que o próprio código recuse
    /// laços longos em vez de depender de alguém lembrar da regra.
    /// </summary>
    public static bool IsAcceptableBlock(TimeSpan duration) =>
        duration <= MaximumObservabilityBlock;
}
