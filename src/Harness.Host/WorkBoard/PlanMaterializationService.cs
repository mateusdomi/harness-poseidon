using System.Diagnostics.CodeAnalysis;
using Harness.Modules.Coordination.Application;
using Harness.Persistence.Abstractions.Governance;
using Harness.Persistence.Abstractions.Coordination;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.WorkChain;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;

namespace Harness.Host.WorkBoard;

public sealed record PlanMaterializationOptions(TimeSpan StaleAfter, int MaximumAttempts)
{
    public void Validate()
    {
        if (StaleAfter < TimeSpan.FromSeconds(10) || StaleAfter > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(StaleAfter));
        }

        if (MaximumAttempts is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumAttempts));
        }
    }
}

/// <summary>Como a tentativa terminou. Nenhum valor aqui é "provavelmente deu certo".</summary>
public enum PlanMaterializationResult
{
    /// <summary>O conjunto previsto foi materializado e o compromisso está concluído.</summary>
    Materialized,

    /// <summary>Já estava íntegro e verificado contra o board; nada foi criado.</summary>
    AlreadyComplete,

    /// <summary>Outro dono vivo detém o trabalho — quem o detém conclui.</summary>
    NotClaimed,

    /// <summary>
    /// A demanda está preservada, mas o Playbook ainda não liberou Desenvolvimento. Não conta
    /// como tentativa nem como falha; o reconciliador volta a avaliá-la quando a fase mudar.
    /// </summary>
    Deferred,

    /// <summary>A guarda de laço interrompeu deliberadamente; o motivo está auditado.</summary>
    Interrupted,

    /// <summary>Terminou em falha visível com um código estável.</summary>
    Failed,
}

public sealed record PlanMaterializationOutcome(
    PlanMaterializationResult Result,
    string? ErrorCode = null,
    int ExpectedCards = 0,
    int MaterializedCards = 0,
    int CreatedCards = 0,
    string? PlanId = null);

