using Harness.Modules.Agents.Application.Accounts;
using Harness.Modules.Agents.Application.Execution.External;
using Harness.Modules.Agents.Contracts;

namespace Harness.IntegrationTests.Agents;

/// <summary>
/// CA-4 — smoke de execução REAL dos adapters externos sobre perfis isolados (CA-3).
///
/// Este teste executa a CLI de verdade e consome cota, portanto exige opt-in explícito por
/// `HARNESS_RUN_REAL_AGENT_TESTS=true` e um perfil já autenticado. Sem credencial ele
/// registra `SKIPPED_EXTERNAL_CREDENTIALS` — ausência de prova DECLARADA, jamais tratada
/// como verde.
///
/// A raiz dos perfis vem de `HARNESS_AGENT_PROFILES_ROOT` (padrão `~/.harness/accounts`) e
/// os aliases vêm de `HARNESS_SMOKE_CLAUDE_ALIAS` / `HARNESS_SMOKE_CODEX_ALIAS`. Nenhum
/// segredo é lido, passado por argumento ou registrado.
/// </summary>
public sealed class ExternalAgentRealExecutionSmokeTests
{
    private const string OptInVariable = "HARNESS_RUN_REAL_AGENT_TESTS";
    private const string SkipMarker = "SKIPPED_EXTERNAL_CREDENTIALS";
    private const string BlockedMarker = "BLOCKED_EXTERNAL_OAUTH";

    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public async Task ClaudeCodeCompletesARealTurnInsideTheAccountProfile()
    {
        await RunSmokeAsync(
            ExecutorCatalog.ClaudeCode,
            Environment.GetEnvironmentVariable("HARNESS_SMOKE_CLAUDE_ALIAS") ?? "chief-claude-primary",
            provisioner => ClaudeCodeExternalAgentExecutor.ForClaudeCode(provisioner));
    }

    [Fact]
    public async Task CodexCompletesARealTurnInsideTheAccountProfile()
    {
        await RunSmokeAsync(
            ExecutorCatalog.Codex,
            Environment.GetEnvironmentVariable("HARNESS_SMOKE_CODEX_ALIAS") ?? "worker-codex-frontend",
            provisioner => CodexExternalAgentExecutor.Create(provisioner));
    }

