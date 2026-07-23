using Harness.Persistence.Abstractions.Delivery;
using Npgsql;
using NpgsqlTypes;

namespace Harness.Persistence.Postgres;

/// <summary>
/// Persistência PostgreSQL dos relatórios de entrega (DEL-04/DEL-05/DEL-10). Espelha o store SQLite:
/// conteúdo imutável, ciclo de vida por UPDATE condicional guardado pelo status, e envios append-only
/// com o destinatário sempre gravado como referência OPACA.
/// </summary>
public sealed class PostgresDeliveryReportStore(NpgsqlDataSource dataSource) : IDeliveryReportStore
{
    private readonly NpgsqlDataSource _dataSource =
        dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public async Task<DeliveryReportRecord> CreateAsync(
        DeliveryReportCreateCommand command, CancellationToken cancellationToken = default)
    {
        Validate(command);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var insert = connection.CreateCommand();
        insert.CommandText =
            """
            INSERT INTO harness.delivery_reports
                (id,tenant_id,project_id,type,format,status,audience,classification,version,
                 content_type,content,data_snapshot,approved_by,approved_at,sent_at,created_at)
            VALUES ($1,$2,$3,$4,$5,'draft',$6,$7,$8,$9,$10,$11,NULL,NULL,NULL,$12);
            """;
        insert.Parameters.Add(Text(command.Id));
        insert.Parameters.Add(Text(command.TenantId));
        insert.Parameters.Add(Text(command.ProjectId));
        insert.Parameters.Add(Text(command.Type));
        insert.Parameters.Add(Text(command.Format));
        insert.Parameters.Add(Text(command.Audience));
        insert.Parameters.Add(Text(command.Classification));
        insert.Parameters.Add(new NpgsqlParameter<int> { TypedValue = command.Version });
        insert.Parameters.Add(Text(command.ContentType));
        insert.Parameters.Add(NullableText(command.Content));
        insert.Parameters.Add(Jsonb(command.DataSnapshotJson));
        insert.Parameters.Add(Timestamp(command.CreatedAt));
        await insert.ExecuteNonQueryAsync(cancellationToken);
        return new DeliveryReportRecord(
            command.TenantId, command.Id, command.ProjectId, command.Type, command.Format, "draft",
            command.Audience, command.Classification, command.Version, command.ContentType,
            command.Content, command.DataSnapshotJson, null, null, null, command.CreatedAt);
    }

    public async Task<DeliveryReportRecord?> GetAsync(
        string tenantId, string projectId, string reportId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var q = connection.CreateCommand();
        q.CommandText = SelectFull + "WHERE tenant_id=$1 AND project_id=$2 AND id=$3 LIMIT 1;";
        q.Parameters.Add(Text(tenantId));
        q.Parameters.Add(Text(projectId));
        q.Parameters.Add(Text(reportId));
        await using var r = await q.ExecuteReaderAsync(cancellationToken);
        return await r.ReadAsync(cancellationToken) ? ReadFull(r) : null;
    }

