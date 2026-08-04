namespace Harness.Modules.Workflows.Product;

/// <summary>
/// De onde a evidência veio — e, portanto, quanto ela vale.
///
/// A distinção existe porque o produto inteiro depende dela: um agente que possa escrever
/// "FrontendBuildPassed = true" e com isso abrir o portão está aprovando o próprio trabalho por
/// meio de texto. A ordem do enum é a força crescente, e o gate compara por ela.
/// </summary>
public enum ProductEvidenceProvenance
{
    /// <summary>
    /// O ator AFIRMOU. Nada foi constatado. Nunca satisfaz requisito técnico — é o default
    /// justamente para que esquecer de declarar a origem reprove, em vez de passar.
    /// </summary>
    Declared,

    /// <summary>
    /// O Poseidon CONSTATOU um fato do estado entregue, sem executar nada: um manifesto existe, um
    /// projeto declara o framework do perfil, uma pasta de migrations tem arquivos. Prova
    /// existência e forma; não prova funcionamento.
    /// </summary>
    Observed,

    /// <summary>
    /// O Poseidon EXECUTOU uma verificação controlada e guardou o resultado reproduzível: comando,
    /// código de saída, artefato, instante e o commit sobre o qual rodou.
    /// </summary>
    Verified,
}

/// <summary>
/// A cadeia de custódia de uma evidência. Sem ela, "o teste passou" é uma frase; com ela é um fato
/// datado, atribuído a um verificador, amarrado a um commit e reproduzível.
/// </summary>
public sealed record ProductEvidenceProvenanceRecord(
    ProductEvidenceProvenance Level,

    /// <summary>Quem produziu: `repository-scanner`, `test-runner`, `actor:<alias>`, …</summary>
    string Source,

    /// <summary>Commit exatamente verificado. Evidência de outro commit não vale para este.</summary>
    string? CommitSha = null,

    DateTimeOffset? ObservedAt = null,

    /// <summary>Comando executado, quando houve execução controlada.</summary>
    string? Command = null,

    int? ExitCode = null,

    /// <summary>Caminho relativo do artefato inspecionado ou produzido.</summary>
    string? Artifact = null,

    /// <summary>Hash do artefato, quando ele precisa ser identificável.</summary>
    string? ArtifactHash = null,

    string? ExecutionId = null,

    string? CardId = null)
{
    /// <summary>Proveniência de um fato constatado por inspeção do repositório entregue.</summary>
    public static ProductEvidenceProvenanceRecord FromRepository(
        string commitSha, DateTimeOffset observedAt, string? artifact = null) =>
        new(ProductEvidenceProvenance.Observed, "repository-scanner", commitSha, observedAt, Artifact: artifact);

    /// <summary>Proveniência de uma verificação executada pelo Poseidon.</summary>
    public static ProductEvidenceProvenanceRecord FromVerifier(
        string source,
        string commitSha,
        DateTimeOffset observedAt,
        string command,
        int exitCode,
        string? executionId = null,
        string? artifact = null) =>
        new(ProductEvidenceProvenance.Verified, source, commitSha, observedAt, command, exitCode,
            artifact, null, executionId);

    /// <summary>Proveniência de uma afirmação do ator. Não satisfaz requisito técnico.</summary>
    public static ProductEvidenceProvenanceRecord FromActor(string accountAlias, string? cardId = null) =>
        new(ProductEvidenceProvenance.Declared, $"actor:{accountAlias}", CardId: cardId);
}
