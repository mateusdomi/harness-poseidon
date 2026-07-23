using Harness.Host.Agents;

namespace Harness.UnitTests.Agents;

/// <summary>
/// PLAT-06: prova de deriva (drift) do espelho documental. Regenera em memória, a partir da fonte
/// única (<see cref="CanonicalAgentDefinitions"/>), e afirma que os ficheiros comprometidos em
/// <c>docs/agents/*.yaml</c> batem byte a byte com a fonte canônica — como o drift test do OpenApi
/// compara o spec comprometido com o vivo. Se falhar, correr <c>tools/backend/generate-agent-docs.sh</c>.
/// </summary>
public sealed class CanonicalAgentDocsDriftTests
{
    [Fact]
    public void CommittedDocsMatchCanonicalSourceExactly()
    {
        var directory = Path.Combine(FindRepositoryRoot(), "docs", "agents");
        foreach (var (fileName, expected) in CanonicalAgentDocs.RenderAll())
        {
            var path = Path.Combine(directory, fileName);
            Assert.True(File.Exists(path), $"Missing committed mirror docs/agents/{fileName}. Run tools/backend/generate-agent-docs.sh.");
            var actual = File.ReadAllText(path).Replace("\r\n", "\n");
            Assert.True(
                actual == expected,
                $"docs/agents/{fileName} drifts from CanonicalAgentDefinitions. Run tools/backend/generate-agent-docs.sh.");
        }
    }

    [Fact]
    public void MirrorsExistForEveryCanonicalPersonaAndNothingElse()
    {
        var directory = Path.Combine(FindRepositoryRoot(), "docs", "agents");
        var expected = CanonicalAgentDefinitions.All
            .Select(seed => CanonicalAgentDocs.FileName(seed))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var actual = Directory.EnumerateFiles(directory, "*.yaml")
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void EveryMirrorCarriesTheDoNotHandEditHeader()
    {
        foreach (var (_, content) in CanonicalAgentDocs.RenderAll())
        {
            Assert.StartsWith(
                "# GENERATED from CanonicalAgentDefinitions — do not hand-edit; run the generator.",
                content,
                StringComparison.Ordinal);
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Harness.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
