using System.Diagnostics;
using System.Diagnostics.Metrics;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Harness.Host.Observability;

internal static class PoseidonTelemetry
{
    internal const string ServiceName = "poseidon-host";
    internal const string ActivitySourceName = "Poseidon";
    internal const string MeterName = "Poseidon";

    internal static ActivitySource ActivitySource { get; } = new(ActivitySourceName);

    private static Meter Meter { get; } = new(MeterName);

    private static Counter<long> DurableOperationCounter { get; } =
        Meter.CreateCounter<long>(
            "poseidon.durable.operation.count",
            description: "Number of durable-engine operations.");

    private static Histogram<double> DurableOperationDuration { get; } =
        Meter.CreateHistogram<double>(
            "poseidon.durable.operation.duration",
            unit: "ms",
            description: "Duration of durable-engine operations.");

    private static Counter<long> OutboxDispatchCounter { get; } =
        Meter.CreateCounter<long>(
            "poseidon.outbox.dispatch.count",
            description: "Number of outbox delivery attempts.");

    private static Histogram<double> OutboxDispatchDuration { get; } =
        Meter.CreateHistogram<double>(
            "poseidon.outbox.dispatch.duration",
            unit: "ms",
            description: "Duration of outbox delivery attempts.");

    private static Counter<long> OutboxRecoveredClaimCounter { get; } =
        Meter.CreateCounter<long>(
            "poseidon.outbox.recovered_claim.count",
            description: "Number of expired outbox claims released for recovery.");

    private static Counter<long> ChiefTurnCounter { get; } =
        Meter.CreateCounter<long>(
            "poseidon.chief.turn.count",
            description: "Number of chief turns processed.");

    private static Histogram<double> ChiefTurnDuration { get; } =
        Meter.CreateHistogram<double>(
            "poseidon.chief.turn.duration",
            unit: "ms",
            description: "Duration of chief turns.");

    /// <summary>
    /// Fase 0A2 (BR-005): renovação, perda e rejeição do lease do turno. Sem esta série, um turno
    /// duplicado por lease vencido só aparecia na fatura — nunca na operação.
    /// </summary>
    private static Counter<long> ChiefTurnLeaseCounter { get; } =
        Meter.CreateCounter<long>(
            "poseidon.chief.turn.lease.count",
            description: "Chief turn lease outcomes (renewed, lost, expired).");

    private static Counter<long> AgentExecutionCounter { get; } =
        Meter.CreateCounter<long>(
            "poseidon.agent.execution.count",
            description: "Number of agent execution attempts.");

    private static Histogram<double> AgentExecutionDuration { get; } =
        Meter.CreateHistogram<double>(
            "poseidon.agent.execution.duration",
            unit: "ms",
            description: "Duration of agent execution attempts.");

    private static Counter<long> ChannelOperationCounter { get; } =
        Meter.CreateCounter<long>(
            "poseidon.channel.operation.count",
            description: "Number of channel operations.");

    private static Histogram<double> ChannelOperationDuration { get; } =
        Meter.CreateHistogram<double>(
            "poseidon.channel.operation.duration",
            unit: "ms",
            description: "Duration of channel operations.");

    internal static IServiceCollection AddPoseidonTelemetry(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var telemetry = services
            .AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(ServiceName))
            .WithTracing(tracing =>
            {
                tracing
                    .AddSource(ActivitySourceName)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddProcessor(new SensitiveTelemetryProcessor())
                    // Amostragem de cauda dos turnos: falha, escalação, guarda de laço e turno
                    // lento são retidos integralmente; o corpo normal entra por fração estável.
                    .AddProcessor(new TurnTailSamplingProcessor());

                if (HasOtlpEndpoint(configuration, "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT"))
                {
                    tracing.AddOtlpExporter();
                }
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddMeter(MeterName)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();

                if (HasOtlpEndpoint(configuration, "OTEL_EXPORTER_OTLP_METRICS_ENDPOINT"))
                {
                    metrics.AddOtlpExporter();
                }
            });

        services.AddLogging(logging =>
            logging.AddOpenTelemetry(options =>
            {
                options.SetResourceBuilder(
                    ResourceBuilder.CreateDefault().AddService(ServiceName));
                options.IncludeScopes = true;
                options.IncludeFormattedMessage = false;
                options.ParseStateValues = true;

                if (HasOtlpEndpoint(configuration, "OTEL_EXPORTER_OTLP_LOGS_ENDPOINT"))
                {
                    options.AddOtlpExporter();
                }
            }));

