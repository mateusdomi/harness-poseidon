using System.Globalization;
using System.Text;
using Harness.Modules.Projects.Contracts;

namespace Harness.Modules.Projects.Application;

/// <summary>
/// CAT-07 — evento bruto de atividade, materializado pela camada de leitura a partir de UMA linha
/// durável. É a entrada PURA do projetor: não há I/O aqui. <see cref="SortId"/> é o id estável da
/// linha de origem, usado como critério de desempate determinístico quando dois eventos ocorrem no
/// mesmo instante.
/// </summary>
public sealed record ProjectActivityEvent(
    string SortId,
    string Kind,
    DateTimeOffset OccurredAt,
    string EntityKind,
    string EntityId,
    string? Title = null,
    string? State = null,
    string? AgentId = null,
    int? AttemptNumber = null);

/// <summary>
/// Projetor PURO e determinístico do read-model de atividade (CAT-07): ordena os eventos do mais
/// recente para o mais antigo, aplica paginação por cursor keyset (instante + id, para ser estável
/// mesmo com empates de timestamp) e humaniza cada evento. Não inventa nada — só reflete o que já
/// foi gravado. Testável isoladamente, sem banco.
/// </summary>
public static class ProjectActivityProjector
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    public static ProjectActivityPage Project(
        string projectId, IReadOnlyList<ProjectActivityEvent> events, string? cursor, int limit)
    {
        ArgumentNullException.ThrowIfNull(events);
        var effectiveLimit = Math.Clamp(limit, 1, MaxLimit);

        var ordered = events
            .OrderByDescending(activity => activity.OccurredAt)
            .ThenByDescending(activity => activity.SortId, StringComparer.Ordinal)
            .ToArray();

        IEnumerable<ProjectActivityEvent> filtered = ordered;
        if (ActivityCursor.TryDecode(cursor, out var at, out var id))
        {
            // Keyset: apenas eventos ESTRITAMENTE mais antigos que o cursor, na mesma ordem total
            // (instante desc, depois id desc). Sem offset — a paginação é estável sob inserções.
            filtered = ordered.Where(activity =>
                activity.OccurredAt < at
                || (activity.OccurredAt == at && string.CompareOrdinal(activity.SortId, id) < 0));
        }

        var window = filtered.Take(effectiveLimit + 1).ToArray();
        var hasMore = window.Length > effectiveLimit;
        var pageEvents = hasMore ? window[..effectiveLimit] : window;
        var items = pageEvents.Select(ToItem).ToArray();
        var next = hasMore
            ? ActivityCursor.Encode(pageEvents[^1].OccurredAt, pageEvents[^1].SortId)
            : null;

        return new ProjectActivityPage(projectId, items, next, items.Length);
    }

    /// <summary>Um cursor é válido quando ausente (primeira página) ou decodificável.</summary>
    public static bool IsValidCursor(string? cursor) =>
        string.IsNullOrEmpty(cursor) || ActivityCursor.TryDecode(cursor, out _, out _);

    private static ProjectActivityItem ToItem(ProjectActivityEvent activity) =>
        new(activity.Kind, Summarize(activity), activity.OccurredAt,
            activity.EntityKind, activity.EntityId, activity.State, activity.AgentId);

    private static string Summarize(ProjectActivityEvent activity)
    {
        var title = string.IsNullOrWhiteSpace(activity.Title) ? activity.EntityId : activity.Title!.Trim();
        return activity.Kind switch
        {
            "demand_created" => $"Demand '{title}' was created.",
            "task_created" => $"Task '{title}' was created.",
            "task_updated" => $"Task '{title}' moved to {activity.State}.",
            "solicitation_created" => $"Request '{title}' was submitted.",
            "attempt_started" => $"Attempt #{activity.AttemptNumber} started on task '{title}'.",
            "attempt_finished" => $"Attempt #{activity.AttemptNumber} on task '{title}' ended as {activity.State}.",
            "forecast_recorded" => "A delivery forecast was recorded.",
            _ => $"{activity.Kind} on {activity.EntityKind} {activity.EntityId}.",
        };
    }

    /// <summary>
    /// Cursor opaco keyset = (instante em ticks UTC, id de origem), codificado em base64url sem
    /// padding. Determinístico e reversível; um valor corrompido simplesmente não decodifica.
    /// </summary>
    internal static class ActivityCursor
    {
        public static string Encode(DateTimeOffset occurredAt, string id)
        {
            var raw = $"{occurredAt.UtcTicks.ToString(CultureInfo.InvariantCulture)}:{id}";
            var bytes = Encoding.UTF8.GetBytes(raw);
            return Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        public static bool TryDecode(string? cursor, out DateTimeOffset occurredAt, out string id)
        {
            occurredAt = default;
            id = string.Empty;
            if (string.IsNullOrEmpty(cursor))
            {
                return false;
            }

            try
            {
                var normalized = cursor.Replace('-', '+').Replace('_', '/');
                switch (normalized.Length % 4)
                {
                    case 2: normalized += "=="; break;
                    case 3: normalized += "="; break;
                }

                var raw = Encoding.UTF8.GetString(Convert.FromBase64String(normalized));
                var separator = raw.IndexOf(':', StringComparison.Ordinal);
                if (separator <= 0 || separator == raw.Length - 1)
                {
                    return false;
                }

                if (!long.TryParse(
                        raw.AsSpan(0, separator), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks)
                    || ticks < 0 || ticks > DateTimeOffset.MaxValue.UtcTicks)
                {
                    return false;
                }

                occurredAt = new DateTimeOffset(ticks, TimeSpan.Zero);
                id = raw[(separator + 1)..];
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }
    }
}
