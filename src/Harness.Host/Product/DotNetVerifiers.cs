using Harness.Modules.Workflows.Product;

namespace Harness.Host.Product;

/// <summary>
/// Base dos verificadores que rodam um processo confiável e traduzem o desfecho em evidência.
/// A tradução fica num lugar só para que nenhuma implementação invente a própria semântica de
/// "passou" — em particular, timeout e erro de infraestrutura NUNCA viram sucesso.
/// </summary>
public abstract class ProcessProductVerifier(TrustedProcessRunner runner) : IProductVerifier
{
    protected TrustedProcessRunner Runner { get; } = runner;

    public abstract ProductEvidenceKind Kind { get; }

    public abstract string Name { get; }

    public abstract bool AppliesTo(ProjectEffectiveProfile profile);

    public abstract Task<ProductVerificationRecord?> VerifyAsync(
        ProductVerificationContext context,
        CancellationToken cancellationToken);

    protected ProductVerificationRecord ToRecord(
        ProductVerificationContext context, TrustedProcessResult result, string? detail = null) =>
        new(
            Kind,
            result.Outcome == VerificationOutcomeKind.Passed,
            Name,
            result.CommandLine,
            result.ExitCode,
            context.CommitSha,
            result.CompletedAt,
            context.AttemptId,
            Path.GetRelativePath(context.WorkspaceRoot, result.WorkingDirectory).Replace('\\', '/'),
            detail ?? Describe(result));

    private static string Describe(TrustedProcessResult result) => result.Outcome switch
    {
        VerificationOutcomeKind.Passed => $"{result.CommandLine} → exit 0 em {result.Duration.TotalSeconds:F1}s",
        VerificationOutcomeKind.TimedOut => $"{result.CommandLine} → estourou o tempo limite",
        VerificationOutcomeKind.InfrastructureError => $"{result.CommandLine} → {result.Output}",
        _ => $"{result.CommandLine} → exit {result.ExitCode}\n{result.Output}",
    };

    /// <summary>
    /// Descobre a superfície .NET a compilar. Solution única vence; sem solution, projeto único
    /// vence. AMBIGUIDADE NÃO É ESCOLHA ALEATÓRIA: com várias solutions, devolve nulo e o
    /// diagnóstico diz que a superfície é ambígua — escolher uma no sorteio produziria uma
    /// evidência que fala de outro produto.
    /// </summary>
    protected static (string? Target, string? Ambiguity) ResolveDotNetSurface(string workspaceRoot)
    {
        var solutions = SafeFind(workspaceRoot, "*.sln")
            .Concat(SafeFind(workspaceRoot, "*.slnx"))
            .ToArray();
        if (solutions.Length == 1)
        {
            return (solutions[0], null);
        }

        if (solutions.Length > 1)
        {
            return (null,
                $"{solutions.Length} solutions na entrega ({string.Join(", ", solutions.Take(3))}…); " +
                "a superfície a verificar é ambígua e escolher uma no sorteio produziria evidência " +
                "sobre outro produto.");
        }

        var projects = SafeFind(workspaceRoot, "*.csproj");
        return projects.Count switch
        {
            1 => (projects[0], null),
            0 => (null, "Nenhuma solution ou projeto .NET encontrado na entrega."),
            _ => (null,
                $"{projects.Count} projetos .NET sem solution que os agrupe; a superfície é ambígua."),
        };
    }

    protected static IReadOnlyList<string> SafeFind(string root, string pattern)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        var found = new List<string>();
        Walk(new DirectoryInfo(root), 0, found, pattern, root);
        found.Sort(StringComparer.Ordinal);
        return found;
    }

    private static void Walk(
        DirectoryInfo directory, int depth, List<string> found, string pattern, string root)
    {
        if (depth > 5 || found.Count >= 32)
        {
            return;
        }

        try
        {
            foreach (var file in directory.EnumerateFiles(pattern))
            {
                found.Add(Path.GetRelativePath(root, file.FullName).Replace('\\', '/'));
            }

            foreach (var child in directory.EnumerateDirectories())
            {
                if (child.Name is "node_modules" or ".git" or "bin" or "obj" or "dist" ||
                    child.Name.StartsWith('.'))
                {
                    continue;
                }

                Walk(child, depth + 1, found, pattern, root);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Diretório ilegível é diretório ausente para efeito de descoberta.
        }
    }
}

/// <summary>Compila o backend .NET da entrega. `--no-restore` não: a entrega precisa restaurar.</summary>
public sealed class DotNetBuildVerifier(TrustedProcessRunner runner) : ProcessProductVerifier(runner)
{
    public override ProductEvidenceKind Kind => ProductEvidenceKind.BackendBuild;

    public override string Name => "dotnet-build";

    public override bool AppliesTo(ProjectEffectiveProfile profile) =>
        profile.Backend.Required &&
        (profile.Backend.Runtime?.Contains(".NET", StringComparison.OrdinalIgnoreCase) ?? true);

    public override async Task<ProductVerificationRecord?> VerifyAsync(
        ProductVerificationContext context, CancellationToken cancellationToken)
    {
        var (target, ambiguity) = ResolveDotNetSurface(context.WorkspaceRoot);
        if (target is null)
        {
            return ambiguity is null
                ? null
                : Unverifiable(context, ambiguity);
        }

        var result = await Runner.RunAsync(
            "dotnet", ["build", target, "-c", "Release", "--nologo"],
            context.WorkspaceRoot, null, TimeSpan.FromMinutes(10), cancellationToken);
        return ToRecord(context, result);
    }

    private ProductVerificationRecord Unverifiable(ProductVerificationContext context, string reason) =>
        new(Kind, false, Name, "dotnet build", -1, context.CommitSha, DateTimeOffset.UtcNow,
            context.AttemptId, ".", reason);
}

/// <summary>Roda a suíte de testes da entrega.</summary>
public sealed class DotNetTestVerifier(TrustedProcessRunner runner) : ProcessProductVerifier(runner)
{
    public override ProductEvidenceKind Kind => ProductEvidenceKind.AutomatedTestsPassed;

    public override string Name => "dotnet-test";

    public override bool AppliesTo(ProjectEffectiveProfile profile) => profile.Backend.Required;

    public override async Task<ProductVerificationRecord?> VerifyAsync(
        ProductVerificationContext context, CancellationToken cancellationToken)
    {
        var (target, _) = ResolveDotNetSurface(context.WorkspaceRoot);
        if (target is null)
        {
            return null;
        }

        // Uma entrega sem NENHUM projeto de teste não tem suíte para rodar. `dotnet test` numa
        // solution sem testes sai com zero e produziria uma evidência que diz "os testes passaram"
        // sobre zero testes — a mentira mais confortável que este gate poderia contar.
        var hasTestProject = SafeFind(context.WorkspaceRoot, "*.csproj")
            .Any(path => path.Contains("test", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("spec", StringComparison.OrdinalIgnoreCase));
        if (!hasTestProject)
        {
            return new ProductVerificationRecord(
                Kind, false, Name, "dotnet test", -1, context.CommitSha, DateTimeOffset.UtcNow,
                context.AttemptId, ".",
                "Nenhum projeto de teste na entrega: não há suíte para executar.");
        }

        var result = await Runner.RunAsync(
            "dotnet", ["test", target, "-c", "Release", "--nologo"],
            context.WorkspaceRoot, null, TimeSpan.FromMinutes(15), cancellationToken);
        return ToRecord(context, result);
    }
}
