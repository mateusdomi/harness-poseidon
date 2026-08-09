using System.Security.Cryptography;
using System.Text.Json;
using Harness.SharedKernel.Identifiers;
using Microsoft.Data.Sqlite;

namespace Harness.Launcher;

public enum DesktopLifecycleAction
{
    PackageManifest,
    Install,
    Update,
    Uninstall,

    /// <summary>Detecção read-only: compara a versão instalada com a da fonte de atualização (G-AUTOUPDATE).</summary>
    CheckUpdate,

    /// <summary>Gatilho de atualização que reusa a maquinaria de <see cref="Update"/> a partir da fonte configurada.</summary>
    SelfUpdate,
}

public enum DesktopLifecycleStatus
{
    Completed,
    AlreadyCurrent,
}

public sealed record DesktopLifecycleCommand
{
    public required DesktopLifecycleAction Action { get; init; }

    public string? PackageDirectory { get; init; }

    public string? InstallDirectory { get; init; }

    public string? DataDirectory { get; init; }

    public string? RuntimeIdentifier { get; init; }

    public string? Version { get; init; }

    /// <summary>Diretório do pacote candidato à atualização (fonte de versão). G-AUTOUPDATE.</summary>
    public string? SourceDirectory { get; init; }

    public static bool IsLifecycleCommand(IReadOnlyList<string> args) =>
        args.Count > 0 && args[0] is
            "package-manifest" or "install" or "update" or "uninstall" or "check-update" or "self-update";

    public static DesktopLifecycleCommand Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (!IsLifecycleCommand(args))
            throw new ArgumentException("Comando de ciclo de vida desktop desconhecido.");
        var action = args[0] switch
        {
            "package-manifest" => DesktopLifecycleAction.PackageManifest,
            "install" => DesktopLifecycleAction.Install,
            "update" => DesktopLifecycleAction.Update,
            "uninstall" => DesktopLifecycleAction.Uninstall,
            "check-update" => DesktopLifecycleAction.CheckUpdate,
            "self-update" => DesktopLifecycleAction.SelfUpdate,
            _ => throw new ArgumentException("Comando de ciclo de vida desktop desconhecido."),
        };
        string? packageDirectory = null;
        string? installDirectory = null;
        string? dataDirectory = null;
        string? runtimeIdentifier = null;
        string? version = null;
        string? sourceDirectory = null;
        for (var index = 1; index < args.Count; index++)
        {
            if (index + 1 >= args.Count)
                throw new ArgumentException($"O argumento {args[index]} exige um valor.");
            var value = args[++index];
            switch (args[index - 1])
            {
                case "--package-dir": packageDirectory = value; break;
                case "--install-dir": installDirectory = value; break;
                case "--data-dir": dataDirectory = value; break;
                case "--rid": runtimeIdentifier = value; break;
                case "--version": version = value; break;
                case "--source": sourceDirectory = value; break;
                default: throw new ArgumentException($"Argumento desconhecido: {args[index - 1]}.");
            }
        }

        packageDirectory ??= AppContext.BaseDirectory;
        dataDirectory ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".harness-poseidon");
        if (action == DesktopLifecycleAction.PackageManifest &&
            (string.IsNullOrWhiteSpace(runtimeIdentifier) || string.IsNullOrWhiteSpace(version)))
        {
            throw new ArgumentException("package-manifest exige --rid e --version.");
        }
        if (action != DesktopLifecycleAction.PackageManifest && string.IsNullOrWhiteSpace(installDirectory))
            throw new ArgumentException($"{args[0]} exige --install-dir.");

        return new DesktopLifecycleCommand
        {
            Action = action,
            PackageDirectory = packageDirectory,
            InstallDirectory = installDirectory,
            DataDirectory = dataDirectory,
            RuntimeIdentifier = runtimeIdentifier,
            Version = version,
            SourceDirectory = sourceDirectory,
        };
    }
}

