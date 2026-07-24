using Harness.Host.GovernanceDocs;

namespace Harness.UnitTests.GovernanceDocs;

public sealed class GovernanceDocsServiceTests : IDisposable
{
    private readonly string _root;
    private readonly GovernanceDocsService _service;

    public GovernanceDocsServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"gov-docs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_root, "governance", "rules"));
        Directory.CreateDirectory(Path.Combine(_root, "docs", "backend"));
        Directory.CreateDirectory(Path.Combine(_root, "secret"));
        File.WriteAllText(Path.Combine(_root, "governance", "core.md"), "# Core\n");
        File.WriteAllText(Path.Combine(_root, "governance", "manifest.yaml"), "version: 1\n");
        File.WriteAllText(Path.Combine(_root, "governance", "rules", "a.md"), "rule a\n");
        File.WriteAllText(Path.Combine(_root, "docs", "INDEX.md"), "# Index\n");
        File.WriteAllText(Path.Combine(_root, "docs", "logo.png"), "binary");
        File.WriteAllText(Path.Combine(_root, "secret", "keys.md"), "top secret\n");
        _service = new GovernanceDocsService(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void TreeListsOnlyEditableFilesUnderAllowlist()
    {
        var files = _service.Tree();
        var paths = files.Select(file => file.Path).ToArray();

        Assert.Contains("governance/core.md", paths);
        Assert.Contains("governance/manifest.yaml", paths);
        Assert.Contains("governance/rules/a.md", paths);
        Assert.Contains("docs/INDEX.md", paths);
        // Binário (png) fica de fora; nada fora do allowlist aparece.
        Assert.DoesNotContain("docs/logo.png", paths);
        Assert.DoesNotContain(paths, path => path.StartsWith("secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReadReturnsContentWithinAllowlist()
    {
        var content = await _service.ReadAsync("governance/core.md", CancellationToken.None);
        Assert.Equal("# Core\n", content.Content);
        Assert.Equal("core.md", content.Name);
        Assert.Equal("governance/core.md", content.Path);
    }

    [Fact]
    public async Task WriteThenReadRoundTrips()
    {
        await _service.WriteAsync("governance/core.md", "# Updated\n", CancellationToken.None);
        var content = await _service.ReadAsync("governance/core.md", CancellationToken.None);
        Assert.Equal("# Updated\n", content.Content);
        Assert.Equal("# Updated\n", File.ReadAllText(Path.Combine(_root, "governance", "core.md")));
    }

    [Fact]
    public async Task WriteCreatesNewFileUnderAllowlist()
    {
        await _service.WriteAsync("docs/new/note.md", "hello\n", CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(_root, "docs", "new", "note.md")));
    }

    [Fact]
    public void DeleteRemovesFileWithinAllowlist()
    {
        _service.Delete("governance/rules/a.md");
        Assert.False(File.Exists(Path.Combine(_root, "governance", "rules", "a.md")));
    }

    [Fact]
    public async Task DeleteMissingFileThrowsNotFound()
    {
        _ = await Assert.ThrowsAsync<GovernanceDocNotFoundException>(
            () => Task.Run(() => _service.Delete("governance/missing.md")));
    }

    // ---- Guardas de segurança: qualquer tentativa de escapar do allowlist ----

    [Theory]
    [InlineData("../secret/keys.md")]                      // traversal para irmão
    [InlineData("governance/../secret/keys.md")]           // traversal do meio
    [InlineData("governance/../../etc/passwd")]            // traversal profundo
    [InlineData("../../etc/passwd")]                        // traversal acima da raiz
    [InlineData("secret/keys.md")]                          // fora do allowlist
    [InlineData("README.md")]                               // raiz do repo (fora)
    [InlineData("governance//core.md")]                    // segmento vazio
    [InlineData("./governance/core.md")]                   // segmento "."
    [InlineData("governance/./core.md")]                   // "." no meio
    [InlineData("")]                                        // vazio
    [InlineData("   ")]                                     // em branco
    public async Task ReadRejectsPathsThatEscapeAllowlist(string path)
    {
        _ = await Assert.ThrowsAsync<GovernanceDocPathException>(
            () => _service.ReadAsync(path, CancellationToken.None));
    }

    [Theory]
    [InlineData("/etc/passwd")]                            // absoluto unix
    [InlineData("governance\\..\\secret\\keys.md")]        // traversal com backslash
    [InlineData("..\\secret\\keys.md")]
    public async Task ReadRejectsAbsoluteAndBackslashTraversal(string path)
    {
        _ = await Assert.ThrowsAsync<GovernanceDocPathException>(
            () => _service.ReadAsync(path, CancellationToken.None));
    }

    [Fact]
    public async Task WriteRejectsTraversalEscape()
    {
        _ = await Assert.ThrowsAsync<GovernanceDocPathException>(
            () => _service.WriteAsync("governance/../secret/pwned.md", "x", CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(_root, "secret", "pwned.md")));
    }

    [Fact]
    public void DeleteRejectsTraversalEscape()
    {
        Assert.Throws<GovernanceDocPathException>(() => _service.Delete("../secret/keys.md"));
        Assert.True(File.Exists(Path.Combine(_root, "secret", "keys.md")));
    }

    [Fact]
    public async Task NonEditableExtensionIsRejected()
    {
        _ = await Assert.ThrowsAsync<GovernanceDocPathException>(
            () => _service.ReadAsync("docs/logo.png", CancellationToken.None));
        _ = await Assert.ThrowsAsync<GovernanceDocPathException>(
            () => _service.WriteAsync("docs/evil.sh", "#!/bin/sh", CancellationToken.None));
    }

    [Fact]
    public async Task SymlinkEscapingAllowlistIsBlocked()
    {
        // Cria um symlink dentro do allowlist apontando para fora dele.
        var linkPath = Path.Combine(_root, "governance", "escape.md");
        var target = Path.Combine(_root, "secret", "keys.md");
        try
        {
            File.CreateSymbolicLink(linkPath, target);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return; // Ambiente sem permissão para symlink; guarda coberta pelos demais testes.
        }

        _ = await Assert.ThrowsAsync<GovernanceDocPathException>(
            () => _service.ReadAsync("governance/escape.md", CancellationToken.None));
        // E não é listado na árvore.
        Assert.DoesNotContain(_service.Tree(), file => file.Path == "governance/escape.md");
    }
}