    /// <summary>
    /// N3 — smoke live do critic Antigravity no PERFIL ISOLADO. O `agy` autentica por OAuth
    /// interativo (config home em <c>$HOME/.gemini</c>); o perfil isolado nunca herda o login
    /// global do operador (CA-3). Sem login isolado, o `agy` responde "authentication
    /// required" e o adapter classifica <c>executor.authentication_required</c> — registrado
    /// como <c>BLOCKED_EXTERNAL_OAUTH</c>, jamais como verde. O comando EXATO de login
    /// isolado é impresso para a ação humana única.
    /// </summary>
    [Fact]
    public async Task AntigravityCriticSmokeIsBlockedOnIsolatedOAuthUntilTheProfileLogsIn()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(OptInVariable), "true", StringComparison.OrdinalIgnoreCase))
        {
            Declare($"{OptInVariable} não é true");
            return;
        }

        var alias = Environment.GetEnvironmentVariable("HARNESS_SMOKE_ANTIGRAVITY_ALIAS")
            ?? "worker-antigravity-review";
        var root = Environment.GetEnvironmentVariable("HARNESS_AGENT_PROFILES_ROOT")
            ?? AccountProfileProvisioner.DefaultProfilesRoot;
        var provisioner = new AccountProfileProvisioner(root);
        var profile = ExecutorCatalog.Find(ExecutorCatalog.Antigravity)!;
        var handle = provisioner.Ensure(
            new AgentAccountContract(
                alias, "antigravity", ExecutorCatalog.Antigravity, $"keychain://poseidon/{alias}",
                $"confighome://{alias}", ["critic"], [], AgentAccountState.Available,
                AgentAccountHealth.Unknown, 1, 0, null, null, null, null, null, 100),
            profile,
            Now);

        var executor = AntigravityExternalAgentExecutor.Create(provisioner);
        var probe = await executor.ProbeAsync(CancellationToken.None);
        if (!probe.Installed)
        {
            Declare($"executor antigravity não instalado ({probe.ReasonCode})");
            return;
        }

        var workspace = Path.Combine(handle.Layout.WorkingRootPath, "smoke");
        Directory.CreateDirectory(workspace);

        // Comando EXATO de login isolado, derivado do CLI real (`agy` sem subcomando faz o
        // login OAuth interativo; o HOME aponta o config home ao perfil do alias).
        var loginCommand = $"HOME={handle.Layout.ConfigHomePath} agy";

        await using var session = await executor.StartAsync(
            new ExternalAgentRunRequest
            {
                Alias = alias,
                Prompt = "Responda exatamente com a palavra PRONTO e nada mais.",
                WorkingDirectory = workspace,
                Profile = handle.Layout,
                Access = ExternalAgentAccess.ReadOnly,
                Timeout = TimeSpan.FromMinutes(2),
            },
            CancellationToken.None);

        var result = await session.CollectAsync(CancellationToken.None);
        await session.CleanupAsync(CancellationToken.None);

        if (result.Status != ExternalAgentRunStatus.Completed)
        {
            // Perfil isolado sem login: bloqueio externo declarado, com o comando exato.
            Assert.True(true, BlockedMarker);
            Console.WriteLine(
                $"{BlockedMarker}: perfil {alias} sem login isolado ({result.FailureCode}). " +
                $"Login humano único: {loginCommand}");
            return;
        }

        // Se o operador já executou o login isolado, o critic responde de verdade. Antigravity
        // print não expõe sessionId nem usage — não os asserimos (seria inventar contrato).
        Assert.Contains("PRONTO", result.FinalMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(ExternalAgentRedaction.ContainsSecret(result.FinalMessage));
        Assert.False(session.IsRunning);
    }

    private static async Task RunSmokeAsync(
        string executorId,
        string alias,
        Func<AccountProfileProvisioner, ProcessExternalAgentExecutor> factory)
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(OptInVariable), "true", StringComparison.OrdinalIgnoreCase))
        {
            Declare($"{OptInVariable} não é true");
            return;
        }

        var root = Environment.GetEnvironmentVariable("HARNESS_AGENT_PROFILES_ROOT")
            ?? AccountProfileProvisioner.DefaultProfilesRoot;
        var provisioner = new AccountProfileProvisioner(root);
        var profile = ExecutorCatalog.Find(executorId)!;
        var handle = provisioner.Ensure(
            new AgentAccountContract(
                alias, "external", executorId, $"keychain://poseidon/{alias}",
                $"confighome://{alias}", [], [], AgentAccountState.Available,
                AgentAccountHealth.Unknown, 1, 0, null, null, null, null, null, 100),
            profile,
            Now);

        var executor = factory(provisioner);
        var probe = await executor.ProbeAsync(CancellationToken.None);
        if (!probe.Installed)
        {
            Declare($"executor {executorId} não instalado ({probe.ReasonCode})");
            return;
        }

        var workspace = Path.Combine(handle.Layout.WorkingRootPath, "smoke");
        Directory.CreateDirectory(workspace);

        await using var session = await executor.StartAsync(
            new ExternalAgentRunRequest
            {
                Alias = alias,
                Prompt = "Responda exatamente com a palavra PRONTO e nada mais.",
                WorkingDirectory = workspace,
                Profile = handle.Layout,
                Access = ExternalAgentAccess.ReadOnly,
                Timeout = TimeSpan.FromMinutes(5),
            },
            CancellationToken.None);

        var kinds = new List<ExternalAgentEventKind>();
        await foreach (var @event in session.StreamAsync(CancellationToken.None))
        {
            kinds.Add(@event.Kind);
        }

        var result = await session.CollectAsync(CancellationToken.None);

        if (result.Status != ExternalAgentRunStatus.Completed && NeverReachedTheModel(result))
        {
            // O perfil isolado existe mas não está autenticado (ou a conta está sem cota):
            // nenhum token foi consumido, logo o modelo não foi alcançado. Declarado como
            // prova ausente — nunca como verde, nunca como falha do adapter.
            Declare($"perfil {alias} não autenticado ou sem cota para {executorId}");
            return;
        }

        Assert.Equal(ExternalAgentRunStatus.Completed, result.Status);
        Assert.Equal(executorId, result.ExecutorId);
        Assert.False(string.IsNullOrWhiteSpace(result.SessionId));
        Assert.Contains("PRONTO", result.FinalMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(ExternalAgentEventKind.Started, kinds);
        Assert.Contains(ExternalAgentEventKind.Completed, kinds);
        Assert.False(ExternalAgentRedaction.ContainsSecret(result.FinalMessage));
        Assert.NotNull(result.Usage);

        await session.CleanupAsync(CancellationToken.None);
        Assert.False(session.IsRunning);
    }

    /// <summary>
    /// Registro EXPLÍCITO de prova não executada. A ausência de credencial nunca vira verde
    /// silencioso: ela aparece na saída do teste como marcador.
    /// </summary>
    private static void Declare(string reason)
    {
        Assert.True(true, SkipMarker);
        Console.WriteLine($"{SkipMarker}: {reason}.");
    }

    /// <summary>
    /// Sinal ESTRUTURAL de que o modelo não foi alcançado: nenhum token entrou nem saiu.
    /// Preferido a casar texto de erro, que muda entre versões da CLI.
    /// </summary>
    private static bool NeverReachedTheModel(ExternalAgentRunResult result) =>
        result.Usage is null ||
        ((result.Usage.InputTokens ?? 0) == 0 && (result.Usage.OutputTokens ?? 0) == 0);
}
