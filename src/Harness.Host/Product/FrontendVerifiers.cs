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
        if (!DeclaresBuildScript(content))
        {
            return new ProductVerificationRecord(
                Kind, false, Name, "npm run build", -1, context.CommitSha, DateTimeOffset.UtcNow,
                context.AttemptId, directory,
                $"{directory}/package.json não declara script `build`: não há build para executar.");
        }

        // O gerenciador vem do LOCKFILE da entrega, não de preferência: instalar com um e buildar
        // com outro produz um build que não é o da entrega.
        var (manager, arguments) = ResolvePackageManager(context.WorkspaceRoot, directory);
        var result = await Runner.RunAsync(
            manager, arguments, context.WorkspaceRoot, directory,
            TimeSpan.FromMinutes(10), cancellationToken);
        return ToRecord(context, result);
    }

    /// <summary>O manifesto que declara o framework do perfil. Nenhum outro serve.</summary>
    private static (string Directory, string Content)? LocateManifest(ProductVerificationContext context)
    {
        var expected = context.Profile.Frontend.Framework;
        foreach (var relative in SafeFind(context.WorkspaceRoot, "package.json"))
        {
            var absolute = Path.Combine(
                context.WorkspaceRoot, relative.Replace('/', Path.DirectorySeparatorChar));
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

    private static bool DeclaresBuildScript(string manifest)
    {
        try
        {
            using var document = JsonDocument.Parse(manifest);
            return document.RootElement.TryGetProperty("scripts", out var scripts) &&
                scripts.ValueKind == JsonValueKind.Object &&
                scripts.TryGetProperty("build", out _);
        }
        catch (JsonException)
        {
            return false;
        }
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

    private static (string Manager, string[] Arguments) ResolvePackageManager(
        string workspaceRoot, string directory)
    {
        var folder = directory == "." ? workspaceRoot : Path.Combine(workspaceRoot, directory);
        if (File.Exists(Path.Combine(folder, "pnpm-lock.yaml")))
        {
            return ("pnpm", ["run", "build"]);
        }

        return File.Exists(Path.Combine(folder, "yarn.lock"))
            ? ("yarn", ["build"])
            : ("npm", ["run", "build", "--silent"]);
    }
}
