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
/// O que NÃO existe hoje como fonte persistida e fica registrado como lacuna: constraint de
/// organização/tenant e restrição regulatória. As duas autoridades continuam suportadas pelo
/// resolvedor; simplesmente não há de onde lê-las ainda.
/// </summary>
public sealed class ProfileDirectiveExtractor(
    IWorkBoardStore board,
    IDocumentCatalogStore? documents = null,
    string? repositoryRoot = null,
    ILogger<ProfileDirectiveExtractor>? logger = null)
{
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

        LogExtracted(logger, projectId, directives.Count, adrs.Count);
        return new Extraction(Deduplicate(directives), adrs, demandText.ToString());
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
