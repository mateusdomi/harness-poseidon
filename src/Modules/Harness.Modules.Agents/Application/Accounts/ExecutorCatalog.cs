using Harness.Modules.Agents.Contracts;

namespace Harness.Modules.Agents.Application.Accounts;

/// <summary>
/// Catálogo canônico dos executores externos suportados (CA-2/CA-4).
///
/// Cada perfil reflete uma CLI REAL, com comando, flags de execução não interativa e
/// variável de isolamento observados por probe na máquina — nunca nomes inventados. Um
/// executor cuja CLI não esteja instalada é reportado como <see cref="AgentAccountState.Unavailable"/>
/// pelo probe, e nunca como suportado.
/// </summary>
public static class ExecutorCatalog
{
    public const string ClaudeCode = "claude-code";
    public const string Codex = "codex";
    public const string Antigravity = "antigravity";
    public const string KimiCode = "kimi-code";
    public const string Glm = "glm";

    private static readonly string[] TextEffort = ["low", "medium", "high"];

    // Níveis aceitos por `claude --effort`, observados na CLI instalada (2.1.216).
    private static readonly string[] ClaudeEffort = ["low", "medium", "high", "xhigh", "max"];

    /// <summary>
    /// Perfis canônicos. `ConfigHomeEnvironmentVariable` é a variável REAL que isola
    /// credenciais/sessão do CLI (CA-3): `CLAUDE_CONFIG_DIR` para Claude Code e derivados,
    /// `CODEX_HOME` para Codex.
    /// </summary>
    public static IReadOnlyList<ExecutorProfile> All { get; } =
    [
        new ExecutorProfile(
            ClaudeCode,
            "Claude Code",
            "claude",
            // -p executa um prompt único; stream-json entrega eventos estruturados.
            ["-p", "--output-format", "stream-json", "--verbose"],
            "CLAUDE_CONFIG_DIR",
            // `USER` é obrigatório: sem ele o Claude Code não alcança o item do Keychain no
            // macOS e responde "Not logged in" mesmo com a conta vinculada no config home.
            // Descoberto no Piloto 1A, por bisecção do ambiente.
            ["PATH", "HOME", "LANG", "USER", "CLAUDE_CONFIG_DIR"],
            new CapabilitySet(
                ["chat", "code", "review"], SupportsStreaming: true, SupportsResume: true,
                SupportsEffort: true, ClaudeEffort, MaxContextTokens: null),
            DetectedVersion: null),

        new ExecutorProfile(
            Codex,
            "Codex CLI",
            "codex",
            // `codex exec` é o modo não interativo oficial.
            ["exec", "--skip-git-repo-check"],
            "CODEX_HOME",
            ["PATH", "HOME", "LANG", "USER", "CODEX_HOME"],
            new CapabilitySet(
                ["chat", "code", "review"], SupportsStreaming: true, SupportsResume: true,
                SupportsEffort: false, [], MaxContextTokens: null),
            DetectedVersion: null),

        new ExecutorProfile(
            Antigravity,
            "Antigravity",
            "agy",
            // --print roda um prompt único e imprime a resposta.
            ["--print"],
            // Sem variável de config home documentada: o isolamento é por working directory
            // e allowlist de ambiente, validado no probe (CA-3).
            null,
            ["PATH", "HOME", "LANG", "USER"],
            new CapabilitySet(
                ["chat", "code", "review"], SupportsStreaming: false, SupportsResume: true,
                SupportsEffort: true, TextEffort, MaxContextTokens: null),
            DetectedVersion: null),

        new ExecutorProfile(
            KimiCode,
            "Kimi Code",
            "kimi",
            ["-p"],
            null,
            ["PATH", "HOME", "LANG", "USER"],
            new CapabilitySet(
                ["chat", "code"], SupportsStreaming: true, SupportsResume: true,
                SupportsEffort: false, [], MaxContextTokens: null),
            DetectedVersion: null),

        new ExecutorProfile(
            Glm,
            "GLM (Claude Code compatível)",
            // GLM é o Claude Code apontado a um endpoint compatível por variáveis de
            // ambiente. Usa o MESMO binário, portanto exige config home próprio para não
            // sobrescrever a sessão das contas Claude (CA-3).
            "claude",
            ["-p", "--output-format", "stream-json", "--verbose"],
            "CLAUDE_CONFIG_DIR",
            ["PATH", "HOME", "LANG", "USER", "CLAUDE_CONFIG_DIR",
             "ANTHROPIC_BASE_URL", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_API_KEY",
             "ANTHROPIC_DEFAULT_HAIKU_MODEL", "ANTHROPIC_DEFAULT_SONNET_MODEL",
             "ANTHROPIC_DEFAULT_OPUS_MODEL"],
            new CapabilitySet(
                ["chat", "code", "review"], SupportsStreaming: true, SupportsResume: true,
                SupportsEffort: true, ClaudeEffort, MaxContextTokens: null),
            DetectedVersion: null),
    ];

    public static ExecutorProfile? Find(string executorId) =>
        All.FirstOrDefault(profile =>
            string.Equals(profile.ExecutorId, executorId, StringComparison.OrdinalIgnoreCase));
}
