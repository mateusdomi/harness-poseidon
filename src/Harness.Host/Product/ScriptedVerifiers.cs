using System.Text.Json;
using Harness.Modules.Workflows.Product;

namespace Harness.Host.Product;

/// <summary>
/// Verifica pelas verificações que a PRÓPRIA ENTREGA declara, num conjunto fechado de nomes
/// convencionados.
///
/// A fronteira de confiança é a que importa aqui: o nome do script vem deste código
/// (<c>test:e2e</c>, <c>test:integration</c>, <c>openapi</c>, …), nunca de texto do agente. O que a
/// entrega define é o CONTEÚDO do script — e esse conteúdo é o mesmo código que o Poseidon já vai
/// compilar e testar. É exatamente o que uma CI faz: roda o script que o repositório declara, pelo
/// nome que a convenção fixou. O que continua proibido é o modelo dizer qual comando executar.
///
/// Uma entrega que não declara o script não produz evidência: a ausência reprova, e é o que impede
/// que "não tem E2E" vire "E2E passou".
/// </summary>
public sealed class ScriptedProductVerifier(
    TrustedProcessRunner runner,
    ProductEvidenceKind kind,
    string scriptName,
    Func<ProjectEffectiveProfile, bool> applies) : ProcessProductVerifier(runner)
{
    public override ProductEvidenceKind Kind { get; } = kind;

    public override string Name { get; } = $"npm-script:{scriptName}";

    public override bool AppliesTo(ProjectEffectiveProfile profile) => applies(profile);

    /// <summary>
    /// O PRODUTO define o que o script faz. O Poseidon escolhe o nome e o invoca, mas o conteúdo
    /// é de quem está sendo avaliado — e é por isso que este nível não libera requisito crítico.
    /// </summary>
    protected override VerificationTrustLevel Trust => VerificationTrustLevel.ProjectControlled;

    public override async Task<ProductVerificationRecord?> VerifyAsync(
        ProductVerificationContext context, CancellationToken cancellationToken)
    {
        var location = LocateScript(context.WorkspaceRoot, scriptName);
        if (location is null)
        {
            return new ProductVerificationRecord(
                Kind, false, Name, $"npm run {scriptName}", -1, context.CommitSha,
                DateTimeOffset.UtcNow, context.AttemptId, ".",
                $"Nenhum manifesto da entrega declara o script `{scriptName}`: não há o que executar, " +
                "e ausência de verificação não é aprovação.",
                Trust);
        }

        var result = await Runner.RunAsync(
            "npm", ["run", scriptName, "--silent"], context.WorkspaceRoot, location,
            TimeSpan.FromMinutes(10), cancellationToken);
        return ToRecord(context, result);
    }

    /// <summary>Diretório do manifesto que declara o script, ou nulo.</summary>
    private static string? LocateScript(string workspaceRoot, string script)
    {
        foreach (var relative in SafeFind(workspaceRoot, "package.json"))
        {
            var absolute = Path.Combine(
                workspaceRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(absolute));
                if (document.RootElement.TryGetProperty("scripts", out var scripts) &&
                    scripts.ValueKind == JsonValueKind.Object &&
                    scripts.TryGetProperty(script, out _))
                {
                    return relative.Contains('/', StringComparison.Ordinal)
                        ? relative[..relative.LastIndexOf('/')]
                        : ".";
                }
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
            {
                // Manifesto ilegível é manifesto ausente para efeito de descoberta.
            }
        }

        return null;
    }
}

/// <summary>
/// Os verificadores por script convencionado. O conjunto de nomes é fechado aqui: é a lista que
/// separa "a CI roda o que o repositório declara" de "o modelo escolhe o que o Poseidon executa".
/// </summary>
public static class ScriptedVerifierCatalog
{
    public static IReadOnlyList<IProductVerifier> Create(TrustedProcessRunner runner) =>
    [
        new ScriptedProductVerifier(
            runner, ProductEvidenceKind.OpenApiGenerated, "openapi",
            profile => profile.Api.OpenApiRequired),
        new ScriptedProductVerifier(
            runner, ProductEvidenceKind.PersistenceVerified, "test:persistence",
            profile => profile.Data.Required),
        new ScriptedProductVerifier(
            runner, ProductEvidenceKind.FrontendBackendIntegration, "test:integration",
            profile => profile.Frontend.Required && profile.Api.Required),
        new ScriptedProductVerifier(
            runner, ProductEvidenceKind.E2EJourneyPassed, "test:e2e",
            profile => profile.Frontend.Required),
        new ScriptedProductVerifier(
            runner, ProductEvidenceKind.SecurityScanPassed, "audit",
            _ => false),
    ];
}
