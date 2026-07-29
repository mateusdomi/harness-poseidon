namespace Harness.SharedKernel.CodeGraph;

/// <summary>Severidade de um diagnóstico de compilação.</summary>
public enum CodeGraphDiagnosticSeverity
{
    Info = 0,
    Warning = 1,

    /// <summary>Não compila. É o que reprova a camada (i) da verificação em camadas.</summary>
    Error = 2
}

/// <summary>
/// Diagnóstico emitido ao derivar o grafo. Existe porque indexar e compilar são a MESMA passada:
/// quem já montou o modelo semântico sabe de graça se o código compila, e esse fato é o insumo mais
/// barato da camada determinística do gate (F14) — apurá-lo aqui evita uma segunda compilação só
/// para responder a mesma pergunta.
/// </summary>
public sealed record CodeGraphDiagnostic(
    string Id,
    CodeGraphDiagnosticSeverity Severity,
    string Message,
    string? FilePath = null,
    int? Line = null);

/// <summary>
/// Até onde os diagnósticos de uma derivação valem como afirmação sobre o build.
///
/// Existe porque a derivação monta UMA compilação sintética sobre as fontes indexadas, e isso não é
/// o build real (não tem os pacotes, as fronteiras de projeto nem os símbolos de compilação de cada
/// csproj). Erro de SINTAXE nessa passada é definitivo — arquivo que não parseia não compila em
/// nenhum build. Erro SEMÂNTICO, não: pode ser apenas referência que a compilação sintética não
/// tinha. Dizer "o build está quebrado" com base no segundo seria inventar reprovação, e o gate
/// perderia a confiança que o torna útil.
/// </summary>
public enum CodeGraphDiagnosticScope
{
    /// <summary>Só o que é definitivo: o arquivo parseia ou não.</summary>
    SyntaxOnly = 0,

    /// <summary>Também os semânticos — exige compilação com todas as referências do build real.</summary>
    Semantic = 1
}

/// <summary>O que indexar. Caminhos são relativos à raiz do repositório do projeto.</summary>
public sealed record CodeGraphBuildRequest(
    string ProjectId,
    string RootPath,
    IReadOnlyList<string>? IncludedDirectories = null,
    IReadOnlyList<string>? ExcludedDirectorySegments = null);

/// <summary>
/// Resultado de uma derivação. <paramref name="FilesIndexed"/> e o
/// <see cref="CodeGraph.Digest"/> juntos respondem "o índice reconstruiu o mesmo grafo?" sem
/// precisar comparar nó por nó.
/// </summary>
public sealed record CodeGraphBuildResult(
    CodeGraph Graph,
    IReadOnlyList<CodeGraphDiagnostic> Diagnostics,
    int FilesIndexed,
    CodeGraphDiagnosticScope DiagnosticScope = CodeGraphDiagnosticScope.SyntaxOnly)
{
    public static CodeGraphBuildResult Empty { get; } = new(CodeGraph.Empty, [], 0);

    /// <summary>Erros de compilação encontrados na mesma passada da indexação.</summary>
    public IReadOnlyList<CodeGraphDiagnostic> Errors =>
        [.. Diagnostics.Where(d => d.Severity == CodeGraphDiagnosticSeverity.Error)];

    public bool Compiles => Errors.Count == 0;
}

/// <summary>
/// Índice de grafo de código por linguagem (B6).
///
/// A interface vive no SharedKernel, e não no módulo que a implementa, por uma razão estrutural: os
/// consumidores do grafo são políticas de <c>Modules.Coordination</c> (validação de plano, raio de
/// impacto), e módulo não pode referenciar módulo — só o SharedKernel. Assim o compilador entra em
/// UM projeto (o Host) e nenhum módulo de domínio herda essa dependência.
///
/// É a mesma fronteira que torna a segunda entrega desta fase segura: acrescentar o servidor TS para
/// o frontend é acrescentar OUTRA implementação desta interface, sem tocar em nenhum consumidor.
/// </summary>
public interface ICodeGraphIndex
{
    /// <summary>Identificador estável da linguagem indexada (ex.: <c>csharp</c>).</summary>
    string Language { get; }

    /// <summary>
    /// Deriva o grafo do zero a partir das fontes. Reconstruir é o único caminho de atualização —
    /// índice que aceita remendo incremental deixa de ser derivado e volta a ser declaração.
    /// </summary>
    Task<CodeGraphBuildResult> BuildAsync(
        CodeGraphBuildRequest request,
        CancellationToken cancellationToken = default);
}
