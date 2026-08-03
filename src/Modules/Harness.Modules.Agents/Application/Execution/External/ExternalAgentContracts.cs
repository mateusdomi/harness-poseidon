using System.Text.Json.Serialization;
using Harness.Modules.Agents.Contracts;

namespace Harness.Modules.Agents.Application.Execution.External;

/// <summary>
/// Modo de escrita concedido ao executor externo. É o único lugar onde a diferença entre
/// ACTOR e CRITIC vira configuração real de processo: o critic nunca recebe ferramenta de
/// escrita (CA-7).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ExternalAgentAccess>))]
public enum ExternalAgentAccess
{
    /// <summary>Somente leitura: avaliação, revisão, crítica.</summary>
    ReadOnly,

    /// <summary>Escrita no working directory concedido: implementação.</summary>
    Workspace,
}

/// <summary>
/// Mecanismo pelo qual a CLI recebe o prompt. É uma característica OBSERVADA do binário,
/// nunca uma preferência: Claude Code e Codex leem do STDIN; `agy --print` só aceita o
/// prompt como argumento posicional.
/// </summary>
public enum ExternalPromptDelivery
{
    /// <summary>Prompt entregue por STDIN — não aparece na tabela de processos.</summary>
    StandardInput,

    /// <summary>Prompt entregue como argumento posicional — única forma aceita pela CLI.</summary>
    PositionalArgument,
}

/// <summary>Situação final de uma execução externa. Conjunto fechado.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ExternalAgentRunStatus>))]
public enum ExternalAgentRunStatus
{
    Completed,
    Failed,
    Cancelled,
    TimedOut,
}

/// <summary>Tipo de evento observado no stream do executor. Conjunto fechado.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ExternalAgentEventKind>))]
public enum ExternalAgentEventKind
{
    /// <summary>Sessão iniciada; carrega o identificador de sessão do executor.</summary>
    Started,

    /// <summary>Texto parcial do modelo.</summary>
    Delta,

    /// <summary>Uso de ferramenta pelo agente (apenas o tipo, nunca o conteúdo bruto).</summary>
    ToolUse,

    /// <summary>Consumo observado (tokens/custo).</summary>
    Usage,

    /// <summary>Sinal de cota do provedor.</summary>
    Quota,

    /// <summary>Turno concluído.</summary>
    Completed,

    /// <summary>Turno falhou.</summary>
    Failed,
}

/// <summary>Consumo observado. Nulo significa DESCONHECIDO; nunca zero inventado.</summary>
public sealed record ExternalAgentUsage(
    long? InputTokens,
    long? CachedInputTokens,
    long? OutputTokens,
    decimal? CostUsd,
    int? TurnCount);

/// <summary>
/// Evento do stream, já sanitizado. `Text` nunca carrega segredo: passa por
/// <see cref="ExternalAgentRedaction"/> antes de sair do adapter.
/// </summary>
public sealed record ExternalAgentEvent(
    ExternalAgentEventKind Kind,
    string? Text = null,
    string? SessionId = null,
    ExternalAgentUsage? Usage = null,
    string? Code = null);

/// <summary>
/// Pedido de execução externa. O prompt NUNCA vai em argumento de linha de comando — ele
/// é entregue por stdin, de modo que não apareça na tabela de processos.
/// </summary>
public sealed record ExternalAgentRunRequest
{
    public required string Alias { get; init; }

    public required string Prompt { get; init; }

    /// <summary>Diretório de trabalho do agente: tipicamente a worktree da tentativa.</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>Perfil isolado da conta (CA-3): config home, logs, sessões.</summary>
    public required AccountProfileLayout Profile { get; init; }

    public ExternalAgentAccess Access { get; init; } = ExternalAgentAccess.ReadOnly;

    /// <summary>Sessão a retomar. Nulo inicia uma sessão nova.</summary>
    public string? ResumeSessionId { get; init; }

    public string? Model { get; init; }

    public string? Effort { get; init; }

    public IReadOnlyList<string> AdditionalDirectories { get; init; } = [];

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Silêncio máximo tolerado: tempo sem UMA linha de saída (stdout ou stderr) antes de a
    /// execução ser tratada como travada.
    ///
    /// Existe porque uma CLI pode ficar viva e muda para sempre. Observado ao vivo em
    /// 2026-08-03: a CLI recebeu 429 de cota, caiu no modo não-streaming, e ficou dezesseis
    /// minutos em `epoll` sem UMA conexão ao modelo — de fora, indistinguível de "o agente
    /// está pensando". O único fim possível era o timeout de trinta minutos, que classifica
    /// como transitório e faz TUDO de novo na mesma conta morta.
    ///
    /// O valor é um COMPROMISSO medido, não um número redondo: dez minutos era a regra de
    /// bolso humana para ir investigar, mas uma ferramenta longa e legítima (uma suíte, um
    /// build) fica muda por mais que isso, e matar um turno saudável custa uma tentativa. Aos
    /// quinze minutos a espera ainda é metade do <see cref="Timeout"/> e nenhum trabalho real
    /// observado nesta operação passou perto.
    ///
    /// <see cref="TimeSpan.Zero"/> desliga a vigilância.
    /// </summary>
    public TimeSpan NoProgressTimeout { get; init; } = TimeSpan.FromMinutes(15);
}

/// <summary>Resultado coletado de uma execução externa.</summary>
public sealed record ExternalAgentRunResult(
    string ExecutorId,
    string Alias,
    string? SessionId,
    ExternalAgentRunStatus Status,
    string FinalMessage,
    IReadOnlyList<string> Deltas,
    ExternalAgentUsage? Usage,
    int? ExitCode,
    string? FailureCode,
    long DurationMs)
{
    public bool Succeeded => Status == ExternalAgentRunStatus.Completed;

    /// <summary>
    /// Diagnóstico limitado e redigido capturado do STDERR quando a CLI falha. Nunca é
    /// preenchido no sucesso e não substitui <see cref="FailureCode"/> nas decisões automáticas.
    /// </summary>
    public string? FailureDiagnostic { get; init; }

    /// <summary>
    /// O que deu errado, DECLARADO pelo adaptador. É este campo — e não o texto de
    /// <see cref="FailureCode"/> — que as decisões automáticas devem consultar.
    /// <see cref="ExternalFailureKind.Unknown"/> significa que o adaptador não soube dizer, e
    /// só nesse caso a heurística de texto legada ainda vale.
    /// </summary>
    public ExternalFailureKind FailureKind { get; init; } = ExternalFailureKind.Unknown;
}

/// <summary>Falha do adapter externa ao modelo; carrega somente CÓDIGO, nunca segredo.</summary>
public sealed class ExternalAgentException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
