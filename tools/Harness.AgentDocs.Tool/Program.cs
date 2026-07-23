using Harness.Host.Agents;

// PLAT-06: gerador re-executável do espelho documental das definições canônicas de agente.
// Uso: dotnet run --project tools/Harness.AgentDocs.Tool -- [generate|check] [<repositoryRoot>]
//   generate (padrão): (re)escreve docs/agents/<key>.yaml de forma determinística.
//   check: verifica que os ficheiros comprometidos batem com a fonte canônica (mesma prova do drift test).

var command = args.Length > 0 && !IsPath(args[0]) ? args[0] : "generate";
var rootArg = args.FirstOrDefault(IsPath);
var repositoryRoot = rootArg is not null
    ? Path.GetFullPath(rootArg)
    : FindRepositoryRoot(Environment.CurrentDirectory);

switch (command)
{
    case "generate":
        var written = CanonicalAgentDocs.Write(repositoryRoot);
        Console.WriteLine($"agent-docs-generate: wrote {string.Join(", ", written)}");
        return 0;

    case "check":
        var drifted = new List<string>();
        var directory = Path.Combine(repositoryRoot, "docs", "agents");
        foreach (var (fileName, content) in CanonicalAgentDocs.RenderAll())
        {
            var path = Path.Combine(directory, fileName);
            var actual = File.Exists(path) ? File.ReadAllText(path).Replace("\r\n", "\n") : null;
            if (actual != content)
            {
                drifted.Add($"{CanonicalAgentDocs.RelativeDirectory}/{fileName}");
            }
        }

        if (drifted.Count > 0)
        {
            Console.Error.WriteLine(
                $"agent-docs-check: drift in {string.Join(", ", drifted)}. Run tools/backend/generate-agent-docs.sh.");
            return 1;
        }

        Console.WriteLine("agent-docs-check: docs/agents is in sync with CanonicalAgentDefinitions.");
        return 0;

    default:
        Console.Error.WriteLine($"Unknown agent-docs command '{command}'. Use generate or check.");
        return 2;
}

static bool IsPath(string value) =>
    value.Contains('/', StringComparison.Ordinal) ||
    value.Contains('\\', StringComparison.Ordinal) ||
    Directory.Exists(value);

static string FindRepositoryRoot(string start)
{
    var directory = new DirectoryInfo(start);
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "Harness.sln")))
        {
            return directory.FullName;
        }

        directory = directory.Parent;
    }

    throw new DirectoryNotFoundException("Could not locate the Poseidon repository root.");
}
