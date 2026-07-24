using System.Text;

namespace Harness.Host.GovernanceDocs;

/// <summary>
/// Leitura/escrita/exclusão dos arquivos de governança e documentação que
/// governam o comportamento do sistema (o backend lê a governança do disco,
/// portanto edições aqui passam a valer para o Chefe/runtime).
///
/// SEGURANÇA: toda operação é restrita estritamente às raízes do allowlist
/// (<c>governance/</c> e <c>docs/</c>) relativas à raiz do repositório. Path
/// traversal (<c>..</c>), paths absolutos, separadores de drive, segmentos
/// vazios e symlinks que escapam do allowlist são bloqueados. Nunca expõe nem
/// escreve fora do allowlist.
/// </summary>
public sealed class GovernanceDocsService
{
    /// <summary>Raízes permitidas (relativas à raiz do repositório).</summary>
    public static readonly IReadOnlyList<string> AllowedRoots = ["governance", "docs"];

    /// <summary>Extensões de texto editáveis. Binários (png, etc.) ficam de fora.</summary>
    private static readonly HashSet<string> EditableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".markdown", ".yaml", ".yml", ".json", ".csv", ".txt", ".tmpl",
    };

    private const long MaxFileBytes = 8L * 1024 * 1024;

    private readonly string _root;

    public GovernanceDocsService(string repositoryRoot)
    {
        if (string.IsNullOrWhiteSpace(repositoryRoot))
            throw new ArgumentException("Repository root is required.", nameof(repositoryRoot));
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));
    }

    /// <summary>Lista, em uma árvore plana, os arquivos de texto sob o allowlist.</summary>
    public IReadOnlyList<GovernanceDocFile> Tree()
    {
        var files = new List<GovernanceDocFile>();
        foreach (var root in AllowedRoots)
        {
            var rootDir = Path.Combine(_root, root);
            if (!Directory.Exists(rootDir)) continue;
            foreach (var path in Directory.EnumerateFiles(rootDir, "*", SearchOption.AllDirectories))
            {
                if (!EditableExtensions.Contains(Path.GetExtension(path))) continue;
                var info = new FileInfo(path);
                // Não lista symlinks que apontam para fora do allowlist.
                if (info.LinkTarget is not null && !IsWithinAllowed(ResolveFinalTarget(info))) continue;
                var relative = Path.GetRelativePath(_root, path).Replace(Path.DirectorySeparatorChar, '/');
                files.Add(new GovernanceDocFile(relative, info.Name, info.Length, info.LastWriteTimeUtc));
            }
        }

        files.Sort(static (a, b) => string.CompareOrdinal(a.Path, b.Path));
        return files;
    }

    public async Task<GovernanceDocContent> ReadAsync(string relativePath, CancellationToken cancellationToken)
    {
        var full = Resolve(relativePath, requireEditableExtension: true);
        if (!File.Exists(full)) throw new GovernanceDocNotFoundException(relativePath);
        var info = new FileInfo(full);
        if (info.Length > MaxFileBytes)
            throw new GovernanceDocPathException("The file exceeds the maximum editable size.");
        var content = await File.ReadAllTextAsync(full, new UTF8Encoding(false), cancellationToken);
        return new GovernanceDocContent(
            Path.GetRelativePath(_root, full).Replace(Path.DirectorySeparatorChar, '/'),
            info.Name, content, info.Length, info.LastWriteTimeUtc);
    }

    public async Task<GovernanceDocContent> WriteAsync(
        string relativePath, string content, CancellationToken cancellationToken)
    {
        var full = Resolve(relativePath, requireEditableExtension: true);
        content ??= string.Empty;
        if (Encoding.UTF8.GetByteCount(content) > MaxFileBytes)
            throw new GovernanceDocPathException("The content exceeds the maximum editable size.");
        var directory = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(directory);
        // Escrita atômica: grava em arquivo temporário e move por cima.
        var temporary = full + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(false), cancellationToken);
            File.Move(temporary, full, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }

        var info = new FileInfo(full);
        return new GovernanceDocContent(
            Path.GetRelativePath(_root, full).Replace(Path.DirectorySeparatorChar, '/'),
            info.Name, content, info.Length, info.LastWriteTimeUtc);
    }

    public void Delete(string relativePath)
    {
        var full = Resolve(relativePath, requireEditableExtension: true);
        if (!File.Exists(full)) throw new GovernanceDocNotFoundException(relativePath);
        File.Delete(full);
    }

    /// <summary>
    /// Valida e resolve um path relativo para um caminho absoluto seguro dentro
    /// do allowlist. Lança <see cref="GovernanceDocPathException"/> em qualquer
    /// tentativa de escapar (traversal, absoluto, fora do allowlist, symlink).
    /// </summary>
    internal string Resolve(string relativePath, bool requireEditableExtension)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new GovernanceDocPathException("Path is required.");

        var normalized = relativePath.Replace('\\', '/').Trim();
        if (Path.IsPathRooted(normalized) || normalized.StartsWith('/'))
            throw new GovernanceDocPathException("Path must be relative.");

        var segments = normalized.Split('/');
        foreach (var segment in segments)
        {
            if (segment.Length == 0)
                throw new GovernanceDocPathException("Path must not contain empty segments.");
            if (segment is "." or "..")
                throw new GovernanceDocPathException("Path traversal is not allowed.");
            if (segment.IndexOfAny(InvalidSegmentChars) >= 0)
                throw new GovernanceDocPathException("Path contains invalid characters.");
        }

        if (!AllowedRoots.Contains(segments[0], StringComparer.Ordinal))
            throw new GovernanceDocPathException(
                $"Path must live under an allowed root: {string.Join(", ", AllowedRoots)}.");

        if (requireEditableExtension && !EditableExtensions.Contains(Path.GetExtension(normalized)))
            throw new GovernanceDocPathException("Only text/governance document files can be edited.");

        var full = Path.GetFullPath(Path.Combine(_root, Path.Combine(segments)));
        if (!IsWithinAllowed(full))
            throw new GovernanceDocPathException("Path escapes the allowlist.");

        EnsureNoSymlinkEscape(full);
        return full;
    }

    private static readonly char[] InvalidSegmentChars = BuildInvalidSegmentChars();

    private static char[] BuildInvalidSegmentChars()
    {
        // Rejeita separadores, drive-colon e caracteres de controle, de forma
        // determinística e independente de plataforma (não confia apenas em
        // Path.GetInvalidFileNameChars, que varia entre SOs).
        var chars = new HashSet<char> { '\0', '/', '\\', ':', '*', '?', '"', '<', '>', '|' };
        for (var c = (char)0; c < ' '; c++) chars.Add(c);
        return [.. chars];
    }

    private bool IsWithinAllowed(string fullPath)
    {
        foreach (var root in AllowedRoots)
        {
            var allowedRoot = Path.GetFullPath(Path.Combine(_root, root));
            if (string.Equals(fullPath, allowedRoot, StringComparison.Ordinal)) return true;
            var prefix = allowedRoot.EndsWith(Path.DirectorySeparatorChar)
                ? allowedRoot
                : allowedRoot + Path.DirectorySeparatorChar;
            if (fullPath.StartsWith(prefix, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    /// <summary>
    /// Percorre o alvo e cada ancestral existente dentro do allowlist; se algum
    /// for um symlink cujo alvo final escapa do allowlist, bloqueia.
    /// </summary>
    private void EnsureNoSymlinkEscape(string fullPath)
    {
        var current = fullPath;
        while (!string.IsNullOrEmpty(current) &&
               current.Length > _root.Length &&
               !string.Equals(current, _root, StringComparison.Ordinal))
        {
            FileSystemInfo? info = File.Exists(current)
                ? new FileInfo(current)
                : Directory.Exists(current) ? new DirectoryInfo(current) : null;
            if (info?.LinkTarget is not null && !IsWithinAllowed(ResolveFinalTarget(info)))
                throw new GovernanceDocPathException("Path resolves outside the allowlist via a symlink.");

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.Ordinal))
                break;
            current = parent;
        }
    }

    private static string ResolveFinalTarget(FileSystemInfo info)
    {
        var resolved = info.ResolveLinkTarget(returnFinalTarget: true);
        return Path.GetFullPath(resolved?.FullName ?? info.FullName);
    }
}

public sealed record GovernanceDocFile(string Path, string Name, long Size, DateTimeOffset ModifiedAt);

public sealed record GovernanceDocContent(
    string Path, string Name, string Content, long Size, DateTimeOffset ModifiedAt);

/// <summary>Path inválido ou tentativa de escapar do allowlist (mapeia para 400).</summary>
public sealed class GovernanceDocPathException(string message) : Exception(message);

/// <summary>Arquivo não encontrado dentro do allowlist (mapeia para 404).</summary>
public sealed class GovernanceDocNotFoundException(string path)
    : Exception($"Governance document not found: {path}.");
