namespace Harness.Persistence.Abstractions.Architecture;

/// <summary>Um nó do grafo como linha. <paramref name="Kind"/> é o valor do enum do domínio.</summary>
public sealed record CodeGraphNodeRow(
    string NodeId,
    string Symbol,
    int Kind,
    string FilePath,
    string Module);

public sealed record CodeGraphEdgeRow(string FromNodeId, string ToNodeId, int Kind);

/// <summary>
/// Cabeçalho de um índice armazenado. <paramref name="Digest"/> é a impressão digital do conteúdo do
/// grafo, e <paramref name="SourceRevision"/> é a revisão do código de onde ele foi derivado — os
/// dois juntos são o que permite dizer se o índice guardado ainda corresponde ao código de hoje.
/// Sem a revisão, um grafo velho é indistinguível de um grafo atual, e medir impacto contra código
/// que já mudou é pior que não medir: parece resposta.
/// </summary>
public sealed record CodeGraphSnapshot(
    string TenantId,
    string ProjectId,
    string Language,
    string Digest,
    int NodeCount,
    int EdgeCount,
    int FilesIndexed,
    string DiagnosticScope,
    int ErrorCount,
    string? SourceRevision,
    DateTimeOffset BuiltAt)
{
    public const string SyntaxOnlyScope = "syntax_only";
    public const string SemanticScope = "semantic";

    public bool Compiles => ErrorCount == 0;
}

public sealed record CodeGraphWrite(
    string TenantId,
    string ProjectId,
    string Language,
    string Digest,
    int FilesIndexed,
    string DiagnosticScope,
    int ErrorCount,
    string? SourceRevision,
    DateTimeOffset BuiltAt,
    IReadOnlyList<CodeGraphNodeRow> Nodes,
    IReadOnlyList<CodeGraphEdgeRow> Edges);

public sealed record CodeGraphContent(
    CodeGraphSnapshot Snapshot,
    IReadOnlyList<CodeGraphNodeRow> Nodes,
    IReadOnlyList<CodeGraphEdgeRow> Edges);

/// <summary>
/// Índice de grafo de código armazenado por projeto e linguagem (B6).
///
/// A operação de escrita é <see cref="ReplaceAsync"/>, e é a única — não existe "atualizar um nó".
/// Isso é intencional: o índice é DERIVADO, e um derivado que aceita remendo incremental deixa de
/// corresponder à fonte no primeiro remendo errado e volta a ser uma declaração que envelhece em
/// silêncio. Reconstruir é a forma de corrigir, e substituir inteiro em uma transação é o que garante
/// que ninguém leia meio grafo novo com meio grafo velho.
/// </summary>
public interface ICodeGraphStore
{
    /// <summary>Só o cabeçalho — o suficiente para comparar digest e revisão sem carregar o grafo.</summary>
    Task<CodeGraphSnapshot?> GetSnapshotAsync(
        string tenantId,
        string projectId,
        string language,
        CancellationToken cancellationToken = default);

    /// <summary>Substitui integralmente o índice do par projeto+linguagem, em uma transação.</summary>
    Task<CodeGraphSnapshot> ReplaceAsync(
        CodeGraphWrite write,
        CancellationToken cancellationToken = default);

    /// <summary>O grafo inteiro, ou nulo quando aquele projeto nunca foi indexado.</summary>
    Task<CodeGraphContent?> LoadAsync(
        string tenantId,
        string projectId,
        string language,
        CancellationToken cancellationToken = default);
}
