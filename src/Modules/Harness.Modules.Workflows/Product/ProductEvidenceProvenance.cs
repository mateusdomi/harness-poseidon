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
/// QUEM controla o que a verificação de fato executa.
///
/// A distinção que a Fase 5 existe para introduzir: um script `test:e2e` cujo conteúdo é
/// <c>node -e "process.exit(0)"</c> roda sob o comando do Poseidon e devolve exit zero — mas o que
/// ele executa foi escrito por quem está sendo avaliado. Nome de script confiável não torna o
/// conteúdo do script uma verificação confiável.
/// </summary>
public enum VerificationTrustLevel
{
    /// <summary>O ator determinou o que rodaria. Não prova nada por si.</summary>
    ActorControlled,

    /// <summary>
    /// O Poseidon escolheu o comando; o PRODUTO define o que ele faz (script do manifesto). Vale
    /// como sinal suplementar, não como prova de requisito crítico.
    /// </summary>
    ProjectControlled,

    /// <summary>
    /// O Poseidon escolheu o comando E o que ele verifica: compilar, subir a aplicação, buscar o
    /// contrato, dirigir o navegador. É o único nível que libera requisito crítico.
    /// </summary>
    PoseidonControlled,
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

    string? CardId = null,

    /// <summary>
    /// Quem controlou o CONTEÚDO da verificação. Default conservador: o que não se declara vale
    /// como controlado pelo produto, e portanto não libera requisito crítico sozinho.
    /// </summary>
    VerificationTrustLevel Trust = VerificationTrustLevel.ProjectControlled)
{
    /// <summary>Proveniência de um fato constatado por inspeção do repositório entregue.</summary>
    public static ProductEvidenceProvenanceRecord FromRepository(
        string commitSha, DateTimeOffset observedAt, string? artifact = null) =>
        new(ProductEvidenceProvenance.Observed, "repository-scanner", commitSha, observedAt,
            Artifact: artifact, Trust: VerificationTrustLevel.PoseidonControlled);

    /// <summary>Proveniência de uma verificação executada pelo Poseidon.</summary>
    public static ProductEvidenceProvenanceRecord FromVerifier(
        string source,
        string commitSha,
        DateTimeOffset observedAt,
        string command,
        int exitCode,
        string? executionId = null,
        string? artifact = null,
        VerificationTrustLevel trust = VerificationTrustLevel.PoseidonControlled) =>
        new(ProductEvidenceProvenance.Verified, source, commitSha, observedAt, command, exitCode,
            artifact, null, executionId, null, trust);

    /// <summary>Proveniência de uma afirmação do ator. Não satisfaz requisito técnico.</summary>
    public static ProductEvidenceProvenanceRecord FromActor(string accountAlias, string? cardId = null) =>
        new(ProductEvidenceProvenance.Declared, $"actor:{accountAlias}", CardId: cardId,
            Trust: VerificationTrustLevel.ActorControlled);
}