public sealed record DesktopLifecycleResult(
    DesktopLifecycleAction Action,
    DesktopLifecycleStatus Status,
    string? BackupId)
{
    /// <summary>Versão atualmente instalada (recibo). Preenchido por check-update/self-update.</summary>
    public string? CurrentVersion { get; init; }

    /// <summary>Versão disponível na fonte de atualização. Preenchido por check-update/self-update.</summary>
    public string? AvailableVersion { get; init; }

    /// <summary>Indica que há uma versão diferente disponível para aplicar.</summary>
    public bool UpdateAvailable { get; init; }

    /// <summary>Diretório do pacote a partir do qual a atualização seria/foi aplicada.</summary>
    public string? PackageDirectory { get; init; }
}

/// <summary>
/// Fonte de atualização local (G-AUTOUPDATE): aponta para o diretório de um pacote publicado mais
/// novo. Sem servidor remoto por ora, é o "arquivo de versão local" com que a versão instalada é
/// comparada. Persistido em <c>&lt;data-dir&gt;/update-source.json</c>.
/// </summary>
public sealed record DesktopUpdateSource(string PackageDirectory);

public sealed record DesktopPackageFile(string Path, long Size, string Sha256);

public sealed record DesktopPackageManifest(
    int SchemaVersion,
    string RuntimeIdentifier,
    string Version,
    IReadOnlyList<DesktopPackageFile> Files);

public sealed record DesktopInstallReceipt(
    int SchemaVersion,
    string RuntimeIdentifier,
    string Version,
    string PackageDigest,
    string DataDirectory,
    DateTimeOffset InstalledAt);

public static class DesktopLifecycleManager
{
    public const string PackageManifestFileName = ".harness-desktop-package.json";
    public const string InstallReceiptFileName = ".harness-desktop-install.json";
    public const string ProcessLeaseFileName = "launcher.pid";
    public const string UpdateSourceFileName = "update-source.json";
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static async Task<DesktopLifecycleResult> ExecuteAsync(
        DesktopLifecycleCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Action == DesktopLifecycleAction.PackageManifest)
        {
            await CreatePackageManifestAsync(command, cancellationToken);
            return new(command.Action, DesktopLifecycleStatus.Completed, null);
        }

        var installDirectory = Canonical(command.InstallDirectory!);
        var dataDirectory = Canonical(command.DataDirectory!);
        ValidateTargets(installDirectory, dataDirectory);

        // check-update é READ-ONLY: não exige o Launcher parado (você checa com o app no ar) e não
        // toca em disco. As demais ações mutam a instalação e exigem o Launcher encerrado.
        if (command.Action == DesktopLifecycleAction.CheckUpdate)
        {
            return await CheckUpdateAsync(command, installDirectory, dataDirectory, cancellationToken);
        }

