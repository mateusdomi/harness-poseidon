using Harness.Modules.Agents.Application.Accounts;
using Harness.Modules.Agents.Contracts;

namespace Harness.UnitTests.Agents;

/// <summary>
/// CA-3: ambiente isolado por conta em disco. As provas centrais são: duas contas do MESMO
/// binário não compartilham autenticação; nenhum caminho fica no repositório oficial;
/// symlink não escapa da raiz; a concessão usa fencing crescente; a limpeza padrão preserva
/// o login; e nenhum segredo é escrito no perfil.
/// </summary>
public sealed class AccountProfileProvisionerTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"harness-profiles-{Guid.NewGuid():N}");

    private static AgentAccountContract Account(
        string alias, string executorId, string? credential = null) =>
        new(alias, "anthropic", executorId, credential ?? $"keychain://poseidon/{alias}",
            $"confighome://{alias}", ["chief-orchestrator"], [],
            AgentAccountState.Available, AgentAccountHealth.Unknown,
            1, 0, null, null, null, null, null, 100);

    private static ExecutorProfile Profile(string executorId) =>
        ExecutorCatalog.Find(executorId) ?? throw new InvalidOperationException(executorId);

    private AccountProfileProvisioner Provisioner(IReadOnlyList<string>? forbidden = null) =>
        new(_root, forbidden);

    [Fact]
    public void TwoAccountsOfTheSameBinaryNeverShareTheConfigHome()
    {
        // Claude Code e GLM usam o MESMO comando `claude`. Sem config home próprio,
        // autenticar uma conta deslogaria a outra.
        var provisioner = Provisioner();
        var chief = provisioner.Ensure(
            Account("chief-claude-primary", ExecutorCatalog.ClaudeCode),
            Profile(ExecutorCatalog.ClaudeCode), Now);
        var glm = provisioner.Ensure(
            Account("worker-glm-general", ExecutorCatalog.Glm),
            Profile(ExecutorCatalog.Glm), Now);

        Assert.NotEqual(chief.Layout.ConfigHomePath, glm.Layout.ConfigHomePath);

        var chiefEnvironment = provisioner.BuildEnvironment(
            chief.Layout, Profile(ExecutorCatalog.ClaudeCode), new Dictionary<string, string?>());
        var glmEnvironment = provisioner.BuildEnvironment(
            glm.Layout, Profile(ExecutorCatalog.Glm), new Dictionary<string, string?>());

        Assert.Equal(chief.Layout.ConfigHomePath, chiefEnvironment["CLAUDE_CONFIG_DIR"]);
        Assert.Equal(glm.Layout.ConfigHomePath, glmEnvironment["CLAUDE_CONFIG_DIR"]);
        Assert.NotEqual(chiefEnvironment["CLAUDE_CONFIG_DIR"], glmEnvironment["CLAUDE_CONFIG_DIR"]);
    }

    [Fact]
    public void CodexUsesItsOwnRealIsolationVariableAndKeepsTheInheritedHome()
    {
        // `CODEX_HOME` é a variável REAL de isolamento do Codex. O HOME herdado é
        // preservado de propósito: trocá-lo removeria a identidade Git global e o worker
        // não conseguiria commitar.
        var provisioner = Provisioner();
        var handle = provisioner.Ensure(
            Account("worker-codex-frontend", ExecutorCatalog.Codex),
            Profile(ExecutorCatalog.Codex), Now);

        var environment = provisioner.BuildEnvironment(
            handle.Layout,
            Profile(ExecutorCatalog.Codex),
            new Dictionary<string, string?> { ["HOME"] = "/Users/operator", ["PATH"] = "/usr/bin" });

        Assert.Equal(handle.Layout.ConfigHomePath, environment["CODEX_HOME"]);
        Assert.Equal("/Users/operator", environment["HOME"]);
    }

    [Fact]
    public void ExecutorWithoutAConfigHomeVariableIsIsolatedByHome()
    {
        // Antigravity não documenta variável de config home: a única separação real de
        // contas é o próprio HOME.
        var provisioner = Provisioner();
        var handle = provisioner.Ensure(
            Account("worker-antigravity-review", ExecutorCatalog.Antigravity),
            Profile(ExecutorCatalog.Antigravity), Now);

        var environment = provisioner.BuildEnvironment(
            handle.Layout,
            Profile(ExecutorCatalog.Antigravity),
            new Dictionary<string, string?> { ["HOME"] = "/Users/operator" });

        Assert.Equal(handle.Layout.ConfigHomePath, environment["HOME"]);
    }

    [Fact]
    public void OnlyAllowlistedEnvironmentVariablesAreInherited()
    {
        var provisioner = Provisioner();
        var handle = provisioner.Ensure(
            Account("worker-codex-frontend", ExecutorCatalog.Codex),
            Profile(ExecutorCatalog.Codex), Now);

        var environment = provisioner.BuildEnvironment(
            handle.Layout,
            Profile(ExecutorCatalog.Codex),
            new Dictionary<string, string?>
            {
                ["PATH"] = "/usr/bin",
                ["AWS_SECRET_ACCESS_KEY"] = "must-not-leak",
                ["GITHUB_TOKEN"] = "must-not-leak",
            });

        Assert.Equal("/usr/bin", environment["PATH"]);
        Assert.False(environment.ContainsKey("AWS_SECRET_ACCESS_KEY"));
        Assert.False(environment.ContainsKey("GITHUB_TOKEN"));
    }

    [Fact]
    public void ProvisioningIsIdempotentAndPreservesTheExistingLogin()
    {
        var provisioner = Provisioner();
        var account = Account("chief-claude-primary", ExecutorCatalog.ClaudeCode);
        var first = provisioner.Ensure(account, Profile(ExecutorCatalog.ClaudeCode), Now);
        var credentialFile = Path.Combine(first.Layout.ConfigHomePath, ".credentials.json");
        File.WriteAllText(credentialFile, "{}");

        var second = provisioner.Ensure(
            account, Profile(ExecutorCatalog.ClaudeCode), Now.AddMinutes(5));

        Assert.Equal(first.Layout, second.Layout);
        Assert.Equal(first.Metadata.Version, second.Metadata.Version);
        Assert.Equal(first.Metadata.CreatedAt, second.Metadata.CreatedAt);
        Assert.True(File.Exists(credentialFile));
    }

    [Fact]
    public void ProfilePathsAreOwnerOnlyAndCarryNoSecret()
    {
        var provisioner = Provisioner();
        var handle = provisioner.Ensure(
            Account("worker-codex-frontend", ExecutorCatalog.Codex),
            Profile(ExecutorCatalog.Codex), Now);

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(handle.Layout.RootPath));
        }

        var metadata = File.ReadAllText(handle.Layout.MetadataPath);
        Assert.Contains("keychain://poseidon/worker-codex-frontend", metadata, StringComparison.Ordinal);
        Assert.DoesNotContain("@", metadata, StringComparison.Ordinal);
    }

    [Fact]
    public void AProfileRootInsideTheOfficialRepositoryIsRefused()
    {
        var repository = Path.Combine(_root, "repo");
        Directory.CreateDirectory(repository);
        var exception = Assert.Throws<AgentAccountValidationException>(
            () => new AccountProfileProvisioner(Path.Combine(repository, "accounts"), [repository]));
        Assert.Equal("profile.root_inside_repository", exception.Code);
    }

    [Fact]
    public void ASymlinkedProfileDirectoryIsRefusedAndReportedByDoctor()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var provisioner = Provisioner();
        var outside = Path.Combine(Path.GetTempPath(), $"harness-escape-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);
        try
        {
            Directory.CreateDirectory(_root);
            var alias = "worker-codex-frontend";
            Directory.CreateSymbolicLink(Path.Combine(_root, alias), outside);

            var exception = Assert.Throws<AgentAccountValidationException>(
                () => provisioner.Ensure(
                    Account(alias, ExecutorCatalog.Codex), Profile(ExecutorCatalog.Codex), Now));
            Assert.Equal("profile.symlink_escape", exception.Code);
            Assert.Contains("profile.symlink_escape", provisioner.Doctor(alias, Now).Findings);
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void TheProfileLeaseUsesIncreasingFencingAndAnOldOwnerCannotRelease()
    {
        var provisioner = Provisioner();
        var alias = "worker-codex-frontend";
        provisioner.Ensure(Account(alias, ExecutorCatalog.Codex), Profile(ExecutorCatalog.Codex), Now);

        var first = provisioner.AcquireLock(alias, "owner-1", Now, TimeSpan.FromMinutes(5), 4242);
        Assert.Equal(1, first.FencingToken);
        Assert.Equal(4242, first.ProcessId);

        var conflict = Assert.Throws<AgentAccountValidationException>(
            () => provisioner.AcquireLock(alias, "owner-2", Now, TimeSpan.FromMinutes(5)));
        Assert.Equal("profile.locked", conflict.Code);

        // Concessão expirada é recuperável por outro dono, com fencing MAIOR.
        var later = Now.AddMinutes(10);
        var second = provisioner.AcquireLock(alias, "owner-2", later, TimeSpan.FromMinutes(5));
        Assert.Equal(2, second.FencingToken);

        var stale = Assert.Throws<AgentAccountValidationException>(
            () => provisioner.ReleaseLock(alias, first.FencingToken));
        Assert.Equal("profile.fencing_conflict", stale.Code);
        Assert.Equal(2, provisioner.ReadLock(alias)!.FencingToken);

        provisioner.ReleaseLock(alias, second.FencingToken);
        Assert.Null(provisioner.ReadLock(alias));
    }

    [Fact]
    public void RecoveryReleasesOnlyExpiredProfileLeases()
    {
        var provisioner = Provisioner();
        provisioner.Ensure(
            Account("worker-codex-frontend", ExecutorCatalog.Codex), Profile(ExecutorCatalog.Codex), Now);
        provisioner.Ensure(
            Account("chief-claude-primary", ExecutorCatalog.ClaudeCode),
            Profile(ExecutorCatalog.ClaudeCode), Now);

        provisioner.AcquireLock("worker-codex-frontend", "dead", Now, TimeSpan.FromMinutes(1));
        provisioner.AcquireLock("chief-claude-primary", "alive", Now, TimeSpan.FromHours(2));

        var recovered = provisioner.RecoverStaleLocks(Now.AddMinutes(30));

        Assert.Equal(["worker-codex-frontend"], recovered);
        Assert.Null(provisioner.ReadLock("worker-codex-frontend"));
        Assert.NotNull(provisioner.ReadLock("chief-claude-primary"));
    }

    [Fact]
    public void EphemeralCleanupClearsWorkButKeepsAuthenticationWhileFullCleanupRemovesIt()
    {
        var provisioner = Provisioner();
        var alias = "worker-codex-frontend";
        var handle = provisioner.Ensure(
            Account(alias, ExecutorCatalog.Codex), Profile(ExecutorCatalog.Codex), Now);
        var credentialFile = Path.Combine(handle.Layout.ConfigHomePath, "auth.json");
        File.WriteAllText(credentialFile, "{}");
        File.WriteAllText(Path.Combine(handle.Layout.WorkingRootPath, "scratch.txt"), "x");
        File.WriteAllText(Path.Combine(handle.Layout.LogRootPath, "run.log"), "x");

        var ephemeral = provisioner.Cleanup(alias, AccountProfileCleanupScope.Ephemeral);

        Assert.True(ephemeral.ConfigHomePreserved);
        Assert.True(File.Exists(credentialFile));
        Assert.Empty(Directory.GetFiles(handle.Layout.WorkingRootPath));
        Assert.Empty(Directory.GetFiles(handle.Layout.LogRootPath));
        Assert.True(provisioner.Doctor(alias, Now).Healthy);

        var full = provisioner.Cleanup(alias, AccountProfileCleanupScope.Full);

        Assert.False(full.ConfigHomePreserved);
        Assert.False(Directory.Exists(handle.Layout.RootPath));
        Assert.Equal(["profile.not_provisioned"], provisioner.Doctor(alias, Now).Findings);
    }

    [Fact]
    public void DoctorReportsMissingDirectoriesAndStaleLeasesWithClosedCodes()
    {
        var provisioner = Provisioner();
        var alias = "worker-codex-frontend";
        var handle = provisioner.Ensure(
            Account(alias, ExecutorCatalog.Codex), Profile(ExecutorCatalog.Codex), Now);
        Assert.True(provisioner.Doctor(alias, Now).Healthy);

        Directory.Delete(handle.Layout.SessionStorePath, recursive: true);
        provisioner.AcquireLock(alias, "owner-1", Now, TimeSpan.FromMinutes(1));

        var report = provisioner.Doctor(alias, Now.AddHours(1));

        Assert.False(report.Healthy);
        Assert.Contains("profile.session_store_missing", report.Findings);
        Assert.Contains("profile.lock_stale", report.Findings);
    }

    [Fact]
    public void AProfileCannotBeBuiltFromAMismatchedExecutorOrARawSecret()
    {
        var provisioner = Provisioner();
        var mismatch = Assert.Throws<AgentAccountValidationException>(
            () => provisioner.Ensure(
                Account("worker-codex-frontend", ExecutorCatalog.Codex),
                Profile(ExecutorCatalog.ClaudeCode), Now));
        Assert.Equal("profile.executor_mismatch", mismatch.Code);

        var secret = Assert.Throws<AgentAccountValidationException>(
            () => provisioner.Ensure(
                Account("worker-codex-frontend", ExecutorCatalog.Codex, "sk-ant-not-a-reference"),
                Profile(ExecutorCatalog.Codex), Now));
        Assert.Equal("account.credential_reference_must_be_opaque", secret.Code);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
