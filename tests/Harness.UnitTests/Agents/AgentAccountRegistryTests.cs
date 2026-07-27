using Harness.Modules.Agents.Application.Accounts;
using Harness.Modules.Agents.Contracts;

namespace Harness.UnitTests.Agents;

/// <summary>
/// CA-2: registro de contas por alias, sem segredo e sem identidade real de usuário.
/// </summary>
public sealed class AgentAccountRegistryTests
{
    private static AgentAccountContract Account(
        string alias = "worker-codex-frontend",
        string executorId = ExecutorCatalog.Codex,
        string credential = "keychain://poseidon/worker-codex-frontend",
        AgentAccountState state = AgentAccountState.Available,
        int concurrency = 1,
        int active = 0,
        IReadOnlyList<string>? roles = null) =>
        new(alias, "openai", executorId, credential, $"confighome://{alias}",
            roles ?? ["frontend-specialist"], ["frontend/**", "docs/frontend/**"],
            state, AgentAccountHealth.Unknown, concurrency, active, null, null, null, null, null, 100);

    private static AccountAvailabilityRecord Observed(
        string alias,
        AgentAccountState state) =>
        new(alias, state, null, "availability.available", 0, DateTimeOffset.UnixEpoch);

    [Fact]
    public void ObservedAvailabilitySurvivesTheProcessAndRehydratesTheRegistry()
    {
        // Toda conta nasce AuthenticationRequired (disponibilidade é comprovada, nunca presumida).
        // Mas a prova JÁ COLHIDA antes do restart não pode ser jogada fora: sem esta hidratação, o
        // reinício do Host devolvia a frota inteira para authentication-required e o loop do chefe
        // adiava todo card com no_eligible_account até um humano rodar o doctor de novo.
        var registry = new AgentAccountRegistry();
        registry.Register(Account(state: AgentAccountState.AuthenticationRequired));

        var applied = registry.ApplyObservedAvailability(
            [Observed("worker-codex-frontend", AgentAccountState.Available)]);

        Assert.Equal(1, applied);
        var account = registry.Get("worker-codex-frontend")!;
        Assert.Equal(AgentAccountState.Available, account.State);
        Assert.Equal(AgentAccountHealth.Healthy, account.Health);
    }

    [Fact]
    public void ObservedAvailabilityNeverReenablesWhatTheOperatorDisabled()
    {
        // O ledger observa disponibilidade; ele não é autoridade sobre a decisão do operador.
        var registry = new AgentAccountRegistry();
        registry.Register(Account(state: AgentAccountState.Disabled));

        var applied = registry.ApplyObservedAvailability(
            [Observed("worker-codex-frontend", AgentAccountState.Available)]);

        Assert.Equal(0, applied);
        Assert.Equal(AgentAccountState.Disabled, registry.Get("worker-codex-frontend")!.State);
    }

    [Fact]
    public void ObservedAvailabilityIgnoresAliasesThatAreNotRegistered()
    {
        // Ledger com conta removida da configuração não pode ressuscitá-la no registro.
        var registry = new AgentAccountRegistry();
        registry.Register(Account(state: AgentAccountState.AuthenticationRequired));

        var applied = registry.ApplyObservedAvailability(
            [Observed("worker-glm-general", AgentAccountState.Available)]);

        Assert.Equal(0, applied);
        Assert.Null(registry.Get("worker-glm-general"));
    }

    [Fact]
    public void EmailIsRejectedAsAlias()
    {
        // Nenhum e-mail entra no registro: identidade real fica só na config local.
        var exception = Assert.Throws<AgentAccountValidationException>(
            () => AgentAccountRegistry.ValidateAlias("pessoa@example.test"));
        Assert.Equal("account.alias_must_not_be_email", exception.Code);
    }

    [Theory]
    [InlineData("Worker-Codex")]
    [InlineData("worker codex")]
    [InlineData("")]
    public void AliasMustBeAStableTechnicalIdentifier(string alias)
    {
        Assert.Throws<AgentAccountValidationException>(() => AgentAccountRegistry.ValidateAlias(alias));
    }

    [Theory]
    [InlineData("keychain://poseidon/worker")]
    [InlineData("secret://poseidon/worker")]
    [InlineData("env://POSEIDON_WORKER_TOKEN")]
    public void OpaqueCredentialReferencesAreAccepted(string reference)
    {
        AgentAccountRegistry.ValidateCredentialReference(reference);
    }

    [Theory]
    [InlineData("sk-ant-api03-not-a-reference")]
    [InlineData("aa718a83d4354669a1e2e0b97f660bf4")]
    [InlineData("https://example.test/secret")]
    public void RawSecretsAndUnknownSchemesAreRejected(string reference)
    {
        // Um segredo cru nunca é aceito: ele pertence ao Keychain/secret store.
        Assert.Throws<AgentAccountValidationException>(
            () => AgentAccountRegistry.ValidateCredentialReference(reference));
    }

    [Fact]
    public void ReservingGrantsAnIncreasingFencingTokenAndBlocksDoubleReservation()
    {
        var registry = new AgentAccountRegistry();
        registry.Register(Account());
        var now = DateTimeOffset.UnixEpoch;

        var first = registry.Reserve("worker-codex-frontend", "attempt-1", "chief", now, TimeSpan.FromMinutes(5));
        Assert.True(first.FencingToken > 0);
        Assert.Equal(AgentAccountState.Reserved, registry.Get("worker-codex-frontend")!.State);

        var conflict = Assert.Throws<AgentAccountValidationException>(
            () => registry.Reserve("worker-codex-frontend", "attempt-2", "chief", now, TimeSpan.FromMinutes(5)));
        Assert.Equal("account.already_reserved", conflict.Code);
    }

