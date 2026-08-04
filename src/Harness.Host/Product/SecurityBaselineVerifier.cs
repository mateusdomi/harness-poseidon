using System.Globalization;
using System.Text;
using Harness.Modules.Coordination.Application;
using Harness.Modules.Workflows.Product;

namespace Harness.Host.Product;

/// <summary>
/// A verificação mínima de segurança da entrega: segredo em código e dependência com
/// vulnerabilidade conhecida.
///
/// Duas decisões, ambas do §15:
///
/// 1. <b>Proporcional.</b> Não é uma plataforma de SAST e não tenta ser. São duas perguntas
///    objetivas, com resposta binária e barata, sobre falhas que aparecem de verdade em código
///    recém-escrito.
/// 2. <b>Ausência de scanner NUNCA é aprovação.</b> Antes deste verificador,
///    <c>SecurityScanPassed</c> não tinha produtor nenhum — o requisito existia no enum e ninguém o
///    satisfazia. A varredura de segredo reusa exatamente os padrões canônicos do repositório
///    (<see cref="DeliverySecretScanGate"/>): um segundo conjunto de padrões seria uma segunda
///    verdade sobre o que é um segredo, e a mais frouxa venceria em silêncio.
/// </summary>
public sealed class SecurityBaselineVerifier(TrustedProcessRunner runner) : ProcessProductVerifier(runner)
{
    /// <summary>Teto do que entra na varredura. Uma entrega não cabe inteira em memória, e não precisa.</summary>
    private const int MaxScannedCharacters = 4_000_000;

    private static readonly string[] ScannedPatterns =
    [
        "*.cs", "*.ts", "*.tsx", "*.js", "*.jsx", "*.json", "*.yaml", "*.yml", "*.env", "*.config",
    ];

    public override ProductEvidenceKind Kind => ProductEvidenceKind.SecurityScanPassed;

    public override string Name => "security-baseline";

    /// <summary>Toda entrega. Segredo em código e dependência vulnerável não dependem de modalidade.</summary>
    public override bool AppliesTo(ProjectEffectiveProfile profile) => true;

    public override async Task<ProductVerificationRecord?> VerifyAsync(
        ProductVerificationContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var (scanned, secrets) = ScanSecrets(context.WorkspaceRoot);
        if (scanned == 0)
        {
            // Nada legível para varrer não é entrega limpa: é entrega que não deu para inspecionar.
            return Record(
                context, false,
                $"{VerificationOutcomeKind.NotSupported}: nenhum arquivo inspecionável na entrega, " +
                "então a varredura de segredo não chegou a acontecer.");
        }

        if (!secrets.IsClean)
        {
            return Record(
                context, false,
                $"Segredo em código na entrega: {secrets.Detail}. " +
                $"({scanned} arquivos varridos com os padrões canônicos do repositório.)");
        }

        var dependencies = await InspectDependenciesAsync(context, cancellationToken);

        return dependencies.Vulnerable
            ? Record(
                context, false,
                $"Dependência com vulnerabilidade conhecida: {dependencies.Detail}")
            : Record(
                context, true,
                $"Varredura de segredo limpa em {scanned.ToString(CultureInfo.InvariantCulture)} arquivos " +
                $"(padrões canônicos do repositório). Dependências: {dependencies.Detail}");
    }

    /// <summary>
    /// Monta um diff sintético das linhas da entrega e o submete ao gate canônico. É a forma de
    /// reusar os padrões sem duplicá-los: o formato que o gate consome é o diff, e uma entrega
    /// inteira é, para efeito de varredura, um diff em que tudo é linha adicionada.
    /// </summary>
    private static (int Files, SecretScanVerdict Verdict) ScanSecrets(string workspaceRoot)
    {
        var builder = new StringBuilder();
        var files = 0;

        foreach (var pattern in ScannedPatterns)
        {
            foreach (var relative in FileDiscovery.Find(workspaceRoot, pattern))
            {
                if (builder.Length >= MaxScannedCharacters)
                {
                    break;
                }

                var absolute = Path.Combine(
                    workspaceRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                string content;
                try
                {
                    content = File.ReadAllText(absolute);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                files++;
                builder.Append("+++ b/").Append(relative).Append('\n');
                foreach (var line in content.Split('\n'))
                {
                    builder.Append('+').Append(line).Append('\n');
                }
            }
        }

        return (files, DeliverySecretScanGate.Inspect(builder.ToString()));
    }

    /// <summary>
    /// Pergunta ao próprio ecossistema se alguma dependência tem vulnerabilidade conhecida. Quando
    /// a consulta não pode acontecer — a lista vive fora e o ambiente da verificação não tem rede —
    /// isso é dito como <c>NotSupported</c>, e não convertido em silêncio para "nenhuma
    /// vulnerabilidade".
    /// </summary>
    private async Task<(bool Vulnerable, string Detail)> InspectDependenciesAsync(
        ProductVerificationContext context, CancellationToken cancellationToken)
    {
        var (target, _) = ResolveDotNetSurface(context.WorkspaceRoot);
        if (target is null)
        {
            return (false, $"{VerificationOutcomeKind.NotSupported} (nenhuma superfície .NET para consultar)");
        }

        var result = await Runner.RunAsync(
            "dotnet", ["list", target, "package", "--vulnerable", "--include-transitive"],
            context.WorkspaceRoot, null, TimeSpan.FromMinutes(5), cancellationToken);

        if (result.Outcome != VerificationOutcomeKind.Passed)
        {
            return (false,
                $"{VerificationOutcomeKind.NotSupported} (a consulta de vulnerabilidades não pôde " +
                $"rodar: {result.CommandLine} → exit {result.ExitCode})");
        }

        var vulnerable = result.Output.Contains("has the following vulnerable packages", StringComparison.OrdinalIgnoreCase) ||
            result.Output.Contains("tem os seguintes pacotes vulneráveis", StringComparison.OrdinalIgnoreCase);

        return vulnerable
            ? (true, Trim(result.Output))
            : (false, "nenhuma vulnerabilidade conhecida nas dependências declaradas");
    }

    private static string Trim(string value) => value.Length <= 800 ? value : value[..800] + "…";

    private ProductVerificationRecord Record(
        ProductVerificationContext context, bool succeeded, string detail) =>
        new(Kind, succeeded, Name, "security-baseline", succeeded ? 0 : -1, context.CommitSha,
            DateTimeOffset.UtcNow, context.AttemptId, ".", detail,
            VerificationTrustLevel.PoseidonControlled);
}
