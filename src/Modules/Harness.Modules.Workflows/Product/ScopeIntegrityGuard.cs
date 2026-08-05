namespace Harness.Modules.Workflows.Product;

/// <summary>Uma capacidade do produto que a decisão proposta faria desaparecer.</summary>
/// <param name="Kind">A evidência que deixaria de ser exigida — e portanto de ser cobrada.</param>
public sealed record CapabilityRemoval(ProductEvidenceKind Kind, string Meaning);

/// <summary>O que a análise de impacto concluiu sobre uma decisão que muda o escopo.</summary>
public sealed record ScopeIntegrityVerdict(
    bool Allowed,
    IReadOnlyList<CapabilityRemoval> Removals,
    string Reason)
{
    public const string ReasonCode = "product:scope_removal_requires_product_authority";

    public static ScopeIntegrityVerdict Permitted(string reason) => new(true, [], reason);
}

/// <summary>
/// A trava que impede uma decisão TÉCNICA de apagar uma capacidade do PRODUTO.
///
/// O caso que a originou tem data e arquivo. Em 2026-08-04T11:37, dentro do run de empréstimos, um
/// ADR chamado <i>"fronteira do piloto: entrega backend-only"</i> foi escrito, revisado por agente
/// distinto e aceito. Ele registrava um raciocínio bom — um agente criando o diretório de interface
/// do zero estaria decidindo arquitetura sozinho, sem ADR e sem revisão — e, com esse raciocínio
/// correto, removeu do produto exatamente a metade que o usuário ia usar. Ninguém confrontou a
/// decisão com o pedido: <i>"quero um sistema para controlar empréstimos"</i>, feito por uma pessoa
/// que ia operar o sistema, não integrá-lo.
///
/// A regra que este tipo impõe é estreita de propósito:
///
/// - <b>escolha técnica</b> — linguagem, framework, banco, estrutura de pastas — é do arquiteto, e
///   um ADR decide sozinho. Nada aqui atrapalha isso;
/// - <b>capacidade do produto</b> — existir interface, existir API, persistir dado — não é decisão
///   de arquitetura. Removê-la exige autoridade de PRODUTO: requisito explícito do usuário ou
///   restrição regulatória.
///
/// A análise é de <b>cobertura</b>, não de texto: compara o que o perfil atual exige com o que o
/// perfil proposto exigiria, e cada evidência que sai da lista é uma capacidade que ninguém mais vai
/// cobrar. Ler a prosa do ADR para adivinhar intenção seria frágil justamente no caso em que a prosa
/// é convincente — e, em 2026-08-04, ela era.
/// </summary>
public static class ScopeIntegrityGuard
{
    /// <summary>
    /// Quem pode remover capacidade de produto. ADR **não** está na lista, e é esse o ponto inteiro:
    /// arquitetura decide COMO o produto é construído, não SE ele existe.
    /// </summary>
    public static bool MayRemoveCapability(ProfileAuthority authority) =>
        authority is ProfileAuthority.ProjectRequirement or ProfileAuthority.Regulatory;

    /// <summary>
    /// Analisa o impacto da decisão sobre a cobertura exigida. Decisão que não remove capacidade
    /// nenhuma passa sem cerimônia — a trava existe para um caso específico e não para atrapalhar
    /// o trabalho normal de arquitetura.
    /// </summary>
    public static ScopeIntegrityVerdict Evaluate(
        ProjectEffectiveProfile current,
        ProjectEffectiveProfile proposed,
        ProfileAuthority authority,
        string sourceId)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(proposed);

        var antes = ProductDeliveryRequirements.For(current).ToHashSet();
        var depois = ProductDeliveryRequirements.For(proposed).ToHashSet();

        var removidas = antes
            .Where(kind => !depois.Contains(kind))
            .OrderBy(kind => kind)
            .Select(kind => new CapabilityRemoval(kind, Meaning(kind)))
            .ToArray();

        if (removidas.Length == 0)
        {
            return ScopeIntegrityVerdict.Permitted(
                "A decisão não remove nenhuma capacidade exigida do produto.");
        }

        if (MayRemoveCapability(authority))
        {
            return new ScopeIntegrityVerdict(
                true,
                removidas,
                $"A decisão remove {removidas.Length} capacidade(s) do produto e vem de " +
                $"{authority}, que tem autoridade para isso. A remoção fica registrada: " +
                string.Join("; ", removidas.Select(item => item.Meaning)));
        }

        return new ScopeIntegrityVerdict(
            false,
            removidas,
            $"BLOQUEADA: a decisão `{sourceId}` removeria {removidas.Length} capacidade(s) do " +
            $"produto — {string.Join("; ", removidas.Select(item => item.Meaning))} — e vem de " +
            $"{authority}. Decisão de arquitetura escolhe COMO o produto é construído; ela não " +
            "decide SE ele existe. Para tirar uma capacidade do escopo é preciso requisito " +
            "explícito de quem pediu o software, ou restrição regulatória. Se a capacidade está " +
            "difícil de construir, o caminho é decidir o que falta para construí-la — não cortar a " +
            "parte que dá sentido ao produto.");
    }

    private static string Meaning(ProductEvidenceKind kind) => kind switch
    {
        ProductEvidenceKind.FrontendPresent => "o produto deixaria de ter interface",
        ProductEvidenceKind.FrontendBuild => "a interface deixaria de ser compilada",
        ProductEvidenceKind.FrontendBackendIntegration =>
            "a conversa entre tela e API deixaria de ser cobrada",
        ProductEvidenceKind.E2EJourneyPassed => "a jornada da pessoa deixaria de ser exercitada",
        ProductEvidenceKind.ApiPresent => "o produto deixaria de expor API",
        ProductEvidenceKind.OpenApiGenerated => "o contrato deixaria de ser publicado",
        ProductEvidenceKind.PersistenceVerified => "o dado deixaria de precisar sobreviver",
        ProductEvidenceKind.DatabaseMigrationValidated => "o schema deixaria de ser cobrado",
        ProductEvidenceKind.BackendPresent => "o produto deixaria de ter servidor",
        ProductEvidenceKind.BackendBuild => "o servidor deixaria de ser compilado",
        ProductEvidenceKind.AutomatedTestsPassed => "a suíte deixaria de ser exigida",
        ProductEvidenceKind.SecurityScanPassed => "a varredura de segurança deixaria de ser exigida",
        ProductEvidenceKind.RunbookPresent => "as instruções de operação deixariam de ser exigidas",
        _ => $"{kind} deixaria de ser exigida",
    };
}