        return services;
    }

    internal static void RecordDurableOperation(
        string operation,
        string result,
        string? state,
        double durationMilliseconds)
    {
        var tags = new TagList
        {
            { "operation", operation },
            { "result", result },
        };
        if (state is not null)
        {
            tags.Add("state", state);
        }

        DurableOperationCounter.Add(1, tags);
        DurableOperationDuration.Record(durationMilliseconds, tags);
    }

    internal static void RecordOutboxDispatch(string result, double durationMilliseconds)
    {
        var tags = new TagList
        {
            { "result", result },
        };
        OutboxDispatchCounter.Add(1, tags);
        OutboxDispatchDuration.Record(durationMilliseconds, tags);
    }

    internal static void RecordOutboxRecoveredClaims(int count)
    {
        if (count > 0)
        {
            OutboxRecoveredClaimCounter.Add(count);
        }
    }

    internal static Activity? StartChiefTurn(
        string tenantId,
        string projectId,
        string conversationId,
        string turnId,
        string agentId)
    {
        var activity = ActivitySource.StartActivity(
            "poseidon.chief.turn",
            ActivityKind.Consumer);
        activity?.SetTag("tenant_id", tenantId);
        activity?.SetTag("project_id", projectId);
        activity?.SetTag("conversation_id", conversationId);
        activity?.SetTag("chief_turn_id", turnId);
        activity?.SetTag("agent_id", agentId);
        return activity;
    }

    internal static Activity? StartChiefContext()
        => ActivitySource.StartActivity("poseidon.chief.context", ActivityKind.Internal);

    internal static Activity? StartChiefInvocation(string? provider, string? model)
    {
        var activity = ActivitySource.StartActivity(
            "poseidon.chief.invoke",
            ActivityKind.Client);
        activity?.SetTag("gen_ai.operation.name", "chat");
        activity?.SetTag("gen_ai.provider.name", provider);
        activity?.SetTag("gen_ai.request.model", model);
        return activity;
    }

    internal static void RecordChiefTurn(string result, double durationMilliseconds)
    {
        // O desfecho precisa estar NO span para a amostragem de cauda poder decidir no fim: sem
        // esta marca, todo turno pareceria concluído e as falhas seriam descartadas como rotina.
        for (var activity = Activity.Current; activity is not null; activity = activity.Parent)
        {
            if (string.Equals(
                    activity.OperationName,
                    TurnTailSamplingProcessor.ChiefTurnActivityName,
                    StringComparison.Ordinal))
            {
                activity.SetTag(TurnTailSamplingProcessor.OutcomeTagName, result);
                break;
            }
        }

        var tags = new TagList
        {
            { "result", result },
        };
        ChiefTurnCounter.Add(1, tags);
        ChiefTurnDuration.Record(durationMilliseconds, tags);
    }

    /// <summary>
    /// Fase 0B1: quantas execuções correram com sandbox atestada e quantas não. Antes o produto
    /// dizia sempre "com sandbox", então esta série não tinha o que medir.
    /// </summary>
    private static Counter<long> SandboxAttestationCounter { get; } =
        Meter.CreateCounter<long>(
            "poseidon.sandbox.attestation.count",
            description: "Sandbox attestations issued, by provider and verification outcome.");

    /// <summary>
    /// Fase 1A: captura e retomada de checkpoint. Sem esta série, "retomou do checkpoint" e
    /// "recomeçou do zero" seriam indistinguíveis de fora.
    /// </summary>
    private static Counter<long> CheckpointCounter { get; } =
        Meter.CreateCounter<long>(
            "poseidon.attempt.checkpoint.count",
            description: "Attempt checkpoint capture and resume outcomes.");

    /// <summary>
    /// Fase 1B: o orçamento de esforço em telemetria. Sem esta série, "o card parou porque acabou o
    /// orçamento" e "o card parou por falha" seriam a mesma linha no gráfico.
    /// </summary>
    private static Counter<long> EffortBudgetCounter { get; } =
        Meter.CreateCounter<long>(
            "poseidon.card.effort.budget.count",
            description: "Effort budget outcomes per card, by reason code.");

    internal static void RecordEffortBudget(string outcome, string reasonCode) =>
        EffortBudgetCounter.Add(
            1, new TagList { { "outcome", outcome }, { "reason_code", reasonCode } });

    internal static void RecordCheckpoint(string outcome) =>
        CheckpointCounter.Add(1, new TagList { { "outcome", outcome } });

    internal static void RecordSandboxAttestation(string provider, bool verified) =>
        SandboxAttestationCounter.Add(
            1,
            new TagList { { "provider", provider }, { "verified", verified } });

    internal static void RecordChiefTurnLease(string outcome) =>
        ChiefTurnLeaseCounter.Add(1, new TagList { { "outcome", outcome } });

    internal static void RecordAgentExecution(
        string executor,
        string result,
        double durationMilliseconds)
    {
        var tags = new TagList
        {
            { "executor", executor },
            { "result", result },
        };
        AgentExecutionCounter.Add(1, tags);
        AgentExecutionDuration.Record(durationMilliseconds, tags);
    }

    internal static void RecordChannelOperation(
        string channel,
        string direction,
        string result,
        double durationMilliseconds)
    {
        var tags = new TagList
        {
            { "channel", channel },
            { "direction", direction },
            { "result", result },
        };
        ChannelOperationCounter.Add(1, tags);
        ChannelOperationDuration.Record(durationMilliseconds, tags);
    }

    internal static bool HasOtlpEndpoint(
        IConfiguration configuration,
        string signalEndpointKey)
    {
        var endpoint = configuration[signalEndpointKey];
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            endpoint = configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        }

        return Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
               && string.IsNullOrEmpty(uri.UserInfo);
    }
}

internal sealed class SensitiveTelemetryProcessor : BaseProcessor<Activity>
{
    private static readonly string[] SensitiveFragments =
    [
        "authorization",
        "cookie",
        "email",
        "exception.message",
        "exception.stacktrace",
        "http.request.body",
        "http.request.header",
        "http.response.body",
        "http.response.header",
        "http.target",
        "http.url",
        "password",
        "prompt",
        "request.body",
        "response.body",
        "secret",
        "token",
        "url.full",
        "url.query",
        "user.",
    ];

    public override void OnEnd(Activity activity)
        => Sanitize(activity);

    internal static void Sanitize(Activity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        foreach (var tag in activity.TagObjects.ToArray())
        {
            if (IsSensitive(tag.Key))
            {
                activity.SetTag(tag.Key, null);
            }
        }
    }

    private static bool IsSensitive(string key) =>
        SensitiveFragments.Any(fragment =>
            key.Contains(fragment, StringComparison.OrdinalIgnoreCase));
}
