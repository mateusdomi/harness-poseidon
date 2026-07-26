using System.Text.Json;
using System.Text.Json.Serialization;
using Harness.Modules.Agents.Contracts;

namespace Harness.Modules.Agents.Application.Accounts;

/// <summary>
/// Papéis lógicos conhecidos. O escopo de paths pertence ao PAPEL, nunca ao provider
/// (CA-1): trocar o executor de um papel não amplia escopo.
/// </summary>
public static class AgentRoles
{
    public const string ChiefOrchestrator = "chief-orchestrator";
    public const string FrontendSpecialist = "frontend-specialist";
    public const string BackendSpecialist = "backend-specialist";
    public const string Critic = "critic";

    /// <summary>
    /// Claims PADRÃO do papel — usados quando o pedido não estreita o escopo. O frontend
    /// possui `frontend/**`+`docs/frontend/**`; o backend possui as raízes de trabalho
    /// backend/compartilhadas (nunca `frontend/**`). Um papel desconhecido não recebe claim.
    ///
    /// Um pedido pode ESTREITAR para sub-paths dentro do limite do papel (para concorrência
    /// granular entre instâncias), mas nunca AMPLIAR: a política de escopo
    /// (<c>AgentPathScopePolicy</c>) recusa qualquer claim fora do papel.
    /// </summary>
    public static IReadOnlyList<string> PathScopesFor(string role) =>
        string.Equals(role, FrontendSpecialist, StringComparison.OrdinalIgnoreCase)
            ? ["frontend/**", "docs/frontend/**"]
            : string.Equals(role, BackendSpecialist, StringComparison.OrdinalIgnoreCase)
                ? BackendDefaultScopes
                : [];

    private static readonly string[] BackendDefaultScopes =
    [
        "src/**", "tests/**", "docs/backend/**", "docs/contracts/**",
        "docs/architecture/**", "docs/decisions/**", "infra/**",
        "tools/backend/**", "governance/**",
    ];

    public static bool IsKnown(string role) =>
        role is ChiefOrchestrator or FrontendSpecialist or BackendSpecialist or Critic;
}

/// <summary>
/// Definição de conta lida da configuração LOCAL do operador. Nenhum e-mail, token ou
/// senha existe nesta forma: apenas alias e referência opaca.
/// </summary>
public sealed record AgentAccountDefinition
{
    [JsonPropertyName("alias")]
    public required string Alias { get; init; }

    [JsonPropertyName("providerKind")]
    public required string ProviderKind { get; init; }

    [JsonPropertyName("executorId")]
    public required string ExecutorId { get; init; }

    [JsonPropertyName("credentialRef")]
    public required string CredentialRef { get; init; }

    [JsonPropertyName("allowedRoles")]
    public IReadOnlyList<string> AllowedRoles { get; init; } = [];

    [JsonPropertyName("allowedPathScopes")]
    public IReadOnlyList<string> AllowedPathScopes { get; init; } = [];

    [JsonPropertyName("concurrencyLimit")]
    public int ConcurrencyLimit { get; init; } = 1;

    [JsonPropertyName("priority")]
    public int Priority { get; init; } = 100;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; } = true;
}

/// <summary>Arquivo de contas do operador (`<home>/.harness/agent-accounts.json`).</summary>
public sealed record AgentAccountsFile
{
    [JsonPropertyName("accounts")]
    public IReadOnlyList<AgentAccountDefinition> Accounts { get; init; } = [];
}

/// <summary>
/// Carrega o registro de contas a partir da configuração local (CA-5).
///
/// Os aliases canônicos de ADR-021 existem por padrão para que o operador não precise
/// escrever configuração antes de usar o produto; o arquivo local, quando existe,
/// SUBSTITUI a definição do alias correspondente. Uma conta nasce
/// <see cref="AgentAccountState.AuthenticationRequired"/> — disponibilidade é comprovada
/// por probe e por autenticação, jamais presumida.
/// </summary>
public static class AgentAccountConfigurationLoader
{
    public const string FileName = "agent-accounts.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Definições canônicas de ADR-021. O mapeamento alias → executor é DOCUMENTADO, não
    /// inferido do nome em runtime.
    /// </summary>
    public static IReadOnlyList<AgentAccountDefinition> CanonicalDefinitions { get; } =
    [
        Canonical("chief-claude-primary", "anthropic", ExecutorCatalog.ClaudeCode, AgentRoles.ChiefOrchestrator),
        Canonical("worker-claude-secondary", "anthropic", ExecutorCatalog.ClaudeCode, AgentRoles.BackendSpecialist),
        Canonical("worker-codex-frontend", "openai", ExecutorCatalog.Codex, AgentRoles.FrontendSpecialist),
        Canonical("worker-codex-critic", "openai", ExecutorCatalog.Codex, AgentRoles.Critic, priority: 80),
        Canonical("worker-kimi-ui", "moonshot", ExecutorCatalog.KimiCode, AgentRoles.FrontendSpecialist, priority: 70),
        Canonical("worker-glm-general", "zhipu", ExecutorCatalog.Glm, AgentRoles.BackendSpecialist, priority: 60),
        // Antigravity é executor de PRIMEIRA CLASSE com vocação de critic, e por isso tem a
        // maior prioridade entre os critics. Não é experimental.
        Canonical("worker-antigravity-review", "antigravity", ExecutorCatalog.Antigravity, AgentRoles.Critic, priority: 90),
    ];