        EnsureLauncherStopped(dataDirectory);
        return command.Action switch
        {
            DesktopLifecycleAction.Install => await InstallOrUpdateAsync(
                command, installDirectory, dataDirectory, isUpdate: false, cancellationToken),
            DesktopLifecycleAction.Update => await InstallOrUpdateAsync(
                command, installDirectory, dataDirectory, isUpdate: true, cancellationToken),
            DesktopLifecycleAction.SelfUpdate => await SelfUpdateAsync(
                command, installDirectory, dataDirectory, cancellationToken),
            DesktopLifecycleAction.Uninstall => Uninstall(installDirectory, dataDirectory),
            _ => throw new InvalidOperationException("Ação desktop inválida."),
        };
    }

    /// <summary>
    /// Detecção de nova versão (G-AUTOUPDATE): compara a versão do recibo instalado com a versão do
    /// manifesto do pacote na fonte de atualização configurada. Não altera nada. Quando não há fonte
    /// configurada ou o pacote não é mais novo, devolve <see cref="DesktopLifecycleStatus.AlreadyCurrent"/>.
    /// </summary>
    private static async Task<DesktopLifecycleResult> CheckUpdateAsync(
        DesktopLifecycleCommand command,
        string installDirectory,
        string dataDirectory,
        CancellationToken cancellationToken)
    {
        var receipt = await ReadReceiptAsync(
            Path.Combine(installDirectory, InstallReceiptFileName), cancellationToken);
        if (receipt is null)
            throw new InvalidOperationException("O destino não é uma instalação Harness gerenciada.");
        ValidateReceipt(receipt, dataDirectory);

        var source = ResolveUpdateSource(command, dataDirectory);
        if (source is null)
        {
            return new(DesktopLifecycleAction.CheckUpdate, DesktopLifecycleStatus.AlreadyCurrent, null)
            {
                CurrentVersion = receipt.Version,
                UpdateAvailable = false,
            };
        }

        var manifest = await ReadSourceManifestAsync(source, receipt.RuntimeIdentifier, cancellationToken);
        var available = !string.Equals(manifest.Version, receipt.Version, StringComparison.Ordinal);
        return new(
            DesktopLifecycleAction.CheckUpdate,
            available ? DesktopLifecycleStatus.Completed : DesktopLifecycleStatus.AlreadyCurrent,
            null)
        {
            CurrentVersion = receipt.Version,
            AvailableVersion = manifest.Version,
            UpdateAvailable = available,
            PackageDirectory = available ? source : null,
        };
    }

    /// <summary>
    /// Gatilho de atualização (G-AUTOUPDATE): resolve a fonte configurada e reusa integralmente a
    /// maquinaria de <see cref="DesktopLifecycleAction.Update"/> (verificação de integridade, backup
    /// offline consistente, troca por rename e rollback automático). No-op verificável quando o
    /// pacote da fonte já é o instalado.
    /// </summary>
    private static async Task<DesktopLifecycleResult> SelfUpdateAsync(
        DesktopLifecycleCommand command,
        string installDirectory,
        string dataDirectory,
        CancellationToken cancellationToken)
    {
        var receipt = await ReadReceiptAsync(
            Path.Combine(installDirectory, InstallReceiptFileName), cancellationToken);
        if (receipt is null)
            throw new InvalidOperationException("A atualização exige uma instalação Harness gerenciada existente.");
        ValidateReceipt(receipt, dataDirectory);

        var source = ResolveUpdateSource(command, dataDirectory)
            ?? throw new InvalidOperationException(
                "Nenhuma fonte de atualização configurada (use --source ou " +
                $"{UpdateSourceFileName} no data dir).");

        // Confere RID e captura a versão-alvo antes de mutar (impede atualizar por cima com uma
        // arquitetura diferente — o InstallOrUpdate valida integridade, mas não o RID da instalação).
        var manifest = await ReadSourceManifestAsync(source, receipt.RuntimeIdentifier, cancellationToken);

        var updateCommand = command with
        {
            Action = DesktopLifecycleAction.Update,
            PackageDirectory = source,
        };
        var result = await InstallOrUpdateAsync(
            updateCommand, installDirectory, dataDirectory, isUpdate: true, cancellationToken);
        return result with
        {
            Action = DesktopLifecycleAction.SelfUpdate,
            CurrentVersion = receipt.Version,
            AvailableVersion = manifest.Version,
            PackageDirectory = source,
            UpdateAvailable = result.Status == DesktopLifecycleStatus.Completed,
        };
    }

    private static string? ResolveUpdateSource(DesktopLifecycleCommand command, string dataDirectory)
    {
        if (!string.IsNullOrWhiteSpace(command.SourceDirectory))
        {
            return Canonical(command.SourceDirectory);
        }

        var pointer = Path.Combine(dataDirectory, UpdateSourceFileName);
        if (!File.Exists(pointer))
        {
            return null;
        }

        RejectReparsePoint(pointer);
        using var stream = File.OpenRead(pointer);
        var descriptor = JsonSerializer.Deserialize<DesktopUpdateSource>(stream, JsonOptions);
        return descriptor is null || string.IsNullOrWhiteSpace(descriptor.PackageDirectory)
            ? null
            : Canonical(descriptor.PackageDirectory);
    }

    private static async Task<DesktopPackageManifest> ReadSourceManifestAsync(
        string sourceDirectory,
        string expectedRuntimeIdentifier,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(sourceDirectory))
            throw new DirectoryNotFoundException("O diretório da fonte de atualização não existe.");
        var manifestPath = Path.Combine(sourceDirectory, PackageManifestFileName);
        if (!File.Exists(manifestPath))
            throw new InvalidOperationException("A fonte de atualização não possui manifesto de pacote.");
        RejectReparsePoint(manifestPath);
        var manifest = await ReadJsonAsync<DesktopPackageManifest>(manifestPath, cancellationToken);
        if (manifest.SchemaVersion != SchemaVersion || string.IsNullOrWhiteSpace(manifest.Version) ||
            string.IsNullOrWhiteSpace(manifest.RuntimeIdentifier))
        {
            throw new InvalidOperationException("O manifesto da fonte de atualização é inválido.");
        }
        if (!string.Equals(manifest.RuntimeIdentifier, expectedRuntimeIdentifier, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "A fonte de atualização tem arquitetura (RID) diferente da instalação corrente.");
        }
        return manifest;
    }

    private static async Task CreatePackageManifestAsync(
        DesktopLifecycleCommand command,
        CancellationToken cancellationToken)
    {
        var packageDirectory = Canonical(command.PackageDirectory!);
        if (!Directory.Exists(packageDirectory))
            throw new DirectoryNotFoundException("O diretório do pacote não existe.");
        var manifestPath = Path.Combine(packageDirectory, PackageManifestFileName);
        var files = new List<DesktopPackageFile>();
        RejectReparsePoint(packageDirectory);
        foreach (var file in EnumerateRegularFiles(packageDirectory).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(file, manifestPath, StringComparison.Ordinal)) continue;
            RejectReparsePoint(file);
            var relative = Path.GetRelativePath(packageDirectory, file).Replace('\\', '/');
            await using var stream = File.OpenRead(file);
            files.Add(new(relative, stream.Length, Convert.ToHexString(
                await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant()));
        }
        if (files.Count == 0) throw new InvalidOperationException("O pacote desktop está vazio.");
        var manifest = new DesktopPackageManifest(
            SchemaVersion,
            command.RuntimeIdentifier!,
            command.Version!,
            files);
        await WriteJsonAtomicallyAsync(manifestPath, manifest, cancellationToken);
    }

    private static async Task<DesktopLifecycleResult> InstallOrUpdateAsync(
        DesktopLifecycleCommand command,
        string installDirectory,
        string dataDirectory,
        bool isUpdate,
        CancellationToken cancellationToken)
    {
        var packageDirectory = Canonical(command.PackageDirectory!);
        if (PathsOverlap(packageDirectory, installDirectory))
            throw new InvalidOperationException("O pacote de origem e o destino da instalação não podem se sobrepor.");
        var verified = await VerifyPackageAsync(packageDirectory, cancellationToken);
        var receiptPath = Path.Combine(installDirectory, InstallReceiptFileName);
        DesktopInstallReceipt? current = null;
        if (Directory.Exists(installDirectory))
        {
            current = await ReadReceiptAsync(receiptPath, cancellationToken);
            if (current is null)
                throw new InvalidOperationException(
                    "O destino existente não é uma instalação Harness gerenciada.");
            ValidateReceipt(current, dataDirectory);
        }
        if (isUpdate && current is null)
            throw new InvalidOperationException("A atualização exige uma instalação Harness gerenciada existente.");
        if (!isUpdate && current is not null && current.PackageDigest != verified.Digest)
            throw new InvalidOperationException("O destino já contém outra instalação Harness; use update.");
        if (current?.PackageDigest == verified.Digest)
            return new(command.Action, DesktopLifecycleStatus.AlreadyCurrent, null);

        string? backupId = null;
        if (isUpdate)
            backupId = await CreateOfflineBackupAsync(dataDirectory, cancellationToken);

        var parent = Directory.GetParent(installDirectory)?.FullName
            ?? throw new InvalidOperationException("O destino de instalação não possui diretório pai seguro.");
        Directory.CreateDirectory(parent);
        var operation = Guid.NewGuid().ToString("N");
        var staging = Path.Combine(parent, $".harness-stage-{operation}");
        var rollback = Path.Combine(parent, $".harness-rollback-{operation}");
        Directory.CreateDirectory(staging);
        try
        {
            CopyVerifiedPackage(packageDirectory, staging, verified.Manifest);
            var receipt = new DesktopInstallReceipt(
                SchemaVersion,
                verified.Manifest.RuntimeIdentifier,
                verified.Manifest.Version,
                verified.Digest,
                dataDirectory,
                DateTimeOffset.UtcNow);
            await WriteJsonAtomicallyAsync(
                Path.Combine(staging, InstallReceiptFileName), receipt, cancellationToken);
            if (Directory.Exists(installDirectory)) Directory.Move(installDirectory, rollback);
            try
            {
                Directory.Move(staging, installDirectory);
            }
            catch
            {
                if (Directory.Exists(rollback) && !Directory.Exists(installDirectory))
                    Directory.Move(rollback, installDirectory);
                throw;
            }
            if (Directory.Exists(rollback)) Directory.Delete(rollback, recursive: true);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
        return new(command.Action, DesktopLifecycleStatus.Completed, backupId);
    }

    private static DesktopLifecycleResult Uninstall(string installDirectory, string dataDirectory)
    {
        if (!Directory.Exists(installDirectory))
            return new(DesktopLifecycleAction.Uninstall, DesktopLifecycleStatus.AlreadyCurrent, null);
        var receipt = ReadReceipt(Path.Combine(installDirectory, InstallReceiptFileName));
        ValidateReceipt(receipt, dataDirectory);
        Directory.Delete(installDirectory, recursive: true);
        return new(DesktopLifecycleAction.Uninstall, DesktopLifecycleStatus.Completed, null);
    }

    private static async Task<(DesktopPackageManifest Manifest, string Digest)> VerifyPackageAsync(
        string packageDirectory,
        CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(packageDirectory, PackageManifestFileName);
        if (!File.Exists(manifestPath))
            throw new InvalidOperationException("O pacote não possui manifesto de integridade.");
        RejectReparsePoint(manifestPath);
        var manifest = await ReadJsonAsync<DesktopPackageManifest>(manifestPath, cancellationToken);
        if (manifest.SchemaVersion != SchemaVersion || manifest.Files.Count == 0 ||
            string.IsNullOrWhiteSpace(manifest.RuntimeIdentifier) || string.IsNullOrWhiteSpace(manifest.Version))
        {
            throw new InvalidOperationException("O manifesto do pacote é inválido ou incompatível.");
        }
        var expectedPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var expected in manifest.Files)
        {
            var path = ResolveOwnedFile(packageDirectory, expected.Path);
            if (!expectedPaths.Add(expected.Path) || !File.Exists(path))
                throw new InvalidOperationException("O manifesto contém arquivo ausente ou duplicado.");
            RejectReparsePoint(path);
            await using var stream = File.OpenRead(path);
            var digest = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken))
                .ToLowerInvariant();
            if (stream.Length != expected.Size ||
                !string.Equals(digest, expected.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Falha de integridade no arquivo do pacote: {expected.Path}.");
            }
        }
        var actualPaths = EnumerateRegularFiles(packageDirectory)
            .Where(path => !string.Equals(path, manifestPath, StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(packageDirectory, path).Replace('\\', '/'));
        if (!expectedPaths.SetEquals(actualPaths))
            throw new InvalidOperationException("O pacote contém arquivo não declarado no manifesto.");
        var executableSuffix = manifest.RuntimeIdentifier.StartsWith("win-", StringComparison.Ordinal)
            ? ".exe"
            : "";
        var required = new[]
        {
            $"Harness.Launcher{executableSuffix}",
            $"Harness.Bruna.Desktop{executableSuffix}",
            "Harness.Host.dll",
            $"runner/Harness.Runner{executableSuffix}",
            "wwwroot/index.html",
        };
        if (required.Any(path => !expectedPaths.Contains(path)))
            throw new InvalidOperationException("O pacote desktop não contém Launcher, Host, Runner e SPA completos.");
        await using var manifestStream = File.OpenRead(manifestPath);
        var manifestDigest = Convert.ToHexString(
            await SHA256.HashDataAsync(manifestStream, cancellationToken)).ToLowerInvariant();
        return (manifest, manifestDigest);
    }

    private static async Task<string?> CreateOfflineBackupAsync(
        string dataDirectory,
        CancellationToken cancellationToken)
    {
        var databasePath = Path.Combine(dataDirectory, "harness.db");
        if (!File.Exists(databasePath)) return null;
        Directory.CreateDirectory(Path.Combine(dataDirectory, "backups"));
        var createdAt = DateTimeOffset.UtcNow;
        var backupId = UlidValue.New(createdAt).ToString();
        var backupRoot = Path.Combine(dataDirectory, "backups", backupId);
        var temporary = backupRoot + ".tmp";
        Directory.CreateDirectory(temporary);
        try
        {
            await using var source = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            await using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(temporary, "harness.db"),
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
            }.ToString());
            await source.OpenAsync(cancellationToken);
            await destination.OpenAsync(cancellationToken);
            source.BackupDatabase(destination);
            CopyDirectoryWithoutLinks(
                Path.Combine(dataDirectory, "catalog"),
                Path.Combine(temporary, "catalog"));
            await WriteJsonAtomicallyAsync(
                Path.Combine(temporary, "manifest.json"),
                new { backupId, createdAt, reason = "desktop-update" },
                cancellationToken);
            Directory.Move(temporary, backupRoot);
            return backupId;
        }
        catch
        {
            if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
            if (Directory.Exists(backupRoot)) Directory.Delete(backupRoot, recursive: true);
            throw;
        }
    }

    private static void EnsureLauncherStopped(string dataDirectory)
    {
        var leasePath = Path.Combine(dataDirectory, ProcessLeaseFileName);
        if (!File.Exists(leasePath)) return;
        try
        {
            using var stream = new FileStream(leasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException(
                "Encerre o Harness Launcher antes desta operação.", exception);
        }
    }

    private static void ValidateTargets(string installDirectory, string dataDirectory)
    {
        var root = Path.GetPathRoot(installDirectory)!;
        var userDirectory = Canonical(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if (string.Equals(installDirectory, root, PathComparison) ||
            string.Equals(installDirectory, userDirectory, PathComparison) ||
            PathsOverlap(installDirectory, dataDirectory))
        {
            throw new InvalidOperationException("Destino de instalação inseguro ou sobreposto ao data dir.");
        }
        RejectExistingReparsePoint(installDirectory);
        RejectExistingReparsePoint(dataDirectory);
        var parent = Directory.GetParent(installDirectory)?.FullName;
        if (parent is null) throw new InvalidOperationException("Destino de instalação inválido.");
        RejectReparseAncestors(parent);
    }

    private static void ValidateReceipt(DesktopInstallReceipt? receipt, string dataDirectory)
    {
        if (receipt is null || receipt.SchemaVersion != SchemaVersion ||
            !string.Equals(Canonical(receipt.DataDirectory), dataDirectory, PathComparison))
        {
            throw new InvalidOperationException(
                "O destino não é uma instalação Harness gerenciada para este data dir.");
        }
    }

    private static async Task<DesktopInstallReceipt?> ReadReceiptAsync(
        string path,
        CancellationToken cancellationToken) =>
        File.Exists(path) ? await ReadJsonAsync<DesktopInstallReceipt>(path, cancellationToken) : null;

    private static DesktopInstallReceipt? ReadReceipt(string path)
    {
        if (!File.Exists(path)) return null;
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<DesktopInstallReceipt>(stream, JsonOptions)
            ?? throw new InvalidOperationException("O recibo da instalação é inválido.");
    }

    private static void CopyVerifiedPackage(
        string source,
        string destination,
        DesktopPackageManifest manifest)
    {
        foreach (var file in manifest.Files)
        {
            var sourceFile = ResolveOwnedFile(source, file.Path);
            var destinationFile = ResolveOwnedFile(destination, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
            File.Copy(sourceFile, destinationFile, overwrite: false);
        }
        File.Copy(
            Path.Combine(source, PackageManifestFileName),
            Path.Combine(destination, PackageManifestFileName),
            overwrite: false);
    }

    private static void CopyDirectoryWithoutLinks(string source, string destination)
    {
        if (!Directory.Exists(source))
        {
            Directory.CreateDirectory(destination);
            return;
        }
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            RejectReparsePoint(directory);
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            RejectReparsePoint(file);
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }
    }

    private static async Task<T> ReadJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException($"JSON inválido: {Path.GetFileName(path)}.");
    }

    private static async Task WriteJsonAtomicallyAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        var temporary = path + $".tmp-{Guid.NewGuid():N}";
        try
        {
            await using (var stream = new FileStream(
                             temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string ResolveOwnedFile(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
            throw new InvalidOperationException("Caminho inválido no manifesto do pacote.");
        var canonicalRoot = Canonical(root);
        var resolved = Canonical(Path.Combine(canonicalRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!resolved.StartsWith(canonicalRoot + Path.DirectorySeparatorChar, PathComparison))
            throw new InvalidOperationException("O manifesto tenta escapar do diretório do pacote.");
        return resolved;
    }

    private static string Canonical(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool PathsOverlap(string first, string second) =>
        IsSameOrAncestor(first, second) || IsSameOrAncestor(second, first);

    private static bool IsSameOrAncestor(string ancestor, string path) =>
        string.Equals(ancestor, path, PathComparison) ||
        path.StartsWith(ancestor + Path.DirectorySeparatorChar, PathComparison);

    private static void RejectExistingReparsePoint(string path)
    {
        if (File.Exists(path) || Directory.Exists(path)) RejectReparsePoint(path);
    }

    private static void RejectReparsePoint(string path)
    {
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException("Links simbólicos não são aceitos no ciclo de vida desktop.");
    }

    private static void RejectReparseAncestors(string path)
    {
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
        {
            if (current.Exists) RejectReparsePoint(current.FullName);
        }
    }

    private static List<string> EnumerateRegularFiles(string root)
    {
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                RejectReparsePoint(entry);
                if (Directory.Exists(entry)) pending.Push(entry);
                else files.Add(entry);
            }
        }
        return files;
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}

public sealed class LauncherProcessLease : IDisposable
{
    private readonly FileStream _stream;
    private bool _disposed;

    private LauncherProcessLease(FileStream stream)
    {
        _stream = stream;
    }

    public static LauncherProcessLease Acquire(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, DesktopLifecycleManager.ProcessLeaseFileName);
        try
        {
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            stream.SetLength(0);
            using (var writer = new StreamWriter(stream, leaveOpen: true))
            {
                writer.Write(Environment.ProcessId);
                writer.Flush();
            }
            stream.Position = 0;
            return new LauncherProcessLease(stream);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException("Já existe um Harness Launcher usando este data dir.", exception);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stream.Dispose();
    }
}
