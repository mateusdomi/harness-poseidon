using System.Text;
using System.Text.Json;
using Harness.Host.Conversations;
using Harness.Modules.Workflows.Product;
using Harness.Persistence.Abstractions.Attention;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.Notifications;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;

namespace Harness.Host.Notifications;

/// <summary>Configuração do monitor. O chat de atenção vem de env/arquivo — nunca do repo.</summary>
public sealed record HumanAttentionMonitorOptions(
    string? TelegramChatId,
    AttentionEscalationOptions Escalation)
{
    public TimeSpan TickInterval { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// O Human Attention Monitor — a metade DETERMINÍSTICA do loop de atenção humana.
///
/// A Bruna decide O QUE precisa do humano (ASK); este serviço decide QUANDO e POR ONDE insistir,
/// pela <see cref="AttentionEscalationPolicy"/> (T+0 in-app, T+1m Telegram, T+15m lembrete,
/// T+60m final, depois silêncio). O Telegram aqui é canal de ATENÇÃO: mensagem curta, sem
/// segredo, sem documento — e como o canal do produto é BIDIRECIONAL, a resposta pode voltar
/// pelo próprio Telegram sem ninguém voltar ao computador.
///
/// BLOQUEIO DE REDE é um estado declarado, não uma falha silenciosa: wifi corporativo costuma
/// bloquear o Telegram. Quando o envio falha por rede, o pedido é marcado
/// <c>network_blocked</c>, uma notificação in-app declara o fato UMA vez, o marco NÃO é
/// consumido e o próximo tick tenta de novo — a escadinha não pula degrau por causa da rede.
/// </summary>
public sealed partial class HumanAttentionMonitor(
    IHumanAttentionStore? attention,
    INotificationStore notifications,
    ILocalProfileStore profiles,
    TelegramChannelOptions telegram,
    HumanAttentionMonitorOptions options,
    IClock clock,
    ILogger<HumanAttentionMonitor> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.TickInterval);
        try
        {
            do
            {
                try
                {
                    await TickAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    LogTickFailure(logger, exception.GetType().Name, null);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Encerramento normal do Host.
        }
    }

    /// <summary>Um passo do monitor — público para o teste determinístico com relógio fake.</summary>
    public async Task TickAsync(CancellationToken cancellationToken)
    {
        if (attention is null)
        {
            // Provider sem a store de atenção: o monitor se declara inativo — nunca meio-vivo.
            return;
        }

        foreach (var profile in await profiles.ListAsync(cancellationToken))
        {
            foreach (var request in await attention.ListOpenAsync(profile.TenantId, null, cancellationToken))
            {
                var decision = AttentionEscalationPolicy.Decide(
                    request.Status, request.ReminderCount, request.CreatedAt, clock.UtcNow,
                    options.Escalation);
                if (decision.Action == AttentionAction.None)
                {
                    continue;
                }

                await ExecuteAsync(profile.TenantId, profile.Id, request, decision, cancellationToken);
            }
        }
    }

    private async Task ExecuteAsync(
        string tenantId,
        string profileId,
        HumanAttentionRecord request,
        AttentionDecision decision,
        CancellationToken cancellationToken)
    {
        var store = attention!;

        if (decision.Action == AttentionAction.NotifyInApp)
        {
            await notifications.CreateAsync(
                new NotificationCreateCommand(
                    tenantId, UlidValue.New(clock.UtcNow).ToString(), profileId,
                    request.Severity, "human_attention",
                    "Decisão sua é necessária",
                    $"{request.Question} (escopo bloqueado: {request.BlockingScope}; o restante do projeto continua.)",
                    $"attention:{request.Id}", null, clock.UtcNow),
                cancellationToken);
            await store.RecordEscalationAsync(
                tenantId, request.Id, decision.NextStatus, decision.NextReminderCount,
                decision.NextActionAt, request.ChannelStatus, cancellationToken);
            return;
        }

        // Passos de Telegram. Sem chat configurado, o canal é declarado ausente e o marco é
        // consumido (não há o que reenviar) — o in-app continua sendo a fonte.
        if (string.IsNullOrWhiteSpace(options.TelegramChatId) || !telegram.Enabled)
        {
            await store.RecordEscalationAsync(
                tenantId, request.Id, decision.NextStatus, decision.NextReminderCount,
                decision.NextActionAt, "unconfigured", cancellationToken);
            return;
        }

        var sent = await TrySendTelegramAsync(request, decision.Action, cancellationToken);
        if (sent)
        {
            await store.RecordEscalationAsync(
                tenantId, request.Id, decision.NextStatus, decision.NextReminderCount,
                decision.NextActionAt, "telegram_sent", cancellationToken);
            return;
        }

        // REDE BLOQUEADA: não consome o marco (mesmos status/contador; o tick seguinte tenta de
        // novo) e declara o fato in-app UMA vez.
        if (!string.Equals(request.ChannelStatus, "network_blocked", StringComparison.Ordinal))
        {
            await notifications.CreateAsync(
                new NotificationCreateCommand(
                    tenantId, UlidValue.New(clock.UtcNow).ToString(), profileId,
                    "high", "human_attention",
                    "Telegram inacessível nesta rede",
                    "Há uma decisão sua pendente e o alerta por Telegram não pôde ser entregue " +
                    "(a rede atual pode bloquear o Telegram — comum em wifi corporativo). O " +
                    "pedido continua visível aqui; o envio será retentado.",
                    $"attention-network:{request.Id}", null, clock.UtcNow),
                cancellationToken);
        }

        await store.RecordEscalationAsync(
            tenantId, request.Id, request.Status, request.ReminderCount,
            request.NextActionAt, "network_blocked", cancellationToken);
    }

    private async Task<bool> TrySendTelegramAsync(
        HumanAttentionRecord request, AttentionAction action, CancellationToken cancellationToken)
    {
        // Mensagem CURTA e segura: projeto, impacto, como responder. Nada de segredo, documento
        // ou stack trace. A resposta pode ser dada AQUI mesmo — o canal é bidirecional.
        var prefix = action switch
        {
            AttentionAction.RemindTelegram => "Lembrete — ",
            AttentionAction.FinalReminder => "Último lembrete — ",
            _ => string.Empty,
        };
        var text = new StringBuilder()
            .AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"{prefix}Poseidon precisa de uma decisão sua")
            .AppendLine()
            .AppendLine(request.Question)
            .AppendLine()
            .AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"Apenas o escopo \"{request.BlockingScope}\" está bloqueado; o restante do projeto continua.")
            .Append("Responda por aqui mesmo, ou abra o Poseidon.")
            .ToString();

        try
        {
            var url = $"{telegram.ApiBaseUrl}/bot{telegram.BotToken}/sendMessage";
            using var response = await _http.PostAsync(
                url,
                new StringContent(
                    JsonSerializer.Serialize(
                        new { chat_id = options.TelegramChatId, text }, JsonOptions),
                    Encoding.UTF8, "application/json"),
                cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException)
        {
            // Timeout/DNS/conexão recusada: a assinatura do bloqueio de rede.
            LogTelegramUnreachable(logger, exception.GetType().Name, null);
            return false;
        }
    }

    public override void Dispose()
    {
        _http.Dispose();
        base.Dispose();
    }

    private static readonly Action<ILogger, string, Exception?> LogTickFailure =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(1, nameof(LogTickFailure)),
            "Human Attention Monitor: falha transitória no tick ({ExceptionType}); o próximo tick continua.");

    private static readonly Action<ILogger, string, Exception?> LogTelegramUnreachable =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(2, nameof(LogTelegramUnreachable)),
            "Telegram inacessível ({ExceptionType}) — rede possivelmente bloqueada; marco preservado para retry.");
}
