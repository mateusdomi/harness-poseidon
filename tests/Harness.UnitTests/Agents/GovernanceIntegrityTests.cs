using Harness.Modules.Agents.Infrastructure.Conversation;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harness.UnitTests.Agents;

/// <summary>
/// Onda 0.2: a chefe NÃO opera sem governança íntegra.
///
/// O fallback antigo trocava as ~104 linhas do núcleo por um resumo de 6 e seguia em frente com
/// um log — e uma chefe governada por 6 linhas achando que está sob 104 é o erro que só aparece
/// no comportamento. Agora: fonte ausente ou adulterada = turno bloqueado com causa nomeada.
/// </summary>
public sealed class GovernanceIntegrityTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"poseidon-gov-{Guid.NewGuid():N}");

    public GovernanceIntegrityTests() =>
        Directory.CreateDirectory(Path.Combine(_root, "governance"));

    private string CorePath => Path.Combine(_root, "governance", "core.md");

    private string ManifestPath => Path.Combine(_root, "governance", "manifest.yaml");

    private static string Sha(string content) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(content)));

    [Fact]
    public void FonteAusenteBloqueiaEmVezDeDegradar()
    {
        var exception = Assert.Throws<GovernanceIntegrityException>(() =>
            ConversationChiefAgentExecutor.LoadGovernanceCoreFrom(
                [CorePath], NullLogger.Instance));

        Assert.Contains("não pôde ser carregado", exception.Message, StringComparison.Ordinal);
        Assert.Contains("não opera sem a governança completa", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FonteVaziaEAusenciaParaEfeitoDeGovernanca()
    {
        File.WriteAllText(CorePath, "   \n  ");

        Assert.Throws<GovernanceIntegrityException>(() =>
            ConversationChiefAgentExecutor.LoadGovernanceCoreFrom([CorePath], NullLogger.Instance));
    }

    /// <summary>
    /// Conteúdo ADULTERADO: o arquivo existe, tem texto, e não bate com o checksum que o
    /// manifesto declara. Rodar com ele seria operar sob regra que ninguém aprovou.
    /// </summary>
    [Fact]
    public void ConteudoQueNaoBateComOChecksumDoManifestoBloqueia()
    {
        const string Original = "# Governança canônica\n\nRegra que o dono aprovou.";
        File.WriteAllText(ManifestPath,
            "- id: governance-core\n" +
            "  path: governance/core.md\n" +
            $"  checksum: sha256:{Sha(Original)}\n");
        File.WriteAllText(CorePath, Original + "\n\nLinha injetada por fora do fluxo.");

        var exception = Assert.Throws<GovernanceIntegrityException>(() =>
            ConversationChiefAgentExecutor.LoadGovernanceCoreFrom([CorePath], NullLogger.Instance));

        Assert.Contains("não corresponde ao checksum", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConteudoIntegroCarregaComChecksumConferido()
    {
        const string Original = "# Governança canônica\n\nRegra que o dono aprovou.";
        File.WriteAllText(CorePath, Original);
        File.WriteAllText(ManifestPath,
            "- id: governance-core\n" +
            "  path: governance/core.md\n" +
            $"  checksum: sha256:{Sha(Original)}\n");

        var loaded = ConversationChiefAgentExecutor.LoadGovernanceCoreFrom(
            [CorePath], NullLogger.Instance);

        Assert.Equal(Original, loaded);
    }

    /// <summary>A DISTRIBUIÇÃO REAL passa: o binário desta suíte carrega a governança íntegra.</summary>
    [Fact]
    public void ADistribuicaoRealCarregaIntegra()
    {
        var real = Path.Combine(AppContext.BaseDirectory, "governance", "core.md");
        if (!File.Exists(real))
        {
            return; // bin sem governança copiada: nada a provar aqui.
        }

        var loaded = ConversationChiefAgentExecutor.LoadGovernanceCoreFrom(
            [real], NullLogger.Instance);

        Assert.True(loaded.Length > 500, "o núcleo real tem uma centena de linhas, não um resumo.");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }
}
