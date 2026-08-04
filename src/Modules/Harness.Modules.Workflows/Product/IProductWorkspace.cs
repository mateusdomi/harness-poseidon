namespace Harness.Modules.Workflows.Product;

/// <summary>
/// A árvore entregue, como o Poseidon a enxerga para constatar fatos.
///
/// É uma abstração e não `System.IO` direto por dois motivos: o inspetor precisa ser testável sem
/// tocar em disco, e a constatação precisa estar amarrada a um <see cref="CommitSha"/> — evidência
/// sem estado verificado é evidência que não sabe sobre o que fala.
/// </summary>
public interface IProductWorkspace
{
    /// <summary>Commit exato da árvore inspecionada.</summary>
    string CommitSha { get; }

    bool FileExists(string relativePath);

    bool DirectoryExists(string relativePath);

    /// <summary>Arquivos que casam o padrão, recursivamente, em caminhos relativos com `/`.</summary>
    IReadOnlyList<string> Find(string pattern, int limit = 200);

    /// <summary>Conteúdo do arquivo, ou <see langword="null"/> quando ausente ou ilegível.</summary>
    string? ReadText(string relativePath);
}
