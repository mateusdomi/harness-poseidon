using Harness.Persistence.Abstractions.Workflows;
using Npgsql;

namespace Harness.Persistence.Postgres;

public sealed class PostgresWorkflowDocumentTemplateStore(NpgsqlDataSource dataSource)
    : IWorkflowDocumentTemplateStore
{
    private readonly NpgsqlDataSource _dataSource =
        dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public async Task<IReadOnlyList<WorkflowDocumentTemplateRecord>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var values = new List<WorkflowDocumentTemplateRecord>();
        await using var query = _dataSource.CreateCommand(
            "SELECT code,name,phase,target_card_type,required_fields_json::text," +
            "metric_formats_json::text,guidance " +
            "FROM harness.workflow_document_templates ORDER BY phase, code;");
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(new WorkflowDocumentTemplateRecord(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4),
                reader.GetString(5), reader.GetString(6)));
        }

        return values;
    }
}
