using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Harness.Modules.Tools.Domain;
using Harness.SharedKernel.Security;

namespace Harness.Modules.Tools.Application;

/// <summary>Perfil de quem chama. O que cada papel PODE fazer é fechado, não sugerido.</summary>
public enum ToolCallProfile
{
    /// <summary>A Bruna. Não executa ferramenta: delega. Qualquer chamada dela é negada.</summary>
    Chief,

    /// <summary>O ator. Escreve apenas na worktree autorizada; sem rede por padrão.</summary>
    Actor,

    /// <summary>O crítico. Somente leitura: nenhuma escrita, nenhuma alteração Git, nenhuma instalação.</summary>
    Critic,
}

/// <summary>Política de rede da chamada. Sem allowlist explícita, não há egresso.</summary>
public sealed record ToolNetworkPolicy(bool Enabled, IReadOnlyList<string> AllowedHosts)
{
    public static ToolNetworkPolicy Denied { get; } = new(false, []);
}

/// <summary>
/// Uma chamada de ferramenta, inteiramente descrita. Cada campo existe porque alguém precisa
/// responder por ele depois: quem pediu, em nome de que trabalho, com qual autorização, sobre quais
/// caminhos, com que limite e por quanto tempo.
/// </summary>
public sealed record ToolCallRequest(
    string TenantId,
    string ProjectId,
    string CardId,
    string AttemptId,
    string AgentId,
    ToolCallProfile Profile,
    string ToolId,
    CapabilityToken Capability,
    long FencingToken,
    IReadOnlyList<string> Arguments,
    IReadOnlyList<string> Paths,
    ToolNetworkPolicy Network,
    TimeSpan Timeout,
    int MaximumOutputBytes,
    string IdempotencyKey,
    bool Mutating,
    ToolRiskTier RiskTier);

/// <summary>Resultado ESTRUTURADO — nunca texto solto que o chamador precise interpretar.</summary>
public sealed record ToolCallResult(
    bool Allowed,
    string Code,
    string Detail,
    string Output,
    bool OutputTruncated,
    bool Replayed,
    int ExitCode)
{
    public static ToolCallResult Deny(string code, string detail) =>
        new(false, code, detail, string.Empty, false, false, -1);
}

/// <summary>Efeito real da ferramenta. O broker decide SE roda; isto é o que roda.</summary>
public delegate Task<ToolCallEffectResult> ToolCallEffect(
    ToolCallRequest request, CancellationToken cancellationToken);

public sealed record ToolCallEffectResult(int ExitCode, string Output);

/// <summary>Registro durável de uma chamada, para idempotência e auditoria.</summary>
public interface IToolCallJournal
{
    /// <summary>Resultado já registrado para a chave, ou nulo. Idempotência de efeito repetido.</summary>
    Task<ToolCallResult?> FindAsync(
        string tenantId, string idempotencyKey, CancellationToken cancellationToken = default);

