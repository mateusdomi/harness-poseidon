using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Harness.Modules.Workflows.Product;

/// <summary>
/// O trabalho corretivo que UMA lacuna de produto exige — derivado, nunca escrito por modelo.
/// </summary>
/// <param name="Fingerprint">
/// Identidade estável da lacuna. Deliberadamente NÃO inclui commit nem tentativa: a mesma falta de
/// interface na avaliação 3 e na 11 é o MESMO problema, e incluir o commit criaria um card novo a
/// cada rodada — que é a forma mais rápida de transformar um mecanismo de correção num gerador de
/// lixo.
/// </param>
/// <param name="Title">Título estável do card. É por ele que a idempotência é conferida.</param>
public sealed record ProductGapCorrection(
    string Fingerprint,
    string Title,
    string Instruction,
    ProductEvidenceKind Kind,
    ProductEvidenceGap Gap);

/// <summary>
/// Converte a reprovação do Definition of Done em TRABALHO, e não em mais um código de erro.
///
/// O run de 2026-08-04 mostrou o custo de não ter isto: o portão sabia exatamente que faltava a
/// interface, publicava `product:definition_of_done_failed`, e a fábrica parou. Foi preciso uma
/// pessoa ler o diagnóstico, entender que "não atende ao DoD" significava "não existe tela" e criar
/// os cards à mão. Enquanto essa tradução depender de alguém, a autonomia tem um buraco do tamanho
/// exato do produto.
///
/// Três decisões que este tipo existe para fixar:
///
/// 1. <b>O que se cria é derivado.</b> O título, o corpo e a expectativa de aceite saem da lacuna e
///    do perfil — nenhum modelo escolhe o que corrigir;
/// 2. <b>Idempotência por natureza, não por texto.</b> A impressão digital é do PAR tipo+lacuna, e
///    é a mesma em toda avaliação enquanto o problema existir;
/// 3. <b>Ausência de card é ausência de dono.</b> Card cancelado, escalado ou substituído não cobre
///    requisito nenhum — a lacuna continua produzindo correção até alguém a resolver de verdade.
/// </summary>
public static class ProductGapCorrections
{
    /// <summary>Prefixo do título dos cards corretivos. Fixo no código: é a chave de idempotência.</summary>
    public const string TitlePrefix = "CORRECAO/DoD";

    /// <summary>
    /// Uma correção para cada lacuna do veredito. Veredito satisfeito não produz nada — o mecanismo
    /// existe para fechar buraco, não para gerar trabalho.
    /// </summary>
    public static IReadOnlyList<ProductGapCorrection> From(
        ProductDeliveryVerdict verdict, ProjectEffectiveProfile profile)
    {
        ArgumentNullException.ThrowIfNull(verdict);
        ArgumentNullException.ThrowIfNull(profile);

        return verdict.Satisfied
            ? []
            : [.. verdict.Findings
                .GroupBy(finding => (finding.Kind, finding.Gap))
                .Select(group => Build(group.First(), profile))];
    }

    private static ProductGapCorrection Build(
        ProductEvidenceFinding finding, ProjectEffectiveProfile profile)
    {
        var fingerprint = Fingerprint(finding.Kind, finding.Gap);
        return new ProductGapCorrection(
            fingerprint,
            $"{TitlePrefix} {fingerprint} — {Summary(finding.Kind)}",
            Instruction(finding, profile),
            finding.Kind,
            finding.Gap);
    }

    /// <summary>
    /// Doze caracteres de SHA-256 sobre tipo+lacuna. Curto o bastante para caber num título legível
    /// e longo o bastante para não colidir num conjunto de treze tipos e seis naturezas.
    /// </summary>
    public static string Fingerprint(ProductEvidenceKind kind, ProductEvidenceGap gap) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{kind}:{gap}")))[..12];

    /// <summary>
    /// O corpo do card. Precisa responder, sem que ninguém traduza: o que falta, por que o perfil
    /// exige, o que o Poseidon já observou, e o que fará o card fechar.
    /// </summary>
    private static string Instruction(ProductEvidenceFinding finding, ProjectEffectiveProfile profile)
    {
        var builder = new StringBuilder();
        builder.Append("# Trabalho corretivo derivado do portão do produto\n\n");
        builder.Append(
            "Este card NÃO foi escrito por um agente: ele foi derivado da reprovação do Definition " +
            "of Done. Corrija a causa; não ajuste a verificação.\n\n");

        builder.Append("# A lacuna\n\n");
        builder.Append(CultureInfo.InvariantCulture, $"- **Evidência exigida:** {finding.Kind}\n");
        builder.Append(CultureInfo.InvariantCulture, $"- **Natureza da falta:** {finding.Gap} — {Explain(finding.Gap)}\n");
        builder.Append(CultureInfo.InvariantCulture, $"- **Diagnóstico do portão:** {finding.Reason}\n\n");

        builder.Append("# Por que o produto exige isto\n\n");
        builder.Append(CultureInfo.InvariantCulture,
            $"O perfil efetivo deste projeto resolveu a modalidade **{profile.Modality}**");
        if (profile.Frontend.Framework is { Length: > 0 } framework)
        {
            builder.Append(CultureInfo.InvariantCulture, $", com interface em {framework}");
        }

        if (profile.Backend.Runtime is { Length: > 0 } runtime)
        {
            builder.Append(CultureInfo.InvariantCulture, $" e servidor em {runtime}");
        }

        builder.Append(". ").Append(Why(finding.Kind)).Append("\n\n");

        builder.Append("# Critério de aceite\n\n");
        builder.Append(CultureInfo.InvariantCulture, $"- {Acceptance(finding.Kind)}\n");
        builder.Append(
            "- A evidência precisa ser produzida pela verificação do Poseidon, não declarada em " +
            "texto: afirmar que ficou pronto não fecha este card.\n");
        builder.Append(
            "- Este card só fecha quando a lacuna sumir do veredito do portão numa nova avaliação.\n");

        return builder.ToString();
    }