    [Fact]
    public void StaleFencingTokenCannotReleaseTheCurrentLease()
    {
        var registry = new AgentAccountRegistry();
        registry.Register(Account());
        var now = DateTimeOffset.UnixEpoch;
        var lease = registry.Reserve("worker-codex-frontend", "attempt-1", "chief", now, TimeSpan.FromMinutes(5));

        var stale = Assert.Throws<AgentAccountValidationException>(
            () => registry.Release("worker-codex-frontend", lease.FencingToken - 1));
        Assert.Equal("account.fencing_conflict", stale.Code);

        registry.Release("worker-codex-frontend", lease.FencingToken);
        Assert.Equal(AgentAccountState.Available, registry.Get("worker-codex-frontend")!.State);
    }

    [Fact]
    public void ConcurrencyLimitIsEnforced()
    {
        var registry = new AgentAccountRegistry();
        registry.Register(Account(concurrency: 1, active: 1));
        var exhausted = Assert.Throws<AgentAccountValidationException>(
            () => registry.Reserve(
                "worker-codex-frontend", "attempt-1", "chief", DateTimeOffset.UnixEpoch, TimeSpan.FromMinutes(5)));
        Assert.Equal("account.concurrency_exhausted", exhausted.Code);
    }

    [Fact]
    public void QuotaLimitedAccountIsNotReservable()
    {
        var registry = new AgentAccountRegistry();
        registry.Register(Account());
        registry.MarkQuotaLimited(
            "worker-codex-frontend", DateTimeOffset.UnixEpoch.AddHours(1), "quota.exhausted");

        var account = registry.Get("worker-codex-frontend")!;
        Assert.Equal(AgentAccountState.QuotaLimited, account.State);
        Assert.Equal("quota.exhausted", account.FailureReason);
        Assert.Throws<AgentAccountValidationException>(
            () => registry.Reserve(
                "worker-codex-frontend", "attempt-1", "chief", DateTimeOffset.UnixEpoch, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void UnknownExecutorIsRejected()
    {
        var registry = new AgentAccountRegistry();
        var exception = Assert.Throws<AgentAccountValidationException>(
            () => registry.Register(Account(executorId: "not-a-real-cli")));
        Assert.Equal("account.executor_unknown", exception.Code);
    }

    [Theory]
    [InlineData(AgentRoles.ChiefOrchestrator)]
    [InlineData(AgentRoles.FrontendSpecialist)]
    [InlineData(AgentRoles.BackendSpecialist)]
    [InlineData(AgentRoles.Critic)]
    public void CanonicalRuntimeRolesAreAccepted(string role)
    {
        // CAT-09: os quatro papéis canônicos de runtime são o conjunto fechado permitido.
        var registry = new AgentAccountRegistry();
        var account = registry.Register(Account(roles: [role]));
        Assert.Equal([role], account.AllowedRoles);
    }

    [Theory]
    [InlineData("product-owner")]
    [InlineData("data-scientist")]
    [InlineData("security-specialist")]
    [InlineData("chief")]
    public void ArbitraryRuntimeRoleIsRejectedWithTypedReason(string role)
    {
        // CAT-09/ADR-022: um "novo papel de negócio" não pode virar um papel de runtime
        // arbitrário — ele é uma especialidade/persona. A conta é recusada de forma tipada.
        var registry = new AgentAccountRegistry();
        var exception = Assert.Throws<AgentAccountValidationException>(
            () => registry.Register(Account(roles: [role])));
        Assert.Equal("account.role_unknown", exception.Code);
    }

    [Fact]
    public void SuggestedAliasesAreAllValidAndCarryNoIdentity()
    {
        Assert.All(AgentAccountRegistry.SuggestedAliases, AgentAccountRegistry.ValidateAlias);
        Assert.DoesNotContain(AgentAccountRegistry.SuggestedAliases, alias => alias.Contains('@'));
    }

    [Fact]
    public void EveryCatalogedExecutorDeclaresIsolationOrJustifiesItsAbsence()
    {
        // Executores que compartilham o MESMO binário precisam de config home próprio,
        // senão duas contas sobrescreveriam a autenticação uma da outra (CA-3).
        var claude = ExecutorCatalog.Find(ExecutorCatalog.ClaudeCode)!;
        var glm = ExecutorCatalog.Find(ExecutorCatalog.Glm)!;
        Assert.Equal(claude.Command, glm.Command);
        Assert.Equal("CLAUDE_CONFIG_DIR", claude.ConfigHomeEnvironmentVariable);
        Assert.Equal("CLAUDE_CONFIG_DIR", glm.ConfigHomeEnvironmentVariable);
        Assert.Equal("CODEX_HOME", ExecutorCatalog.Find(ExecutorCatalog.Codex)!.ConfigHomeEnvironmentVariable);
    }

    [Fact]
    public void NoExecutorProfileEmbedsASecret()
    {
        // A allowlist nomeia variáveis; valores de segredo nunca vivem no catálogo.
        Assert.All(ExecutorCatalog.All, profile =>
            Assert.All(profile.EnvironmentAllowlist, name =>
                Assert.DoesNotContain("://", name, StringComparison.Ordinal)));
    }
}
