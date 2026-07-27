using System.Collections.Concurrent;
using Harness.SharedKernel.Identifiers;
using Npgsql;

namespace Harness.Persistence.Postgres;

/// <summary>
/// Selects a connection pool whose PostgreSQL startup options are permanently
/// constrained to the ambient Host tenant and the non-bypass runtime role.
/// The owner data source remains available only when no tenant scope exists,
/// which is required for migrations and trusted bootstrap/background routines.
/// </summary>
public sealed class PostgresTenantDataSource
{
    private static readonly AsyncLocal<TenantScope?> CurrentScope = new();
    private static readonly ConcurrentDictionary<string, NpgsqlDataSource> TenantPools =
        new(StringComparer.Ordinal);

    private readonly NpgsqlDataSource _owner;

    private PostgresTenantDataSource(NpgsqlDataSource owner)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
    }

    public static implicit operator PostgresTenantDataSource(NpgsqlDataSource owner) =>
        new(owner);

    public static IDisposable Enter(string tenantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        if (!UlidValue.TryParse(tenantId, out _))
        {
            throw new ArgumentException("Tenant id must be a valid ULID.", nameof(tenantId));
        }

        var previous = CurrentScope.Value;
        var current = new TenantScope(tenantId, previous);
        CurrentScope.Value = current;
        return new ScopeLease(current);
    }

    public ValueTask<NpgsqlConnection> OpenConnectionAsync(
        CancellationToken cancellationToken = default) =>
        SelectedDataSource().OpenConnectionAsync(cancellationToken);

    public NpgsqlCommand CreateCommand(string commandText) =>
        SelectedDataSource().CreateCommand(commandText);

    private NpgsqlDataSource SelectedDataSource()
    {
        var tenantId = CurrentScope.Value?.TenantId;
        if (tenantId is null)
        {
            return _owner;
        }

        var key = $"{_owner.ConnectionString}\n{tenantId}";
        return TenantPools.GetOrAdd(
            key,
            _ =>
            {
                var connection = new NpgsqlConnectionStringBuilder(_owner.ConnectionString)
                {
                    Options =
                        $"-c role=poseidon_runtime -c poseidon.tenant_id={tenantId}",
                };
                return NpgsqlDataSource.Create(connection.ConnectionString);
            });
    }

    private sealed record TenantScope(string TenantId, TenantScope? Previous);

    private sealed class ScopeLease(TenantScope scope) : IDisposable
    {
        private TenantScope? _scope = scope;

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _scope, null);
            if (current is null)
            {
                return;
            }

            if (!ReferenceEquals(CurrentScope.Value, current))
            {
                throw new InvalidOperationException(
                    "PostgreSQL tenant scopes must be disposed in reverse order.");
            }

            CurrentScope.Value = current.Previous;
        }
    }
}
