using System.Text.Json;
using Harness.Launcher;
using Microsoft.Data.Sqlite;

namespace Harness.IntegrationTests.Launcher;

public sealed class DesktopLifecycleTests
{
    [Fact]
    public async Task InstallsUpdatesBacksUpAndUninstallsWithoutDeletingUserData()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "integration-artifacts",
            $"desktop-lifecycle-{Guid.NewGuid():N}");
        var packageV1 = Path.Combine(root, "package-v1");
        var packageV2 = Path.Combine(root, "package-v2");
        var install = Path.Combine(root, "Applications", "Harness");
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(packageV1);
        Directory.CreateDirectory(packageV2);
        Directory.CreateDirectory(data);
        try
        {
            await CreatePackageAsync(packageV1, "v1", timeout.Token);
            var installResult = await DesktopLifecycleManager.ExecuteAsync(
                Command(DesktopLifecycleAction.Install, packageV1, install, data), timeout.Token);
            Assert.Equal(DesktopLifecycleStatus.Completed, installResult.Status);
            Assert.Equal("v1", await File.ReadAllTextAsync(
                Path.Combine(install, "assets", "version.txt"), timeout.Token));
            Assert.True(File.Exists(Path.Combine(install, DesktopLifecycleManager.InstallReceiptFileName)));

            var repeated = await DesktopLifecycleManager.ExecuteAsync(
                Command(DesktopLifecycleAction.Install, packageV1, install, data), timeout.Token);
            Assert.Equal(DesktopLifecycleStatus.AlreadyCurrent, repeated.Status);

            await CreateDatabaseAsync(Path.Combine(data, "harness.db"), timeout.Token);
            Directory.CreateDirectory(Path.Combine(data, "catalog"));
            await File.WriteAllTextAsync(Path.Combine(data, "catalog", "document.txt"), "preservado", timeout.Token);
            await CreatePackageAsync(packageV2, "v2", timeout.Token);

            await File.WriteAllTextAsync(Path.Combine(packageV2, "assets", "version.txt"), "corrompido", timeout.Token);
            await Assert.ThrowsAsync<InvalidOperationException>(() => DesktopLifecycleManager.ExecuteAsync(
                Command(DesktopLifecycleAction.Update, packageV2, install, data), timeout.Token));
            Assert.Equal("v1", await File.ReadAllTextAsync(
                Path.Combine(install, "assets", "version.txt"), timeout.Token));

            await CreatePackageAsync(packageV2, "v2", timeout.Token);
            using (LauncherProcessLease.Acquire(data))
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => DesktopLifecycleManager.ExecuteAsync(
                    Command(DesktopLifecycleAction.Update, packageV2, install, data), timeout.Token));
            }

            var update = await DesktopLifecycleManager.ExecuteAsync(
                Command(DesktopLifecycleAction.Update, packageV2, install, data), timeout.Token);
            Assert.Equal(DesktopLifecycleStatus.Completed, update.Status);
            Assert.NotNull(update.BackupId);
            Assert.Equal("v2", await File.ReadAllTextAsync(
                Path.Combine(install, "assets", "version.txt"), timeout.Token));
            Assert.False(File.Exists(Path.Combine(install, "obsolete-v1.txt")));
            Assert.Equal("preservado", await File.ReadAllTextAsync(
                Path.Combine(data, "catalog", "document.txt"), timeout.Token));
            var backupRoot = Path.Combine(data, "backups", update.BackupId!);
            Assert.True(File.Exists(Path.Combine(backupRoot, "manifest.json")));
            Assert.Equal(42, await ReadDatabaseValueAsync(Path.Combine(backupRoot, "harness.db"), timeout.Token));

            var updateRepeated = await DesktopLifecycleManager.ExecuteAsync(
                Command(DesktopLifecycleAction.Update, packageV2, install, data), timeout.Token);
            Assert.Equal(DesktopLifecycleStatus.AlreadyCurrent, updateRepeated.Status);
            Assert.Single(Directory.EnumerateDirectories(Path.Combine(data, "backups")));

            var uninstall = await DesktopLifecycleManager.ExecuteAsync(
                Command(DesktopLifecycleAction.Uninstall, packageV2, install, data), timeout.Token);
            Assert.Equal(DesktopLifecycleStatus.Completed, uninstall.Status);
            Assert.False(Directory.Exists(install));
            Assert.True(File.Exists(Path.Combine(data, "harness.db")));
            Assert.True(Directory.Exists(backupRoot));

            var uninstallRepeated = await DesktopLifecycleManager.ExecuteAsync(
                Command(DesktopLifecycleAction.Uninstall, packageV2, install, data), timeout.Token);
            Assert.Equal(DesktopLifecycleStatus.AlreadyCurrent, uninstallRepeated.Status);
            await Assert.ThrowsAsync<InvalidOperationException>(() => DesktopLifecycleManager.ExecuteAsync(
                Command(
                    DesktopLifecycleAction.Uninstall,
                    packageV2,
                    Path.GetPathRoot(install)!,
                    data),
                timeout.Token));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DetectsNewVersionAndAppliesUpdateReusingLifecycleMachinery()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "integration-artifacts",
            $"auto-update-{Guid.NewGuid():N}");
        var packageV1 = Path.Combine(root, "package-v1");
        var packageV2 = Path.Combine(root, "package-v2");
        var install = Path.Combine(root, "Applications", "Harness");
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(packageV1);
        Directory.CreateDirectory(packageV2);
        Directory.CreateDirectory(data);
        try
        {
            await CreatePackageAsync(packageV1, "v1", timeout.Token);
            await DesktopLifecycleManager.ExecuteAsync(
                Command(DesktopLifecycleAction.Install, packageV1, install, data), timeout.Token);

            // Sem fonte configurada: detecção honesta de "nada novo".
            var noSource = await DesktopLifecycleManager.ExecuteAsync(
                CheckCommand(install, data, source: null), timeout.Token);
            Assert.Equal(DesktopLifecycleStatus.AlreadyCurrent, noSource.Status);
            Assert.False(noSource.UpdateAvailable);
            Assert.Equal("v1", noSource.CurrentVersion);

            // Fonte aponta a MESMA versão instalada: sem atualização.
            var sameVersion = await DesktopLifecycleManager.ExecuteAsync(
                CheckCommand(install, data, packageV1), timeout.Token);
            Assert.Equal(DesktopLifecycleStatus.AlreadyCurrent, sameVersion.Status);
            Assert.False(sameVersion.UpdateAvailable);

            // Fonte aponta versão nova: detecção positiva, read-only (nada muda no disco).
            await CreatePackageAsync(packageV2, "v2", timeout.Token);
            var detected = await DesktopLifecycleManager.ExecuteAsync(
                CheckCommand(install, data, packageV2), timeout.Token);
            Assert.Equal(DesktopLifecycleStatus.Completed, detected.Status);
            Assert.True(detected.UpdateAvailable);
            Assert.Equal("v1", detected.CurrentVersion);
            Assert.Equal("v2", detected.AvailableVersion);
            Assert.Equal("v1", await File.ReadAllTextAsync(
                Path.Combine(install, "assets", "version.txt"), timeout.Token));

            // Fonte configurada no data dir (arquivo update-source.json) + self-update aplica a v2,
            // reusando o backup offline consistente (exige um banco no data dir).
            await CreateDatabaseAsync(Path.Combine(data, "harness.db"), timeout.Token);
            await File.WriteAllTextAsync(
                Path.Combine(data, DesktopLifecycleManager.UpdateSourceFileName),
                $"{{\"packageDirectory\":{JsonSerializer.Serialize(packageV2)}}}",
                timeout.Token);
            var applied = await DesktopLifecycleManager.ExecuteAsync(
                SelfUpdateCommand(install, data, source: null), timeout.Token);
            Assert.Equal(DesktopLifecycleAction.SelfUpdate, applied.Action);
            Assert.Equal(DesktopLifecycleStatus.Completed, applied.Status);
            Assert.True(applied.UpdateAvailable);
            Assert.NotNull(applied.BackupId);
            Assert.Equal("v2", await File.ReadAllTextAsync(
                Path.Combine(install, "assets", "version.txt"), timeout.Token));

            // Reaplicar é no-op verificável; detecção volta a "nada novo".
            var reapplied = await DesktopLifecycleManager.ExecuteAsync(
                SelfUpdateCommand(install, data, source: null), timeout.Token);
            Assert.Equal(DesktopLifecycleStatus.AlreadyCurrent, reapplied.Status);
            Assert.False(reapplied.UpdateAvailable);
            var afterUpdate = await DesktopLifecycleManager.ExecuteAsync(
                CheckCommand(install, data, packageV2), timeout.Token);
            Assert.False(afterUpdate.UpdateAvailable);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ParsesLifecycleCommandsStrictly()
    {
        var command = DesktopLifecycleCommand.Parse(
            ["update", "--package-dir", "/package", "--install-dir", "/install", "--data-dir", "/data"]);
        Assert.Equal(DesktopLifecycleAction.Update, command.Action);
        Assert.Throws<ArgumentException>(() => DesktopLifecycleCommand.Parse(["update", "--package-dir"]));
        Assert.Throws<ArgumentException>(() => DesktopLifecycleCommand.Parse(["install", "--wat", "x"]));
        Assert.Throws<ArgumentException>(() => DesktopLifecycleCommand.Parse(
            ["package-manifest", "--package-dir", "/package"]));

        var check = DesktopLifecycleCommand.Parse(
            ["check-update", "--install-dir", "/install", "--data-dir", "/data", "--source", "/new"]);
        Assert.Equal(DesktopLifecycleAction.CheckUpdate, check.Action);
        Assert.Equal("/new", check.SourceDirectory);
        var self = DesktopLifecycleCommand.Parse(
            ["self-update", "--install-dir", "/install", "--data-dir", "/data"]);
        Assert.Equal(DesktopLifecycleAction.SelfUpdate, self.Action);
        Assert.Throws<ArgumentException>(() => DesktopLifecycleCommand.Parse(["check-update"]));
    }

    private static DesktopLifecycleCommand CheckCommand(string install, string data, string? source) => new()
    {
        Action = DesktopLifecycleAction.CheckUpdate,
        InstallDirectory = install,
        DataDirectory = data,
        SourceDirectory = source,
    };

    private static DesktopLifecycleCommand SelfUpdateCommand(string install, string data, string? source) => new()
    {
        Action = DesktopLifecycleAction.SelfUpdate,
        InstallDirectory = install,
        DataDirectory = data,
        SourceDirectory = source,
    };

    private static DesktopLifecycleCommand Command(
        DesktopLifecycleAction action,
        string package,
        string install,
        string data) => new()
        {
            Action = action,
            PackageDirectory = package,
            InstallDirectory = install,
            DataDirectory = data,
        };

    private static async Task CreatePackageAsync(
        string package,
        string version,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.Combine(package, "assets"));
        Directory.CreateDirectory(Path.Combine(package, "runner"));
        Directory.CreateDirectory(Path.Combine(package, "wwwroot"));
        await File.WriteAllTextAsync(Path.Combine(package, "Harness.Launcher"), "binário", cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(package, "Harness.Host.dll"), "host", cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(package, "runner", "Harness.Runner"), "runner", cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(package, "wwwroot", "index.html"), "spa", cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(package, "assets", "version.txt"), version, cancellationToken);
        var obsolete = Path.Combine(package, "obsolete-v1.txt");
        if (version == "v1") await File.WriteAllTextAsync(obsolete, "antigo", cancellationToken);
        else if (File.Exists(obsolete)) File.Delete(obsolete);
        await DesktopLifecycleManager.ExecuteAsync(new DesktopLifecycleCommand
        {
            Action = DesktopLifecycleAction.PackageManifest,
            PackageDirectory = package,
            RuntimeIdentifier = "osx-arm64",
            Version = version,
        }, cancellationToken);
    }

    private static async Task CreateDatabaseAsync(string path, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE evidence(value INTEGER NOT NULL); INSERT INTO evidence VALUES(42);";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<long> ReadDatabaseValueAsync(string path, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM evidence;";
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }
}
