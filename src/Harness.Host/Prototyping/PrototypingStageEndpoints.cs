using Harness.Host.Profiles;
using Harness.Modules.Coordination.Application;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.Organizations;
using Harness.Persistence.Abstractions.Projects;
using Harness.Persistence.Abstractions.Prototyping;
using Harness.SharedKernel.Identifiers;

namespace Harness.Host.Prototyping;

/// <summary>
/// O ESTADO DA ETAPA DE PROTOTIPAÇÃO, lido de dados reais (D11).
///
/// As políticas da fase — leitura de intake e portão "aprovado OU herdado" — eram puras e
/// testadas, mas ninguém as chamava: nenhuma superfície do produto sabia dizer se a etapa
/// estava satisfeita, por herança ou por aprovação, nem o que ainda faltava perguntar. Este
/// endpoint é o elo: ele lê o que EXISTE (marca e modelos da organização, pacote de telas
/// armazenado, protótipo publicado, modo de prototipação do projeto) e devolve o veredito
/// junto com a explicação em linguagem de negócio.
///
/// Três decisões de honestidade, para ninguém confundir leitura com invenção:
///
/// 1. <b>A aplicabilidade vem do MODO DE PROTOTIPAÇÃO que o projeto já declara</b>
///    (`externalPrototype` | `guidelinesOnly` | `autonomousGeneration` | `notApplicable`) — dado
///    existente, não heurística sobre tecnologias. `notApplicable` significa que a etapa não
///    existe para esta demanda, e é o dono quem disse isso.
/// 2. <b>"Protótipo aprovado" é lido como `published`</b>, porque o domínio de protótipos NÃO
///    tem estado de aprovação (`draft/generating/ready/published/archived`). É o sinal positivo
///    terminal que existe hoje; um estado explícito de aprovação seria melhor e está registrado
///    como achado, não improvisado aqui.
/// 3. <b>O caminho de entrada é derivado do que está ARMAZENADO</b> — pacote de telas presente
///    significa caminho "pacote React". O caminho "documento de requisitos" é detectado durante
///    a conversa do intake, não no acervo, e por isso este read model não o afirma.
/// </summary>
public static class PrototypingStageEndpoints
{
    /// <summary>Modo em que o projeto declara que prototipação não se aplica.</summary>
    public const string NotApplicableMode = "notApplicable";

    /// <summary>
    /// Etiqueta que marca uma referência visual como o pacote de telas do projeto. É etiqueta e
    /// não origem porque `source` é um conjunto fechado no banco (`upload|url|generated`) — o
    /// pacote continua sendo um upload; o que muda é o papel dele.
    /// </summary>
    public const string DesignSystemTag = "design-system";

    /// <summary>Estado de protótipo que o produto tem hoje como aval positivo terminal.</summary>
    public const string ApprovedPrototypeState = "published";

    public static IEndpointRouteBuilder MapPrototypingStage(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/projects/{id}/prototyping-stage", GetAsync)
            .WithTags("prototyping")
            .Produces<PrototypingStageContract>()
            .ProducesProblem(400)
            .ProducesProblem(401)
            .ProducesProblem(404);
        return endpoints;
    }

    private static async Task<IResult> GetAsync(
        string id,
        HttpRequest request,
        ILocalProfileStore profiles,
        IProjectStore projects,
        IOrganizationStore organizations,
        IPrototypeStore prototypes,
        CancellationToken token)
    {
        if (!UlidValue.TryParse(id, out _))
        {
            return Results.Problem(
                title: "invalid_project_id", detail: "Project ID must be a ULID.", statusCode: 400);
        }

        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null)
        {
            return Results.Problem(
                title: "local_session_required",
                detail: "A local profile session is required.",
                statusCode: 401);
        }

        var project = await projects.GetAsync(profile.TenantId, id, token);
        if (project is null)
        {
            return Results.Problem(
                title: "project_not_found",
                detail: "The requested resource does not exist.",
                statusCode: 404);
        }

