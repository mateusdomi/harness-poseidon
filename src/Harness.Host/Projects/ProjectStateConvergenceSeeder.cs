using Harness.Persistence.Abstractions.Organizations;
using Harness.Persistence.Abstractions.Projects;
using Harness.Persistence.Abstractions.Prototyping;
using Harness.Persistence.Abstractions.Workflows;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;

namespace Harness.Host.Projects;

/// <summary>Conta o que esta chamada efetivamente convergiu (0/false quando já convergido).</summary>
public sealed record ProjectStateConvergenceResult(
    bool RunStarted,
    bool PrototypeRegistered,
    bool BrandFilled)
{
    public static ProjectStateConvergenceResult None { get; } = new(false, false, false);

    public bool ChangedAnything => RunStarted || PrototypeRegistered || BrandFilled;
}

/// <summary>
/// RN-03: um projeto REAL cadastrado nunca deve aparecer "sem fase / 0% / sem protótipo / sem marca".
/// O próprio Poseidon nasceu antes de várias regras e, para o dono, o cockpit/chat mostravam
/// "Nenhuma execução / Fase: nenhuma", sem protótipo e sem marca da organização — embora o projeto
/// EXISTA e esteja em desenvolvimento ativo (o front está no ar).
///
/// Este seeder converge, de forma idempotente e no startup (gate <c>AgentRuns.Enabled</c>, o mesmo
/// padrão de <c>ChiefCliProviderCatalogSeeder</c> e <c>ProjectWorkflowConvergenceSeeder</c>), o
/// ESTADO REAL do projeto Poseidon — preenchendo apenas o que é verdadeiro/derivável e NUNCA
/// fabricando progresso ("100% aprovado" e afins):
///
/// 1. <b>Fase/estado</b>: se o projeto já tem um workflow vinculado mas NENHUMA run, cria a run real
///    do binding e a INICIA (mesma sequência do endpoint <c>POST /workflow-runs</c>: CreateRun +
///    Start). A run passa a "running" (em andamento) e a primeira fase do workflow fica ATIVA — a
///    verdade honesta de que o projeto está sob execução. NÃO avança fases nem tica objetivos/gates
///    (isso fabricaria aprovações que não existem): a run começa no início, como qualquer execução.
///    Assim o cockpit/chat deixam de mostrar "Nenhuma execução / Fase: nenhuma".
///
/// 2. <b>Progresso</b>: NÃO é gravado nada aqui. O progresso é DERIVADO do estado real da run pelo
///    próprio motor de Workflows (soma ponderada de objetivos executed/validated/approved). Como a
///    run apenas começou, o progresso derivado é honestamente baixo — não é um "0% enganoso"
///    inventado, é o valor REAL de uma execução recém-iniciada, que sobe conforme o trabalho avança.
///
/// 3. <b>Protótipo</b>: registra o FRONT ATUAL do Poseidon como protótipo do projeto (o front
///    EXISTE), em estado "ready" (pronto para visualização), sem URL pública fabricada — não há
///    deploy publicado, então NÃO transiciona para "published" (que exigiria uma URL real). A
///    descrição deixa claro que é o front real em execução local.
///
/// 4. <b>Marca da organização</b>: se a organização dona do projeto está sem marca (todos os campos
///    nulos), preenche uma marca default plausível (paleta e tipografia) — sem logo, porque não há
///    um asset real e uma URL de logo fabricada quebraria/mentiria.
///
/// Idempotente: run só é criada quando não existe nenhuma; protótipo só quando o registro fixo ainda
/// não existe; marca só quando está inteiramente vazia. Reexecutar não duplica nada. Roda por tenant;
/// se o projeto Poseidon não existir no tenant, é um no-op silencioso.
/// </summary>
public sealed class ProjectStateConvergenceSeeder(
    IProjectStore projects,
    IWorkflowCatalogStore workflowCatalog,
    IWorkflowStore workflowAuthority,
    IPrototypeStore prototypes,
    IOrganizationStore organizations,
    IClock clock)
{
    /// <summary>O projeto "Poseidon" (nascido antes das regras) cujo estado esta convergência preenche.</summary>
    public const string PoseidonProjectId = "01KY36JQ8A48Q2TVYJMA5Q1N4F";

    /// <summary>Id fixo do protótipo "Front atual do Poseidon" — garante idempotência (não duplica).</summary>
    public const string FrontPrototypeId = "01KY36JQ8A48Q2TVYJMA5Q1PR0";

    /// <summary>Id fixo da run de convergência — a run real do binding, iniciada no startup.</summary>
    public const string ConvergenceRunId = "01KY36JQ8A48Q2TVYJMA5Q1RN1";

    // Marca default plausível para o "Grupo Poseidon": paleta marinha (Poseidon = mar) e tipografia
    // neutra. Sem logo: não há asset real; uma URL fabricada quebraria — honestidade > preenchimento.
    private static readonly OrganizationBrandRecord DefaultBrand =
        new(LogoUrl: null, PrimaryColor: "#0E4C92", SecondaryColor: "#38B2AC", Typography: "Inter");

    private readonly IProjectStore _projects = projects ?? throw new ArgumentNullException(nameof(projects));
    private readonly IWorkflowCatalogStore _workflowCatalog =
        workflowCatalog ?? throw new ArgumentNullException(nameof(workflowCatalog));
    private readonly IWorkflowStore _workflowAuthority =
        workflowAuthority ?? throw new ArgumentNullException(nameof(workflowAuthority));
    private readonly IPrototypeStore _prototypes = prototypes ?? throw new ArgumentNullException(nameof(prototypes));
    private readonly IOrganizationStore _organizations =
        organizations ?? throw new ArgumentNullException(nameof(organizations));
    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <summary>
    /// Converge o estado do projeto Poseidon dentro de um tenant. Idempotente e seguro: se o projeto
    /// não existe no tenant, retorna <see cref="ProjectStateConvergenceResult.None"/> sem efeitos.
    /// </summary>
    public async Task<ProjectStateConvergenceResult> EnsureConvergedAsync(
        string tenantId, string actorProfileId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorProfileId);

        var project = await _projects.GetAsync(tenantId, PoseidonProjectId, cancellationToken);
        if (project is null)
        {
            return ProjectStateConvergenceResult.None;
        }

        var runStarted = await EnsurePhaseAsync(tenantId, project, cancellationToken);
        var prototypeRegistered = await EnsureFrontPrototypeAsync(tenantId, actorProfileId, project, cancellationToken);
        var brandFilled = await EnsureOrganizationBrandAsync(tenantId, project, cancellationToken);

        return new ProjectStateConvergenceResult(runStarted, prototypeRegistered, brandFilled);
    }

    /// <summary>
    /// Se há um binding sem nenhuma run, cria a run real do binding e a inicia — a mesma sequência do
    /// endpoint de criação de run. Se não há binding (o <c>ProjectWorkflowConvergenceSeeder</c> ainda
    /// não vinculou), não faz nada: converge no próximo restart, depois que o workflow existir.
    /// </summary>
    private async Task<bool> EnsurePhaseAsync(
        string tenantId, ProjectRecord project, CancellationToken cancellationToken)
    {
        var bindings = await _workflowCatalog.ListBindingsAsync(
            tenantId, project.Id, null, 1, cancellationToken);
        if (bindings.Count == 0)
        {
            return false;
        }

        var binding = bindings[0];

        var existingRuns = await _workflowCatalog.ListRunsAsync(
            tenantId, binding.Id, null, 1, cancellationToken);
        if (existingRuns.Count > 0)
        {
            return false;
        }

        var now = _clock.UtcNow;
        var created = await _workflowAuthority.CreateRunAsync(
            new WorkflowRunCreateCommand(
                tenantId,
                project.Id,
                binding.ActiveVersionId,
                ConvergenceRunId,
                $"seed:project-state:run:{project.Id}",
                now,
                binding.Id),
            cancellationToken);

        // Inicia a run: a primeira fase do workflow fica ATIVA e o estado vira "running" (em
        // andamento). Se um replay idempotente devolveu a run já criada, o Start converge o estado.
        var started = await _workflowAuthority.TransitionRunAsync(
            new WorkflowRunTransitionCommand(
                tenantId,
                ConvergenceRunId,
                WorkflowRunTransition.Start,
                created.RunVersion,
                $"seed:project-state:run-start:{project.Id}",
                now.AddTicks(1)),
            cancellationToken);

        // Só reportamos "iniciada agora" quando o Start foi efetivamente aplicado. Qualquer outro
        // status (replay/estado inválido) é benigno: a run existe e converge no próximo restart.
        return started.Status is WorkflowRunMutationStatus.Applied
            or WorkflowRunMutationStatus.IdempotentReplay;
    }

    /// <summary>
    /// Registra o front atual do Poseidon como protótipo do projeto, uma única vez (id fixo). Deixa
    /// em "ready" (pronto para visualização) — sem URL pública, pois não há deploy publicado.
    /// </summary>
    private async Task<bool> EnsureFrontPrototypeAsync(
        string tenantId, string actorProfileId, ProjectRecord project, CancellationToken cancellationToken)
    {
        // Protótipo é inaplicável quando o projeto tem waiver: respeitamos e não forçamos.
        if (string.Equals(project.Prototyping.Mode, "notApplicable", StringComparison.Ordinal))
        {
            return false;
        }

        var existing = await _prototypes.GetPrototypeAsync(tenantId, FrontPrototypeId, cancellationToken);
        if (existing is not null)
        {
            return false;
        }

        var now = _clock.UtcNow;
        await _prototypes.CreatePrototypeAsync(
            new PrototypeCreateCommand(
                tenantId,
                actorProfileId,
                FrontPrototypeId,
                project.Id,
                "Front atual do Poseidon",
                "Referência visual do produto: o próprio front-end do Poseidon, em execução, " +
                "usado como protótipo real do projeto. Registrado automaticamente porque o front EXISTE.",
                SourceDocumentId: null,
                now),
            cancellationToken);

        // draft -> ready: o front existe e está pronto para visualização. NÃO publicamos (published
        // exigiria uma URL real de deploy, que não temos — fabricá-la seria desonesto).
        await _prototypes.TransitionPrototypeAsync(
            new PrototypeTransitionCommand(
                tenantId,
                actorProfileId,
                FrontPrototypeId,
                "ready",
                Url: null,
                ThumbnailUrl: null,
                now.AddTicks(1)),
            cancellationToken);

        return true;
    }

    /// <summary>
    /// Se a organização dona do projeto está inteiramente sem marca, preenche a marca default
    /// plausível. Só toca a marca (preserva nome/slug/plano/templates/políticas).
    /// </summary>
    private async Task<bool> EnsureOrganizationBrandAsync(
        string tenantId, ProjectRecord project, CancellationToken cancellationToken)
    {
        var organization = await _organizations.GetAsync(
            tenantId, project.OrganizationId, cancellationToken);
        if (organization is null || !IsBrandEmpty(organization.Brand))
        {
            return false;
        }

        var result = await _organizations.UpdateAsync(
            new OrganizationUpdateCommand(
                tenantId,
                organization.Id,
                organization.Name,
                organization.Slug,
                organization.Plan,
                DefaultBrand,
                organization.Version),
            cancellationToken);

        return result.Status == OrganizationMutationStatus.Applied;
    }

    private static bool IsBrandEmpty(OrganizationBrandRecord brand) =>
        string.IsNullOrWhiteSpace(brand.LogoUrl) &&
        string.IsNullOrWhiteSpace(brand.PrimaryColor) &&
        string.IsNullOrWhiteSpace(brand.SecondaryColor) &&
        string.IsNullOrWhiteSpace(brand.Typography);
}
