using System.Text.Json;
using System.Text.Json.Serialization;

namespace Harness.Modules.Operations;

/// <summary>Situação de um E2E acompanhado pela operação.</summary>
public sealed record E2EState
{
    public string? Status { get; init; }
    public string? ProjectId { get; init; }
    public string? ProjectName { get; init; }
    public string? ConversationId { get; init; }
    public int Phase { get; init; }
    public string? PhaseName { get; init; }
}

/// <summary>
/// Estado factual da operação, FORA do contexto do modelo.
///
/// O contexto de uma sessão é volátil, cresce, deriva e morre com ela. Uma sessão nova que
/// precise reler trinta mil tokens para descobrir o que falta já começou errado. Aqui o
/// estado é um arquivo pequeno, versionado e legível por código — o supervisor decide sobre
/// ele sem consultar nenhum modelo.
/// </summary>
public sealed record OperationState
{
    public string Operation { get; init; } = "poseidon-final-validation";
    public int SchemaVersion { get; init; } = 1;
    public DateTimeOffset? UpdatedAt { get; init; }

    public string? Branch { get; init; }
    public string? HeadCommit { get; init; }

    public E2EState? CleanE2E { get; init; }

    public string? GeneratedProduct { get; init; }
    public string? RecoveryTests { get; init; }

    public int MandatoryGatesFailed { get; init; }
    public int MandatoryTestsPending { get; init; }

    public bool HumanDecisionRequired { get; init; }
    public IReadOnlyList<string> ExternalBlockers { get; init; } = [];

    public string? NextAction { get; init; }

    /// <summary>
    /// Findings bloqueantes e trabalho executável NÃO são campos editáveis do estado: são
    /// derivados de FINDINGS.jsonl. Um número que o próprio agente escreve é um número que o
    /// próprio agente pode zerar para poder ir embora.
    /// </summary>
    [JsonIgnore]
    public int BlockingFindings { get; init; }

    [JsonIgnore]
    public int ExecutableWork { get; init; }

    /// <summary>
    /// Parte do <see cref="ExecutableWork"/> que não depende do proprietário. É este número
    /// — e não o campo <see cref="HumanDecisionRequired"/>, que o próprio agente escreve —
    /// que decide se vale relançar uma sessão.
    /// </summary>
    [JsonIgnore]
    public int AgentExecutableWork { get; init; }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static OperationState Parse(string json) =>
        JsonSerializer.Deserialize<OperationState>(json, Options)
        ?? throw new InvalidOperationException("STATE.json inválido.");

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    /// <summary>
    /// Junta o estado declarado com os findings observados. É este objeto — e não o arquivo
    /// cru — que o <see cref="CompletionGate"/> avalia.
    /// </summary>
    public OperationState WithFindings(IReadOnlyList<OperationFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);
        return this with
        {
            BlockingFindings = findings.Count(finding => finding.IsBlocking),
            ExecutableWork = findings.Count(finding => finding.IsExecutable),
            AgentExecutableWork = findings.Count(finding => finding.IsAgentExecutable),
        };
    }
}