        var organization = await organizations.GetAsync(profile.TenantId, project.OrganizationId, token);
        var references = await prototypes.ListReferencesAsync(profile.TenantId, id, null, 200, token);
        var projectPrototypes = await prototypes.ListPrototypesAsync(profile.TenantId, id, null, 200, token);

        var bundle = references.FirstOrDefault(reference =>
            reference.Tags.Contains(DesignSystemTag, StringComparer.OrdinalIgnoreCase));
        // O PACOTE DE TELAS CONTA COMO IDENTIDADE. A política de intake trata o pacote como
        // resposta visual completa (não pergunta nada depois dele), mas a do portão pede marca à
        // parte — quem anexou as telas ficaria preso num portão pedindo cor primária que já veio
        // dentro do arquivo. Aqui as duas leituras concordam; a divergência está registrada no
        // quadro para quem for unificar as políticas.
        var assets = new OrganizationDesignAssets(
            HasBrand: HasBrand(organization?.Brand) || HasBrand(project.Brand) || bundle is not null,
            HasDesignSystem: bundle is not null,
            HasScreenTemplates: organization?.TemplateKeys.Count > 0);

        // O acervo faz o papel do anexo: pacote armazenado é a mesma evidência que o anexo do
        // intake trouxe, e é o que sustenta o caminho "pacote React" depois da conversa.
        var attachments = bundle is null
            ? Array.Empty<IntakeAttachment>()
            : [new IntakeAttachment($"{bundle.Title}.zip", "application/zip", 1)];

        var reading = PrototypingIntakePolicy.Read(attachments, assets);
        var applies = !string.Equals(
            project.Prototyping.Mode, NotApplicableMode, StringComparison.Ordinal);
        var verdict = PrototypingStagePolicy.Evaluate(
            applies,
            assets,
            projectPrototypes.Any(prototype =>
                string.Equals(prototype.State, ApprovedPrototypeState, StringComparison.Ordinal)));

        // DISPENSA REGISTRADA É DECISÃO DO DONO, e o portão não a atropela: o veredito continua
        // dizendo a verdade sobre o que falta, mas deixa de barrar o avanço. O motivo da dispensa
        // viaja junto para a tela nunca mostrar bloqueio sem explicar por que ele não vale.
        var waiver = project.Prototyping.Waiver;
        return Results.Ok(new PrototypingStageContract(
            project.Id,
            verdict.State.ToString(),
            verdict.BlocksAdvance && waiver is null,
            verdict.ReasonCode,
            verdict.BusinessMessage,
            reading.Path.ToString(),
            reading.Inherited,
            reading.Questions,
            bundle?.Id,
            project.Prototyping.Mode,
            waiver?.Reason));
    }

    private static bool HasBrand(OrganizationBrandRecord? brand) =>
        !string.IsNullOrWhiteSpace(brand?.LogoUrl) || !string.IsNullOrWhiteSpace(brand?.PrimaryColor);

    private static bool HasBrand(ProjectBrandRecord? brand) =>
        !string.IsNullOrWhiteSpace(brand?.LogoUrl) || !string.IsNullOrWhiteSpace(brand?.PrimaryColor);
}

/// <summary>
/// Estado da etapa de Prototipação para uma demanda. <paramref name="Inherited"/> e
/// <paramref name="Questions"/> existem para o produto nunca herdar em silêncio: o dono vê o
/// que veio de outro lugar e o que ainda será perguntado a ele.
/// </summary>
public sealed record PrototypingStageContract(
    string ProjectId,
    string State,
    bool BlocksAdvance,
    string ReasonCode,
    string BusinessMessage,
    string EntryPath,
    IReadOnlyList<string> Inherited,
    IReadOnlyList<string> Questions,
    string? BundleReferenceId,
    string PrototypingMode,
    string? WaiverReason);