    private static string Explain(ProductEvidenceGap gap) => gap switch
    {
        ProductEvidenceGap.Missing => "nenhuma evidência foi produzida",
        ProductEvidenceGap.Failed => "a evidência foi produzida e reprovou",
        ProductEvidenceGap.Declared => "houve apenas afirmação do executor, sem verificação",
        ProductEvidenceGap.Stale => "a prova é de outro commit",
        ProductEvidenceGap.Untrusted => "quem definiu o conteúdo da verificação foi o próprio produto",
        _ => "natureza não classificada",
    };

    private static string Summary(ProductEvidenceKind kind) => kind switch
    {
        ProductEvidenceKind.BackendPresent => "o produto não tem servidor",
        ProductEvidenceKind.BackendBuild => "o servidor não compila",
        ProductEvidenceKind.FrontendPresent => "o produto não tem interface",
        ProductEvidenceKind.FrontendBuild => "a interface não compila",
        ProductEvidenceKind.FrontendBackendIntegration => "a interface não conversa com a API",
        ProductEvidenceKind.ApiPresent => "o produto não expõe API",
        ProductEvidenceKind.OpenApiGenerated => "o contrato OpenAPI não é publicado",
        ProductEvidenceKind.DatabaseMigrationValidated => "o schema não é reproduzível",
        ProductEvidenceKind.PersistenceVerified => "o dado não sobrevive",
        ProductEvidenceKind.AutomatedTestsPassed => "a suíte não passa",
        ProductEvidenceKind.E2EJourneyPassed => "a jornada não foi exercitada",
        ProductEvidenceKind.RunbookPresent => "o produto não tem instruções de operação",
        ProductEvidenceKind.SecurityScanPassed => "a varredura de segurança reprovou",
        _ => kind.ToString(),
    };

    private static string Why(ProductEvidenceKind kind) => kind switch
    {
        ProductEvidenceKind.FrontendPresent or ProductEvidenceKind.FrontendBuild =>
            "Quem pediu este software vai OPERÁ-LO; sem interface a entrega é peça, não produto.",
        ProductEvidenceKind.FrontendBackendIntegration =>
            "Uma tela que nunca chama o servidor mostra dado embutido, e dado embutido não é o produto.",
        ProductEvidenceKind.E2EJourneyPassed =>
            "Produto operado por pessoa precisa ter a jornada principal percorrida de ponta a ponta.",
        ProductEvidenceKind.PersistenceVerified =>
            "Ter migration prova estrutura; nada nela prova que o dado sobrevive a um reinício.",
        ProductEvidenceKind.OpenApiGenerated =>
            "O contrato é o que permite alguém consumir a API sem ler o código.",
        ProductEvidenceKind.SecurityScanPassed =>
            "Segredo em código e dependência vulnerável reprovam qualquer entrega, em qualquer modalidade.",
        _ => "O perfil efetivo exige esta evidência para que a entrega seja considerada completa.",
    };

    private static string Acceptance(ProductEvidenceKind kind) => kind switch
    {
        ProductEvidenceKind.FrontendPresent =>
            "Existe projeto de interface na entrega, no framework que o perfil decidiu.",
        ProductEvidenceKind.FrontendBuild => "O build da interface roda e sai com zero.",
        ProductEvidenceKind.FrontendBackendIntegration =>
            "Durante a jornada, chamadas da interface chegam à API — medido pelo Poseidon.",
        ProductEvidenceKind.E2EJourneyPassed =>
            "A jornada principal é percorrida no navegador, com navegação e asserções reais.",
        ProductEvidenceKind.PersistenceVerified =>
            "Um dado escrito pela API sobrevive ao reinício da aplicação e é lido de volta.",
        ProductEvidenceKind.OpenApiGenerated =>
            "A aplicação no ar publica um documento OpenAPI válido, com pelo menos uma operação.",
        ProductEvidenceKind.BackendBuild => "O build do servidor roda e sai com zero.",
        ProductEvidenceKind.AutomatedTestsPassed => "A suíte da entrega executa e passa.",
        ProductEvidenceKind.SecurityScanPassed =>
            "Nenhum segredo em código e nenhuma dependência com vulnerabilidade conhecida.",
        ProductEvidenceKind.RunbookPresent => "Existe instrução de execução legível por quem não escreveu.",
        _ => "A evidência exigida passa a ser produzida e aprovada pelo portão.",
    };
}
