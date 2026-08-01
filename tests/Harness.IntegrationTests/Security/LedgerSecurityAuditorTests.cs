using Harness.Host.Security;
using Harness.Modules.Tools.Application;
using Harness.Modules.Tools.Domain;
using Harness.Persistence.Abstractions.Governance;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Sqlite;
using Harness.SharedKernel.Identifiers;

namespace Harness.IntegrationTests.Security;

/// <summary>
/// Prova o critério de aceite da Fase 4 no lado do PEP: ferramenta pedida fora do perfil é
/// BLOQUEADA (a ferramenta interna nunca roda) e AUDITADA no `audit_ledger` append-only, com
/// detalhe sem entrada da ferramenta nem segredo, e a cadeia de hash permanece válida. Autorização
/// legítima também é registrada — o ledger responde "quem pôde o quê, quando".
/// </summary>
public sealed class LedgerSecurityAuditorTests
{
    private const string ToolId = "code-editor";

    [Fact]
    public async Task ChiefToolExecutionIsDeniedAndBothDecisionsReachTheLedger()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "integration-artifacts",
            $"capability-audit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var dispatcher = await SqliteWriteDispatcher.CreateAsync(Path.Combine(root, "security.db"));
            await using (dispatcher)
            {
                await SqliteMigrationRunner.ApplyAsync(dispatcher, timeout.Token);
                var now = DateTimeOffset.UtcNow;
                var tenantId = await EnsureTenantAsync(dispatcher, now, timeout.Token);

                var ledger = new SqliteAuditEventStore(dispatcher);
                var auditor = new LedgerSecurityAuditor(ledger);
                var pep = new SecurityPolicyEnforcementPoint(auditor);

                var projectId = UlidValue.New(now.AddMilliseconds(2)).ToString();
                var cardId = UlidValue.New(now.AddMilliseconds(3)).ToString();
                var attemptId = UlidValue.New(now.AddMilliseconds(4)).ToString();
                var chiefId = UlidValue.New(now.AddMilliseconds(5)).ToString();
                var specialistId = UlidValue.New(now.AddMilliseconds(6)).ToString();

                var inner = new RecordingExecutor();
                var executor = new PolicyCheckedToolExecutor<string, string>(inner, pep);
                var descriptor = new ToolPolicyDescriptor(ToolId, true, ToolRiskTier.Critical, "{}", "{}");
                var context = new ToolPolicyContext(
                    "Development",
                    ToolRiskTier.Critical,
                    new HashSet<string> { ToolId },
                    SandboxActive: true);

                CapabilityToken Grant(CapabilityActorKind kind, string actorId) => pep.Issue(
                    new CapabilityGrantRequest(
                        kind,
                        actorId,
                        tenantId,
                        projectId,
                        cardId,
                        attemptId,
                        CapabilityOperation.ToolExecution,
                        [ToolId],
                        [ToolId],
                        ["src/**"],
                        now.AddMinutes(30),
                        7));

                CapabilityAuthorizationRequest Authorization(CapabilityActorKind kind, string actorId) => new(
                    kind,
                    actorId,
                    tenantId,
                    projectId,
                    cardId,
                    attemptId,
                    CapabilityOperation.ToolExecution,
                    ToolId,
                    ToolId,
                    "src/Harness.Host/Program.cs",
                    7);

                // A Bruna pedindo execução: negada pelo perfil, sem tocar a ferramenta.
                var denied = await Assert.ThrowsAsync<CapabilityDeniedException>(() => executor.ExecuteAsync(
                    new PolicyCheckedToolInvocation<string>(
                        "escrever arquivo",
                        descriptor,
                        context,
                        ToolRiskTier.Medium,
                        Grant(CapabilityActorKind.Chief, chiefId),
                        Authorization(CapabilityActorKind.Chief, chiefId)),
                    timeout.Token));

                Assert.Equal("chief_execution_denied", denied.Decision.Code);
                Assert.Equal(0, inner.CallCount);

                // O mesmo pedido por um especialista: autorizado e igualmente registrado.
                Assert.Equal(
                    "ESCREVER ARQUIVO",
                    await executor.ExecuteAsync(
                        new PolicyCheckedToolInvocation<string>(
                            "escrever arquivo",
                            descriptor,
                            context,
                            ToolRiskTier.Medium,
                            Grant(CapabilityActorKind.Specialist, specialistId),
                            Authorization(CapabilityActorKind.Specialist, specialistId)),
                        timeout.Token));
                Assert.Equal(1, inner.CallCount);

                var events = await ledger.ListAsync(new AuditEventQuery(tenantId, null, 50), timeout.Token);
                var denialEntry = Assert.Single(events, entry => entry.Action == "capability.denied");
                Assert.Equal("chief", denialEntry.ActorKind);
                Assert.Equal(chiefId, denialEntry.ActorId);
                Assert.Equal("work_task", denialEntry.TargetType);
                Assert.Equal(cardId, denialEntry.TargetId);
                Assert.Contains("code=chief_execution_denied", denialEntry.Detail);
                Assert.Contains($"tool={ToolId}", denialEntry.Detail);
                Assert.Contains("operation=ToolExecution", denialEntry.Detail);
                Assert.DoesNotContain("escrever arquivo", denialEntry.Detail);

                var allowedEntry = Assert.Single(events, entry => entry.Action == "capability.allowed");
                Assert.Equal("agent", allowedEntry.ActorKind);
                Assert.Equal(specialistId, allowedEntry.ActorId);

                Assert.True((await ledger.VerifyIntegrityAsync(tenantId, timeout.Token)).Valid);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    internal static async Task<string> EnsureTenantAsync(
        SqliteWriteDispatcher dispatcher,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var profiles = new SqliteLocalProfileStore(dispatcher);
        var created = await profiles.CreateAsync(
            new LocalProfileCreateCommand(
                UlidValue.New(now).ToString(),
                "Personal",
                UlidValue.New(now.AddMilliseconds(1)).ToString(),
                "Mateus",
                null,
                null,
                "pt-BR",
                now),
            cancellationToken);
        return created.Status == LocalProfileMutationStatus.Applied
            ? created.Profile!.TenantId
            : (await profiles.ListAsync(cancellationToken))[0].TenantId;
    }

    private sealed class RecordingExecutor : IToolExecutor<string, string>
    {
        public string ToolId => LedgerSecurityAuditorTests.ToolId;

        public int CallCount { get; private set; }

        public Task<string> ExecuteAsync(string input, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(input.ToUpperInvariant());
        }
    }

}
