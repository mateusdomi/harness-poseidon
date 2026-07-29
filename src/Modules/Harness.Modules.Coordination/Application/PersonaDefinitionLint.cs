namespace Harness.Modules.Coordination.Application;

/// <summary>Definição de persona submetida para criação ou alteração.</summary>
public sealed record PersonaDefinition(
    string Key,
    string DisplayName,
    IReadOnlyList<string> Tools,
    IReadOnlyList<string> AllowedScopes,
    IReadOnlyList<string> DeniedScopes);

public sealed record PersonaLintFinding(string Code, string Message);

public sealed record PersonaLintResult(bool Accepted, IReadOnlyList<PersonaLintFinding> Findings);

/// <summary>
/// Lint de definição de persona, Default-FAIL na criação e na alteração (B8).
///
/// Uma persona é uma procuração: ela diz quais ferramentas um agente pode usar e onde ele pode
/// escrever. Definições perigosas não parecem perigosas na hora de criar — parecem convenientes.
/// "Dá acesso a tudo para não travar" resolve o atrito de hoje e cria o incidente de amanhã.
///
/// Três combinações que este lint recusa, e o porquê de cada uma:
///
/// 1. <b>Executar comando arbitrário + escrever no repositório inteiro.</b> Cada uma isolada é
///    defensável; juntas, são acesso irrestrito com aparência de configuração.
/// 2. <b>Escopo permitido = raiz.</b> Escopo largo derrota todo o resto do sistema: fronteira
///    negativa, ScopeClaim e hook de escopo passam a não restringir nada.
/// 3. <b>Ausência de denylist.</b> Sem ela, caminhos que nunca deveriam ser tocados por agente —
///    governança, segredos, o próprio quadro — ficam abertos por omissão. Omissão não é decisão.
///
/// Default-FAIL: o que este lint não entende, ele recusa. Uma persona recusada custa uma conversa;
/// uma persona perigosa aceita custa um incidente.
/// </summary>
public static class PersonaDefinitionLint
{
    public const string DangerousToolCombination = "persona.dangerous_tool_combination";
    public const string ScopeTooBroad = "persona.scope_too_broad";
    public const string MissingDenylist = "persona.missing_denylist";
    public const string ProtectedPathAllowed = "persona.protected_path_allowed";
    public const string NoToolsDeclared = "persona.no_tools_declared";

    /// <summary>Ferramentas que executam comando arbitrário — o multiplicador de qualquer escopo.</summary>
    private static readonly string[] ArbitraryExecutionTools = ["bash", "shell", "exec", "run_command"];

    /// <summary>Ferramentas que escrevem no repositório.</summary>
    private static readonly string[] WriteTools = ["write", "edit", "apply_patch", "create_file"];

    /// <summary>Caminhos que nenhuma persona pode receber como escopo permitido.</summary>
    private static readonly string[] ProtectedPaths =
        ["governance", "coordination", ".github", "tools/backend/.tooling"];

    /// <summary>Escopos que equivalem à raiz do repositório.</summary>
    private static readonly string[] RootLikeScopes = [".", "/", "*", "**", "./", "src", "**/*"];

    public static PersonaLintResult Inspect(PersonaDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Key);

        var findings = new List<PersonaLintFinding>();
        var tools = definition.Tools ?? [];
        var allowed = definition.AllowedScopes ?? [];
        var denied = definition.DeniedScopes ?? [];

        if (tools.Count == 0)
        {
            findings.Add(new PersonaLintFinding(
                NoToolsDeclared,
                "A persona não declara ferramenta nenhuma — ela não conseguiria executar tarefa alguma."));
        }

        var hasArbitraryExecution = tools.Any(tool =>
            ArbitraryExecutionTools.Contains(tool.Trim(), StringComparer.OrdinalIgnoreCase));
        var hasWrite = tools.Any(tool =>
            WriteTools.Contains(tool.Trim(), StringComparer.OrdinalIgnoreCase));
        var rootLike = allowed.Any(IsRootLike);

        if (rootLike)
        {
            findings.Add(new PersonaLintFinding(
                ScopeTooBroad,
                "O escopo permitido equivale à raiz do repositório: fronteira negativa, ScopeClaim e " +
                "hook de escopo deixam de restringir qualquer coisa."));
        }

        if (hasArbitraryExecution && (hasWrite || rootLike))
        {
            findings.Add(new PersonaLintFinding(
                DangerousToolCombination,
                "Executar comando arbitrário somado a escrever amplamente é acesso irrestrito com " +
                "aparência de configuração. Separe as duas capacidades em personas distintas."));
        }

        if (denied.Count == 0)
        {
            findings.Add(new PersonaLintFinding(
                MissingDenylist,
                "Sem denylist, caminhos que nunca deveriam ser tocados ficam abertos por omissão — " +
                "e omissão não é decisão."));
        }

        var protectedAllowed = allowed
            .Where(scope => ProtectedPaths.Any(protectedPath =>
                scope.Trim().TrimEnd('/').StartsWith(protectedPath, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (protectedAllowed.Length > 0)
        {
            findings.Add(new PersonaLintFinding(
                ProtectedPathAllowed,
                $"Caminho protegido no escopo permitido: {string.Join(", ", protectedAllowed)}."));
        }

        // Default-FAIL: qualquer achado recusa. Uma persona recusada custa uma conversa; uma
        // persona perigosa aceita custa um incidente.
        return new PersonaLintResult(findings.Count == 0, findings);
    }

    /// <summary>
    /// Escopo que equivale à raiz. Um escopo que só tem barras ("/", "//") vira vazio ao normalizar
    /// — e vazio é a raiz, não "nenhum escopo". Tratá-lo como não-raiz deixaria passar exatamente a
    /// permissão mais ampla possível, pela forma mais curta de escrevê-la.
    /// </summary>
    private static bool IsRootLike(string scope)
    {
        var normalized = (scope ?? string.Empty).Trim().TrimEnd('/');
        return normalized.Length == 0 ||
            RootLikeScopes.Contains(normalized, StringComparer.OrdinalIgnoreCase);
    }
}
