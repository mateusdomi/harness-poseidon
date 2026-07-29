namespace Harness.Modules.Coordination.Application;

/// <summary>Perfil de rigor dos hooks, derivado do risco do card.</summary>
public enum HookRigor
{
    /// <summary>Mínimo: barra o que é sempre errado (segredo, escopo alheio).</summary>
    Minimal = 0,

    /// <summary>Padrão: soma formatação e testes da área tocada.</summary>
    Standard = 1,

    /// <summary>Estrito: soma varredura completa e proíbe editar sem teste correspondente.</summary>
    Strict = 2
}

/// <summary>Um hook gerado para a worktree da tentativa.</summary>
public sealed record GeneratedHook(string Event, string Command, string Rationale);

/// <summary>Configuração de hooks da worktree, pronta para o CLI aplicar em tempo de edição.</summary>
public sealed record WorktreeHookPlan(
    HookRigor Rigor,
    IReadOnlyList<GeneratedHook> Hooks,
    IReadOnlyList<string> DeniedScopes);

/// <summary>
/// Hooks por worktree (B8).
///
/// Regras que só existem no enunciado do card são cumpridas por boa vontade: o agente lê "não toque
/// em X", trabalha quarenta minutos, e descobre no portão que tocou. O custo desse aprendizado é uma
/// rodada inteira.
///
/// Hook é a mesma regra aplicada no instante da edição, pelo próprio CLI, dentro da worktree da
/// tentativa. Ele não convence o agente: ele impede o arquivo de ser salvo fora do escopo. A regra
/// deixa de depender de disciplina e passa a depender do sistema — que é onde ela deveria estar.
///
/// O rigor acompanha o risco por um motivo prático: hook estrito numa mudança visual transforma
/// cada salvamento numa espera, e o agente aprende a contornar o hook em vez de respeitá-lo.
/// </summary>
public static class WorktreeHookPolicy
{
    public static HookRigor RigorFor(RiskTier risk)
    {
        if (!Enum.IsDefined(risk))
        {
            throw new ArgumentOutOfRangeException(nameof(risk));
        }

        return risk switch
        {
            RiskTier.Critical => HookRigor.Strict,
            RiskTier.High => HookRigor.Strict,
            RiskTier.Medium => HookRigor.Standard,
            _ => HookRigor.Minimal
        };
    }

    /// <summary>
    /// Gera o plano de hooks da tentativa. <paramref name="allowedScopes"/> são os ScopeClaims do
    /// card; <paramref name="deniedScopes"/> são as fronteiras negativas dos irmãos.
    /// </summary>
    public static WorktreeHookPlan Generate(
        RiskTier risk,
        string language,
        IReadOnlyList<string> allowedScopes,
        IReadOnlyList<string> deniedScopes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        ArgumentNullException.ThrowIfNull(allowedScopes);
        ArgumentNullException.ThrowIfNull(deniedScopes);

        var rigor = RigorFor(risk);
        var hooks = new List<GeneratedHook>();

        // Sempre, em qualquer rigor: segredo em commit é dano que não se desfaz, porque o histórico
        // guarda o que a correção apagou.
        hooks.Add(new GeneratedHook(
            "pre-commit",
            "tools/backend/scan-secrets.sh",
            "Segredo publicado não se apaga do histórico: barrar antes é a única correção que funciona."));

        // Sempre: escopo alheio. É a fronteira negativa deixando de ser recomendação.
        if (deniedScopes.Count > 0)
        {
            hooks.Add(new GeneratedHook(
                "pre-edit",
                "guard-scope --deny " + string.Join(",", deniedScopes.Order(StringComparer.Ordinal)),
                "A fronteira do card aplicada na edição, não descoberta no portão uma rodada depois."));
        }

        if (rigor >= HookRigor.Standard)
        {
            hooks.Add(new GeneratedHook(
                "pre-commit",
                FormatCommandFor(language),
                "Formatação divergente vira ruído no diff e esconde a mudança real na revisão."));
            hooks.Add(new GeneratedHook(
                "pre-submit",
                TestCommandFor(language, allowedScopes),
                "Teste da área tocada antes de ocupar revisor: o defeito mais barato de achar."));
        }

        if (rigor == HookRigor.Strict)
        {
            hooks.Add(new GeneratedHook(
                "pre-submit",
                "tools/backend/verify.sh",
                "Em risco alto o custo de uma verificação completa é menor que o de um retorno."));
            hooks.Add(new GeneratedHook(
                "pre-commit",
                "guard-test-coverage --changed",
                "Mudança de comportamento sem teste correspondente é mudança que ninguém consegue defender."));
        }

        return new WorktreeHookPlan(
            rigor,
            hooks,
            deniedScopes.Order(StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// O plano corresponde ao card? Confere que toda fronteira do card virou negação de escopo —
    /// hook que não cobre a fronteira declarada é falsa sensação de proteção.
    /// </summary>
    public static bool MatchesCard(
        WorktreeHookPlan plan,
        RiskTier risk,
        IReadOnlyList<string> expectedDeniedScopes)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(expectedDeniedScopes);

        return plan.Rigor == RigorFor(risk) &&
            expectedDeniedScopes
                .Order(StringComparer.Ordinal)
                .SequenceEqual(plan.DeniedScopes, StringComparer.Ordinal);
    }

    private static string FormatCommandFor(string language) =>
        language.Trim().ToLowerInvariant() switch
        {
            "csharp" or "c#" => "tools/backend/dotnet.sh format Harness.sln --verify-no-changes",
            "typescript" or "ts" => "npm run lint --prefix frontend",
            _ => "tools/backend/verify-governance.sh"
        };

    private static string TestCommandFor(string language, IReadOnlyList<string> scopes)
    {
        var scope = scopes.Count > 0 ? scopes[0] : ".";
        return language.Trim().ToLowerInvariant() switch
        {
            "csharp" or "c#" => $"tools/backend/dotnet.sh test --filter Scope~{scope}",
            "typescript" or "ts" => "npm run test --prefix frontend",
            _ => "tools/backend/verify.sh"
        };
    }
}
