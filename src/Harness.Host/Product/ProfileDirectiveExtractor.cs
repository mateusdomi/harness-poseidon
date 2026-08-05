using Harness.Modules.Workflows.Product;
using Harness.Persistence.Abstractions.Documents;
using Harness.Persistence.Abstractions.WorkChain;

namespace Harness.Host.Product;

/// <summary>
/// Lê as decisões humanas de onde elas JÁ VIVEM e as normaliza em diretivas de perfil.
///
/// Nenhuma fonte nova foi criada: o pedido do usuário está nas solicitações e demandas do quadro,
/// e as decisões arquiteturais aprovadas estão no catálogo de documentos, com tipo e estado. Criar
/// `project_overrides` ao lado disso produziria duas verdades sobre a mesma coisa — exatamente o
/// defeito que `governance/core.md` chama de "duas fontes canônicas para o mesmo tema".
///
/// A CONSTRAINT DE ORGANIZAÇÃO passou a ter fonte real: as políticas da organização
/// (`organizations.policies_json`), que já existiam persistidas e nunca chegavam ao resolvedor.
/// Uma política habilitada cujo chave siga `engineering:<área>=<valor>` vira diretiva com
/// autoridade `OrganizationConstraint` — abaixo do requisito do usuário e do ADR, acima do
/// baseline, que é exatamente onde um padrão corporativo deve morar. O que continua sem fonte
/// persistida, e registrado como lacuna: restrição regulatória.
/// </summary>
public sealed class ProfileDirectiveExtractor(
    IWorkBoardStore board,
    IDocumentCatalogStore? documents = null,
    string? repositoryRoot = null,
    ILogger<ProfileDirectiveExtractor>? logger = null,
    Harness.Persistence.Abstractions.Organizations.IOrganizationStore? organizations = null,
    Harness.Persistence.Abstractions.Projects.IProjectStore? projects = null)
{
    /// <summary>Prefixo das políticas de engenharia da organização. `engineering:database=SQL Server`.</summary>
    private const string EngineeringPolicyPrefix = "engineering:";

    /// <summary>Tipos de documento que carregam decisão arquitetural aprovada.</summary>
    private static readonly string[] DecisionKinds = ["adr", "decision", "decisao", "decisão"];

    /// <summary>Estados que significam "aprovado". Rascunho não decide nada.</summary>
    private static readonly string[] ApprovedStates = ["approved", "aprovado", "accepted", "aceito"];

    public sealed record Extraction(
        IReadOnlyList<ProfileDirective> Directives,
        IReadOnlyList<string> ActiveAdrs,
        string DemandText);

    public async Task<Extraction> ExtractAsync(
        string tenantId,
        string projectId,
        CancellationToken cancellationToken)
    {
        var directives = new List<ProfileDirective>();
        var adrs = new List<string>();

        // 1. REQUISITO DO PROJETO — o que o usuário escreveu. A solicitação é a fonte original;
        //    a demanda é a leitura dela pela chefe. As duas entram, com a solicitação primeiro
        //    porque é a palavra do dono.
        var demandText = new System.Text.StringBuilder();
        var solicitations = await board.ListSolicitationsAsync(
            tenantId, projectId, null, 50, cancellationToken);
        foreach (var solicitation in solicitations)
        {
            demandText.AppendLine(solicitation.Title).AppendLine(solicitation.Body);
            directives.AddRange(ProfileDirectiveParser.Parse(
                $"{solicitation.Title}\n{solicitation.Body}",
                ProfileAuthority.ProjectRequirement,
                solicitation.Id,
                "solicitation"));
        }

        var demands = await board.ListDemandsAsync(tenantId, projectId, null, null, 50, cancellationToken);
        foreach (var demand in demands)
        {
            demandText.AppendLine(demand.Title).AppendLine(demand.Description);
            directives.AddRange(ProfileDirectiveParser.Parse(
                $"{demand.Title}\n{demand.Description}",
                ProfileAuthority.ProjectRequirement,
                demand.Id,
                "demand"));
        }

        // 2. DECISÃO APROVADA — ADRs no catálogo de documentos. Só o que está aprovado decide.
        if (documents is not null)
        {
            directives.AddRange(await ExtractDecisionsAsync(tenantId, projectId, adrs, cancellationToken));
        }

        // 3. CONSTRAINT DA ORGANIZAÇÃO — as políticas de engenharia persistidas na organização do
        //    projeto. É a tese "o desenvolvedor não repete standards em todo projeto": o padrão
        //    corporativo entra sozinho, e o requisito do projeto continua podendo sobrescrevê-lo,
        //    porque a precedência do resolvedor já decide isso.
        directives.AddRange(await ExtractOrganizationConstraintsAsync(
            tenantId, projectId, cancellationToken));

        LogExtracted(logger, projectId, directives.Count, adrs.Count);
        return new Extraction(Deduplicate(directives), adrs, demandText.ToString());
    }

    private async Task<IReadOnlyList<ProfileDirective>> ExtractOrganizationConstraintsAsync(
        string tenantId,
        string projectId,
        CancellationToken cancellationToken)
    {
        if (organizations is null || projects is null)
        {
            return [];
        }

        var project = await projects.GetAsync(tenantId, projectId, cancellationToken);
        if (project is null)
        {
            return [];
        }

        var organization = await organizations.GetAsync(
            tenantId, project.OrganizationId, cancellationToken);
        if (organization is null)
        {
            return [];
        }

        var directives = new List<ProfileDirective>();
        foreach (var policy in organization.Policies.Where(item => item.Enabled))
        {
            // Forma fechada `engineering:<área>=<valor>`. Política que não segue a forma não é
            // erro — é política de outro assunto (billing, acesso), e este extrator não opina.
            if (!policy.Key.StartsWith(EngineeringPolicyPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var body = policy.Key[EngineeringPolicyPrefix.Length..];
            var separator = body.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0 || separator == body.Length - 1)
            {
                continue;
            }

            directives.Add(new ProfileDirective(
                ProfileAuthority.OrganizationConstraint,
                body[..separator].Trim().ToLowerInvariant(),
                body[(separator + 1)..].Trim(),
                $"Política da organização {organization.Name}: {policy.Description}"));
        }

        return directives;
    }

    private async Task<IReadOnlyList<ProfileDirective>> ExtractDecisionsAsync(
        string tenantId,
        string projectId,
        List<string> adrs,
        CancellationToken cancellationToken)
    {
        var directives = new List<ProfileDirective>();
        var page = await documents!.ListDocumentsAsync(tenantId, projectId, null, 100, cancellationToken);

        foreach (var document in page)
        {
            if (!DecisionKinds.Contains(document.Kind, StringComparer.OrdinalIgnoreCase) ||
                !ApprovedStates.Contains(document.State, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            adrs.Add(document.Title);

            // O TÍTULO do ADR é a decisão dita em uma linha — é o que a convenção MADR pede, e o
            // que sobrevive quando o corpo do documento está fora do alcance deste processo.
            // Quando a raiz do repositório é conhecida, o corpo entra também.
            var body = await ReadBodyAsync(tenantId, document.Id, cancellationToken);
            directives.AddRange(ProfileDirectiveParser.Parse(
                body is null ? document.Title : $"{document.Title}\n{body}",
                ProfileAuthority.ApprovedDecision,
                document.Id,
                "adr",
                document.Title));
        }

        return directives;
    }

    /// <summary>
    /// Corpo da versão mais recente, lido do catálogo em disco quando a raiz é conhecida. Ausência
    /// não é erro: o título do ADR continua sendo fonte de decisão.
    /// </summary>
    private async Task<string?> ReadBodyAsync(
        string tenantId, string documentId, CancellationToken cancellationToken)
    {
        if (repositoryRoot is null)
        {
            return null;
        }

        var versions = await documents!.ListVersionsAsync(tenantId, documentId, null, 20, cancellationToken);
        var latest = versions.OrderByDescending(version => version.Version).FirstOrDefault();
        if (latest is null || string.IsNullOrWhiteSpace(latest.CatalogPath))
        {
            return null;
        }

        var path = TrustedProcessRunner.ResolveConfined(
            repositoryRoot, Path.GetDirectoryName(latest.CatalogPath)?.Replace('\\', '/'));
        if (path is null)
        {
            return null;
        }

        var file = Path.Combine(path, Path.GetFileName(latest.CatalogPath));
        try
        {
            return File.Exists(file) ? File.ReadAllText(file) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Duas diretivas idênticas (mesma área, autoridade e valor) vindas de fontes diferentes não
    /// são conflito — são a mesma decisão dita duas vezes. Deduplicar aqui evita que a repetição
    /// vire falso conflito e trave o perfil.
    /// </summary>
    private static IReadOnlyList<ProfileDirective> Deduplicate(IEnumerable<ProfileDirective> directives) =>
    [
        .. directives
            .GroupBy(directive => (
                directive.Area.ToLowerInvariant(),
                directive.Authority,
                directive.Value.ToLowerInvariant()))
            .Select(group => group.First()),
    ];

    private static void LogExtracted(ILogger? logger, string projectId, int directives, int adrs)
    {
        if (logger is not null)
        {
            Extracted(logger, projectId, directives, adrs, null);
        }
    }

    private static readonly Action<ILogger, string, int, int, Exception?> Extracted =
        LoggerMessage.Define<string, int, int>(
            LogLevel.Information,
            new EventId(1, nameof(Extracted)),
            "Perfil do projeto {ProjectId}: {Directives} diretiva(s) extraída(s) de fontes " +
            "canônicas e {Adrs} ADR(s) aprovado(s).");
}
