using Harness.Modules.Coordination.Application;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.Messaging;
using Harness.Persistence.Abstractions.Projects;
using Harness.Persistence.Abstractions.WorkChain;

namespace Harness.Host.Agents;

/// <summary>
/// O predicado de elegibilidade de PROJETO, em um lugar só.
///
/// Ele existia como método privado dentro do laço do chefe, o que tornava impossível responder "o
/// que o Poseidon vai executar se eu ligá-lo agora?" sem reimplementar a regra — e uma regra
/// reimplementada é uma regra que diverge no dia em que alguém mexe numa das cópias. O inventário
/// abaixo e o laço de despacho chamam esta função, e nenhum dos dois tem opinião própria.
/// </summary>
public static class StartupWorkEligibility
{
    /// <summary>
    /// Só projeto ATIVO entra no ciclo autônomo. `paused` é decisão explícita do dono e `archived`
    /// é fim de vida; nenhum dos dois pode consumir cota, slot de despacho ou disparar revisão.
    ///
    /// Projeto removido nem chega aqui: o store filtra <c>deleted_at IS NULL</c> na listagem, e é
    /// por isso que remover é mais forte do que pausar.
    /// </summary>
    public static bool ProjectIsRunnable(ProjectRecord project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return string.Equals(project.State, "active", StringComparison.Ordinal);
    }
}

/// <summary>Quanto trabalho o Poseidon encontraria se subisse agora.</summary>
public sealed record StartupWorkSnapshot(
    int RunnableProjects,
    int RunnableCards,
    int ResumableAttempts,
    int PendingOutbox,
    IReadOnlyList<string> Detail)
{
    /// <summary>Nada a despachar. É o estado que precede a criação de um projeto novo.</summary>
    public bool IsEmpty =>
        RunnableCards == 0 && ResumableAttempts == 0 && PendingOutbox == 0;

    public string Summary() =>
        $"projects={RunnableProjects} cards={RunnableCards} attempts={ResumableAttempts} " +
        $"outbox={PendingOutbox}";
}

/// <summary>
/// Responde uma pergunta operacional que não tinha resposta: <b>se eu ligar o Poseidon agora, o que
/// ele vai tentar executar?</b>
///
/// Ela passou a importar por um motivo concreto: durante o desenvolvimento nasceram dezenas de
/// projetos de teste, alguns parados no meio do playbook. Um Host que subisse e os encontrasse
/// elegíveis gastaria em trabalho velho a cota que o projeto real precisa — e o sintoma só
/// apareceria depois, como "o provedor acabou".
///
/// <b>Não é um subsistema.</b> É uma consulta que percorre exatamente as mesmas fontes e o mesmo
/// predicado do laço de despacho: mesma listagem de projetos, mesma página de cards `ready` não
/// arquivados, mesma <see cref="CardReadinessEvaluator"/>. Se a regra mudar, este número muda
/// junto — que é a única forma de um inventário ser confiável.
///
/// O que ele NÃO cobre, e é honesto dizer: circuito de card aberto, freio por não-progresso e
/// backoff de despacho podem reduzir ainda mais o que de fato roda. O inventário é o teto do que
/// seria considerado, não a previsão exata — e um teto é o número certo para decidir se é seguro
/// ligar a máquina.
/// </summary>
public sealed class StartupWorkInventory(
    ILocalProfileStore profiles,
    IProjectStore projects,
    IWorkBoardStore board,
    IOutboxStore? outbox = null)
{
    public async Task<StartupWorkSnapshot> InspectAsync(CancellationToken cancellationToken)
    {
        var runnableProjects = 0;
        var runnableCards = 0;
        var resumableAttempts = 0;
        var detail = new List<string>();

        foreach (var profile in await profiles.ListAsync(cancellationToken))
        {
            var listed = await projects.ListAsync(profile.TenantId, null, 50, cancellationToken);
            foreach (var project in listed.Where(StartupWorkEligibility.ProjectIsRunnable))
            {
                runnableProjects++;

                // A MESMA consulta do laço: `ready`, arquivo ativo, mesmo teto de página.
                var page = await board.PageTasksAsync(
                    profile.TenantId,
                    new BoardTaskPageQuery(project.Id, null, null, "ready", null, null, "active", null, 0, 50),
                    cancellationToken);

                foreach (var task in page.Items)
                {
                    var instructions = await board.ListInstructionsAsync(
                        profile.TenantId, task.Id, null, 50, cancellationToken);
                    var readiness = CardReadinessEvaluator.Evaluate(new CardReadinessFacts(
                        task.CardType,
                        instructions.Count >= 1,
                        string.Equals(task.State, "blocked", StringComparison.Ordinal) ||
                            !string.IsNullOrWhiteSpace(task.BlockedReason)));

                    if (readiness.IsDispatchable)
                    {
                        runnableCards++;
                        detail.Add($"card:{project.Id}:{task.Id}:{task.CardType}");
                    }

                    var attempts = await board.ListAttemptsAsync(
                        profile.TenantId, task.Id, null, 20, cancellationToken);
                    resumableAttempts += attempts.Count(attempt =>
                        string.Equals(attempt.State, "running", StringComparison.Ordinal) ||
                        string.Equals(attempt.State, "pending", StringComparison.Ordinal));
                }

                if (page.Items.Count > 0)
                {
                    detail.Add($"project:{project.Id}:{page.Items.Count}_ready");
                }
            }
        }

        var pending = outbox is null ? 0 : await CountPendingAsync(outbox, cancellationToken);
        return new StartupWorkSnapshot(
            runnableProjects, runnableCards, resumableAttempts, pending, detail);
    }

    private static async Task<int> CountPendingAsync(
        IOutboxStore outbox, CancellationToken cancellationToken)
    {
        try
        {
            // Lease de duração ZERO: o inventário quer saber se existe item pendente, não
            // processá-lo. Tomar posse por tempo real faria a consulta atrapalhar o despachante.
            var pending = await outbox.TryAcquireNextAsync(
                "startup-work-inventory", TimeSpan.Zero, DateTimeOffset.UtcNow, cancellationToken);
            return pending is null ? 0 : 1;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Inventário que não consegue ler a fila devolve o pior caso conhecido em vez de zero:
            // relatar "nada pendente" por causa de um erro de leitura seria a forma mais silenciosa
            // de este mecanismo mentir.
            return -1;
        }
    }
}
