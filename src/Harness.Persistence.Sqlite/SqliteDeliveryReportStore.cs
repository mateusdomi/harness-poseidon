using System.Globalization;
using Harness.Persistence.Abstractions.Delivery;
using Microsoft.Data.Sqlite;

namespace Harness.Persistence.Sqlite;

/// <summary>
/// Persistência SQLite dos relatórios de entrega (DEL-04/DEL-05/DEL-10). O conteúdo do relatório é
/// imutável; só o ciclo de vida avança por UPDATE CONDICIONAL (guardado pelo status atual), e os
/// envios são INSERT append-only. O destinatário gravado é sempre a referência OPACA.
/// </summary>
public sealed class SqliteDeliveryReportStore(SqliteWriteDispatcher dispatcher) : IDeliveryReportStore
{
    private readonly SqliteWriteDispatcher _dispatcher =
        dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    public Task<DeliveryReportRecord> CreateAsync(
        DeliveryReportCreateCommand command, CancellationToken cancellationToken = default)
    {
        Validate(command);
        return _dispatcher.ExecuteAsync(async (c, t) =>
        {
            await using var insert = c.CreateCommand();
            insert.CommandText =
                """
                INSERT INTO delivery_reports
                    (id,tenant_id,project_id,type,format,status,audience,classification,version,
                     content_type,content,data_snapshot,approved_by,approved_at,sent_at,created_at)
                VALUES ($id,$tenant,$project,$type,$format,'draft',$audience,$classification,$version,
                     $contentType,$content,$snapshot,NULL,NULL,NULL,$createdAt);
                """;
            Add(insert, "$id", command.Id);
            Add(insert, "$tenant", command.TenantId);
            Add(insert, "$project", command.ProjectId);
            Add(insert, "$type", command.Type);
            Add(insert, "$format", command.Format);
            Add(insert, "$audience", command.Audience);
            Add(insert, "$classification", command.Classification);
            Add(insert, "$version", command.Version);
            Add(insert, "$contentType", command.ContentType);
            Add(insert, "$content", command.Content);
            Add(insert, "$snapshot", command.DataSnapshotJson);
            Add(insert, "$createdAt", Store(command.CreatedAt));
            await insert.ExecuteNonQueryAsync(t);
            return new DeliveryReportRecord(
                command.TenantId, command.Id, command.ProjectId, command.Type, command.Format,
                "draft", command.Audience, command.Classification, command.Version, command.ContentType,
                command.Content, command.DataSnapshotJson, null, null, null, command.CreatedAt);
        }, cancellationToken);
    }

