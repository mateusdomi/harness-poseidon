namespace Harness.Host.Workers;

public sealed record ChiefTurnWorkerOptions(
    TimeSpan PollInterval,
    TimeSpan LeaseDuration)
{
    public bool ContextBundlesEnabled { get; init; } = true;

    /// <summary>
    /// Orçamento do bundle documental da chefe. Subiu de 12.000 quando o turno passou a pedir
    /// contexto pelo vocabulário real (fase do projeto, workflow do playbook, persona catalogada):
    /// o conjunto selecionado cresceu e, no orçamento antigo, as fatias de MEMÓRIA — que são o
    /// resultado da recuperação feita para AQUELE turno — eram as primeiras a cair, porque entram
    /// por último na ordem determinística. Perder a memória recuperada para caber documento
    /// estático é o oposto do que a recuperação existe para fazer.
    /// </summary>
    public int ContextTokenBudget { get; init; } = 16000;

    public TimeSpan ActivityHeartbeatInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Fase 0A2 (BR-005): o lease é renovado PELO batimento, então ele precisa durar bem mais que o
    /// intervalo entre batimentos. Com a folga apertada, uma renovação atrasada por uma escrita
    /// lenta deixaria o turno vivo parecer abandonado — e outro worker o reexecutaria, dobrando o
    /// custo. Exijo pelo menos o triplo: sobram duas tentativas antes de o lease vencer.
    /// </summary>
    public void Validate()
    {
        if (PollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(PollInterval), "The poll interval must be positive.");
        }

        if (ActivityHeartbeatInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ActivityHeartbeatInterval), "The heartbeat interval must be positive.");
        }

        if (LeaseDuration < ActivityHeartbeatInterval * 3)
        {
            throw new ArgumentOutOfRangeException(
                nameof(LeaseDuration),
                "The turn lease must last at least three heartbeat intervals so a delayed renewal never orphans a live turn.");
        }
    }
}
