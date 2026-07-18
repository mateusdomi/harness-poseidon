using System.Globalization;
using System.Text.Json;
using Harness.Persistence.Abstractions.Organizations;
using Microsoft.Data.Sqlite;

namespace Harness.Persistence.Sqlite;

public sealed class SqliteOrganizationStore(SqliteWriteDispatcher dispatcher) : IOrganizationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly SqliteWriteDispatcher _dispatcher =
        dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    public Task<OrganizationRecord?> GetAsync(
        string tenantId,
        string organizationId,
        CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(
            (connection, token) => ReadAsync(connection, tenantId, organizationId, token),
            cancellationToken);

    public Task<IReadOnlyList<OrganizationRecord>> ListAsync(
        string tenantId,
        string? afterId,
        int limit,
        CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync<IReadOnlyList<OrganizationRecord>>(
            async (connection, token) =>
            {
                var organizations = new List<OrganizationRecord>();
                await using var command = connection.CreateCommand();
                command.CommandText =
                    $"{SelectSql} WHERE tenant_id=$tenantId AND ($afterId IS NULL OR id>$afterId) ORDER BY id LIMIT $limit;";
                Add(command, "$tenantId", tenantId);
                Add(command, "$afterId", afterId is null ? DBNull.Value : afterId);
                Add(command, "$limit", limit);
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    organizations.Add(Read(reader));
                }

                return organizations;
            },
            cancellationToken);

    public Task<OrganizationMutationResult> CreateAsync(
        OrganizationCreateCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _dispatcher.ExecuteAsync(
            (connection, token) => CreateCoreAsync(connection, command, token),
            cancellationToken);
    }

    public Task<OrganizationMutationResult> UpdateAsync(
        OrganizationUpdateCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _dispatcher.ExecuteAsync(
            (connection, token) => UpdateCoreAsync(connection, command, token),
            cancellationToken);
    }

    private static async Task<OrganizationMutationResult> CreateCoreAsync(
        SqliteConnection connection,
        OrganizationCreateCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var insert = connection.CreateCommand();
            insert.CommandText =
                """
                INSERT INTO organizations
                    (id,tenant_id,name,slug,plan,logo_url,primary_color,secondary_color,typography,
                     default_workflow_template_ids_json,template_keys_json,policies_json,version,created_at)
                VALUES
                    ($id,$tenantId,$name,$slug,$plan,$logoUrl,$primaryColor,$secondaryColor,$typography,
                     '[]','[]','[]',1,$createdAt);
                """;
            Bind(insert, command.OrganizationId, command.TenantId, command.Name, command.Slug,
                command.Plan, command.Brand);
            Add(insert, "$createdAt", Store(command.OccurredAt));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            return new OrganizationMutationResult(OrganizationMutationStatus.AlreadyExists);
        }

        return new OrganizationMutationResult(
            OrganizationMutationStatus.Applied,
            await ReadAsync(connection, command.TenantId, command.OrganizationId, cancellationToken));
    }

    private static async Task<OrganizationMutationResult> UpdateCoreAsync(
        SqliteConnection connection,
        OrganizationUpdateCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var update = connection.CreateCommand();
            update.CommandText =
                """
                UPDATE organizations
                SET name=$name,slug=$slug,plan=$plan,logo_url=$logoUrl,
                    primary_color=$primaryColor,secondary_color=$secondaryColor,typography=$typography,
                    version=version+1
                WHERE tenant_id=$tenantId AND id=$id AND version=$expectedVersion;
                """;
            Bind(update, command.OrganizationId, command.TenantId, command.Name, command.Slug,
                command.Plan, command.Brand);
            Add(update, "$expectedVersion", command.ExpectedVersion);
            if (await update.ExecuteNonQueryAsync(cancellationToken) == 1)
            {
                return new OrganizationMutationResult(
                    OrganizationMutationStatus.Applied,
                    await ReadAsync(connection, command.TenantId, command.OrganizationId, cancellationToken));
            }
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            return new OrganizationMutationResult(OrganizationMutationStatus.AlreadyExists);
        }

        return new OrganizationMutationResult(
            await ReadAsync(connection, command.TenantId, command.OrganizationId, cancellationToken) is null
                ? OrganizationMutationStatus.NotFound
                : OrganizationMutationStatus.VersionConflict);
    }

    private static async Task<OrganizationRecord?> ReadAsync(
        SqliteConnection connection,
        string tenantId,
        string organizationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"{SelectSql} WHERE tenant_id=$tenantId AND id=$id;";
        Add(command, "$tenantId", tenantId);
        Add(command, "$id", organizationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    private static OrganizationRecord Read(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
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
            DateTimeOffset.Parse(reader.GetString(12), CultureInfo.InvariantCulture),
            reader.GetInt64(13));

    private static T[] Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T[]>(json, JsonOptions)
        ?? throw new InvalidOperationException("Persisted organization JSON cannot be null.");

    private static string? Nullable(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static void Bind(
        SqliteCommand command,
        string id,
        string tenantId,
        string name,
        string slug,
        string plan,
        OrganizationBrandRecord brand)
    {
        Add(command, "$id", id);
        Add(command, "$tenantId", tenantId);
        Add(command, "$name", name);
        Add(command, "$slug", slug);
        Add(command, "$plan", plan);
        Add(command, "$logoUrl", brand.LogoUrl is null ? DBNull.Value : brand.LogoUrl);
        Add(command, "$primaryColor", brand.PrimaryColor is null ? DBNull.Value : brand.PrimaryColor);
        Add(command, "$secondaryColor", brand.SecondaryColor is null ? DBNull.Value : brand.SecondaryColor);
        Add(command, "$typography", brand.Typography is null ? DBNull.Value : brand.Typography);
    }

    private const string SelectSql =
        """
        SELECT tenant_id,id,name,slug,plan,logo_url,primary_color,secondary_color,typography,
               default_workflow_template_ids_json,template_keys_json,policies_json,created_at,version
        FROM organizations
        """;

    private static string Store(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static void Add(SqliteCommand command, string name, object value) =>
        command.Parameters.AddWithValue(name, value);
}