    public async Task<IReadOnlyList<DeliveryReportRecord>> ListByProjectAsync(
        string tenantId, string projectId, int limit, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var q = connection.CreateCommand();
        q.CommandText = SelectMeta +
            "WHERE tenant_id=$1 AND project_id=$2 ORDER BY created_at DESC, id DESC LIMIT $3;";
        q.Parameters.Add(Text(tenantId));
        q.Parameters.Add(Text(projectId));
        q.Parameters.Add(new NpgsqlParameter<int> { TypedValue = Math.Clamp(limit, 1, 500) });
        var results = new List<DeliveryReportRecord>();
        await using var r = await q.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken))
        {
            results.Add(ReadMeta(r));
        }

        return results;
    }

    public async Task<int> CountByTypeAsync(
        string tenantId, string projectId, string type, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var q = connection.CreateCommand();
        q.CommandText =
            "SELECT COUNT(*) FROM harness.delivery_reports WHERE tenant_id=$1 AND project_id=$2 AND type=$3;";
        q.Parameters.Add(Text(tenantId));
        q.Parameters.Add(Text(projectId));
        q.Parameters.Add(Text(type));
        return Convert.ToInt32(
            await q.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<bool> ApproveAsync(
        DeliveryReportApproveCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var update = connection.CreateCommand();
        update.CommandText =
            """
            UPDATE harness.delivery_reports
            SET status='approved', approved_by=$1, approved_at=$2
            WHERE tenant_id=$3 AND project_id=$4 AND id=$5 AND status='draft';
            """;
        update.Parameters.Add(Text(command.ApprovedBy));
        update.Parameters.Add(Timestamp(command.ApprovedAt));
        update.Parameters.Add(Text(command.TenantId));
        update.Parameters.Add(Text(command.ProjectId));
        update.Parameters.Add(Text(command.ReportId));
        return await update.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<bool> MarkSentAsync(
        DeliveryReportSendCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText =
                """
                UPDATE harness.delivery_reports SET status='sent', sent_at=$1
                WHERE tenant_id=$2 AND project_id=$3 AND id=$4 AND status='approved';
                """;
            update.Parameters.Add(Timestamp(command.SentAt));
            update.Parameters.Add(Text(command.TenantId));
            update.Parameters.Add(Text(command.ProjectId));
            update.Parameters.Add(Text(command.ReportId));
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }
        }

        await using (var audit = connection.CreateCommand())
        {
            audit.Transaction = transaction;
            audit.CommandText =
                """
                INSERT INTO harness.delivery_report_sends
                    (id,tenant_id,report_id,version,channel,recipient_reference,sent_by,result,sent_at)
                VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9);
                """;
            audit.Parameters.Add(Text(command.SendId));
            audit.Parameters.Add(Text(command.TenantId));
            audit.Parameters.Add(Text(command.ReportId));
            audit.Parameters.Add(new NpgsqlParameter<int> { TypedValue = command.Version });
            audit.Parameters.Add(Text(command.Channel));
            audit.Parameters.Add(Text(command.RecipientReference));
            audit.Parameters.Add(Text(command.SentBy));
            audit.Parameters.Add(Text(command.Result));
            audit.Parameters.Add(Timestamp(command.SentAt));
            await audit.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<DeliveryReportSendRecord>> ListSendsAsync(
        string tenantId, string reportId, int limit, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var q = connection.CreateCommand();
        q.CommandText =
            "SELECT tenant_id,id,report_id,version,channel,recipient_reference,sent_by,result,sent_at " +
            "FROM harness.delivery_report_sends WHERE tenant_id=$1 AND report_id=$2 " +
            "ORDER BY sent_at DESC, id DESC LIMIT $3;";
        q.Parameters.Add(Text(tenantId));
        q.Parameters.Add(Text(reportId));
        q.Parameters.Add(new NpgsqlParameter<int> { TypedValue = Math.Clamp(limit, 1, 500) });
        var results = new List<DeliveryReportSendRecord>();
        await using var r = await q.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken))
        {
            results.Add(new DeliveryReportSendRecord(
                r.GetString(0).TrimEnd(), r.GetString(1).TrimEnd(), r.GetString(2).TrimEnd(),
                r.GetInt32(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7),
                r.GetFieldValue<DateTimeOffset>(8)));
        }

        return results;
    }

    private const string SelectFull =
        "SELECT tenant_id,id,project_id,type,format,status,audience,classification,version," +
        "content_type,content,data_snapshot,approved_by,approved_at,sent_at,created_at FROM harness.delivery_reports ";

    private const string SelectMeta =
        "SELECT tenant_id,id,project_id,type,format,status,audience,classification,version," +
        "content_type,approved_by,approved_at,sent_at,created_at, (content IS NOT NULL) FROM harness.delivery_reports ";

    private static DeliveryReportRecord ReadFull(NpgsqlDataReader r) => new(
        r.GetString(0).TrimEnd(), r.GetString(1).TrimEnd(), r.GetString(2).TrimEnd(), r.GetString(3),
        r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7), r.GetInt32(8), r.GetString(9),
        r.IsDBNull(10) ? null : r.GetString(10),
        r.IsDBNull(11) ? null : r.GetString(11),
        r.IsDBNull(12) ? null : r.GetString(12),
        r.IsDBNull(13) ? null : r.GetFieldValue<DateTimeOffset>(13),
        r.IsDBNull(14) ? null : r.GetFieldValue<DateTimeOffset>(14),
        r.GetFieldValue<DateTimeOffset>(15));

    private static DeliveryReportRecord ReadMeta(NpgsqlDataReader r) => new(
        r.GetString(0).TrimEnd(), r.GetString(1).TrimEnd(), r.GetString(2).TrimEnd(), r.GetString(3),
        r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7), r.GetInt32(8), r.GetString(9),
        r.GetBoolean(14) ? string.Empty : null,
        DataSnapshotJson: null,
        r.IsDBNull(10) ? null : r.GetString(10),
        r.IsDBNull(11) ? null : r.GetFieldValue<DateTimeOffset>(11),
        r.IsDBNull(12) ? null : r.GetFieldValue<DateTimeOffset>(12),
        r.GetFieldValue<DateTimeOffset>(13));

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

    private static NpgsqlParameter<string> Text(string value) => new() { TypedValue = value };

    private static NpgsqlParameter NullableText(string? value) =>
        new() { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)value ?? DBNull.Value };

    private static NpgsqlParameter Jsonb(string value) =>
        new() { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = value };

    private static NpgsqlParameter<DateTimeOffset> Timestamp(DateTimeOffset value) =>
        new() { TypedValue = value };
}
