using System.Text.Json;
using Harness.Modules.Workflows.Product;

namespace Harness.Host.Product;

/// <summary>
/// Compila o frontend da entrega, no framework que o PERFIL decidiu.
///
/// O framework nunca é um literal aqui: um projeto que sobrescreveu React por Angular tem de ser
/// verificado como Angular. Procurar React nesse projeto reprovaria uma decisão legítima e
/// aprovada — e o gate estaria cobrando o baseline no lugar do perfil.
/// </summary>
public sealed class FrontendBuildVerifier(TrustedProcessRunner runner) : ProcessProductVerifier(runner)
{
    public override ProductEvidenceKind Kind => ProductEvidenceKind.FrontendBuild;

    public override string Name => "frontend-build";

    public override bool AppliesTo(ProjectEffectiveProfile profile) => profile.Frontend.Required;

    public override async Task<ProductVerificationRecord?> VerifyAsync(
        ProductVerificationContext context, CancellationToken cancellationToken)
    {
        var manifest = LocateManifest(context);
        if (manifest is null)
        {
            return new ProductVerificationRecord(
                Kind, false, Name, "npm run build", -1, context.CommitSha, DateTimeOffset.UtcNow,
                context.AttemptId, ".",
                $"Nenhum manifesto declarando {context.Profile.Frontend.Framework ?? "o framework do perfil"} " +
                "foi encontrado na entrega.");
        }

        var (directory, content) = manifest.Value;
        if (!FrontendLocator.DeclaresScript(content, "build"))
        {
            return new ProductVerificationRecord(
                Kind, false, Name, "npm run build", -1, context.CommitSha, DateTimeOffset.UtcNow,
                context.AttemptId, directory,
                $"{directory}/package.json não declara script `build`: não há build para executar.");
        }

        // O gerenciador vem do LOCKFILE da entrega, não de preferência: instalar com um e buildar
        // com outro produz um build que não é o da entrega.
        var (manager, arguments) = FrontendLocator.ResolvePackageManager(
            context.WorkspaceRoot, directory, "build");
        var result = await Runner.RunAsync(
            manager, arguments, context.WorkspaceRoot, directory,
            TimeSpan.FromMinutes(10), cancellationToken);
        return ToRecord(context, result);
    }

    private static (string Directory, string Content)? LocateManifest(ProductVerificationContext context) =>
        FrontendLocator.LocateManifest(context.WorkspaceRoot, context.Profile.Frontend.Framework);
}

/// <summary>
/// Onde está o frontend da entrega, no framework que o PERFIL decidiu.
///
/// Vive separado porque três verificações precisam da mesma resposta — compilar, servir para a
/// jornada e apontar a integração — e três buscas independentes acabariam divergindo justamente
/// no projeto que sobrescreveu o baseline, que é o caso em que errar custa caro.
/// </summary>
public static class FrontendLocator
{
    /// <summary>O manifesto que declara o framework do perfil. Nenhum outro serve.</summary>
    public static (string Directory, string Content)? LocateManifest(
        string workspaceRoot, string? expected)
    {
        foreach (var relative in Discover(workspaceRoot, "package.json"))
        {
            var absolute = Path.Combine(
                workspaceRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            string content;
            try
            {
                content = File.ReadAllText(absolute);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (expected is { Length: > 0 } && !DeclaresFramework(content, expected))
            {
                continue;
            }

            var directory = relative.Contains('/', StringComparison.Ordinal)
                ? relative[..relative.LastIndexOf('/')]
                : ".";
            return (directory, content);
        }

        return null;
    }

    /// <summary>Gerenciador de pacotes vindo do LOCKFILE da entrega, com o argumento do script pedido.</summary>
    public static (string Manager, string[] Arguments) ResolvePackageManager(
        string workspaceRoot, string directory, string script)
    {
        var folder = directory == "." ? workspaceRoot : Path.Combine(workspaceRoot, directory);
        if (File.Exists(Path.Combine(folder, "pnpm-lock.yaml")))
        {
            return ("pnpm", ["run", script]);
        }

        return File.Exists(Path.Combine(folder, "yarn.lock"))
            ? ("yarn", [script])
            : ("npm", ["run", script, "--silent"]);
    }

    /// <summary>O manifesto declara este script?</summary>
    public static bool DeclaresScript(string manifest, string script)
    {
        try
        {
            using var document = JsonDocument.Parse(manifest);
            return document.RootElement.TryGetProperty("scripts", out var scripts) &&
                scripts.ValueKind == JsonValueKind.Object &&
                scripts.TryGetProperty(script, out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static IReadOnlyList<string> Discover(string root, string pattern) =>
        FileDiscovery.Find(root, pattern);

    private static bool DeclaresFramework(string manifest, string framework)
    {
        var needle = framework.Trim().ToLowerInvariant() switch
        {
            "react" => "react",
            "angular" => "@angular/core",
            "vue" => "vue",
            "svelte" => "svelte",
            "next" or "next.js" or "nextjs" => "next",
            var other => other,
        };

        return Dependencies(manifest).Contains(needle, StringComparer.OrdinalIgnoreCase);
    }

    private static List<string> Dependencies(string manifest)
    {
        try
        {
            using var document = JsonDocument.Parse(manifest);
            var names = new List<string>();
            foreach (var section in (string[])["dependencies", "devDependencies", "peerDependencies"])
            {
                if (document.RootElement.TryGetProperty(section, out var element) &&
                    element.ValueKind == JsonValueKind.Object)
                {
                    names.AddRange(element.EnumerateObject().Select(property => property.Name));
                }
            }

            return names;
        }
        catch (JsonException)
        {
            return [];
        }
    }

}