    public static string DefaultFilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".harness",
            FileName);

    /// <summary>Lê o arquivo local, se existir, e mescla sobre as definições canônicas.</summary>
    public static IReadOnlyList<AgentAccountDefinition> LoadDefinitions(string? filePath = null)
    {
        var path = filePath ?? DefaultFilePath;
        var merged = CanonicalDefinitions.ToDictionary(
            definition => definition.Alias, StringComparer.OrdinalIgnoreCase);

        if (!File.Exists(path))
        {
            return [.. merged.Values.OrderBy(definition => definition.Alias, StringComparer.Ordinal)];
        }

        AgentAccountsFile? file;
        try
        {
            file = JsonSerializer.Deserialize<AgentAccountsFile>(File.ReadAllText(path), Json);
        }
        catch (JsonException)
        {
            // Configuração inválida NÃO degrada silenciosamente para o padrão: o operador
            // precisa saber que o arquivo dele não foi aplicado.
            throw new AgentAccountValidationException("account.configuration_invalid");
        }

        foreach (var definition in file?.Accounts ?? [])
        {
            AgentAccountRegistry.ValidateAlias(definition.Alias);
            AgentAccountRegistry.ValidateCredentialReference(definition.CredentialRef);
            merged[definition.Alias] = definition;
        }

        return [.. merged.Values.OrderBy(definition => definition.Alias, StringComparer.Ordinal)];
    }

    /// <summary>Materializa o registro. Contas desabilitadas entram como `Disabled`.</summary>
    public static AgentAccountRegistry Load(string? filePath = null)
    {
        var registry = new AgentAccountRegistry();
        foreach (var definition in LoadDefinitions(filePath))
        {
            registry.Register(ToContract(definition));
        }

        return registry;
    }

    public static AgentAccountContract ToContract(AgentAccountDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        // O escopo de path pertence ao PAPEL: um arquivo do operador que omite (ou zera)
        // `allowedPathScopes` herda os escopos canônicos dos papéis da conta. Sem isto, a conta
        // ficaria SILENCIOSAMENTE inelegível para qualquer card com claim (o scheduler exige que
        // cada claim do card esteja na lista da conta) — e o loop do chefe adiaria para sempre.
        // Uma lista explícita não vazia continua mandando (restrição deliberada do operador).
        var pathScopes = definition.AllowedPathScopes.Count > 0
            ? definition.AllowedPathScopes
            : [.. definition.AllowedRoles
                .SelectMany(AgentRoles.PathScopesFor)
                .Distinct(StringComparer.Ordinal)];
        return new AgentAccountContract(
            definition.Alias,
            definition.ProviderKind,
            definition.ExecutorId,
            definition.CredentialRef,
            $"confighome://{definition.Alias}",
            definition.AllowedRoles,
            pathScopes,
            // Uma conta nunca nasce Available: instalação e autenticação são comprovadas,
            // nunca presumidas.
            definition.Enabled ? AgentAccountState.AuthenticationRequired : AgentAccountState.Disabled,
            AgentAccountHealth.Unknown,
            Math.Max(1, definition.ConcurrencyLimit),
            ActiveAttempts: 0,
            CurrentAttemptId: null,
            Quota: null,
            CooldownUntil: null,
            LastSuccessfulSmokeAt: null,
            FailureReason: null,
            definition.Priority);
    }

    private static AgentAccountDefinition Canonical(
        string alias, string providerKind, string executorId, string role, int priority = 100) =>
        new()
        {
            Alias = alias,
            ProviderKind = providerKind,
            ExecutorId = executorId,
            CredentialRef = $"keychain://poseidon/{alias}",
            AllowedRoles = [role],
            AllowedPathScopes = AgentRoles.PathScopesFor(role),
            ConcurrencyLimit = 1,
            Priority = priority,
        };
}
