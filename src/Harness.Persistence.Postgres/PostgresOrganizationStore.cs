using System.Text.Json;
using Harness.Persistence.Abstractions.Organizations;
using Npgsql;
using NpgsqlTypes;

namespace Harness.Persistence.Postgres;

public sealed class PostgresOrganizationStore(NpgsqlDataSource dataSource) : IOrganizationStore
{
    private const string SelectSql =
        """
        SELECT tenant_id,id,name,slug,plan,logo_url,primary_color,secondary_color,typography,
               default_workflow_template_ids_json::text,template_keys_json::text,policies_json::text,
               created_at,version
        FROM harness.organizations
        """;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly NpgsqlDataSource _dataSource =
        dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public async Task<OrganizationRecord?> GetAsync(
        string tenantId,
        string organizationId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        return await ReadAsync(connection, tenantId, organizationId, cancellationToken);
    }

    public async Task<IReadOnlyList<OrganizationRecord>> ListAsync(
        string tenantId,
        string? afterId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var organizations = new List<OrganizationRecord>();
        await using var command = _dataSource.CreateCommand(
            $"{SelectSql} WHERE tenant_id=$1 AND ($2::text IS NULL OR id>$2) ORDER BY id LIMIT $3;");
        command.Parameters.Add(Text(tenantId));
        command.Parameters.Add(NullableText(afterId));
        command.Parameters.Add(Integer(limit));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            organizations.Add(Read(reader));
        }

        return organizations;
    }

    public Task<OrganizationMutationResult> CreateAsync(
        OrganizationCreateCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return CreateCoreAsync(command, cancellationToken);
    }

    public Task<OrganizationMutationResult> UpdateAsync(
        OrganizationUpdateCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return UpdateCoreAsync(command, cancellationToken);
    }

    private async Task<OrganizationMutationResult> CreateCoreAsync(
        OrganizationCreateCommand command,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var insert = connection.CreateCommand();
            insert.CommandText =
                """
                INSERT INTO harness.organizations
                    (id,tenant_id,name,slug,plan,logo_url,primary_color,secondary_color,typography,
                     default_workflow_template_ids_json,template_keys_json,policies_json,version,created_at)
                VALUES
                    ($1,$2,$3,$4,$5,$6,$7,$8,$9,'[]'::jsonb,'[]'::jsonb,'[]'::jsonb,1,$10);
                """;
            Bind(insert, command.OrganizationId, command.TenantId, command.Name, command.Slug,
                command.Plan, command.Brand);
            insert.Parameters.Add(Timestamp(command.OccurredAt));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (PostgresException exception) when (IsConstraintViolation(exception))
        {
            return new OrganizationMutationResult(OrganizationMutationStatus.AlreadyExists);
        }

        return new OrganizationMutationResult(
            OrganizationMutationStatus.Applied,
            await ReadAsync(connection, command.TenantId, command.OrganizationId, cancellationToken));
    }

    private async Task<OrganizationMutationResult> UpdateCoreAsync(
        OrganizationUpdateCommand command,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var update = connection.CreateCommand();
            update.CommandText =
                """
                UPDATE harness.organizations
                SET name=$3,slug=$4,plan=$5,logo_url=$6,primary_color=$7,secondary_color=$8,typography=$9,
                    version=version+1
                WHERE tenant_id=$2 AND id=$1 AND version=$10;
                """;
            Bind(update, command.OrganizationId, command.TenantId, command.Name, command.Slug,
                command.Plan, command.Brand);
            update.Parameters.Add(Bigint(command.ExpectedVersion));
            if (await update.ExecuteNonQueryAsync(cancellationToken) == 1)
            {
                return new OrganizationMutationResult(
                    OrganizationMutationStatus.Applied,
                    await ReadAsync(connection, command.TenantId, command.OrganizationId, cancellationToken));
            }
        }
        catch (PostgresException exception) when (IsConstraintViolation(exception))
        {
            return new OrganizationMutationResult(OrganizationMutationStatus.AlreadyExists);
        }

        return new OrganizationMutationResult(
            await ReadAsync(connection, command.TenantId, command.OrganizationId, cancellationToken) is null
                ? OrganizationMutationStatus.NotFound
                : OrganizationMutationStatus.VersionConflict);
    }

    private static async Task<OrganizationRecord?> ReadAsync(
        NpgsqlConnection connection,
        string tenantId,
        string organizationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"{SelectSql} WHERE tenant_id=$1 AND id=$2;";
        command.Parameters.Add(Text(tenantId));
        command.Parameters.Add(Text(organizationId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    private static OrganizationRecord Read(NpgsqlDataReader reader) =>
        new(
            reader.GetString(0).TrimEnd(),
            reader.GetString(1).TrimEnd(),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            new OrganizationBrandRecord(
                Nullable(reader, 5),
                Nullable(reader, 6),
                Nullable(reader, 7),
                Nullable(reader, 8)),
            Deserialize<string>(reader.GetString(9)),
            Deserialize<string>(reader.GetString(10)),
            Deserialize<OrganizationPolicyRecord>(reader.GetString(11)),
            reader.GetFieldValue<DateTimeOffset>(12),
            reader.GetInt64(13));

    private static T[] Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T[]>(json, JsonOptions)
        ?? throw new InvalidOperationException("Persisted organization JSON cannot be null.");

    private static string? Nullable(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static void Bind(
        NpgsqlCommand command,
        string id,
        string tenantId,
        string name,
        string slug,
        string plan,
        OrganizationBrandRecord brand)
    {
        command.Parameters.Add(Text(id));
        command.Parameters.Add(Text(tenantId));
        command.Parameters.Add(Text(name));
        command.Parameters.Add(Text(slug));
        command.Parameters.Add(Text(plan));
        command.Parameters.Add(NullableText(brand.LogoUrl));
        command.Parameters.Add(NullableText(brand.PrimaryColor));
        command.Parameters.Add(NullableText(brand.SecondaryColor));
        command.Parameters.Add(NullableText(brand.Typography));
    }

    private static bool IsConstraintViolation(PostgresException exception) =>
        exception.SqlState.StartsWith("23", StringComparison.Ordinal);

    private static NpgsqlParameter<string> Text(string value) => new() { TypedValue = value };

    private static NpgsqlParameter<int> Integer(int value) => new() { TypedValue = value };

    private static NpgsqlParameter<long> Bigint(long value) => new() { TypedValue = value };

    private static NpgsqlParameter<DateTimeOffset> Timestamp(DateTimeOffset value) =>
        new() { TypedValue = value };

    private static NpgsqlParameter NullableText(string? value) => new()
    {
        NpgsqlDbType = NpgsqlDbType.Text,
        Value = (object?)value ?? DBNull.Value,
    };
}
