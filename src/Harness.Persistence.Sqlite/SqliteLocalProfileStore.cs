using System.Globalization;
using Harness.Persistence.Abstractions.Identity;
using Microsoft.Data.Sqlite;

namespace Harness.Persistence.Sqlite;

public sealed class SqliteLocalProfileStore(SqliteWriteDispatcher dispatcher) : ILocalProfileStore
{
    private readonly SqliteWriteDispatcher _dispatcher =
        dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    public Task<LocalProfileRecord?> GetAsync(
        string profileId,
        CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(
            (connection, token) => ReadAsync(connection, profileId, token),
            cancellationToken);

    public Task<IReadOnlyList<LocalProfileRecord>> ListAsync(
        CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync<IReadOnlyList<LocalProfileRecord>>(
            async (connection, token) =>
            {
                var profiles = new List<LocalProfileRecord>();
                await using var command = connection.CreateCommand();
                command.CommandText = $"{SelectSql} ORDER BY id;";
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    profiles.Add(Read(reader));
                }

                return profiles;
            },
            cancellationToken);

    public Task<LocalProfileMutationResult> CreateAsync(
        LocalProfileCreateCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _dispatcher.ExecuteAsync(
            (connection, token) => CreateCoreAsync(connection, command, token),
            cancellationToken);
    }

    public Task<LocalProfileMutationResult> UpdateAsync(
        LocalProfileUpdateCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _dispatcher.ExecuteAsync(
            (connection, token) => UpdateCoreAsync(connection, command, token),
            cancellationToken);
    }

    private static async Task<LocalProfileMutationResult> CreateCoreAsync(
        SqliteConnection connection,
        LocalProfileCreateCommand command,
        CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        if (command.JoinExistingTenant)
        {
            await using var tenantExists = connection.CreateCommand();
            tenantExists.Transaction = transaction;
            tenantExists.CommandText = "SELECT EXISTS(SELECT 1 FROM tenants WHERE id=$id);";
            Add(tenantExists, "$id", command.TenantId);
            if (Convert.ToInt64(await tenantExists.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 0)
            {
                await transaction.CommitAsync(cancellationToken);
                return new LocalProfileMutationResult(LocalProfileMutationStatus.NotFound);
            }
        }
        else
        {
            await using (var exists = connection.CreateCommand())
            {
                exists.Transaction = transaction;
                exists.CommandText = "SELECT EXISTS(SELECT 1 FROM local_users);";
                if (Convert.ToInt64(await exists.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1)
                {
                    await transaction.CommitAsync(cancellationToken);
                    return new LocalProfileMutationResult(LocalProfileMutationStatus.AlreadyExists);
                }
            }

            await using (var tenant = connection.CreateCommand())
            {
                tenant.Transaction = transaction;
                tenant.CommandText =
                    "INSERT INTO tenants (id,name,version,created_at) VALUES ($id,$name,1,$createdAt);";
                Add(tenant, "$id", command.TenantId);
                Add(tenant, "$name", command.TenantName);
                Add(tenant, "$createdAt", Store(command.OccurredAt));
                await tenant.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await using (var profile = connection.CreateCommand())
        {
            profile.Transaction = transaction;
            profile.CommandText =
                """
                INSERT INTO local_users
                    (id,tenant_id,display_name,email,avatar_url,locale,last_active_at,version,created_at,role)
                VALUES ($id,$tenantId,$displayName,$email,$avatarUrl,$locale,$occurredAt,1,$occurredAt,$role);
                """;
            Bind(profile, command.ProfileId, command.TenantId, command.DisplayName, command.Email,
                command.AvatarUrl, command.Locale, command.OccurredAt);
            Add(profile, "$role", LocalProfileRoleCodec.ToStorage(
                command.JoinExistingTenant ? LocalProfileRole.Member : LocalProfileRole.Admin));
            await profile.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var settings = connection.CreateCommand())
        {
            settings.Transaction = transaction;
            settings.CommandText =
                "INSERT INTO profile_settings (tenant_id,id,profile_id,language,updated_at) VALUES ($tenantId,$id,$id,$locale,$occurredAt);";
            Add(settings, "$tenantId", command.TenantId);
            Add(settings, "$id", command.ProfileId);
            Add(settings, "$locale", command.Locale);
            Add(settings, "$occurredAt", Store(command.OccurredAt));
            await settings.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new LocalProfileMutationResult(
            LocalProfileMutationStatus.Applied,
            await ReadAsync(connection, command.ProfileId, cancellationToken));
    }

    private static async Task<LocalProfileMutationResult> UpdateCoreAsync(
        SqliteConnection connection,
        LocalProfileUpdateCommand command,
        CancellationToken cancellationToken)
    {
        await using var update = connection.CreateCommand();
        update.CommandText =
            """
            UPDATE local_users
            SET display_name=$displayName,email=$email,avatar_url=$avatarUrl,locale=$locale,
                last_active_at=$occurredAt,version=version+1
            WHERE id=$id AND version=$expectedVersion;
            """;
        Add(update, "$displayName", command.DisplayName);
        Add(update, "$email", command.Email is null ? DBNull.Value : command.Email);
        Add(update, "$avatarUrl", command.AvatarUrl is null ? DBNull.Value : command.AvatarUrl);
        Add(update, "$locale", command.Locale);
        Add(update, "$occurredAt", Store(command.OccurredAt));
        Add(update, "$id", command.ProfileId);
        Add(update, "$expectedVersion", command.ExpectedVersion);
        if (await update.ExecuteNonQueryAsync(cancellationToken) == 1)
        {
            return new LocalProfileMutationResult(
                LocalProfileMutationStatus.Applied,
                await ReadAsync(connection, command.ProfileId, cancellationToken));
        }

        return new LocalProfileMutationResult(
            await ReadAsync(connection, command.ProfileId, cancellationToken) is null
                ? LocalProfileMutationStatus.NotFound
                : LocalProfileMutationStatus.VersionConflict);
    }

    private static async Task<LocalProfileRecord?> ReadAsync(
        SqliteConnection connection,
        string profileId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"{SelectSql} WHERE id=$id;";
        Add(command, "$id", profileId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    private static LocalProfileRecord Read(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetString(5),
            DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture),
            reader.GetInt64(8),
            LocalProfileRoleCodec.Parse(reader.GetString(9)));

    private static void Bind(
        SqliteCommand command,
        string id,
        string tenantId,
        string displayName,
        string? email,
        string? avatarUrl,
        string locale,
        DateTimeOffset occurredAt)
    {
        Add(command, "$id", id);
        Add(command, "$tenantId", tenantId);
        Add(command, "$displayName", displayName);
        Add(command, "$email", email is null ? DBNull.Value : email);
        Add(command, "$avatarUrl", avatarUrl is null ? DBNull.Value : avatarUrl);
        Add(command, "$locale", locale);
        Add(command, "$occurredAt", Store(occurredAt));
    }

    private const string SelectSql =
        "SELECT tenant_id,id,display_name,email,avatar_url,locale,created_at,COALESCE(last_active_at,created_at),version,role FROM local_users";

    private static string Store(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static void Add(SqliteCommand command, string name, object value) =>
        command.Parameters.AddWithValue(name, value);
}
