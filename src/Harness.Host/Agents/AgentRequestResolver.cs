using Harness.Modules.Agents.Application.Accounts;
using Harness.Modules.Governance.Coordination;
using Harness.Persistence.Abstractions.Agents;
using Harness.Persistence.Abstractions.Projects;
using Harness.Persistence.Abstractions.WorkChain;
using Harness.SharedKernel.Time;

namespace Harness.Host.Agents;

/// <summary>Desfecho da resolução de uma solicitação, para log e teste.</summary>
public sealed record AgentRequestResolution(
    string RequestId, string Kind, string State, string ReasonCode, string? Answer);

/// <summary>
/// A chefe RESPONDENDO às solicitações estruturadas dos agentes.
///
/// Antes, um executor que precisava de uma informação só tinha o caminho caro: falhar, ser
/// reprovado e escalar depois de N ciclos — uma tentativa inteira gasta para descobrir uma frase.
/// Aqui a solicitação vira um item de fila que a chefe resolve com o que já existe: o card, a
/// instrução, o escopo concedido e as convenções do projeto. Só sobe ao dono o que o sistema
/// genuinamente não pode resolver.
///
/// A ordem das tentativas de resolução é a da política de produto: consultar o card, consultar o
/// contexto, seguir a convenção existente e só então escalar.
/// </summary>
public sealed partial class AgentRequestResolver(
    IAgentRequestStore requests,
    IWorkBoardStore board,
    IProjectStore projects,
    IClock clock,
    ILogger<AgentRequestResolver> logger)
{
    private readonly IAgentRequestStore _requests = requests ?? throw new ArgumentNullException(nameof(requests));
    private readonly IWorkBoardStore _board = board ?? throw new ArgumentNullException(nameof(board));
    private readonly IProjectStore _projects = projects ?? throw new ArgumentNullException(nameof(projects));
    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    public async Task<IReadOnlyList<AgentRequestResolution>> ResolveOpenAsync(
        string tenantId, string projectId, CancellationToken cancellationToken)
    {
        var open = await _requests.ListOpenAsync(tenantId, projectId, 50, cancellationToken);
        if (open.Count == 0)
        {
            return [];
        }

        var project = await _projects.GetAsync(tenantId, projectId, cancellationToken);
        var surfaceMap = project is null || string.IsNullOrWhiteSpace(project.RepositoryUrl)
            ? RepositorySurfaceMap.Empty
            : RepositorySurfaceMap.Build(Path.GetFullPath(project.RepositoryUrl));

        var resolutions = new List<AgentRequestResolution>(open.Count);
        foreach (var request in open)
        {
            resolutions.Add(await ResolveAsync(request, surfaceMap, cancellationToken));
        }

        return resolutions;
    }

    private async Task<AgentRequestResolution> ResolveAsync(
        AgentRequestRecord request,
        RepositorySurfaceMap surfaceMap,
        CancellationToken cancellationToken)
    {
        var kind = AgentRequestPolicy.ParseKind(request.Kind);
        var routing = AgentRequestPolicy.Route(kind, $"{request.Question} {request.Reason}");
        if (routing == AgentRequestRouting.EscalateToHuman)
        {
            // Escalonamento LEGÍTIMO: dinheiro, credencial que o sistema não tem, decisão legal,
            // conflito canônico. A solicitação fica registrada como escalada — o anúncio ao dono
            // é feito pelo laço do chefe, que é a única voz com ele.
            return await ApplyAsync(
                request, "escalated", "human", "request.requires_external_authority",
                "Esta decisão depende de um recurso ou de uma autoridade que o sistema não possui. " +
                "Levada ao dono do projeto.",
                cancellationToken);
        }

        return kind == AgentRequestKind.ScopeExpansion
            ? await ResolveScopeExpansionAsync(request, surfaceMap, cancellationToken)
            : await ResolveOperationalAsync(request, cancellationToken);
    }

    /// <summary>
    /// EXPANSÃO DE ESCOPO. O agente descobriu no meio do trabalho que precisa de um path fora do
    /// claim. Isso não pode ser silencioso nem virar falha: a policy valida os paths pedidos
    /// contra o papel, e o que passa é concedido com registro. Ampliação para a raiz inteira é
    /// recusada — seria desfazer o estreitamento que torna o paralelismo possível.
    /// </summary>
    private async Task<AgentRequestResolution> ResolveScopeExpansionAsync(
        AgentRequestRecord request,
        RepositorySurfaceMap surfaceMap,
        CancellationToken cancellationToken)
    {
        if (request.RequestedPaths.Count == 0)
        {
            return await ApplyAsync(
                request, "rejected", "chief", "scope.no_paths_requested",
                "A expansão precisa declarar quais paths, e nenhum foi informado.",
                cancellationToken);
        }

        var task = await _board.GetTaskAsync(request.TenantId, request.TaskId, cancellationToken);
        var instructions = task is null
            ? []
            : await _board.ListInstructionsAsync(request.TenantId, request.TaskId, null, 5, cancellationToken);
        var resolution = ChiefCardResolver.Resolve(
            task?.Title ?? string.Empty,
            instructions.Count > 0 ? instructions[^1].Body : string.Empty,
            [], task?.Priority ?? "medium", surfaceMap: surfaceMap);
        var kindOfScope = string.Equals(
            resolution.Role, AgentRoles.FrontendSpecialist, StringComparison.OrdinalIgnoreCase)
            ? AgentPathScopeKind.FrontendSpecialist
            : AgentPathScopeKind.Backend;

        // A policy de path é a MESMA da aquisição: traversal, path absoluto, curinga no meio e
        // fonte canônica continuam recusados aqui, senão a expansão viraria a porta dos fundos.
        var decision = AgentPathScopePolicy.Evaluate(kindOfScope, request.RequestedPaths);
        if (!decision.Allowed)
        {
            return await ApplyAsync(
                request, "rejected", "chief", decision.Code,
                $"Paths recusados pela política de escopo: {string.Join(", ", decision.RejectedClaims)}.",
                cancellationToken);
        }

        // Ampliar de volta para a raiz do papel desfaria o estreitamento e devolveria o produto ao
        // paralelismo de um card por papel. Um pedido desses é sinal de card mal decomposto.
        var roleRoots = AgentRoles.PathScopesFor(resolution.Role);
        if (request.RequestedPaths.Any(path => roleRoots.Contains(path, StringComparer.OrdinalIgnoreCase)))
        {
            return await ApplyAsync(
                request, "rejected", "chief", "scope.expansion_to_role_root",
                "Ampliar para a raiz inteira do papel desfaria o escopo por card. " +
                "Se o trabalho realmente atravessa o repositório, o card precisa ser decomposto.",
                cancellationToken);
        }

        return await ApplyAsync(
            request, "answered", "chief", "scope.expansion_granted",
            $"Expansão concedida para: {string.Join(", ", request.RequestedPaths)}. " +
            "Uma nova tentativa é criada com o escopo ampliado e token novo; a tentativa atual " +
            "encerra sem publicar.",
            cancellationToken);
    }

    /// <summary>
    /// Decisão OPERACIONAL. A chefe responde com o que já existe: a instrução do card carrega o
    /// escopo, os critérios de aceite e os gates, e é a fonte que o agente deveria ter lido. Uma
    /// opção recomendada pelo próprio agente é aceita quando existe — ele está mais perto do
    /// código do que qualquer heurística aqui.
    /// </summary>
    private async Task<AgentRequestResolution> ResolveOperationalAsync(
        AgentRequestRecord request, CancellationToken cancellationToken)
    {
        var instructions = await _board.ListInstructionsAsync(
            request.TenantId, request.TaskId, null, 5, cancellationToken);
        var instruction = instructions.Count > 0 ? instructions[^1].Body : string.Empty;

        if (request.RecommendedOption is { Length: > 0 } recommended)
        {
            return await ApplyAsync(
                request, "answered", "chief", "request.recommendation_accepted",
                $"Siga a sua recomendação: {recommended}. " +
                "Ela é reversível e está dentro do escopo do card; registre a decisão na evidência.",
                cancellationToken);
        }

        if (request.Options.Count > 0)
        {
            // Sem recomendação, a chefe escolhe a PRIMEIRA opção declarada e diz por quê: decidir é
            // atribuição dela, e devolver a pergunta ao agente só gastaria outro ciclo.
            return await ApplyAsync(
                request, "answered", "chief", "request.option_selected",
                $"Decisão: {request.Options[0]}. Escolhi a alternativa mais conservadora entre as " +
                "que você listou; se o trabalho mostrar que ela não serve, reporte com evidência " +
                "em vez de trocar por conta própria.",
                cancellationToken);
        }

        return await ApplyAsync(
            request, "answered", "chief", "request.answered_from_card",
            "A resposta está no próprio card: siga o escopo, os critérios de aceite e os padrões " +
            "já estabelecidos no repositório do projeto. Onde a informação realmente faltar, " +
            "declare a lacuna na evidência em vez de inventar." +
            (instruction.Length > 0 ? string.Empty : " (O card não tem instrução registrada.)"),
            cancellationToken);
    }

    private async Task<AgentRequestResolution> ApplyAsync(
        AgentRequestRecord request,
        string state,
        string answeredBy,
        string reasonCode,
        string answer,
        CancellationToken cancellationToken)
    {
        var applied = await _requests.AnswerAsync(
            new AgentRequestAnswerCommand(
                request.TenantId, request.RequestId, state, answeredBy, answer, reasonCode,
                request.FencingToken, _clock.UtcNow),
            cancellationToken);
        if (applied is null)
        {
            // Fencing não bateu: a tentativa que perguntou já foi substituída. Responder assim
            // mesmo entregaria a decisão a quem não pode mais ouvi-la.
            LogRequestStale(logger, request.RequestId, request.Kind);
            return new AgentRequestResolution(
                request.RequestId, request.Kind, request.State, "request.stale_fencing", null);
        }

        LogRequestResolved(logger, request.RequestId, request.Kind, state, reasonCode);
        return new AgentRequestResolution(request.RequestId, request.Kind, state, reasonCode, answer);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Chief: solicitação {RequestId} ({Kind}) resolvida como {State}/{ReasonCode}.")]
    private static partial void LogRequestResolved(
        ILogger logger, string requestId, string kind, string state, string reasonCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Chief: solicitação {RequestId} ({Kind}) ignorada — a tentativa que perguntou já foi substituída.")]
    private static partial void LogRequestStale(ILogger logger, string requestId, string kind);
}
