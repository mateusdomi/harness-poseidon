using Harness.Modules.Agents.Application.Accounts;
using Harness.Modules.Agents.Contracts;

namespace Harness.UnitTests.Agents;

/// <summary>
/// N4/CA-6: a seleção de contas é EXPLICÁVEL e fail-closed. Cada regra da missão vira uma
/// prova: adapter ausente ⇒ Unavailable, sem login ⇒ AuthenticationRequired, cota esgotada ⇒
/// QuotaLimited, saúde ≠ cota, cota ≠ instalação, concorrência, actor≠critic, conflito de
/// escopo, e a preferência nunca sobrepõe uma recusa.
/// </summary>
public sealed class AgentAccountSchedulerTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);

    private static AgentAccountContract Account(
        string alias,
        string executorId,
        string role,
        AgentAccountState state = AgentAccountState.Available,
        int priority = 100,
        int concurrency = 1,
        int active = 0,
        AgentAccountHealth health = AgentAccountHealth.Healthy,
        DateTimeOffset? cooldownUntil = null,
        IReadOnlyList<string>? pathScopes = null) =>
        new(alias, "provider", executorId, $"keychain://poseidon/{alias}", $"confighome://{alias}",
            [role], pathScopes ?? [], state, health, concurrency, active, null, null,
            cooldownUntil, null, null, priority);

    private static AgentAccountRegistry RegistryOf(params AgentAccountContract[] accounts)
    {
        var registry = new AgentAccountRegistry();
        foreach (var account in accounts)
        {
            registry.Register(account);
        }

        return registry;
    }

    private static AccountSchedulingRequest CriticRequest(string? actorAlias = null) => new()
    {
        Role = AgentRoles.Critic,
        RequiredCapability = "review",
        Now = Now,
        ForCritic = true,
        ActorAlias = actorAlias,
    };

    [Fact]
    public void UnknownExecutorIsRejectedBeforeScheduling()
    {
        // Executor desconhecido é recusado no cadastro, antes de qualquer preferência do scheduler.
        var registry = new AgentAccountRegistry();

        var exception = Assert.Throws<AgentAccountValidationException>(() =>
            registry.Register(Account("worker-sem-adapter", "executor-sem-adapter", AgentRoles.FrontendSpecialist)));

        Assert.Equal("account.executor_unknown", exception.Message);
    }

    [Fact]
    public void InstalledWithoutLoginIsAuthenticationRequired()
    {
        var registry = RegistryOf(
            Account("worker-codex-critic", ExecutorCatalog.Codex, AgentRoles.Critic,
                state: AgentAccountState.AuthenticationRequired));

        var decision = new AgentAccountScheduler().Select(registry, CriticRequest());

        Assert.Null(decision.SelectedAlias);
        Assert.Equal("account.authentication_required", Assert.Single(decision.Candidates).ReasonCode);
    }

    [Fact]
    public void ExhaustedQuotaIsQuotaLimitedUntilResetButUnknownQuotaDoesNotBlock()
    {
        var registry = RegistryOf(
            Account("worker-codex-critic", ExecutorCatalog.Codex, AgentRoles.Critic));

        var exhausted = CriticRequest() with
        {
            Quotas = new Dictionary<string, AccountQuotaSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["worker-codex-critic"] = new AccountQuotaSnapshot(
                    "cli-probe", Now, QuotaStatus.Exhausted, QuotaConfidence.High,
                    RemainingFraction: 0, ResetAt: Now.AddHours(1), StaleAfter: TimeSpan.FromMinutes(5)),
            },
        };
        Assert.Null(new AgentAccountScheduler().Select(registry, exhausted).SelectedAlias);

        // Cota desconhecida (CLI não publica número) NÃO bloqueia: instalação está provada e o
        // limite conservador fica na concorrência. Nunca se inventa percentual.
        var unknown = CriticRequest() with
        {
            Quotas = new Dictionary<string, AccountQuotaSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["worker-codex-critic"] = AccountQuotaSnapshot.UnknownFrom("cli-probe", Now, TimeSpan.FromMinutes(5)),
            },
        };
        Assert.Equal("worker-codex-critic", new AgentAccountScheduler().Select(registry, unknown).SelectedAlias);
    }

    [Fact]
    public void HealthIsNotQuotaAndADegradedButInstalledAccountStillRuns()
    {
        // Saúde reduzida é operacional: elegível, apenas não preferida a igual prioridade.
        var registry = RegistryOf(
            Account("worker-codex-critic", ExecutorCatalog.Codex, AgentRoles.Critic,
                state: AgentAccountState.Degraded, health: AgentAccountHealth.Degraded));

        var decision = new AgentAccountScheduler().Select(registry, CriticRequest());

        Assert.Equal("worker-codex-critic", decision.SelectedAlias);
        Assert.Equal("account.eligible_degraded", Assert.Single(decision.Candidates).ReasonCode);
    }

    [Fact]
    public void AnAccountNearTheQuotaLimitLosesToOneWithHeadroomButIsNeverDiscarded()
    {
        // O contrato já dizia "roteamento deve preferir alternativa" para NearLimit e NADA
        // consultava o estado: a conta a ponto de esgotar competia de igual para igual e, ganhando
        // por prioridade, morria no meio da tentativa.
        var near = RegistryOf(
            Account("worker-a-near", ExecutorCatalog.Codex, AgentRoles.Critic, priority: 200),
            Account("worker-b-full", ExecutorCatalog.Codex, AgentRoles.Critic, priority: 200));
        var request = CriticRequest() with
        {
            Quotas = new Dictionary<string, AccountQuotaSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["worker-a-near"] = new(
                    "cli-probe", Now, QuotaStatus.NearLimit, QuotaConfidence.High,
                    0.05, null, TimeSpan.FromMinutes(5)),
            },
        };

        var decision = new AgentAccountScheduler().Select(near, request);

        Assert.Equal("worker-b-full", decision.SelectedAlias);
        // Perder a vez não é ser descartada: ela continua no fallback, com o motivo explícito.
        Assert.Equal(["worker-a-near"], decision.FallbackAliases);
        Assert.Equal(
            "account.eligible_near_limit",
            decision.Candidates.Single(c => c.Alias == "worker-a-near").ReasonCode);
    }

    [Fact]
    public void BeingTheOnlyAccountNearTheLimitStillRunsTheWork()
    {
        // Bloquear seria descartar capacidade real e parar trabalho curto/crítico.
        var registry = RegistryOf(
            Account("worker-codex-critic", ExecutorCatalog.Codex, AgentRoles.Critic));
        var request = CriticRequest() with
        {
            Quotas = new Dictionary<string, AccountQuotaSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["worker-codex-critic"] = new(
                    "cli-probe", Now, QuotaStatus.NearLimit, QuotaConfidence.High,
                    0.05, null, TimeSpan.FromMinutes(5)),
            },
        };

        Assert.Equal("worker-codex-critic", new AgentAccountScheduler().Select(registry, request).SelectedAlias);
    }

    [Fact]
    public void AStaleNearLimitMeasurementDoesNotDePreferAnyone()
    {
        // Sem proveniência fresca não há sinal — a mesma regra que impede bloquear por dado velho.
        var registry = RegistryOf(
            Account("worker-a-near", ExecutorCatalog.Codex, AgentRoles.Critic, priority: 200),
            Account("worker-b-full", ExecutorCatalog.Codex, AgentRoles.Critic, priority: 200));
        var request = CriticRequest() with
        {
            Quotas = new Dictionary<string, AccountQuotaSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["worker-a-near"] = new(
                    "cli-probe", Now.AddHours(-2), QuotaStatus.NearLimit, QuotaConfidence.Low,
                    0.05, null, TimeSpan.FromMinutes(5)),
            },
        };

        var decision = new AgentAccountScheduler().Select(registry, request);

        // Empate real: vence o desempate determinístico por alias, não o dado vencido.
        Assert.Equal("worker-a-near", decision.SelectedAlias);
    }

    [Fact]
    public void ANarrowerScopeIsAcceptedInsideAnAllowedRoot()
    {
        // Achado ao vivo: o escopo permitido da conta é uma RAIZ de trabalho, mas a comparação era
        // por igualdade — então o card que pedia `frontend/src/features/approvals/**`, mais
        // estreito e portanto mais seguro, era recusado com `path_scope_not_allowed`. Quatro cards
        // de módulos diferentes ficaram adiados a cada ciclo com a frota inteira disponível.
        var registry = RegistryOf(
            Account("worker-codex-frontend", ExecutorCatalog.Codex, AgentRoles.FrontendSpecialist,
                pathScopes: ["frontend/**", "docs/frontend/**"]));

        var decision = new AgentAccountScheduler().Select(registry, new AccountSchedulingRequest
        {
            Role = AgentRoles.FrontendSpecialist,
            RequiredCapability = "code",
            Now = Now,
            RequiredPathScopes = ["frontend/src/features/approvals/**"],
        });

        Assert.Equal("worker-codex-frontend", decision.SelectedAlias);
    }

    [Fact]
    public void AScopeOutsideEveryAllowedRootIsStillRefused()
    {
        // Aceitar o mais estreito não pode virar aceitar qualquer coisa.
        var registry = RegistryOf(
            Account("worker-codex-frontend", ExecutorCatalog.Codex, AgentRoles.FrontendSpecialist,
                pathScopes: ["frontend/**"]));

        var decision = new AgentAccountScheduler().Select(registry, new AccountSchedulingRequest
        {
            Role = AgentRoles.FrontendSpecialist,
            RequiredCapability = "code",
            Now = Now,
            RequiredPathScopes = ["src/Modules/Harness.Modules.Agents/**"],
        });

        Assert.Null(decision.SelectedAlias);
        Assert.Equal("account.path_scope_not_allowed", Assert.Single(decision.Candidates).ReasonCode);
    }

    [Fact]
    public void AnAccountNeverRunsAboveItsConcurrencyLimit()
    {
        var registry = RegistryOf(
            Account("worker-codex-critic", ExecutorCatalog.Codex, AgentRoles.Critic,
                state: AgentAccountState.Running, concurrency: 1, active: 1));

        var decision = new AgentAccountScheduler().Select(registry, CriticRequest());

        Assert.Null(decision.SelectedAlias);
        Assert.Equal("account.concurrency_exhausted", Assert.Single(decision.Candidates).ReasonCode);
    }

    [Fact]
    public void TheSameAccountIsNeverActorAndCritic()
    {
        var registry = RegistryOf(
            Account("worker-antigravity-review", ExecutorCatalog.Antigravity, AgentRoles.Critic, priority: 90));

        var decision = new AgentAccountScheduler().Select(
            registry, CriticRequest(actorAlias: "worker-antigravity-review"));

        Assert.Null(decision.SelectedAlias);
        Assert.Equal("account.actor_cannot_be_critic", Assert.Single(decision.Candidates).ReasonCode);
    }

    [Fact]
    public void APathScopeConflictPreventsScheduling()
    {
        // O actor de backend não pode reivindicar frontend/**: escopo não permitido ⇒ recusa.
        var registry = RegistryOf(
            Account("worker-claude-secondary", ExecutorCatalog.ClaudeCode, AgentRoles.BackendSpecialist,
                pathScopes: []));

        var decision = new AgentAccountScheduler().Select(
            registry,
            new AccountSchedulingRequest
            {
                Role = AgentRoles.BackendSpecialist,
                RequiredCapability = "code",
                Now = Now,
                RequiredPathScopes = ["frontend/**"],
            });

        Assert.Null(decision.SelectedAlias);
        Assert.Equal("account.path_scope_not_allowed", Assert.Single(decision.Candidates).ReasonCode);
    }

    [Fact]
    public void PreferenceNeverOverridesFailClosedLowerPriorityEligibleWins()
    {
        // Antigravity (prio 90) é o critic preferido, mas está sem login; o Codex critic
        // (prio 80) elegível é escolhido. A preferência não promove o inelegível.
        var registry = RegistryOf(
            Account("worker-antigravity-review", ExecutorCatalog.Antigravity, AgentRoles.Critic,
                state: AgentAccountState.AuthenticationRequired, priority: 90),
            Account("worker-codex-critic", ExecutorCatalog.Codex, AgentRoles.Critic, priority: 80));

        var decision = new AgentAccountScheduler().Select(registry, CriticRequest());

        Assert.Equal("worker-codex-critic", decision.SelectedAlias);
        Assert.Empty(decision.FallbackAliases);
        var blocked = decision.Candidates.Single(c => c.Alias == "worker-antigravity-review");
        Assert.False(blocked.Eligible);
        Assert.Equal("account.authentication_required", blocked.ReasonCode);
    }

    [Fact]
    public void ThePreferredEligibleWinsAndTheRestBecomeOrderedFallbacks()
    {
        var registry = RegistryOf(
            Account("worker-antigravity-review", ExecutorCatalog.Antigravity, AgentRoles.Critic, priority: 90),
            Account("worker-codex-critic", ExecutorCatalog.Codex, AgentRoles.Critic, priority: 80));

        var decision = new AgentAccountScheduler().Select(registry, CriticRequest());

        Assert.Equal("worker-antigravity-review", decision.SelectedAlias);
        Assert.Equal(["worker-codex-critic"], decision.FallbackAliases);
    }

    [Fact]
    public void WhenThePreferredCriticIsDrainedByQuotaTheSchedulerHandsOffToTheFallback()
    {
        // 5.2: cota esgotada na conta preferida ⇒ o scheduler seleciona o fallback tipado,
        // sem duplicar nem inventar. O estado QuotaLimited persistido é respeitado.
        var registry = RegistryOf(
            Account("worker-antigravity-review", ExecutorCatalog.Antigravity, AgentRoles.Critic, priority: 90),
            Account("worker-codex-critic", ExecutorCatalog.Codex, AgentRoles.Critic, priority: 80));
        var scheduler = new AgentAccountScheduler();

        Assert.Equal("worker-antigravity-review", scheduler.Select(registry, CriticRequest()).SelectedAlias);

        // Drain por cota: a conta preferida entra em cooldown até o reset da janela.
        registry.MarkQuotaLimited("worker-antigravity-review", Now.AddHours(1), "quota.exhausted");

        var afterDrain = scheduler.Select(registry, CriticRequest());
        Assert.Equal("worker-codex-critic", afterDrain.SelectedAlias);
        Assert.Equal(
            "account.quota_limited",
            afterDrain.Candidates.Single(c => c.Alias == "worker-antigravity-review").ReasonCode);
    }

    [Fact]
    public void ARoleMismatchIsNeverScheduledEvenIfEverythingElseIsHealthy()
    {
        var registry = RegistryOf(
            Account("chief-claude-primary", ExecutorCatalog.ClaudeCode, AgentRoles.ChiefOrchestrator));

        var decision = new AgentAccountScheduler().Select(registry, CriticRequest());

        Assert.Null(decision.SelectedAlias);
        Assert.Equal("account.role_not_allowed", Assert.Single(decision.Candidates).ReasonCode);
    }

    [Fact]
    public void AnExhaustedAccountNeverReceivesAnAttemptEvenAsTheOnlyCandidate()
    {
        var registry = RegistryOf(
            Account("worker-codex-critic", ExecutorCatalog.Codex, AgentRoles.Critic));
        var request = CriticRequest() with
        {
            Quotas = new Dictionary<string, AccountQuotaSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["worker-codex-critic"] = new(
                    "cli-probe", Now, QuotaStatus.Exhausted, QuotaConfidence.High,
                    RemainingFraction: 0, ResetAt: Now.AddHours(1), StaleAfter: TimeSpan.FromMinutes(5)),
            },
        };

        var decision = new AgentAccountScheduler().Select(registry, request);

        Assert.Null(decision.SelectedAlias);
        Assert.Empty(decision.FallbackAliases);
        Assert.Equal(
            "account.quota_limited",
            decision.Candidates.Single(c => c.Alias == "worker-codex-critic").ReasonCode);
    }

    [Fact]
    public void AStaleAvailableQuotaSnapshotNeverProvesAvailabilityOverARealBlock()
    {
        // Uma medição VENCIDA não é prova de nada — nem para bloquear (já coberto por
        // AStaleNearLimitMeasurementDoesNotDePreferAnyone), nem para "provar" que a conta tem
        // margem. O bloqueio real (aqui, autenticação pendente) nunca é destravado por uma
        // leitura antiga de cota, mesmo que ela diga "Available".
        var registry = RegistryOf(
            Account("worker-codex-critic", ExecutorCatalog.Codex, AgentRoles.Critic,
                state: AgentAccountState.AuthenticationRequired));
        var staleAvailable = CriticRequest() with
        {
            Quotas = new Dictionary<string, AccountQuotaSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["worker-codex-critic"] = new(
                    "cli-probe", Now.AddDays(-1), QuotaStatus.Available, QuotaConfidence.Low,
                    RemainingFraction: 0.9, ResetAt: null, StaleAfter: TimeSpan.FromMinutes(5)),
            },
        };

        var decision = new AgentAccountScheduler().Select(registry, staleAvailable);

        Assert.Null(decision.SelectedAlias);
        Assert.Equal(
            "account.authentication_required",
            Assert.Single(decision.Candidates).ReasonCode);
    }

    [Fact]
    public void AStaleAvailableQuotaSnapshotNeverProvesAvailabilityOverConcurrency()
    {
        // Mesmo cenário, do lado da concorrência: a conta está ocupada de verdade (ActiveAttempts
        // no limite); uma leitura antiga de cota "Available" não pode destravar um slot que não
        // existe.
        var registry = RegistryOf(
            Account("worker-codex-critic", ExecutorCatalog.Codex, AgentRoles.Critic,
                state: AgentAccountState.Running, concurrency: 1, active: 1));
        var staleAvailable = CriticRequest() with
        {
            Quotas = new Dictionary<string, AccountQuotaSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["worker-codex-critic"] = new(
                    "cli-probe", Now.AddDays(-1), QuotaStatus.Available, QuotaConfidence.Low,
                    RemainingFraction: 0.9, ResetAt: null, StaleAfter: TimeSpan.FromMinutes(5)),
            },
        };

        var decision = new AgentAccountScheduler().Select(registry, staleAvailable);

        Assert.Null(decision.SelectedAlias);
        Assert.Equal(
            "account.concurrency_exhausted",
            Assert.Single(decision.Candidates).ReasonCode);
    }

    [Fact]
    public void UnknownQuotaAppliesTheLocalConcurrencyLimitInsteadOfEstimatingRemainingFraction()
    {
        // account.eligible_* nunca nasce de um percentual inventado: UnknownFrom nem carrega
        // RemainingFraction. A política conservadora para Unknown é o limite local de
        // concorrência — quando ele já está no teto, é ESSA razão que bloqueia, nunca uma cota
        // fabricada.
        var unknownQuota = AccountQuotaSnapshot.UnknownFrom("cli-probe", Now, TimeSpan.FromMinutes(5));
        Assert.Null(unknownQuota.RemainingFraction);

        var registry = RegistryOf(
            Account("worker-codex-critic", ExecutorCatalog.Codex, AgentRoles.Critic,
                state: AgentAccountState.Running, concurrency: 1, active: 1));
        var request = CriticRequest() with
        {
            Quotas = new Dictionary<string, AccountQuotaSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["worker-codex-critic"] = unknownQuota,
            },
        };

        var decision = new AgentAccountScheduler().Select(registry, request);

        Assert.Null(decision.SelectedAlias);
        Assert.Equal(
            "account.concurrency_exhausted",
            Assert.Single(decision.Candidates).ReasonCode);
    }

    [Fact]
    public void TheRouteDecisionRecordsWhichQuotaSnapshotWasUsedForTheSelectedAccount()
    {
        // Contrato de quota (N4/5.1): "a decisão de rota registra o snapshot usado". Antes desta
        // fatia, AccountSelectionCandidate/Decision não carregavam o snapshot considerado —
        // a decisão era explicável por código de razão, mas não auditável até a medição concreta.
        var registry = RegistryOf(
            Account("worker-codex-critic", ExecutorCatalog.Codex, AgentRoles.Critic));
        var snapshot = new AccountQuotaSnapshot(
            "cli-probe", Now, QuotaStatus.NearLimit, QuotaConfidence.High,
            0.05, null, TimeSpan.FromMinutes(5));
        var request = CriticRequest() with
        {
            Quotas = new Dictionary<string, AccountQuotaSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["worker-codex-critic"] = snapshot,
            },
        };

        var decision = new AgentAccountScheduler().Select(registry, request);

        Assert.Equal("worker-codex-critic", decision.SelectedAlias);
        Assert.Equal(snapshot, decision.SelectedQuotaSnapshot);
        Assert.Equal(snapshot, Assert.Single(decision.Candidates).QuotaSnapshotUsed);
    }

    [Fact]
    public void TheRouteDecisionRecordsNoSnapshotWhenNoneWasObserved()
    {
        var registry = RegistryOf(
            Account("worker-codex-critic", ExecutorCatalog.Codex, AgentRoles.Critic));

        var decision = new AgentAccountScheduler().Select(registry, CriticRequest());

        Assert.Equal("worker-codex-critic", decision.SelectedAlias);
        Assert.Null(decision.SelectedQuotaSnapshot);
        Assert.Null(Assert.Single(decision.Candidates).QuotaSnapshotUsed);
    }

    [Fact]
    public async Task ConcurrentReservationsNeverExceedTheAccountConcurrencyLimit()
    {
        // Regressão do defeito real: Reserve() fazia "checar concessão/limite, escrever de volta"
        // sem lock sobre um Dictionary comum. Sob concorrência real (tentativas distintas
        // disputando a MESMA conta), duas reservas podiam passar a checagem antes de qualquer uma
        // escrever — e mais de uma tentativa acabava concedida para uma conta de concorrência 1,
        // exatamente o invariante que o scheduler existe para impor. O teste dispara muitas
        // reservas simultâneas, uma Task por tentativa (nunca Parallel.For, cujo grau de
        // paralelismo é limitado ao número de núcleos e travaria contra a barreira em qualquer
        // máquina com menos de `attempts` núcleos lógicos), alinhadas por barreira para maximizar
        // a chance de colisão, e prova que exatamente UMA é concedida e o estado final da conta é
        // consistente com ela.
        const int attempts = 64;

        var registry = new AgentAccountRegistry();
        registry.Register(Account(
            "worker-codex-critic", ExecutorCatalog.Codex, AgentRoles.Critic, concurrency: 1));

        using var barrier = new Barrier(attempts);
        var granted = new bool[attempts];

        var workers = Enumerable.Range(0, attempts).Select(index => Task.Run(() =>
        {
            barrier.SignalAndWait();
            try
            {
                registry.Reserve(
                    "worker-codex-critic", $"attempt-{index}", $"owner-{index}", Now, TimeSpan.FromMinutes(5));
                granted[index] = true;
            }
            catch (AgentAccountValidationException exception) when (
                exception.Code is "account.concurrency_exhausted" or "account.already_reserved")
            {
                granted[index] = false;
            }
        }));
        await Task.WhenAll(workers);

        var grantedCount = granted.Count(value => value);
        Assert.Equal(1, grantedCount);

        var account = registry.Get("worker-codex-critic")!;
        Assert.Equal(1, account.ActiveAttempts);
        Assert.Equal(AgentAccountState.Reserved, account.State);
    }
}
