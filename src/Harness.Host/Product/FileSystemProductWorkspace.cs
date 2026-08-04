using Harness.Modules.Workflows.Product;

namespace Harness.Host.Product;

/// <summary>
/// A árvore entregue, lida do disco, dentro de uma raiz. Toda leitura é confinada à raiz e a
/// enumeração ignora as pastas que só produzem ruído e custo (`node_modules`, `bin`, `obj`, `.git`).
///
/// O <paramref name="commitSha"/> é obrigatório porque é ele que amarra a evidência ao estado
/// verificado: sem isso, uma constatação de ontem aprovaria a entrega de hoje.
/// </summary>
public sealed class FileSystemProductWorkspace(string root, string commitSha) : IProductWorkspace
{
    private static readonly string[] Ignored =
        [".git", "node_modules", "bin", "obj", "dist", ".next", ".vs", ".idea"];

    private readonly string _root = Path.GetFullPath(root);

    public string CommitSha { get; } = commitSha;

    public bool FileExists(string relativePath) =>
        Resolve(relativePath) is { } path && File.Exists(path);

    public bool DirectoryExists(string relativePath) =>
        Resolve(relativePath) is { } path && Directory.Exists(path);

    public IReadOnlyList<string> Find(string pattern, int limit = 200)
    {
        if (!Directory.Exists(_root))
        {
            return [];
        }

        var results = new List<string>(Math.Min(limit, 64));
        foreach (var file in EnumerateSafely(_root, pattern))
        {
            results.Add(Path.GetRelativePath(_root, file).Replace(Path.DirectorySeparatorChar, '/'));
            if (results.Count >= limit)
            {
                break;
            }
        }

        results.Sort(StringComparer.Ordinal);
        return results;
    }

    public string? ReadText(string relativePath)
    {
        var path = Resolve(relativePath);
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        try
        {
            // Arquivo gigante não vira contexto nem prova: cortar a leitura protege o inspetor de
            // um bundle minificado de dezenas de megabytes.
            var info = new FileInfo(path);
            return info.Length > 2 * 1024 * 1024 ? null : File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IEnumerable<string> EnumerateSafely(string directory, string pattern)
    {
        IEnumerable<string> files;
        IEnumerable<string> directories;
        try
        {
            files = Directory.EnumerateFiles(directory, pattern);
            directories = Directory.EnumerateDirectories(directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var file in files)
        {
            yield return file;
        }

        foreach (var child in directories)
        {
            var name = Path.GetFileName(child);
            if (Ignored.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var file in EnumerateSafely(child, pattern))
            {
                yield return file;
            }
        }
    }

    /// <summary>Caminho absoluto dentro da raiz, ou nulo quando o pedido tenta escapar dela.</summary>
    private string? Resolve(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            return null;
        }

        var candidate = Path.GetFullPath(Path.Combine(
            _root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        return candidate.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            string.Equals(candidate, _root, StringComparison.Ordinal)
            ? candidate
            : null;
    }
}