/// <summary>
/// Fase 0A1: o consumidor durável do compromisso de materialização. Executa sempre a partir do
/// estado persistido — nunca de um objeto que ficou na memória do turno —, adquire o trabalho com
/// dono e tentativa, converge o board para o conjunto previsto e só então declara conclusão.
///
/// É seguro chamar quantas vezes for: entrega duplicada do evento, retry do outbox, reconciliador e
/// dois Hosts concorrentes convergem para o mesmo board porque a identidade do card é a fatia do
/// plano e a unicidade está no banco.
/// </summary>
public sealed class PlanMaterializationService(
    IPlanMaterializationStore jobs,
    IWorkBoardStore board,
    IDemandPlanStore plans,
    DemandPlanMaterializer materializer,
    ILocalProfileStore localProfiles,
    IChiefLoopGuardStore loopGuards,
    IMastClassificationStore mastClassifications,
    IAuditEventStore audit,
    IClock clock,
    PlanMaterializationOptions options,
    IPlanMaterializationFaultInjector faults,
    Harness.Host.Workflows.ActivePhaseResolver activePhases,
    ILogger<PlanMaterializationService> logger)
{
    /// <summary>
    /// Falhas que uma nova tentativa NÃO resolve: repetir só produziria ruído e esconderia o fato.
    /// Ficam visíveis em <c>failed</c> com o código, à espera de correção do plano ou da demanda.
    /// </summary>
    public static readonly IReadOnlySet<string> TerminalErrorCodes =
        new HashSet<string>(
            [
                "demand_missing",
                "local_profile_missing",
                "loop_guard_interrupted",
                "plan_slice_key_duplicated",
                "plan_dependency_unresolved",
                "plan_cards_unexpected",
            ],
            StringComparer.Ordinal);

    private static readonly Action<ILogger, string, string, int, Exception?> Materialized =
        LoggerMessage.Define<string, string, int>(
            LogLevel.Information,
            new EventId(2110, nameof(Materialized)),
            "Plan materialization for demand {DemandId} settled plan {PlanId} with {CreatedCards} new cards.");

    private static readonly Action<ILogger, string, string, Exception?> FailedLog =
        LoggerMessage.Define<string, string>(
            LogLevel.Error,
            new EventId(2111, nameof(FailedLog)),
            "Plan materialization for demand {DemandId} failed with {ErrorCode}.");

    private readonly PlanMaterializationOptions _options = Validated(options);

    private static PlanMaterializationOptions Validated(PlanMaterializationOptions value)
    {
        ArgumentNullException.ThrowIfNull(value);
        value.Validate();
        return value;
    }

    /// <summary>
    /// Executa o compromisso da demanda. <paramref name="ownerId"/> identifica quem está tentando —
    /// o par (dono, tentativa) é o fencing das escritas finais.
    /// </summary>
    public async Task<PlanMaterializationOutcome> RunAsync(
        string tenantId,
        string demandId,
        string ownerId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(demandId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        var current = await jobs.GetAsync(tenantId, demandId, cancellationToken);
        if (current is null)
        {
            // Nunca houve compromisso: não há o que materializar e inventar um seria criar
            // trabalho que ninguém pediu.
            return new PlanMaterializationOutcome(
                PlanMaterializationResult.Failed, "materialization_not_requested");
        }

        // O retry NUNCA conclui apenas porque o registro (ou o marker) diz `completed`: a
        // completude é medida no board. Só quando a medida bate é que a tentativa termina cedo.
        var reopenCompleted = false;
        if (string.Equals(current.Status, PlanMaterializationStatus.Completed, StringComparison.Ordinal))
        {
            if (await IsSettledAsync(current, cancellationToken))
            {
                return new PlanMaterializationOutcome(
                    PlanMaterializationResult.AlreadyComplete,
                    ExpectedCards: current.ExpectedCards ?? 0,
                    MaterializedCards: current.MaterializedCards ?? 0,
                    PlanId: current.PlanId);
            }

            reopenCompleted = true;
        }

        // Gate absoluto do Playbook para demandas propostas pela Bruna. O turno continua
        // registrando a necessidade do usuário e sua proveniência no banco, mas a decomposição
        // em cards de implementação só acontece quando a Fase 5 está realmente ativa. Antes
        // desta guarda, um primeiro "pode tocar o projeto?" criava cards Backend em andamento
        // dentro de Triagem, contornando documentos, Conselho e gate de liberação.
        //
        // Ausência de fase é Default-FAIL apenas para trabalho gerado pelo próprio turno. O
        // caminho manual (TurnId nulo) permanece compatível com projetos sem workflow.
        if (current.TurnId is not null)
        {
            var phase = await activePhases.ResolveSnapshotAsync(
                tenantId, current.ProjectId, cancellationToken);
            if (phase is null || phase.Order < 5)
            {
                return new PlanMaterializationOutcome(
                    PlanMaterializationResult.Deferred,
                    "workflow.development_not_released");
            }
        }

        var job = await jobs.TryBeginAsync(
            new PlanMaterializationBeginCommand(
                tenantId, demandId, ownerId, clock.UtcNow, _options.StaleAfter, reopenCompleted),
            cancellationToken);
        if (job is null)
        {
            return new PlanMaterializationOutcome(PlanMaterializationResult.NotClaimed);
        }

        try
        {
            await faults.SignalAsync(PlanMaterializationStage.BeforeConsume, cancellationToken);
            return await ExecuteAsync(job, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PlanMaterializationIntegrityException exception)
        {
            await FailAsync(job, exception.Code, cancellationToken);
            return new PlanMaterializationOutcome(
                PlanMaterializationResult.Failed, exception.Code, PlanId: job.PlanId);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Falha possivelmente transitória: o estado fica visível em `failed` e o erro sobe
            // para quem chamou reagendar (outbox) — nunca é engolido em silêncio.
            await FailAsync(job, exception.GetType().Name, cancellationToken);
            throw;
        }
    }

    private async Task<PlanMaterializationOutcome> ExecuteAsync(
        PlanMaterializationRecord job,
        CancellationToken cancellationToken)
    {
        var demand = await board.GetDemandAsync(job.TenantId, job.DemandId, cancellationToken);
        if (demand is null)
        {
            return await FailedOutcomeAsync(job, "demand_missing", cancellationToken);
        }

        var profile = (await localProfiles.ListAsync(cancellationToken))
            .FirstOrDefault(candidate => string.Equals(
                candidate.TenantId, job.TenantId, StringComparison.Ordinal));
        if (profile is null)
        {
            return await FailedOutcomeAsync(job, "local_profile_missing", cancellationToken);
        }

        // Fase 1D: o APRENDIZADO entra na decisão. A distribuição MAST das tentativas recentes
        // deste projeto diz qual modo de falha se repete, e cada categoria pede uma correção
        // diferente — fatiar menor, exigir critério explícito ou revisar mais fundo. Classificar
        // sem consumir era telemetria bonita: o painel media e o planejamento continuava igual.
        var correction = await ReadMastCorrectionAsync(job, cancellationToken);
        var saved = await materializer.EnsurePlanAsync(
            job.TenantId, demand, job.Request.AcceptanceCriteria, ToHints(job.Request.Surfaces),
            job.Request.Specialty, clock.UtcNow, cancellationToken);

        // B9/F12: materializar um plano é a Bruna gerando trabalho a partir do próprio output — é
        // aqui que o laço se fecha. A guarda só se aplica ao trabalho que o PRÓPRIO turno gerou;
        // uma materialização pedida por um humano no board não é autodisparo.
        if (job.TurnId is not null &&
            !await MayMaterializeAsync(job, demand, saved.Plan.Id, cancellationToken))
        {
            await FailAsync(job, "loop_guard_interrupted", cancellationToken);
            return new PlanMaterializationOutcome(
                PlanMaterializationResult.Interrupted, "loop_guard_interrupted",
                PlanId: saved.Plan.Id);
        }

        var activePhase = await activePhases.ResolveAsync(
            job.TenantId, demand.ProjectId, cancellationToken);
        var outcome = await materializer.MaterializeAsync(
            job.TenantId, profile.Id, saved.Plan, demand, clock.UtcNow, faults, activePhase,
            cancellationToken);

        var expected = saved.Plan.Cards.Count;
        var settled = await jobs.TryCompleteAsync(
            new PlanMaterializationCompleteCommand(
                job.TenantId, job.DemandId, job.OwnerId!, job.AttemptCount, saved.Plan.Id,
                expected, outcome.Cards.Count, clock.UtcNow),
            cancellationToken);
        if (!settled)
        {
            // Perdemos o fencing: outro dono assumiu o MESMO trabalho idempotente e conclui por
            // nós. O board já está correto; nada aqui pode ser declarado.
            return new PlanMaterializationOutcome(
                PlanMaterializationResult.NotClaimed, PlanId: saved.Plan.Id);
        }

        if (outcome.CreatedCards > 0 && job.TurnId is not null)
        {
            await RecordMaterializationCauseAsync(job, demand, saved.Plan.Id, cancellationToken);
        }

        await faults.SignalAsync(PlanMaterializationStage.BeforeAcknowledgement, cancellationToken);
        Materialized(logger, job.DemandId, saved.Plan.Id, outcome.CreatedCards, null);
        return new PlanMaterializationOutcome(
            PlanMaterializationResult.Materialized,
            ExpectedCards: expected,
            MaterializedCards: outcome.Cards.Count,
            CreatedCards: outcome.CreatedCards,
            PlanId: saved.Plan.Id);
    }


    /// <summary>
    /// Fase 1D: lê a distribuição MAST do projeto e traduz em ajuste de planejamento, AUDITANDO a
    /// decisão com a evidência que a produziu. Uma decisão de máquina que não pode ser citada é
    /// indistinguível de capricho — e a primeira pergunta de quem discorda dela é "por quê?".
    ///
    /// Falha de leitura não derruba o planejamento: sem sinal, decompõe-se como antes.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Learning must inform planning, never block it.")]
    private async Task<MastCorrection> ReadMastCorrectionAsync(
        PlanMaterializationRecord job, CancellationToken cancellationToken)
    {
        try
        {
            var classifications = await mastClassifications.ListByProjectAsync(
                job.TenantId, job.ProjectId, cancellationToken);
            var observations = classifications
                .GroupBy(record => record.FailureModeCode, StringComparer.Ordinal)
                .Select(group => new MastObservation(
                    group.Key,
                    MastTaxonomy.Find(group.Key)?.Category ?? MastCategory.SpecificationAndDesign,
                    group.Count()))
                .ToArray();
            var correction = MastCorrectionPolicy.Evaluate(observations);
            if (correction.HasSignal)
            {
                await audit.AppendAsync(
                    new AuditEventAppendCommand(
                        job.TenantId, "system", null, "plan.mastCorrectionApplied", "demand",
                        job.DemandId,
                        $"{correction.ReasonCode}: {correction.Evidence}",
                        clock.UtcNow),
                    cancellationToken);
            }

            return correction;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new MastCorrection(
                false, 0, false, false, MastCorrectionPolicy.ReasonNoSignal,
                $"A distribuição MAST não pôde ser lida ({exception.GetType().Name}).");
        }
    }

    /// <summary>
    /// O registro diz `completed`: isso só vale se o board CONCORDAR. Compara o conjunto real de
    /// fatias do plano com o previsto — é o que impede um plano invisivelmente incompleto de
    /// continuar passando por concluído.
    /// </summary>
    public async Task<bool> IsSettledAsync(
        PlanMaterializationRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.PlanId is null)
        {
            return false;
        }

        var plan = await plans.GetAsync(record.TenantId, record.PlanId, cancellationToken);
        if (plan is null || plan.MaterializedAt is null)
        {
            return false;
        }

        var materialized = await board.ListPlanCardsAsync(
            record.TenantId, record.PlanId, cancellationToken);
        HashSet<string> expected;
        try
        {
            expected = new HashSet<string>(
                DemandPlanMaterializer.SlicePlan(plan).Select(slice => slice.Key),
                StringComparer.Ordinal);
        }
        catch (PlanMaterializationIntegrityException)
        {
            // Um plano que não se deixa fatiar não pode ser declarado resolvido. Responder "não" é
            // o certo: a tentativa seguinte reabre o compromisso e registra o código do defeito no
            // lugar onde ele fica visível, em vez de a exceção escapar para o chamador.
            return false;
        }

        var actual = new HashSet<string>(
            materialized.Select(card => card.SliceKey), StringComparer.Ordinal);
        return actual.Count == materialized.Count && expected.SetEquals(actual);
    }

    private async Task<PlanMaterializationOutcome> FailedOutcomeAsync(
        PlanMaterializationRecord job, string code, CancellationToken cancellationToken)
    {
        await FailAsync(job, code, cancellationToken);
        return new PlanMaterializationOutcome(PlanMaterializationResult.Failed, code);
    }

    private async Task FailAsync(
        PlanMaterializationRecord job, string code, CancellationToken cancellationToken)
    {
        await jobs.TryFailAsync(
            new PlanMaterializationFailCommand(
                job.TenantId, job.DemandId, job.OwnerId!, job.AttemptCount, code, clock.UtcNow),
            cancellationToken);
        FailedLog(logger, job.DemandId, code, null);
    }

    /// <summary>
    /// Consulta as guardas de laço antes de materializar. Barrar aqui é preferível a barrar depois:
    /// uma vez materializados, os cards já ocuparam agentes e já geraram os eventos que realimentam
    /// o ciclo. A interrupção é sempre auditada com a evidência.
    /// </summary>
    private async Task<bool> MayMaterializeAsync(
        PlanMaterializationRecord job,
        BoardDemandRecord demand,
        string planId,
        CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var limits = new ChiefLoopGuardLimits();
        var causeKey = ChiefLoopGuardPolicy.PlanKey(planId);
        var facts = new ChiefLoopGuardFacts(
            SelfTriggeredTurnsForDemand: await loopGuards.CountSelfTriggeredTurnsAsync(
                job.TenantId, demand.Id, cancellationToken),
            // A reentrância por plano é medida pelo grafo: um plano que já causou esta demanda
            // antes está reentrando nela agora.
            PlanTurnInFlight: false,
            RecentSelfTriggeredTurns: await loopGuards.ListSelfTriggeredTurnsSinceAsync(
                job.TenantId, demand.Id, now - limits.Window, cancellationToken),
            CausalEdges: (await loopGuards.ListCausalEdgesAsync(
                    job.TenantId, job.ProjectId, cancellationToken))
                .Select(edge => new CausalEdge(edge.CauseKey, edge.EffectKey))
                .ToArray());
        var verdict = ChiefLoopGuardPolicy.Evaluate(
            new ChiefTurnTrigger(
                demand.Id,
                now,
                // O trabalho que a Bruna cria a partir do próprio plano é autodisparado por
                // definição: o dono pediu a demanda, não cada volta do ciclo.
                SelfTriggered: true,
                DemandPlanId: planId,
                CauseKey: causeKey),
            facts,
            limits);
        if (verdict.Allowed)
        {
            await loopGuards.RecordSelfTriggeredTurnAsync(
                new ChiefSelfTriggeredTurnRecord(
                    job.TenantId,
                    UlidValue.New(now).ToString(),
                    job.ProjectId,
                    demand.Id,
                    planId,
                    job.TurnId!,
                    causeKey,
                    now),
                cancellationToken);
            return true;
        }

        await loopGuards.RecordInterruptionAsync(
            new ChiefLoopInterruptionRecord(
                job.TenantId,
                UlidValue.New(now).ToString(),
                job.ProjectId,
                demand.Id,
                planId,
                verdict.ReasonCode,
                verdict.Detail,
                verdict.Cycle?.Path ?? [],
                now),
            cancellationToken);
        return false;
    }

    /// <summary>
    /// Materializou: registra "este plano gerou esta demanda" no grafo causal. É esta aresta que
    /// permite ao detector enxergar a volta quando ela se fechar.
    /// </summary>
    private Task RecordMaterializationCauseAsync(
        PlanMaterializationRecord job,
        BoardDemandRecord demand,
        string planId,
        CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        return loopGuards.AddCausalEdgeAsync(
            new ChiefCausalEdgeRecord(
                job.TenantId,
                UlidValue.New(now).ToString(),
                job.ProjectId,
                ChiefLoopGuardPolicy.DemandKey(demand.Id),
                ChiefLoopGuardPolicy.PlanKey(planId),
                "plan.materialized",
                now),
            cancellationToken);
    }

    /// <summary>
    /// Traduz a superfície declarada pelo Chefe nas dicas do planner. Nulo em ambos os lados
    /// significa "não declarei": o planner segue inferindo do texto, como antes.
    /// </summary>
    private static DemandDecompositionHints? ToHints(PlanMaterializationSurfaces? surfaces) =>
        surfaces is null
            ? null
            : new DemandDecompositionHints(
                surfaces.Frontend,
                surfaces.ExternalCredential,
                surfaces.TechnicalUncertainty,
                surfaces.Decision,
                surfaces.Backend);
}
