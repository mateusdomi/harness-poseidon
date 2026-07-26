namespace Harness.Persistence.Migration;

public sealed record AssistedMigrationOptions(
    string SqliteDatabasePath,
    bool Confirmed)
{
    public const string ConnectionEnvironmentVariable = "POSEIDON_POSTGRES_CONNECTION";

    public static AssistedMigrationOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? sqliteDatabasePath = null;
        var confirmed = false;
        for (var index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--sqlite" when index + 1 < args.Count:
                    sqliteDatabasePath = args[++index];
                    break;
                case "--confirm":
                    confirmed = true;
                    break;
                default:
                    throw new ArgumentException(
                        "Uso: --sqlite <arquivo> --confirm. A conexão vem somente do ambiente.");
            }
        }

        if (string.IsNullOrWhiteSpace(sqliteDatabasePath))
        {
            throw new ArgumentException("O arquivo SQLite de origem é obrigatório.");
        }

        if (!confirmed)
        {
            throw new ArgumentException(
                "A migração altera o PostgreSQL de destino; repita com --confirm.");
        }

        return new AssistedMigrationOptions(Path.GetFullPath(sqliteDatabasePath), confirmed);
    }
}
