namespace Harness.Modules.Workflows.Product;

/// <summary>
/// A política DETERMINÍSTICA de escalonamento de atenção humana (Human Attention Loop).
///
/// A IA decide o SIGNIFICADO da dúvida (CLOSED/INFER/ASK/DEFER); o CÓDIGO decide como escalá-la
/// — nunca "a Bruna achou que já passou tempo demais, talvez mande Telegram". A linha do tempo,
/// parametrizável e sem hardcode disperso:
///
///   T+0   → notificação in-app (chat + Notification Center)
///   T+1m  → Telegram (canal de ATENÇÃO — mensagem curta e segura; a resposta pode voltar pelo
///           próprio Telegram, que é bidirecional)
///   T+15m → lembrete, SOMENTE se ainda open/notified e ainda bloqueando trabalho
///   T+60m → lembrete final
///   depois → NADA. Spam periódico infinito destrói o canal.
///
/// Timeout NUNCA vira resposta: um ASK que exige autoridade humana permanece bloqueando o seu
/// subgrafo até a resposta chegar — o trabalho independente continua, a inferência não é
/// autorizada pelo relógio.
/// </summary>
public sealed record AttentionEscalationOptions(
    TimeSpan TelegramAfter,
    TimeSpan FirstReminderAfter,
    TimeSpan FinalReminderAfter)
{
    public static AttentionEscalationOptions Default { get; } = new(
        TelegramAfter: TimeSpan.FromMinutes(1),
        FirstReminderAfter: TimeSpan.FromMinutes(15),
        FinalReminderAfter: TimeSpan.FromMinutes(60));
}

public enum AttentionAction
{
    /// <summary>Nada a fazer agora (aguardando o próximo marco, ou já esgotado).</summary>
    None,

    /// <summary>T+0: notificação in-app.</summary>
    NotifyInApp,

    /// <summary>T+1m: mensagem de atenção no Telegram.</summary>
    NotifyTelegram,

    /// <summary>T+15m: lembrete no Telegram (ainda sem reconhecimento).</summary>
    RemindTelegram,

    /// <summary>T+60m: lembrete final. Depois dele, silêncio.</summary>
    FinalReminder,
}

public sealed record AttentionDecision(
    AttentionAction Action,
    string NextStatus,
    int NextReminderCount,
    DateTimeOffset? NextActionAt);

public static class AttentionEscalationPolicy
{
    /// <summary>
    /// Decide o próximo passo para um pedido ABERTO. Pura: (estado, agora, opções) → decisão.
    /// Reconhecido (acknowledged) para os lembretes agressivos — a pessoa JÁ VIU; insistir é
    /// ruído. Respondido/superado nunca chega aqui.
    /// </summary>
    public static AttentionDecision Decide(
        string status,
        int reminderCount,
        DateTimeOffset createdAt,
        DateTimeOffset now,
        AttentionEscalationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var age = now - createdAt;

        // Reconhecimento pára a escadinha: quem abriu o pedido sabe dele.
        if (status == "acknowledged")
        {
            return new AttentionDecision(AttentionAction.None, status, reminderCount, null);
        }

        return (status, reminderCount) switch
        {
            // T+0 — in-app imediato; o próximo marco é o Telegram.
            ("open", _) => new AttentionDecision(
                AttentionAction.NotifyInApp, "notified", 0, createdAt + options.TelegramAfter),

            // T+1m — Telegram (uma vez); o próximo marco é o primeiro lembrete.
            ("notified", 0) when age >= options.TelegramAfter => new AttentionDecision(
                AttentionAction.NotifyTelegram, "notified", 1,
                createdAt + options.FirstReminderAfter),

            // T+15m — lembrete, só se continua sem reconhecimento.
            ("notified", 1) when age >= options.FirstReminderAfter => new AttentionDecision(
                AttentionAction.RemindTelegram, "notified", 2,
                createdAt + options.FinalReminderAfter),

            // T+60m — o último. next_action_at nulo = nunca mais.
            ("notified", 2) when age >= options.FinalReminderAfter => new AttentionDecision(
                AttentionAction.FinalReminder, "notified", 3, null),

            _ => new AttentionDecision(AttentionAction.None, status, reminderCount, null),
        };
    }
}
