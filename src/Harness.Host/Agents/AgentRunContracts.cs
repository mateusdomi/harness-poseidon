using System.Text.Json.Serialization;
using Harness.Modules.Agents.Application.Accounts;
using Harness.Modules.Agents.Contracts;
using Harness.Modules.Agents.Application.Execution.External;
using Harness.Modules.Governance.Coordination;
using Harness.Persistence.Abstractions.AttemptWorkspaces;

namespace Harness.Host.Agents;

/// <summary>
/// Configuração do bootstrap governado de agentes (CA-5), seção `Harness:AgentRuns`.
///
/// Nasce DESLIGADO: um produto recém-instalado não executa agente externo sem que o
/// operador declare a raiz controlada e habilite o recurso.
/// </summary>
public sealed record AgentRunSettings
{
    public bool Enabled { get; init; }

    /// <summary>Raiz dos perfis isolados por conta (CA-3). Nunca dentro do repositório.</summary>
    public string? ProfilesRoot { get; init; }

    /// <summary>Raiz controlada onde worktrees de tentativa podem existir.</summary>
    public string? ControlledRoot { get; init; }

    /// <summary>Arquivo local de contas; ausente usa os aliases canônicos de ADR-021.</summary>
    public string? AccountsFilePath { get; init; }

    /// <summary>
    /// Raiz do arquivo de artifacts de tentativas reprovadas (patch + manifest de
    /// provenance), fora do repositório. Ausente usa <c>~/.harness/pilots</c>.
    /// </summary>
    public string? ArchiveRoot { get; init; }

    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(10);

    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(15);

    public TimeSpan RunTimeout { get; init; } = TimeSpan.FromMinutes(30);

    public int ContextTokenBudget { get; init; } = 8000;
}

/// <summary>Situação de um run de agente. Conjunto fechado.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AgentRunStatus>))]
public enum AgentRunStatus
{
    /// <summary>Claims, conta e worktree adquiridos; executor ainda não iniciado.</summary>
    Accepted,

    Running,
    Completed,
    Failed,
    Cancelled,

    /// <summary>Claim incompatível com outra tentativa viva.</summary>
    ScopeConflict,

    /// <summary>Recusado por política: papel, escopo, conta ou configuração.</summary>
    Rejected,
}

/// <summary>Pedido de bootstrap de um run de agente.</summary>
public sealed record StartAgentRunCommand
{
    public required string TenantId { get; init; }

    public required string ProjectId { get; init; }

    public required string TaskId { get; init; }

    public required string AttemptId { get; init; }

    /// <summary>Papel LÓGICO. Define o escopo de paths, nunca o provider.</summary>
    public required string Role { get; init; }

    /// <summary>Alias da conta. Nunca um e-mail, nunca uma credencial.</summary>
    public required string AccountAlias { get; init; }

    public required string Instruction { get; init; }

    public required string RepositoryRoot { get; init; }

    public required string ControlledRoot { get; init; }

    public required string BranchName { get; init; }

    public required string WorktreePath { get; init; }

    public required IReadOnlyList<string> ScopeClaims { get; init; }

    public required string Owner { get; init; }

    public required string IdempotencyKey { get; init; }

    public AgentPathScopeKind PathScopeKind { get; init; } = AgentPathScopeKind.Backend;

    public ExternalAgentAccess Access { get; init; } = ExternalAgentAccess.Workspace;

    public string BaseReference { get; init; } = "HEAD";

    public string? Model { get; init; }

    public string? Effort { get; init; }

    public string? ResumeSessionId { get; init; }

    public string RiskTier { get; init; } = "medium";

    public IReadOnlyList<string> AcceptanceCriteria { get; init; } = [];

    /// <summary>
    /// Contexto de continuação governada, quando esta tentativa retoma o trabalho de uma
    /// tentativa anterior reprovada. Nulo para um run do zero.
    /// </summary>
    public ContinuationContext? Continuation { get; init; }
}

/// <summary>
/// Estado observável de um run. Os campos duráveis vêm de `attempt_workspaces`; os campos
/// do executor vêm da sessão viva e do resultado coletado.
/// </summary>
public sealed record AgentRunSnapshot(
    string RunId,
    string AttemptId,
    string AccountAlias,
    string Role,
    string ExecutorId,
    AgentRunStatus Status,
    AttemptWorkspaceSnapshot? Workspace,
    IReadOnlyList<AttemptScopeConflict> Conflicts,
    ExternalAgentRunResult? Execution,
    string? SessionId,
    int? ProcessId,
    long? AccountFencingToken,
    string? BundleChecksum,
    string? ReceiptTurnId,
    string? FinalError);

/// <summary>Diagnóstico de uma conta: perfil em disco + executor observado por probe.</summary>
public sealed record AgentAccountDoctorReport(
    string Alias,
    string ExecutorId,
    bool AdapterImplemented,
    bool ExecutorInstalled,
    string? DetectedVersion,
    string ProbeReasonCode,
    AccountProfileDoctorReport Profile,
    bool Authenticated);

/// <summary>Pedido de revisão independente de uma tentativa (CA-7).</summary>
public sealed record AgentCriticReviewCommand
{
    public required string AttemptId { get; init; }

    /// <summary>Conta do revisor. Precisa ser diferente da conta do actor.</summary>
    public required string CriticAlias { get; init; }

    public required string ActorAlias { get; init; }

    /// <summary>Diretório somente-leitura de onde o critic lê o repositório.</summary>
    public required string ReviewDirectory { get; init; }

    public required string Diff { get; init; }

    public string TestEvidence { get; init; } = "(nenhuma evidência de teste foi fornecida)";

    public IReadOnlyList<string> AcceptanceCriteria { get; init; } = [];

    public IReadOnlyList<string> ScopeClaims { get; init; } = [];

    public string? Model { get; init; }
}
