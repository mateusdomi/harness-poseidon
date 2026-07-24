using Harness.Modules.Architecture.Domain;
using Harness.Persistence.Abstractions.Architecture;
using Harness.Persistence.Abstractions.Projects;
using Harness.SharedKernel.Time;

namespace Harness.Host.Architecture;

/// <summary>Contagem do que este seed efetivamente criou (0/0 quando já semeado).</summary>
public sealed record ArchitectureSelfMapSeedResult(int ElementsCreated, int RelationshipsCreated)
{
    public static ArchitectureSelfMapSeedResult None { get; } = new(0, 0);

    public bool ChangedAnything => ElementsCreated > 0 || RelationshipsCreated > 0;
}

/// <summary>
/// UX-PROTO/ARCH: a tela <c>Architecture Hub</c> (<c>/architecture</c>) do projeto Poseidon nasce
/// VAZIA (0 elementos) porque ninguém nunca cadastrou a arquitetura do próprio Poseidon — o dono
/// quer poder abrir e ver o produto já mapeado, para testar. RN-03 já registrou o front como
/// protótipo; aqui, de forma HONESTA e derivada da estrutura REAL do repositório
/// (<c>src/</c>, <c>src/Modules/</c>, <c>frontend/</c>), semeamos o MODELO estruturado do Hub —
/// elementos + relacionamentos vigentes (<c>state='implemented'</c>) — via o mesmo
/// <see cref="IArchitectureStore"/> usado por ARC-01/02/03. Sem contrato novo, sem tabela nova.
///
/// Gateado APENAS por <c>AgentRuns.Enabled</c> (mesmo padrão de
/// <c>ProjectStateConvergenceSeeder</c>/<c>ChiefCliProviderCatalogSeeder</c>), roda no startup por
/// tenant e é IDEMPOTENTE: todos os ids são FIXOS e cada linha só é criada quando ainda não existe,
/// então rodar duas vezes não duplica. Se o projeto Poseidon não existe no tenant, é um no-op
/// silencioso (nada de arquitetura órfã).
///
/// O que é modelado — e de onde deriva (NADA inventado):
/// <list type="bullet">
///   <item><b>Poseidon</b> (system) — o produto/control plane (raiz do repo).</item>
///   <item><b>Harness.Host</b> (container) — a API/control plane ASP.NET (<c>src/Harness.Host</c>).</item>
///   <item><b>Frontend</b> (container) — a SPA React (<c>frontend/</c>).</item>
///   <item><b>Fleet de execução de agentes (CLI)</b> (container) — runner + launcher que executam
///     agentes via CLI (<c>src/Harness.Runner</c>, <c>src/Harness.Launcher</c>).</item>
///   <item><b>Persistência (SQLite/Postgres)</b> (dataStore) — store dual
///     (<c>src/Harness.Persistence.*</c>).</item>
///   <item><b>Módulos de domínio</b> (component) — um por diretório real em <c>src/Modules/</c>:
///     Coordination, Conversations, Delivery, Architecture, Governance, Providers, Agents,
///     Workflows, Projects.</item>
/// </list>
/// Relacionamentos reais: o system Poseidon <c>contains</c> os 4 containers/dataStore; o Host
/// <c>contains</c> cada módulo de domínio; o Frontend <c>calls</c> o Host (REST <c>/api/v1</c>); o
/// Host <c>depends-on</c> a Persistência; o Host <c>uses</c> o Fleet (orquestra runs de agente).
/// </summary>
public sealed class ArchitectureSelfMapSeeder(
    IArchitectureStore store, IProjectStore projects, IClock clock)
{
    /// <summary>O projeto "Poseidon" cujo Hub esta semeadura preenche (mesmo id de RN-03).</summary>
    public const string PoseidonProjectId = "01KY36JQ8A48Q2TVYJMA5Q1N4F";

    // Prefixo ULID-shaped (26 chars, alfabeto Crockford) compartilhado por todos os ids fixos deste
    // mapa. O sufixo E##### / R##### garante unicidade e idempotência (ids estáveis entre restarts).
    private const string IdPrefix = "01KY36JQ8A48Q2TVYJMA";

    // Ids fixos dos elementos ------------------------------------------------------------------------
    private const string PoseidonId = IdPrefix + "E00001";
    private const string HostId = IdPrefix + "E00002";
    private const string FrontendId = IdPrefix + "E00003";
    private const string FleetId = IdPrefix + "E00004";
    private const string PersistenceId = IdPrefix + "E00005";

    private static readonly IReadOnlyDictionary<string, string> Empty =
        new Dictionary<string, string>(StringComparer.Ordinal);

    // Os elementos "topo" do modelo, derivados 1:1 de diretórios reais do repositório.
    private static readonly IReadOnlyList<SeedElement> TopElements =
    [
        new(PoseidonId, ArchitectureKinds.SystemKind, "Poseidon",
            "Control plane .NET com execução durável de agentes, contratos tipados e proof gates. " +
            "O produto como um todo (raiz do repositório).", "repo"),
        new(HostId, "container", "Harness.Host (API / Control Plane)",
            "Host ASP.NET que expõe a API /api/v1, SignalR e os hosted services do produto.",
            "src/Harness.Host"),
        new(FrontendId, "container", "Frontend (React SPA)",
            "Single-page application React que consome a API do Host e desenha as telas do produto.",
            "frontend"),
        new(FleetId, "container", "Fleet de execução de agentes (CLI)",
            "Runner e launcher que executam agentes externos via CLI, orquestrados pelo Host.",
            "src/Harness.Runner, src/Harness.Launcher"),
        new(PersistenceId, "dataStore", "Persistência (SQLite/Postgres)",
            "Store durável dual: SQLite local e Postgres, com a mesma abstração de persistência.",
            "src/Harness.Persistence.*"),
    ];

    // Módulos de domínio: um component por diretório REAL em src/Modules/Harness.Modules.<Nome>.
    // Conjunto curado dos módulos centrais (menos é mais); cada um existe no repo.
    private static readonly IReadOnlyList<(string Suffix, string Name)> DomainModules =
    [
        ("E00006", "Coordination"),
        ("E00007", "Conversations"),
        ("E00008", "Delivery"),
        ("E00009", "Architecture"),
        ("E00010", "Governance"),
        ("E00011", "Providers"),
        ("E00012", "Agents"),
        ("E00013", "Workflows"),
        ("E00014", "Projects"),
    ];

    private readonly IArchitectureStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IProjectStore _projects = projects ?? throw new ArgumentNullException(nameof(projects));
    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <summary>
    /// Semeia (uma única vez) o mapa de arquitetura do Poseidon dentro de um tenant. Idempotente e
    /// seguro: se o projeto Poseidon não existe no tenant, retorna
    /// <see cref="ArchitectureSelfMapSeedResult.None"/> sem efeitos.
    /// </summary>
    public async Task<ArchitectureSelfMapSeedResult> EnsureSeededAsync(
        string tenantId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        var project = await _projects.GetAsync(tenantId, PoseidonProjectId, cancellationToken);
        if (project is null)
        {
            return ArchitectureSelfMapSeedResult.None;
        }

        var elementsCreated = 0;
        foreach (var element in TopElements)
        {
            elementsCreated += await EnsureElementAsync(
                tenantId, element.Id, element.Kind, element.Name, element.Description,
                element.Source, cancellationToken);
        }

        foreach (var (suffix, name) in DomainModules)
        {
            elementsCreated += await EnsureElementAsync(
                tenantId, IdPrefix + suffix, "component", $"Módulo {name}",
                $"Módulo de domínio {name} do Poseidon (composto pelo Host).",
                $"src/Modules/Harness.Modules.{name}", cancellationToken);
        }

        var relationshipsCreated = 0;

        // system Poseidon CONTÉM os 4 containers/dataStore de topo.
        foreach (var (suffix, targetId) in new[]
                 {
                     ("R00001", HostId), ("R00002", FrontendId),
                     ("R00003", FleetId), ("R00004", PersistenceId),
                 })
        {
            relationshipsCreated += await EnsureRelationshipAsync(
                tenantId, IdPrefix + suffix, PoseidonId, targetId, "contains", cancellationToken);
        }

        // Host CONTÉM cada módulo de domínio (os módulos são compostos pelo Host).
        var relIndex = 5;
        foreach (var (moduleSuffix, _) in DomainModules)
        {
            var relSuffix = "R" + relIndex.ToString("D5", System.Globalization.CultureInfo.InvariantCulture);
            relationshipsCreated += await EnsureRelationshipAsync(
                tenantId, IdPrefix + relSuffix, HostId, IdPrefix + moduleSuffix, "contains", cancellationToken);
            relIndex++;
        }

        // Arestas de integração reais entre os containers.
        relationshipsCreated += await EnsureRelationshipAsync(
            tenantId, IdPrefix + "R00014", FrontendId, HostId, "calls", cancellationToken);
        relationshipsCreated += await EnsureRelationshipAsync(
            tenantId, IdPrefix + "R00015", HostId, PersistenceId, "depends-on", cancellationToken);
        relationshipsCreated += await EnsureRelationshipAsync(
            tenantId, IdPrefix + "R00016", HostId, FleetId, "uses", cancellationToken);

        return new ArchitectureSelfMapSeedResult(elementsCreated, relationshipsCreated);
    }

    private async Task<int> EnsureElementAsync(
        string tenantId, string id, string kind, string name, string description, string source,
        CancellationToken cancellationToken)
    {
        var existing = await _store.GetElementAsync(tenantId, id, cancellationToken);
        if (existing is not null)
        {
            return 0;
        }

        var now = _clock.UtcNow;
        var properties = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["source"] = source,
            ["seed"] = "poseidon-self-map",
        };
        await _store.CreateElementAsync(
            new ArchitectureElementRecord(
                tenantId, id, PoseidonProjectId, kind, name, description, properties,
                ArchitectureKinds.Implemented, false, 1, null, null, null, now, now),
            cancellationToken);
        return 1;
    }

    private async Task<int> EnsureRelationshipAsync(
        string tenantId, string id, string sourceId, string targetId, string kind,
        CancellationToken cancellationToken)
    {
        var existing = await _store.GetRelationshipAsync(tenantId, id, cancellationToken);
        if (existing is not null)
        {
            return 0;
        }

        var now = _clock.UtcNow;
        await _store.CreateRelationshipAsync(
            new ArchitectureRelationshipRecord(
                tenantId, id, PoseidonProjectId, sourceId, targetId, kind, Empty,
                ArchitectureKinds.Implemented, 1, null, null, null, now, now),
            cancellationToken);
        return 1;
    }

    private sealed record SeedElement(
        string Id, string Kind, string Name, string Description, string Source);
}
