using System.Security.Cryptography;
using System.Text;
using Harness.Persistence.Abstractions.Documents;

namespace Harness.Host.Documents;

public sealed class FileSystemDocumentContentCatalog(string rootPath) : IDocumentContentCatalog
{
    private readonly string _root = Path.GetFullPath(
        string.IsNullOrWhiteSpace(rootPath) ? throw new ArgumentException("Catalog root is required.", nameof(rootPath)) : rootPath);

    public async Task WriteAsync(string catalogPath, string body, string expectedHash,
        CancellationToken cancellationToken = default)
    {
        var target = Resolve(catalogPath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (File.Exists(target))
        {
            // A autoridade documental é idempotente; o blob precisa acompanhar. Um retry depois
            // de a transação gravar o documento não pode falhar só porque o mesmo conteúdo já foi
            // publicado. Conteúdo diferente no mesmo path continua sendo conflito real.
            VerifyHash(await File.ReadAllBytesAsync(target, cancellationToken), expectedHash);
            return;
        }
        var temporary = target + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, body, new UTF8Encoding(false), cancellationToken);
            VerifyHash(await File.ReadAllBytesAsync(temporary, cancellationToken), expectedHash);
            try
            {
                File.Move(temporary, target, overwrite: false);
            }
            catch (IOException) when (File.Exists(target))
            {
                // Duas projeções idempotentes podem vencer a corrida de existência. Só aceitamos
                // a vencedora se o hash for exatamente o esperado.
                VerifyHash(await File.ReadAllBytesAsync(target, cancellationToken), expectedHash);
            }
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public async Task<string> ReadAsync(string catalogPath, string expectedHash,
        CancellationToken cancellationToken = default)
    {
        var bytes = await File.ReadAllBytesAsync(Resolve(catalogPath), cancellationToken);
        VerifyHash(bytes, expectedHash);
        return Encoding.UTF8.GetString(bytes);
    }

    public string ResolveReadPath(string catalogPath, string expectedHash)
    {
        var path = Resolve(catalogPath);
        VerifyHash(File.ReadAllBytes(path), expectedHash);
        return path;
    }

    public Task DeleteAsync(string catalogPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = Resolve(catalogPath);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    private string Resolve(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathRooted(relativePath) || relativePath.Contains('\\'))
            throw new ArgumentException("Catalog path must be relative.", nameof(relativePath));
        var path = Path.GetFullPath(Path.Combine(_root, relativePath));
        var prefix = _root.EndsWith(Path.DirectorySeparatorChar) ? _root : _root + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
            throw new ArgumentException("Catalog path escapes its root.", nameof(relativePath));
        return path;
    }

    private static void VerifyHash(byte[] bytes, string expectedHash)
    {
        var actual = Convert.ToHexString(SHA256.HashData(bytes));
        if (!string.Equals(actual, expectedHash, StringComparison.Ordinal))
            throw new InvalidDataException("Document content hash does not match its catalog record.");
    }
}
