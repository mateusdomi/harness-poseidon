using Harness.Persistence.Abstractions.Workflows;

namespace Harness.Persistence.Sqlite;

public sealed class SqliteWorkflowDocumentTemplateStore(SqliteWriteDispatcher dispatcher)
    : IWorkflowDocumentTemplateStore
{
    private readonly SqliteWriteDispatcher _dispatcher =
        dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    public Task<IReadOnlyList<WorkflowDocumentTemplateRecord>> ListAsync(
        CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync<IReadOnlyList<WorkflowDocumentTemplateRecord>>(async (connection, token) =>
        {
            var values = new List<WorkflowDocumentTemplateRecord>();
            await using var query = connection.CreateCommand();
            query.CommandText =
                "SELECT code,name,phase,target_card_type,required_fields_json,metric_formats_json,guidance " +
                "FROM workflow_document_templates ORDER BY phase, code;";
            await using var reader = await query.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                values.Add(new WorkflowDocumentTemplateRecord(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), reader.GetString(4),
                    reader.GetString(5), reader.GetString(6)));
            }

            return values;
        }, cancellationToken);
}