    public Task<DeliveryReportRecord?> GetAsync(
        string tenantId, string projectId, string reportId, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(async (c, t) =>
        {
            await using var q = c.CreateCommand();
            q.CommandText = SelectFull +
                "WHERE tenant_id=$tenant AND project_id=$project AND id=$id LIMIT 1;";
            Add(q, "$tenant", tenantId);
            Add(q, "$project", projectId);
            Add(q, "$id", reportId);
            await using var r = await q.ExecuteReaderAsync(t);
            return await r.ReadAsync(t) ? ReadFull(r) : null;
        }, cancellationToken);

    public Task<IReadOnlyList<DeliveryReportRecord>> ListByProjectAsync(
        string tenantId, string projectId, int limit, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(async (c, t) =>
        {
            await using var q = c.CreateCommand();
            q.CommandText = SelectMeta +
                "WHERE tenant_id=$tenant AND project_id=$project ORDER BY created_at DESC, id DESC LIMIT $limit;";
            Add(q, "$tenant", tenantId);
            Add(q, "$project", projectId);
            Add(q, "$limit", Math.Clamp(limit, 1, 500));
            var results = new List<DeliveryReportRecord>();
            await using var r = await q.ExecuteReaderAsync(t);
            while (await r.ReadAsync(t))
            {
                results.Add(ReadMeta(r));
            }

            return (IReadOnlyList<DeliveryReportRecord>)results;
        }, cancellationToken);

    public Task<int> CountByTypeAsync(
        string tenantId, string projectId, string type, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(async (c, t) =>
        {
            await using var q = c.CreateCommand();
            q.CommandText =
                "SELECT COUNT(*) FROM delivery_reports WHERE tenant_id=$tenant AND project_id=$project AND type=$type;";
            Add(q, "$tenant", tenantId);
            Add(q, "$project", projectId);
            Add(q, "$type", type);
            return Convert.ToInt32(await q.ExecuteScalarAsync(t), CultureInfo.InvariantCulture);
        }, cancellationToken);

    public Task<bool> ApproveAsync(
        DeliveryReportApproveCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _dispatcher.ExecuteAsync(async (c, t) =>
        {
            await using var update = c.CreateCommand();
            update.CommandText =
                """
                UPDATE delivery_reports
                SET status='approved', approved_by=$by, approved_at=$at
                WHERE tenant_id=$tenant AND project_id=$project AND id=$id AND status='draft';
                """;
            Add(update, "$by", command.ApprovedBy);
            Add(update, "$at", Store(command.ApprovedAt));
            Add(update, "$tenant", command.TenantId);
            Add(update, "$project", command.ProjectId);
            Add(update, "$id", command.ReportId);
            return await update.ExecuteNonQueryAsync(t) == 1;
        }, cancellationToken);
    }

    public Task<bool> MarkSentAsync(
        DeliveryReportSendCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _dispatcher.ExecuteAsync(async (c, t) =>
        {
            await using var update = c.CreateCommand();
            update.CommandText =
                """
                UPDATE delivery_reports SET status='sent', sent_at=$at
                WHERE tenant_id=$tenant AND project_id=$project AND id=$id AND status='approved';
                """;
            Add(update, "$at", Store(command.SentAt));
            Add(update, "$tenant", command.TenantId);
            Add(update, "$project", command.ProjectId);
            Add(update, "$id", command.ReportId);
            if (await update.ExecuteNonQueryAsync(t) != 1)
            {
                return false;
            }

            await using var audit = c.CreateCommand();
            audit.CommandText =
                """
                INSERT INTO delivery_report_sends
                    (id,tenant_id,report_id,version,channel,recipient_reference,sent_by,result,sent_at)
                VALUES ($id,$tenant,$report,$version,$channel,$recipient,$by,$result,$at);
                """;
            Add(audit, "$id", command.SendId);
            Add(audit, "$tenant", command.TenantId);
            Add(audit, "$report", command.ReportId);
            Add(audit, "$version", command.Version);
            Add(audit, "$channel", command.Channel);
            Add(audit, "$recipient", command.RecipientReference);
            Add(audit, "$by", command.SentBy);
            Add(audit, "$result", command.Result);
            Add(audit, "$at", Store(command.SentAt));
            await audit.ExecuteNonQueryAsync(t);
            return true;
        }, cancellationToken);
    }

    public Task<IReadOnlyList<DeliveryReportSendRecord>> ListSendsAsync(
        string tenantId, string reportId, int limit, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(async (c, t) =>
        {
            await using var q = c.CreateCommand();
            q.CommandText =
                "SELECT tenant_id,id,report_id,version,channel,recipient_reference,sent_by,result,sent_at " +
                "FROM delivery_report_sends WHERE tenant_id=$tenant AND report_id=$report " +
                "ORDER BY sent_at DESC, id DESC LIMIT $limit;";
            Add(q, "$tenant", tenantId);
            Add(q, "$report", reportId);
            Add(q, "$limit", Math.Clamp(limit, 1, 500));
            var results = new List<DeliveryReportSendRecord>();
            await using var r = await q.ExecuteReaderAsync(t);
            while (await r.ReadAsync(t))
            {
                results.Add(new DeliveryReportSendRecord(
                    r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetString(4),
                    r.GetString(5), r.GetString(6), r.GetString(7), Parse(r.GetString(8))));
            }

            return (IReadOnlyList<DeliveryReportSendRecord>)results;
        }, cancellationToken);

    private const string SelectFull =
        "SELECT tenant_id,id,project_id,type,format,status,audience,classification,version," +
        "content_type,content,data_snapshot,approved_by,approved_at,sent_at,created_at FROM delivery_reports ";

    private const string SelectMeta =
        "SELECT tenant_id,id,project_id,type,format,status,audience,classification,version," +
        "content_type,approved_by,approved_at,sent_at,created_at, (content IS NOT NULL) FROM delivery_reports ";

    private static DeliveryReportRecord ReadFull(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
        r.GetString(6), r.GetString(7), r.GetInt32(8), r.GetString(9),
        r.IsDBNull(10) ? null : r.GetString(10),
        r.IsDBNull(11) ? null : r.GetString(11),
        r.IsDBNull(12) ? null : r.GetString(12),
        r.IsDBNull(13) ? null : Parse(r.GetString(13)),
        r.IsDBNull(14) ? null : Parse(r.GetString(14)),
        Parse(r.GetString(15)));

    private static DeliveryReportRecord ReadMeta(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
        r.GetString(6), r.GetString(7), r.GetInt32(8), r.GetString(9),
        // Meta view carrega apenas a disponibilidade (content IS NOT NULL) — nunca o corpo nem a foto.
        r.GetInt32(14) == 1 ? string.Empty : null,
        DataSnapshotJson: null,
        r.IsDBNull(10) ? null : r.GetString(10),
        r.IsDBNull(11) ? null : Parse(r.GetString(11)),
        r.IsDBNull(12) ? null : Parse(r.GetString(12)),
        Parse(r.GetString(13)));

    private static void Validate(DeliveryReportCreateCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.TenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ProjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Type);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Format);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Audience);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Classification);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ContentType);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.DataSnapshotJson);
        ArgumentOutOfRangeException.ThrowIfLessThan(command.Version, 1);
    }

    private static void Add(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static string Store(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
