using Harness.Persistence.Abstractions.Agents;
using Harness.Persistence.Abstractions.Identity;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;

namespace Harness.Host.Agents;

/// <summary>
/// Fase 9 — as ESPECIALIDADES do playbook (§4) como dados no `team_specialty_catalog`: as nove
/// personas da fábrica (product-owner, arquiteto, tech-lead, qa, devops, sre-sustentacao,
/// security, dba-dados, dev-executor), cada uma com a fase que lidera e a mentalidade de uma
/// linha do próprio playbook. Idempotente por chave estável: reexecutar não duplica nem
/// sobrescreve curadoria do usuário.
/// </summary>
public sealed class PlaybookSpecialtySeeder(
    ITeamSpecialtyCatalogStore catalog,
    IClock clock)
{
    /// <summary>Chave → (nome, descrição) — o conteúdo vem do playbook §4, não é inventado.</summary>
    public static readonly IReadOnlyList<(string Key, string Name, string Description)> Specialties =
    [
        ("product-owner", "Product Owner",
            "Fases 1★, 2★, 4★(co), 7★(co). Valor de negócio antes de funcionalidade; tradutor cliente↔engenharia."),
        ("arquiteto", "Arquiteto",
            "Fase 3★ (coordena) + consultas 5, 6, 8, 9. Trade-offs, não \"melhor solução\"; não escreve código de produção — decide, documenta e revisa."),
        ("tech-lead", "Tech Lead",
            "Fases 4★(co), 5★ (revisor). Ponte arquitetura↔código; review como ensino; captura ADRs incrementais."),
        ("qa", "QA",
            "Fase 6★ e instrumentação da 7. Qualidade é cultura; projeta teste desde a Fase 3."),
        ("devops", "DevOps",
            "Fase 8★. Deploy é processo industrial; rollback é parte do plano A."),
        ("sre-sustentacao", "SRE / Sustentação",
            "Fase 9★. Guardião do SLO; postmortem blameless gera ação."),
        ("security", "Security",
            "Fases 3★ (threat model), 5, 6, 7, 9. Pensa como atacante; quebra a cadeia de ataque onde dói."),
        ("dba-dados", "DBA / Dados",
            "Fases 3★ (modelo de dados), 5, 6, 8, 9. Dados são ativo; modela por queries e crescimento reais."),
        ("dev-executor", "Dev Executor",
            "Fase 5 (N em paralelo). Constrói dentro do padrão, em worktree isolada com ScopeClaim."),
    ];

    /// <summary>Semeia as especialidades ausentes do tenant. Devolve quantas criou.</summary>
    public async Task<int> EnsureSeededAsync(
        string tenantId, string actorProfileId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorProfileId);
        var existing = await catalog.ListSpecialtiesAsync(tenantId, null, null, 200, cancellationToken);
        var known = new HashSet<string>(
            existing.Select(specialty => specialty.Key),
            StringComparer.OrdinalIgnoreCase);

        var created = 0;
        foreach (var (key, name, description) in Specialties)
        {
            if (known.Contains(key))
            {
                continue;
            }

            var now = clock.UtcNow;
            _ = await catalog.CreateSpecialtyAsync(
                new SpecialtyCreateCommand(
                    tenantId, actorProfileId, UlidValue.New(now).ToString(),
                    key, name, description, TeamId: null, now),
                cancellationToken);
            created++;
        }

        return created;
    }
}

/// <summary>
/// Roda o seed das especialidades do playbook no startup, por tenant, sem nunca impedir o boot
/// (mesmo padrão dos demais seeders idempotentes de inicialização).
/// </summary>
public sealed partial class PlaybookSpecialtySeedHostedService(
    PlaybookSpecialtySeeder seeder,
    ILocalProfileStore profiles,
    ILogger<PlaybookSpecialtySeedHostedService> logger) : IHostedService
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Fase 9: semeadas {Count} especialidade(s) do playbook no tenant {TenantId}.")]
    private static partial void Seeded(ILogger logger, int count, string tenantId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Fase 9: seed de especialidades adiado no startup: {Reason}. Reexecuta no próximo restart.")]
    private static partial void Deferred(ILogger logger, string reason);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            foreach (var group in (await profiles.ListAsync(cancellationToken))
                .GroupBy(profile => profile.TenantId, StringComparer.Ordinal))
            {
                var created = await seeder.EnsureSeededAsync(
                    group.Key, group.First().Id, cancellationToken);
                if (created > 0)
                {
                    Seeded(logger, created, group.Key);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // O seed idempotente nunca impede o boot; tenta de novo no restart.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            Deferred(logger, exception.GetType().Name);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
