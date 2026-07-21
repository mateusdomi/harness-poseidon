using System.Text.Json;
using Harness.Modules.Agents.Contracts;

namespace Harness.Modules.Agents.Application.Accounts;

/// <summary>
/// Provisiona e valida o ambiente ISOLADO de cada conta de agente em disco (CA-3).
///
/// Invariantes:
/// - duas contas do mesmo CLI nunca compartilham config home, portanto autenticar uma
///   jamais desloga a outra;
/// - nenhum caminho do perfil fica dentro do repositório oficial;
/// - nenhum caminho escapa da raiz por symlink;
/// - a criação é idempotente e as permissões são restritas ao dono;
/// - o arquivo de metadados guarda apenas a REFERÊNCIA opaca da credencial — nunca o
///   segredo, nunca o e-mail, nunca a identidade real;
/// - a concessão do perfil usa fencing crescente e um dono antigo não a libera.
/// </summary>
public sealed class AccountProfileProvisioner
{
    private const string ConfigDirectoryName = "config";
    private const string WorkDirectoryName = "work";
    private const string SessionDirectoryName = "sessions";
    private const string LogDirectoryName = "logs";
    private const string MetadataFileName = "profile.json";
    private const string LockFileName = "profile.lock";

    private static readonly UnixFileMode OwnerOnlyDirectory =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private static readonly UnixFileMode OwnerOnlyFile =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
    };

    private readonly string _root;
    private readonly IReadOnlyList<string> _forbiddenRoots;

    /// <param name="profilesRoot">Raiz das contas, fora do repositório (ex.: `~/.harness/accounts`).</param>
    /// <param name="forbiddenRoots">
    /// Raízes proibidas — tipicamente o checkout oficial. Um perfil dentro do repositório
    /// poluiria a árvore versionada e violaria o isolamento.
    /// </param>
    public AccountProfileProvisioner(string profilesRoot, IReadOnlyList<string>? forbiddenRoots = null)
    {
        if (string.IsNullOrWhiteSpace(profilesRoot) || !Path.IsPathRooted(profilesRoot))
        {
            throw new AgentAccountValidationException("profile.root_must_be_absolute");
        }

        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(profilesRoot));
        _forbiddenRoots = (forbiddenRoots ?? [])
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(root => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)))
            .ToArray();

        foreach (var forbidden in _forbiddenRoots)
        {
            if (IsWithin(_root, forbidden))
            {
                throw new AgentAccountValidationException("profile.root_inside_repository");
            }
        }
    }

    /// <summary>Raiz padrão do operador: `<home>/.harness/accounts`.</summary>
    public static string DefaultProfilesRoot =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".harness",
            "accounts");

    public string Root => _root;

    /// <summary>Layout determinístico do alias. Não toca no disco.</summary>
    public AccountProfileLayout Layout(string alias)
    {
        AgentAccountRegistry.ValidateAlias(alias);
        var root = Path.Combine(_root, alias);
        return new AccountProfileLayout(
            alias,
            root,
            Path.Combine(root, ConfigDirectoryName),
            Path.Combine(root, WorkDirectoryName),
            Path.Combine(root, SessionDirectoryName),
            Path.Combine(root, LogDirectoryName),
            Path.Combine(root, MetadataFileName),
            Path.Combine(root, LockFileName));
    }

    /// <summary>
    /// Cria (ou revalida) o ambiente da conta. Idempotente: uma segunda chamada preserva o
    /// config home existente — portanto preserva a autenticação — e apenas incrementa a
    /// versão dos metadados quando algo mudou.
    /// </summary>
    public AccountProfileHandle Ensure(
        AgentAccountContract account,
        ExecutorProfile profile,
        DateTimeOffset now,
        string owner = "poseidon")
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(profile);
        if (!string.Equals(account.ExecutorId, profile.ExecutorId, StringComparison.OrdinalIgnoreCase))
        {
            throw new AgentAccountValidationException("profile.executor_mismatch");
        }

        AgentAccountRegistry.ValidateAlias(account.Alias);
        AgentAccountRegistry.ValidateCredentialReference(account.CredentialReference);

        var layout = Layout(account.Alias);
        CreateDirectory(layout.RootPath);
        CreateDirectory(layout.ConfigHomePath);
        CreateDirectory(layout.WorkingRootPath);
        CreateDirectory(layout.SessionStorePath);
        CreateDirectory(layout.LogRootPath);
        GuardAgainstEscape(layout);

        var previous = ReadMetadata(layout);
        var metadata = new AccountProfileMetadata(
            account.Alias,
            profile.ExecutorId,
            account.CredentialReference,
            profile.ConfigHomeEnvironmentVariable,
            [.. profile.EnvironmentAllowlist],
            owner,
            previous?.Version ?? 1,
            account.Health,
            previous?.CreatedAt ?? now,
            now);

        if (previous is null || !SameShape(previous, metadata))
        {
            metadata = metadata with { Version = (previous?.Version ?? 0) + 1 };
            WriteMetadata(layout, metadata);
        }

        return new AccountProfileHandle(layout, metadata, ReadLock(layout));
    }

    /// <summary>
    /// Monta o ambiente do subprocesso por ALLOWLIST, apontando a variável real de
    /// isolamento do executor para o config home desta conta. Uma variável fora da
    /// allowlist nunca é herdada, mesmo que exista no ambiente do Host.
    /// </summary>
    public IReadOnlyDictionary<string, string> BuildEnvironment(
        AccountProfileLayout layout,
        ExecutorProfile profile,
        IReadOnlyDictionary<string, string?>? source = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(profile);
        if (!IsWithin(Path.GetFullPath(layout.RootPath), _root))
        {
            // Um layout de outra raiz apontaria o executor para um config home não
            // governado por este provisionador.
            throw new AgentAccountValidationException("profile.layout_foreign");
        }

        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in profile.EnvironmentAllowlist)
        {
            var value = source is not null
                ? source.TryGetValue(name, out var candidate) ? candidate : null
                : Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value))
            {
                environment[name] = value;
            }
        }

        if (profile.ConfigHomeEnvironmentVariable is { Length: > 0 } configHomeVariable)
        {
            // O config home da CONTA sempre vence o do ambiente herdado: é o que garante
            // que duas contas do mesmo binário não sobrescrevam o login uma da outra.
            environment[configHomeVariable] = layout.ConfigHomePath;
        }
        else
        {
            // Executor sem variável de config home documentada (Antigravity, Kimi Code): a
            // única separação real é o próprio HOME. Onde existe variável dedicada NÃO se
            // troca o HOME, porque isso removeria a identidade Git global e quebraria o
            // commit do worker.
            environment["HOME"] = layout.ConfigHomePath;
        }

        return environment;
    }

    /// <summary>
    /// Adquire a concessão exclusiva do perfil com fencing crescente. Uma concessão viva de
    /// outro dono bloqueia; uma concessão expirada é recuperada com fencing maior, de modo
    /// que o dono antigo não consiga mais liberar nem escrever.
    /// </summary>
    public AccountProfileLock AcquireLock(
        string alias,
        string ownerId,
        DateTimeOffset now,
        TimeSpan duration,
        int? processId = null)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
        {
            throw new AgentAccountValidationException("profile.owner_required");
        }

        var layout = Layout(alias);
        if (!Directory.Exists(layout.RootPath))
        {
            throw new AgentAccountValidationException("profile.not_provisioned");
        }

        var current = ReadLock(layout);
        if (current is not null && current.ExpiresAt > now &&
            !string.Equals(current.OwnerId, ownerId, StringComparison.Ordinal))
        {
            throw new AgentAccountValidationException("profile.locked");
        }

        var acquired = new AccountProfileLock(
            alias,
            ownerId,
            processId ?? Environment.ProcessId,
            (current?.FencingToken ?? 0) + 1,
            now,
            now.Add(duration));
        WriteJson(layout.LockPath, acquired);
        return acquired;
    }

    /// <summary>Renova a concessão; um fencing antigo nunca renova a vigente.</summary>
    public AccountProfileLock RenewLock(
        string alias, long fencingToken, DateTimeOffset now, TimeSpan duration)
    {
        var layout = Layout(alias);
        var current = ReadLock(layout) ??
            throw new AgentAccountValidationException("profile.lock_absent");
        if (current.FencingToken != fencingToken)
        {
            throw new AgentAccountValidationException("profile.fencing_conflict");
        }

        var renewed = current with { ExpiresAt = now.Add(duration) };
        WriteJson(layout.LockPath, renewed);
        return renewed;
    }

    /// <summary>Libera a concessão. Um fencing antigo não libera a concessão vigente.</summary>
    public void ReleaseLock(string alias, long fencingToken)
    {
        var layout = Layout(alias);
        var current = ReadLock(layout);
        if (current is null)
        {
            return;
        }

        if (current.FencingToken != fencingToken)
        {
            throw new AgentAccountValidationException("profile.fencing_conflict");
        }

        File.Delete(layout.LockPath);
    }

    public AccountProfileLock? ReadLock(string alias) => ReadLock(Layout(alias));

    /// <summary>
    /// Remove concessões expiradas de todos os perfis provisionados e devolve os aliases
    /// recuperados. É o passo de recovery após crash: nenhuma conta fica presa por um
    /// processo morto.
    /// </summary>
    public IReadOnlyList<string> RecoverStaleLocks(DateTimeOffset now)
    {
        if (!Directory.Exists(_root))
        {
            return [];
        }

        var recovered = new List<string>();
        foreach (var directory in Directory.EnumerateDirectories(_root).Order(StringComparer.Ordinal))
        {
            var alias = Path.GetFileName(directory);
            AccountProfileLock? current;
            try
            {
                current = ReadLock(Layout(alias));
            }
            catch (AgentAccountValidationException)
            {
                // Diretório que não corresponde a um alias válido não é um perfil.
                continue;
            }

            if (current is not null && current.ExpiresAt <= now)
            {
                File.Delete(Layout(alias).LockPath);
                recovered.Add(alias);
            }
        }

        return recovered;
    }

    /// <summary>
    /// Limpeza. <see cref="AccountProfileCleanupScope.Ephemeral"/> apaga trabalho, sessões e
    /// logs e PRESERVA o config home — a conta continua autenticada.
    /// <see cref="AccountProfileCleanupScope.Full"/> apaga o perfil inteiro e exige novo login.
    /// </summary>
    public AccountProfileCleanupResult Cleanup(string alias, AccountProfileCleanupScope scope)
    {
        var layout = Layout(alias);
        var removed = new List<string>();
        if (scope == AccountProfileCleanupScope.Full)
        {
            if (Directory.Exists(layout.RootPath))
            {
                DeleteDirectory(layout.RootPath);
                removed.Add(layout.RootPath);
            }

            return new AccountProfileCleanupResult(alias, scope, removed, ConfigHomePreserved: false);
        }

        foreach (var path in new[] { layout.WorkingRootPath, layout.SessionStorePath, layout.LogRootPath })
        {
            if (Directory.Exists(path))
            {
                DeleteDirectory(path);
                CreateDirectory(path);
                removed.Add(path);
            }
        }

        return new AccountProfileCleanupResult(alias, scope, removed, ConfigHomePreserved: true);
    }

    /// <summary>
    /// Diagnóstico do perfil. Reporta somente o que foi OBSERVADO no disco, por código
    /// fechado — nunca conteúdo de credencial.
    /// </summary>
    public AccountProfileDoctorReport Doctor(string alias, DateTimeOffset now)
    {
        var layout = Layout(alias);
        var findings = new List<string>();
        if (!Directory.Exists(layout.RootPath))
        {
            return new AccountProfileDoctorReport(alias, false, ["profile.not_provisioned"]);
        }

        foreach (var (path, code) in new[]
        {
            (layout.ConfigHomePath, "profile.config_home_missing"),
            (layout.WorkingRootPath, "profile.working_root_missing"),
            (layout.SessionStorePath, "profile.session_store_missing"),
            (layout.LogRootPath, "profile.log_root_missing"),
        })
        {
            if (!Directory.Exists(path))
            {
                findings.Add(code);
            }
        }

        if (!File.Exists(layout.MetadataPath))
        {
            findings.Add("profile.metadata_missing");
        }

        if (!OperatingSystem.IsWindows() && Directory.Exists(layout.RootPath))
        {
            var mode = File.GetUnixFileMode(layout.RootPath);
            if ((mode & ~OwnerOnlyDirectory) != 0)
            {
                findings.Add("profile.permissions_unsafe");
            }
        }

        if (Escapes(layout))
        {
            findings.Add("profile.symlink_escape");
        }

        if (ReadLock(layout) is { } current && current.ExpiresAt <= now)
        {
            findings.Add("profile.lock_stale");
        }

        return findings.Count == 0
            ? AccountProfileDoctorReport.Ok(alias)
            : new AccountProfileDoctorReport(alias, false, findings);
    }

    private static bool SameShape(AccountProfileMetadata previous, AccountProfileMetadata current) =>
        string.Equals(previous.ExecutorId, current.ExecutorId, StringComparison.Ordinal) &&
        string.Equals(previous.CredentialReference, current.CredentialReference, StringComparison.Ordinal) &&
        string.Equals(previous.ConfigHomeEnvironmentVariable, current.ConfigHomeEnvironmentVariable, StringComparison.Ordinal) &&
        string.Equals(previous.Owner, current.Owner, StringComparison.Ordinal) &&
        previous.Health == current.Health &&
        previous.EnvironmentAllowlist.SequenceEqual(current.EnvironmentAllowlist, StringComparer.Ordinal);

    private static AccountProfileMetadata? ReadMetadata(AccountProfileLayout layout)
    {
        if (!File.Exists(layout.MetadataPath))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<AccountProfileMetadata>(
                File.ReadAllText(layout.MetadataPath), Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void WriteMetadata(AccountProfileLayout layout, AccountProfileMetadata metadata)
    {
        // Defesa em profundidade: o arquivo de metadados nunca recebe segredo — apenas a
        // referência opaca já validada.
        AgentAccountRegistry.ValidateCredentialReference(metadata.CredentialReference);
        WriteJson(layout.MetadataPath, metadata);
    }

    private static void WriteJson<T>(string path, T value)
    {
        var temporary = $"{path}.tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, Json));
        SetFileMode(temporary);
        File.Move(temporary, path, overwrite: true);
    }

    private static AccountProfileLock? ReadLock(AccountProfileLayout layout)
    {
        if (!File.Exists(layout.LockPath))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<AccountProfileLock>(
                File.ReadAllText(layout.LockPath), Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void CreateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            return;
        }

        Directory.CreateDirectory(path, OwnerOnlyDirectory);
        File.SetUnixFileMode(path, OwnerOnlyDirectory);
    }

    private static void SetFileMode(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, OwnerOnlyFile);
        }
    }

    private static void DeleteDirectory(string path) => Directory.Delete(path, recursive: true);

    private void GuardAgainstEscape(AccountProfileLayout layout)
    {
        if (Escapes(layout))
        {
            throw new AgentAccountValidationException("profile.symlink_escape");
        }

        foreach (var forbidden in _forbiddenRoots)
        {
            if (IsWithin(ResolveReal(layout.RootPath), forbidden))
            {
                throw new AgentAccountValidationException("profile.root_inside_repository");
            }
        }
    }

    private bool Escapes(AccountProfileLayout layout)
    {
        var realRoot = ResolveReal(_root);
        foreach (var path in new[]
        {
            layout.RootPath, layout.ConfigHomePath, layout.WorkingRootPath,
            layout.SessionStorePath, layout.LogRootPath,
        })
        {
            if (!Directory.Exists(path))
            {
                continue;
            }

            if (!IsWithin(ResolveReal(path), realRoot))
            {
                return true;
            }
        }

        return false;
    }

    private static string ResolveReal(string path)
    {
        // `ResolveLinkTarget` devolve o alvo real quando o caminho é um symlink; um alvo
        // fora da raiz é escape, mesmo que o caminho textual pareça interno.
        var resolved = Path.GetFullPath(path);
        try
        {
            var info = new DirectoryInfo(resolved);
            if (info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
            {
                resolved = Path.GetFullPath(target.FullName);
            }

            var parent = Path.GetDirectoryName(resolved);
            if (parent is { Length: > 0 } && Directory.Exists(parent))
            {
                var parentInfo = new DirectoryInfo(parent);
                if (parentInfo.LinkTarget is not null &&
                    parentInfo.ResolveLinkTarget(returnFinalTarget: true) is { } parentTarget)
                {
                    resolved = Path.Combine(
                        Path.GetFullPath(parentTarget.FullName), Path.GetFileName(resolved));
                }
            }
        }
        catch (IOException)
        {
            // Link quebrado ou ciclo: trate como não resolvível e mantenha o caminho textual.
        }

        return Path.TrimEndingDirectorySeparator(resolved);
    }

    private static bool IsWithin(string candidate, string root) =>
        string.Equals(candidate, root, StringComparison.Ordinal) ||
        candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
}
