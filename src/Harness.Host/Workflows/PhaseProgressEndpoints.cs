using Harness.Host.Profiles;
using Harness.Modules.Workflows.Application;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.WorkChain;
using Harness.Persistence.Abstractions.Workflows;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;

namespace Harness.Host.Workflows;

/// <summary>
/// O progresso REAL de uma fase, para a tela. Separa três eixos que a interface vinha misturando:
/// o trabalho ACEITO (o percentual), o trabalho EM VOO (situação operacional, que nunca infla o
/// número) e a DECISÃO do portão (que é outro eixo — 100% pode coexistir com "aguardando
/// aprovação", e manter a fase em 99% por causa do humano mentiria sobre o que a equipe entregou).
/// </summary>
public static class PhaseProgressEndpoints
{
    public static IEndpointRouteBuilder MapPhaseProgress(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/workflow-runs/{runId}/phases/{phaseKey}/progress", GetAsync)
            .WithTags("workflows")
            .Produces<PhaseProgressContract>()
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);

        // CANCELAR UMA OBRIGAÇÃO É DECISÃO DE ESCOPO, e por isso é do dono — não do agente.
        //
        // O domínio já sabia cancelar e já exigia motivo; faltava a porta. Sem ela, uma release
        // que decide em pleno voo que uma fatia sai do plano fica travada para sempre: o portão
        // exige todas as obrigatórias aceitas, e a que saiu do escopo nunca vai ser aceita nem
        // pode ser marcada como entregue. Foi o que prendeu a fase 5 da prova limpa depois de a
        // fronteira do piloto virar backend-only.
        //
        // A trava contra abuso é a do domínio e continua inteira: cancelar sem motivo é recusado,
        // e a obrigação cancelada sai da conta INTEIRA — numerador e denominador —, de modo que
        // cancelar o que falhou não fabrica 100%. A evidência entra junto para que o registro diga
        // POR QUE saiu, e não apenas que saiu.
        endpoints.MapPost(
            "/api/v1/workflow-runs/{runId}/phases/{phaseKey}/obligations/{obligationId}/cancel",
            CancelAsync)
            .WithTags("workflows")
            .Produces<PhaseObligationCancelResult>()
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        return endpoints;
    }

    /// <param name="Reason">
    /// Por que a obrigação saiu do plano. Obrigatório: o domínio recusa cancelamento sem motivo,
    /// e é esse texto que separa "decidimos não fazer" de "não conseguimos fazer".
    /// </param>
    /// <param name="Evidence">
    /// Onde a decisão está registrada — ADR, commit, card substituto. Opcional no contrato e
    /// esperado na prática: uma decisão de escopo sem endereço vira boato no ledger.
    /// </param>
    public sealed record PhaseObligationCancelRequest(
        string Reason, IReadOnlyList<string>? Evidence = null);

    /// <param name="CardClosed">
    /// O card espelho fechou junto. <see langword="false"/> com <paramref name="CardId"/> presente
    /// significa divergência entre quadro e portão — declarada de propósito, para não sumir.
    /// </param>
    public sealed record PhaseObligationCancelResult(
        string ObligationId, string State, string? CardId = null, bool CardClosed = false);

    private static async Task<IResult> CancelAsync(
        string runId,
        string phaseKey,
        string obligationId,
        PhaseObligationCancelRequest body,
        HttpRequest request,
        ILocalProfileStore profiles,
        IPhaseObligationStore obligations,
        IWorkBoardStore board,
        IClock clock,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(body);
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null)
        {
            return Results.Problem(statusCode: 401, title: "unauthenticated");
        }

        if (string.IsNullOrWhiteSpace(body.Reason))
        {
            return Results.Problem(
                statusCode: 400,
                title: "reason_required",
                detail: "Cancelar uma obrigação exige motivo: sem ele, remover o que falhou " +
                    "seria o caminho curto para fabricar 100%.");
        }

        var applied = await obligations.UpdateStateAsync(
            new PhaseObligationStateCommand(
                profile.TenantId,
                obligationId,
                "cancelled",
                body.Evidence ?? [],
                body.Reason.Trim(),
                clock.UtcNow),
            token);

        if (!applied)
        {
            return Results.Problem(statusCode: 404, title: "obligation_not_found");
        }

        // O CARD PRECISA FECHAR JUNTO, senão o quadro contradiz o portão: a fase avança porque a
        // obrigação saiu do plano, e o card fica em "precisa de atenção" para sempre, pedindo uma
        // decisão que já foi tomada. Dois registros do mesmo fato que não conversam custam mais
        // confiança do que qualquer um deles entrega sozinho.
        //
        // Falhar aqui NÃO desfaz o cancelamento: a obrigação é a fonte do portão, e deixá-la
        // pendente porque o card resistiu seria travar a fase por causa do espelho. O resultado
        // diz o que aconteceu com cada um, para que a divergência apareça em vez de sumir.
        var cardClosed = false;
        var current = await obligations.ListCurrentAsync(profile.TenantId, runId, phaseKey, token);
        var cardId = current
            .FirstOrDefault(item => string.Equals(item.ObligationId, obligationId, StringComparison.Ordinal))
            ?.CardId;
        if (!string.IsNullOrWhiteSpace(cardId))
        {
            try
            {
                _ = await board.DismissTaskAsync(
                    new BoardTaskDismissCommand(
                        profile.TenantId,
                        cardId,
                        $"{BoardTaskDismissalPolicy.ScopeDecisionReasonPrefix} {body.Reason.Trim()}",
                        "system",
                        clock.UtcNow),
                    token);
                cardClosed = true;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Divergência declarada, nunca silenciosa: o chamador recebe cardClosed=false.
            }
        }

        return Results.Ok(new PhaseObligationCancelResult(obligationId, "cancelled", cardId, cardClosed));
    }

    private static async Task<IResult> GetAsync(
        string runId,
        string phaseKey,
        HttpRequest request,
        ILocalProfileStore profiles,
        IPhaseObligationStore obligations,
        IWorkflowStore workflows,
        CancellationToken token)
    {
        if (!UlidValue.TryParse(runId, out _))
        {
            return Results.Problem(statusCode: 400, title: "invalid_run_id", detail: "ID must be a ULID.");
        }

        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null)
        {
            return Results.Problem(
                statusCode: 401, title: "local_session_required",
                detail: "A local profile session is required.");
        }

        var current = await obligations.ListCurrentAsync(profile.TenantId, runId, phaseKey, token);
        if (current.Count == 0)
        {
            // Etapa que ainda não começou NÃO é erro: é o estado normal de toda fase futura. Como
            // 404, a tela do painel abria com OITO requisições vermelhas no console a cada carga —
            // ruído que esconde erro de verdade e faz um produto saudável parecer quebrado. O 404
            // fica onde ainda significa alguma coisa: execução inexistente ou etapa que não é
            // daquela esteira.
            var run = await workflows.ReadRunAggregateAsync(profile.TenantId, runId, token);
            var phase = run?.Phases.FirstOrDefault(candidate =>
                string.Equals(candidate.Key, phaseKey, StringComparison.Ordinal));
            if (phase is null)
            {
                return Results.Problem(
                    statusCode: 404, title: "phase_plan_not_found",
                    detail: "The workflow run or phase does not exist.");
            }

            return Results.Ok(new PhaseProgressContract(
                runId, phaseKey, 0, 0m, 0, 0, 0, 0, 0, 0, 0, 0, false, []));
        }

        var snapshot = PhaseProgressEvaluator.Evaluate(
        [
            .. current.Select(item => new PhaseObligation(
                item.ObligationKey,
                PhaseProgressEvaluator.ParseKind(item.Kind),
                item.Description,
                item.Required,
                (decimal)item.Weight,
                PhaseProgressEvaluator.ParseState(item.State),
                item.Source,
                item.CardId,
                item.ObjectiveKey,
                item.ArtifactRef)),
        ]);

        return Results.Ok(new PhaseProgressContract(
            runId,
            phaseKey,
            current[0].PlanVersion,
            snapshot.Percentage,
            snapshot.RequiredTotal,
            snapshot.RequiredAccepted,
            snapshot.InProgress,
            snapshot.InReview,
            snapshot.Blocked,
            snapshot.Pending,
            snapshot.OptionalTotal,
            snapshot.OptionalAccepted,
            snapshot.TechnicallyComplete,
            [
                .. current.Select(item => new PhaseObligationContract(
                    item.ObligationKey, item.Kind, item.Description, item.Required,
                    item.Weight, item.State, item.Source, item.CardId, item.ObjectiveKey,
                    item.ArtifactRef, item.Evidence, item.Reason)),
            ]));
    }
}

public sealed record PhaseObligationContract(
    string ObligationKey,
    string Kind,
    string Description,
    bool Required,
    double Weight,
    string State,
    string Source,
    string? CardId,
    string? ObjectiveKey,
    string? ArtifactRef,
    IReadOnlyList<string> Evidence,
    string? Reason);

public sealed record PhaseProgressContract(
    string RunId,
    string PhaseKey,
    int PlanVersion,
    decimal Percentage,
    int RequiredTotal,
    int RequiredAccepted,
    int InProgress,
    int InReview,
    int Blocked,
    int Pending,
    int OptionalTotal,
    int OptionalAccepted,
    bool TechnicallyComplete,
    IReadOnlyList<PhaseObligationContract> Obligations);