    Task RecordAsync(
        ToolCallRequest request, ToolCallResult result, DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Fase 0B2 (BR-002/BR-013): o gateway tipado por CHAMADA.
///
/// O PEP existente autorizava o BINÁRIO executor uma vez, no começo da tentativa; nenhuma chamada
/// posterior voltava a passar por ele. Autorizar o processo e não os efeitos é autorizar a intenção
/// e não o ato — qualquer coisa que o processo fizesse depois estava fora do alcance da política.
///
/// Aqui cada chamada carrega tenant, projeto, card, tentativa, agente, ferramenta, capability,
/// fencing, argumentos, caminhos, rede, timeout, limite de saída e chave de idempotência. E cada
/// decisão é auditada — inclusive as negativas, que são as que interessam quando algo dá errado.
///
/// LIMITE CONHECIDO, declarado e não disfarçado: chamadas que um CLI externo faz DENTRO do próprio
/// processo (bash, leitura de arquivo, rede) não passam por aqui — o produto não intercepta o
/// interior de um binário de terceiro. O que este broker garante é que todo efeito controlável
/// PELO control plane passa por política, e que o perfil da Bruna não executa ferramenta nenhuma.
/// </summary>
public sealed class ToolCallBroker(
    SecurityPolicyEnforcementPoint pep,
    IToolCallJournal journal,
    TimeProvider timeProvider)
{
    /// <summary>Teto absoluto de saída retida. Acima disso, trunca e diz que truncou.</summary>
    public const int MaximumOutputCeiling = 1 << 20;

    private readonly SecurityPolicyEnforcementPoint _pep = pep ?? throw new ArgumentNullException(nameof(pep));
    private readonly IToolCallJournal _journal = journal ?? throw new ArgumentNullException(nameof(journal));
    private readonly TimeProvider _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<ToolCallResult> InvokeAsync(
        ToolCallRequest request, ToolCallEffect effect, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(effect);
        var now = _time.GetUtcNow();

        if (Validate(request) is { Allowed: false } invalid)
        {
            await _journal.RecordAsync(request, invalid, now, cancellationToken);
            return invalid;
        }

        // Repetição da MESMA chave devolve o resultado anterior sem repetir o efeito. É o que
        // separa "tentar de novo" de "fazer duas vezes".
        if (await _journal.FindAsync(request.TenantId, request.IdempotencyKey, cancellationToken)
            is { } replayed)
        {
            return replayed with { Replayed = true };
        }

        var decision = await _pep.AuthorizeAsync(
            request.Capability,
            new CapabilityAuthorizationRequest(
                Map(request.Profile),
                request.AgentId,
                request.TenantId,
                request.ProjectId,
                request.CardId,
                request.AttemptId,
                CapabilityOperation.ToolExecution,
                request.ToolId,
                request.ToolId,
                request.Paths.Count > 0 ? request.Paths[0] : null,
                request.FencingToken),
            cancellationToken);
        if (!decision.Allowed)
        {
            var denied = ToolCallResult.Deny(decision.Code, decision.Detail);
            await _journal.RecordAsync(request, denied, now, cancellationToken);
            return denied;
        }

        // Cada caminho é verificado, não só o primeiro: autorizar pelo primeiro e executar sobre
        // todos seria uma porta aberta com aparência de política.
        foreach (var path in request.Paths.Skip(1))
        {
            var pathDecision = await _pep.AuthorizeAsync(
                request.Capability,
                new CapabilityAuthorizationRequest(
                    Map(request.Profile), request.AgentId, request.TenantId, request.ProjectId,
                    request.CardId, request.AttemptId, CapabilityOperation.ToolExecution,
                    request.ToolId, request.ToolId, path, request.FencingToken),
                cancellationToken);
            if (!pathDecision.Allowed)
            {
                var denied = ToolCallResult.Deny(pathDecision.Code, pathDecision.Detail);
                await _journal.RecordAsync(request, denied, now, cancellationToken);
                return denied;
            }
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Timeout);
        ToolCallEffectResult produced;
        try
        {
            produced = await effect(request, timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested &&
            !cancellationToken.IsCancellationRequested)
        {
            var timedOut = ToolCallResult.Deny(
                "tool_call_timeout",
                $"The tool call exceeded {request.Timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)}s.");
            await _journal.RecordAsync(request, timedOut, _time.GetUtcNow(), cancellationToken);
            return timedOut;
        }

        // A saída é REDIGIDA antes de qualquer coisa: ela vai para o journal, para a auditoria e
        // para o contexto do próximo turno. Um segredo aqui se multiplica sozinho.
        var output = PersistenceSanitizer.SanitizeText(produced.Output);
        var truncated = false;
        var limit = Math.Min(request.MaximumOutputBytes, MaximumOutputCeiling);
        if (Encoding.UTF8.GetByteCount(output) > limit)
        {
            output = Truncate(output, limit);
            truncated = true;
        }

        var result = new ToolCallResult(
            true, "tool_call_allowed", "The broker authorized and executed the call.",
            output, truncated, false, produced.ExitCode);
        await _journal.RecordAsync(request, result, _time.GetUtcNow(), cancellationToken);
        return result;
    }

    /// <summary>Chave de idempotência estável derivada do que a chamada É, não de quando ocorreu.</summary>
    public static string ComposeIdempotencyKey(
        string attemptId, string toolId, IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var builder = new StringBuilder(attemptId).Append('|').Append(toolId);
        foreach (var argument in arguments)
        {
            builder.Append('|').Append(argument);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))
            .ToLower(CultureInfo.InvariantCulture);
    }

    private static ToolCallResult Validate(ToolCallRequest request)
    {
        if (request.Profile == ToolCallProfile.Chief)
        {
            // A Bruna delega. Se ela executasse, o trabalho deixaria de ter dono identificável e
            // a fronteira entre quem decide e quem faz desapareceria.
            return ToolCallResult.Deny(
                "chief_cannot_execute_tools",
                "The chief profile delegates work; it never executes tools directly.");
        }

        if (request.Profile == ToolCallProfile.Critic && request.Mutating)
        {
            return ToolCallResult.Deny(
                "critic_is_read_only",
                "The critic profile is read-only: no writes, no Git changes, no installs.");
        }

        if (request.Network.Enabled && request.Network.AllowedHosts.Count == 0)
        {
            return ToolCallResult.Deny(
                "network_allowlist_required",
                "Network access requires an explicit host allowlist.");
        }

        if (request.Timeout <= TimeSpan.Zero || request.Timeout > TimeSpan.FromHours(1))
        {
            return ToolCallResult.Deny(
                "tool_call_timeout_invalid",
                "The tool call timeout must be positive and no longer than one hour.");
        }

        if (request.MaximumOutputBytes <= 0)
        {
            return ToolCallResult.Deny(
                "tool_call_output_limit_invalid",
                "The tool call output limit must be positive.");
        }

        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            return ToolCallResult.Deny(
                "tool_call_idempotency_required",
                "Every tool call must carry an idempotency key.");
        }

        return SecretTextProtector.ContainsSensitiveCommandArgument(request.Arguments)
            ? ToolCallResult.Deny(
                "tool_call_argument_sensitive",
                "Secrets and credential switches are forbidden in tool arguments; use a secret reference.")
            : new ToolCallResult(true, "valid", "valid", string.Empty, false, false, 0);
    }

    private static CapabilityActorKind Map(ToolCallProfile profile) => profile switch
    {
        ToolCallProfile.Actor => CapabilityActorKind.Worker,
        ToolCallProfile.Critic => CapabilityActorKind.Specialist,
        _ => CapabilityActorKind.Chief,
    };

    private static string Truncate(string value, int limitBytes)
    {
        const string Marker = "\n[output truncated by the tool broker]";
        var markerBytes = Encoding.UTF8.GetByteCount(Marker);
        var budget = Math.Max(0, limitBytes - markerBytes);
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length <= budget)
        {
            return value + Marker;
        }

        var kept = budget;
        // Não cortar no meio de um caractere multibyte: um acento partido vira lixo no relatório.
        while (kept > 0 && (bytes[kept] & 0xC0) == 0x80)
        {
            kept--;
        }

        return Encoding.UTF8.GetString(bytes, 0, kept) + Marker;
    }
}
