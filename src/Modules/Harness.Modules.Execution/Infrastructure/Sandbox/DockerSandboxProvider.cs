using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Harness.Modules.Execution.Application.Sandbox;

namespace Harness.Modules.Execution.Infrastructure.Sandbox;

public sealed partial class DockerSandboxProvider : ISandboxProvider
{
    public const string ManagedLabel = "com.harness.managed=true";
    private const string AttemptLabelName = "com.harness.attempt";
    private readonly string _dockerExecutable;

    public DockerSandboxProvider(string? dockerExecutable = null)
    {
        _dockerExecutable = dockerExecutable ?? FindDockerExecutable();
    }

    public async Task BuildImageAsync(
        string attemptId,
        string imageName,
        string buildContext,
        CancellationToken cancellationToken = default)
    {
        ValidateAttemptId(attemptId);
        ValidateImageName(imageName);
        ArgumentException.ThrowIfNullOrWhiteSpace(buildContext);
        var fullContext = Path.GetFullPath(buildContext);
        if (!Directory.Exists(fullContext))
        {
            throw new DirectoryNotFoundException($"Docker build context was not found: {fullContext}");
        }

        var result = await RunDockerAsync(
            [
                "build",
                "--label", ManagedLabel,
                "--label", $"{AttemptLabelName}={attemptId}",
                "--tag", imageName,
                fullContext,
            ],
            cancellationToken);
        EnsureSuccess("build the managed sandbox image", result);
    }

    public async Task<ISandboxProcessSession> OpenProcessSessionAsync(
        SandboxProcessRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateProcessRequest(request);

        var internalNetwork = $"harness-internal-{request.AttemptId}";
        var egressNetwork = $"harness-egress-{request.AttemptId}";
        var cacheVolume = $"harness-cache-{request.AttemptId}";
        var stateVolume = $"harness-state-{request.AttemptId}";
        var proxyContainer = $"harness-proxy-{request.AttemptId}";
        var sandboxContainer = $"harness-sandbox-{request.AttemptId}";
        var attemptLabel = $"{AttemptLabelName}={request.AttemptId}";

        try
        {
            EnsureSuccess(
                "create the internal process network",
                await RunDockerAsync(
                    ["network", "create", "--internal", "--label", ManagedLabel, "--label", attemptLabel, internalNetwork],
                    cancellationToken));
            EnsureSuccess(
                "create the proxy process network",
                await RunDockerAsync(
                    ["network", "create", "--label", ManagedLabel, "--label", attemptLabel, egressNetwork],
                    cancellationToken));
            foreach (var volume in new[] { cacheVolume, stateVolume })
            {
                EnsureSuccess(
                    "create a managed process volume",
                    await RunDockerAsync(
                        ["volume", "create", "--label", ManagedLabel, "--label", attemptLabel, volume],
                        cancellationToken));
            }

            // Volumes nascem do daemon com dono root; o contêiner do agente roda como UID 10001
            // (decisão das imagens, para a worktree ter dono previsível) e não conseguiria gravar
            // o próprio estado — EACCES no primeiro mkdir. Um init one-shot como root ajusta o
            // dono ANTES de a sandbox subir; o contêiner do agente segue sem privilégio.
            EnsureSuccess(
                "chown the process volumes to the sandbox user",
                await RunDockerAsync(
                    [
                        "run", "--rm", "--entrypoint", "sh", "--user", "0:0",
                        "--label", ManagedLabel,
                        "--label", attemptLabel,
                        "--mount", $"type=volume,source={cacheVolume},target=/cache",
                        "--mount", $"type=volume,source={stateVolume},target=/codex-state",
                        request.AgentImageName,
                        "-c", "chown 10001:10001 /cache /codex-state",
                    ],
                    cancellationToken));

            EnsureSuccess(
                "start the process egress proxy",
                await RunDockerAsync(
                    [
                        "run", "--detach",
                        "--name", proxyContainer,
                        "--hostname", "harness-proxy",
                        "--label", ManagedLabel,
                        "--label", attemptLabel,
                        "--network", internalNetwork,
                        "--network-alias", "harness-proxy",
                        "--read-only",
                        "--tmpfs", "/tmp:rw,noexec,nosuid,size=8m",
                        "--cap-drop", "ALL",
                        "--security-opt", "no-new-privileges",
                        "--memory", "64m",
                        "--cpus", "0.25",
                        "--pids-limit", "64",
                        request.ProxyImageName,
                        request.ProxyCommand,
                    ],
                    cancellationToken));
            EnsureSuccess(
                "connect the process proxy to egress",
                await RunDockerAsync(
                    ["network", "connect", "--alias", "harness-egress-proxy", egressNetwork, proxyContainer],
                    cancellationToken));

            var worktreeMount =
                $"type=bind,source={Path.GetFullPath(request.WorktreePath)},target=/workspace";

            // O contêiner da sandbox é criado VIVO e OCIOSO na abertura da sessão — e não no
            // spawn do agente — por uma razão de ordem: a attestation precisa de um contêiner
            // real para inspecionar ANTES de autorizar as ferramentas da tentativa. Um
            // `docker run` adiado para o spawn deixava a attestation sem objeto (ela resolvia
            // `unverified:none` e toda persona com ferramentas era recusada), e atestar antes
            // de criar seria atestar uma intenção, não uma fronteira.
            var createArguments = new List<string>
            {
                "run", "--detach", "--rm",
                "--name", sandboxContainer,
                "--label", ManagedLabel,
                "--label", attemptLabel,
                "--network", internalNetwork,
                "--read-only",
                "--tmpfs", $"/tmp:rw,noexec,nosuid,size={request.WritableDiskBytes.ToString(CultureInfo.InvariantCulture)}",
                "--storage-opt", $"size={request.WritableDiskBytes.ToString(CultureInfo.InvariantCulture)}",
                "--mount", worktreeMount,
                "--mount", $"type=volume,source={cacheVolume},target=/cache",
                "--mount", $"type=volume,source={stateVolume},target=/codex-state",
                "--workdir", "/workspace",
                "--env", "CODEX_HOME=/codex-state",
                "--env", "HTTP_PROXY=http://harness-proxy:8080",
                "--env", "HTTPS_PROXY=http://harness-proxy:8080",
                "--env", "NO_PROXY=",
                "--cap-drop", "ALL",
                "--security-opt", "no-new-privileges",
                "--memory", request.MemoryBytes.ToString(CultureInfo.InvariantCulture),
                "--cpus", request.CpuLimit.ToString(CultureInfo.InvariantCulture),
                "--pids-limit", request.PidsLimit.ToString(CultureInfo.InvariantCulture),
            };

            // O config home ISOLADO da conta entra somente-leitura em /account-config e é
            // copiado para o volume de estado gravável no arranque do agente: a autenticação
            // chega ao contêiner sem segredo em camada de imagem, e o CLI continua podendo
            // gravar sessão.
            if (request.AccountConfigHomePath is { Length: > 0 } configHome)
            {
                createArguments.Add("--mount");
                createArguments.Add(
                    $"type=bind,source={Path.GetFullPath(configHome)},target=/account-config,readonly");
            }

            // Ambiente do contêiner: no argv vai SÓ o nome da variável (`--env NOME`); o valor
            // é entregue ao processo cliente no momento da criação (RunDockerAsync abaixo).
            // `--env NOME=valor` colocaria credencial na tabela de processos.
            foreach (var name in (request.ContainerEnvironment?.Keys ?? Enumerable.Empty<string>()))
            {
                createArguments.Add("--env");
                createArguments.Add(name);
            }

            // `--entrypoint sh`: a imagem pode definir um ENTRYPOINT próprio (o serviço de
            // sandbox do PoC, por exemplo) — sem o override, o comando ocioso viraria argumento
            // do serviço, que morreria de imediato e levaria o contêiner (--rm) junto.
            createArguments.Add("--entrypoint");
            createArguments.Add("sh");
            createArguments.Add(request.AgentImageName);
            createArguments.AddRange("-c", "while :; do sleep 3600; done");
            EnsureSuccess(
                "start the idle sandbox container",
                await RunDockerAsync(createArguments, cancellationToken, request.ContainerEnvironment));

            // O agente entra DEPOIS, por `docker exec`: o argv final vira
            // `sh -c '<bootstrap>; exec "$@"' sh <cli> <args...>` — o bootstrap hidrata o
            // estado gravável a partir do config home montado e o `exec` entrega o processo
            // ao CLI com os argumentos do executor intactos (stdin/stdou via -i).
            var prefixArguments = new List<string>
            {
                "exec", "--interactive", sandboxContainer,
                "sh", "-c",
                "if [ -d /account-config ]; then cp -a /account-config/. /codex-state/ 2>/dev/null || true; fi; exec \"$@\"",
                "sh", request.ContainerExecutable,
            };

            var plan = new SandboxProcessPlan(
                _dockerExecutable,
                prefixArguments,
                "/workspace",
                RootFilesystemReadOnly: true,
                WorktreeIsolated: true,
                EgressRestricted: true,
                ResourceLimitsApplied: true);
            return new DockerSandboxProcessSession(this, request.AttemptId, plan);
        }
        catch
        {
            await CleanupAsync(request.AttemptId, CancellationToken.None);
            throw;
        }
    }

    public async Task<SandboxRunResult> RunAsync(
        SandboxRunRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);

        var suffix = request.AttemptId;
        var internalNetwork = $"harness-internal-{suffix}";
        var egressNetwork = $"harness-egress-{suffix}";
        var cacheVolume = $"harness-cache-{suffix}";
        var targetContainer = $"harness-target-{suffix}";
        var proxyContainer = $"harness-proxy-{suffix}";
        var sandboxContainer = $"harness-sandbox-{suffix}";
        var attemptLabel = $"{AttemptLabelName}={request.AttemptId}";

        EnsureSuccess(
            "create the internal sandbox network",
            await RunDockerAsync(
                ["network", "create", "--internal", "--label", ManagedLabel, "--label", attemptLabel, internalNetwork],
                cancellationToken));
        EnsureSuccess(
            "create the proxy egress network",
            await RunDockerAsync(
                ["network", "create", "--label", ManagedLabel, "--label", attemptLabel, egressNetwork],
                cancellationToken));
        EnsureSuccess(
            "create the managed cache volume",
            await RunDockerAsync(
                ["volume", "create", "--label", ManagedLabel, "--label", attemptLabel, cacheVolume],
                cancellationToken));

        EnsureSuccess(
            "start the allowlisted target",
            await RunDockerAsync(
                [
                    "run", "--detach",
                    "--name", targetContainer,
                    "--label", ManagedLabel,
                    "--label", attemptLabel,
                    "--network", egressNetwork,
                    "--network-alias", "harness-target",
                    "--read-only",
                    "--tmpfs", "/tmp:rw,noexec,nosuid,size=4m",
                    "--cap-drop", "ALL",
                    "--security-opt", "no-new-privileges",
                    "--memory", "32m",
                    "--cpus", "0.25",
                    "--pids-limit", "32",
                    request.ImageName,
                    "target",
                ],
                cancellationToken));
        EnsureSuccess(
            "start the allowlisting proxy",
            await RunDockerAsync(
                [
                    "run", "--detach",
                    "--name", proxyContainer,
                    "--hostname", "harness-proxy",
                    "--label", ManagedLabel,
                    "--label", attemptLabel,
                    "--network", internalNetwork,
                    "--network-alias", "harness-proxy",
                    "--read-only",
                    "--tmpfs", "/tmp:rw,noexec,nosuid,size=4m",
                    "--cap-drop", "ALL",
                    "--security-opt", "no-new-privileges",
                    "--memory", "32m",
                    "--cpus", "0.25",
                    "--pids-limit", "32",
                    request.ImageName,
                    "proxy",
                ],
                cancellationToken));
        EnsureSuccess(
            "connect the proxy to the egress network",
            await RunDockerAsync(
                ["network", "connect", "--alias", "harness-target-proxy", egressNetwork, proxyContainer],
                cancellationToken));

        var worktreeMount = $"type=bind,source={Path.GetFullPath(request.WorktreePath)},target=/workspace";
        var cacheMount = $"type=volume,source={cacheVolume},target=/cache";
        var creation = await RunDockerAsync(
            [
                "create",
                "--name", sandboxContainer,
                "--label", ManagedLabel,
                "--label", attemptLabel,
                "--network", internalNetwork,
                "--read-only",
                "--tmpfs", $"/tmp:rw,noexec,nosuid,size={request.WritableDiskBytes.ToString(CultureInfo.InvariantCulture)}",
                "--storage-opt", $"size={request.WritableDiskBytes.ToString(CultureInfo.InvariantCulture)}",
                "--mount", worktreeMount,
                "--mount", cacheMount,
                "--workdir", "/workspace",
                "--env", "HTTP_PROXY=http://harness-proxy:8080",
                "--env", "HTTPS_PROXY=http://harness-proxy:8080",
                "--env", "NO_PROXY=",
                "--cap-drop", "ALL",
                "--security-opt", "no-new-privileges",
                "--memory", request.MemoryBytes.ToString(CultureInfo.InvariantCulture),
                "--cpus", request.CpuLimit.ToString(CultureInfo.InvariantCulture),
                "--pids-limit", request.PidsLimit.ToString(CultureInfo.InvariantCulture),
                request.ImageName,
                "probe",
            ],
            cancellationToken);
        EnsureSuccess("create the sandbox container", creation);

        var execution = await RunDockerAsync(["start", "--attach", sandboxContainer], cancellationToken);
        var inspection = await InspectSandboxAsync(sandboxContainer, cancellationToken);
        return new SandboxRunResult(
            sandboxContainer,
            execution.ExitCode,
            execution.StandardOutput.Trim(),
            inspection.CpuLimit,
            inspection.MemoryBytes,
            inspection.WritableDiskBytes,
            inspection.PidsLimit,
            inspection.RootFilesystemReadOnly,
            inspection.NetworkMode);
    }

    public async Task<SandboxResourceInventory> DetectResourcesAsync(
        string attemptId,
        CancellationToken cancellationToken = default)
    {
        ValidateAttemptId(attemptId);
        var filter = $"label={AttemptLabelName}={attemptId}";
        return new SandboxResourceInventory(
            await ListNamesAsync(["container", "ls", "--all", "--filter", filter, "--format", "{{.Names}}"], cancellationToken),
            await ListNamesAsync(["network", "ls", "--filter", filter, "--format", "{{.Name}}"], cancellationToken),
            await ListNamesAsync(["volume", "ls", "--filter", filter, "--format", "{{.Name}}"], cancellationToken),
            await ListNamesAsync(["image", "ls", "--filter", filter, "--format", "{{.Repository}}:{{.Tag}}"], cancellationToken));
    }

    public async Task CleanupAsync(
        string attemptId,
        CancellationToken cancellationToken = default)
    {
        var inventory = await DetectResourcesAsync(attemptId, cancellationToken);
        foreach (var container in inventory.Containers)
        {
            await RemoveManagedResourceAsync("container", container, ["container", "rm", "--force", container], cancellationToken);
        }

        foreach (var network in inventory.Networks)
        {
            await RemoveManagedResourceAsync("network", network, ["network", "rm", network], cancellationToken);
        }

        foreach (var volume in inventory.Volumes)
        {
            await RemoveManagedResourceAsync("volume", volume, ["volume", "rm", volume], cancellationToken);
        }

        foreach (var image in inventory.Images)
        {
            await RemoveManagedResourceAsync("image", image, ["image", "rm", image], cancellationToken);
        }
    }

    /// <summary>
    /// Fase 0B1 (BR-002): a attestation é derivada do que o runtime REALMENTE reporta sobre o
    /// container desta tentativa — não da configuração que pedimos. A mera criação de um container
    /// não prova contenção: o que prova é o `inspect` devolver rootfs somente-leitura, rede negada
    /// e limites aplicados.
    ///
    /// Runtime ausente, container inexistente ou inspeção que falhe produzem attestation NÃO
    /// verificada com o motivo. Nunca lança: o chamador precisa poder NEGAR a execução sabendo por
    /// quê, e não descobrir por uma exceção genérica no meio do fluxo.
    /// </summary>
    public async Task<SandboxAttestation> AttestAsync(
        SandboxAttestationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(_dockerExecutable))
        {
            return SandboxAttestation.Absent(
                request.TenantId, request.ProjectId, request.AttemptId,
                "The container runtime is not available on this host.", request.IssuedAt);
        }

        SandboxResourceInventory inventory;
        try
        {
            inventory = await DetectResourcesAsync(
                request.ResourceSelector ?? request.AttemptId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return SandboxAttestation.Absent(
                request.TenantId, request.ProjectId, request.AttemptId,
                $"The container runtime did not answer: {exception.GetType().Name}.",
                request.IssuedAt);
        }

        // O inventário traz TODOS os recursos do rótulo — proxy, alvo e sandbox. A fronteira
        // que interessa à política é a do contêiner onde o agente roda; atestar o proxy (a
        // ordenação alfabética o colocaria primeiro) mediria a caixa errada.
        var selector = request.ResourceSelector ?? request.AttemptId;
        var container = inventory.Containers.Count > 0
            ? inventory.Containers.FirstOrDefault(
                  name => string.Equals(
                      name, $"harness-sandbox-{selector}", StringComparison.Ordinal)) ??
                inventory.Containers[0]
            : null;
        if (container is null)
        {
            return SandboxAttestation.Absent(
                request.TenantId, request.ProjectId, request.AttemptId,
                "No sandbox container exists for this attempt.", request.IssuedAt);
        }

        SandboxInspection inspection;
        string version;
        try
        {
            inspection = await InspectSandboxAsync(container, cancellationToken);
            var versionResult = await RunDockerAsync(
                ["version", "--format", "{{.Server.Version}}"], cancellationToken);
            version = versionResult.StandardOutput.Trim();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return SandboxAttestation.Absent(
                request.TenantId, request.ProjectId, request.AttemptId,
                $"The sandbox container could not be inspected: {exception.GetType().Name}.",
                request.IssuedAt);
        }

        // `none` nega egresso por construção. A arquitetura de SESSÃO usa outra fronteira
        // equivalente: o contêiner é ligado somente a uma rede `--internal` (sem gateway para
        // fora) cujo único outro membro é o proxy gerenciado — o egresso possível passa pela
        // allowlist do proxy. A prova é o flag Internal da própria rede, lido do runtime,
        // não a configuração que pedimos.
        var egressRestricted = string.Equals(
            inspection.NetworkMode, "none", StringComparison.OrdinalIgnoreCase);
        if (!egressRestricted && InternalNetworkName().IsMatch(inspection.NetworkMode))
        {
            egressRestricted = await IsInternalNetworkAsync(
                inspection.NetworkMode, cancellationToken);
        }
        var limitsApplied = inspection.MemoryBytes > 0 && inspection.CpuLimit > 0 &&
            inspection.PidsLimit > 0;
        var verified = inspection.RootFilesystemReadOnly && egressRestricted && limitsApplied;
        return new SandboxAttestation(
            request.TenantId,
            request.ProjectId,
            request.AttemptId,
            "docker",
            string.IsNullOrWhiteSpace(version) ? "unknown" : version,
            container,
            [.. inventory.Volumes],
            inspection.NetworkMode,
            inspection.RootFilesystemReadOnly,
            WorktreeIsolated: true,
            egressRestricted,
            limitsApplied,
            verified,
            verified
                ? "The container runtime confirmed read-only rootfs, denied network and applied limits."
                : $"Container '{container}' does not satisfy the boundary: rootfsReadOnly={inspection.RootFilesystemReadOnly}, network='{inspection.NetworkMode}', limits={limitsApplied}.",
            request.IssuedAt);
    }

    private async Task<bool> IsInternalNetworkAsync(
        string networkName,
        CancellationToken cancellationToken)
    {
        // O nome vem da inspeção do contêiner e é validado antes de virar argumento.
        if (!InternalNetworkName().IsMatch(networkName))
        {
            return false;
        }

        var result = await RunDockerAsync(
            ["network", "inspect", networkName, "--format", "{{.Internal}}"], cancellationToken);
        return result.ExitCode == 0 &&
            string.Equals(result.StandardOutput.Trim(), "true", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex("^harness-internal-[a-z0-9-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex InternalNetworkName();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex EnvironmentVariableName();

    private async Task<SandboxInspection> InspectSandboxAsync(
        string containerName,
        CancellationToken cancellationToken)
    {
        var result = await RunDockerAsync(["container", "inspect", containerName], cancellationToken);
        EnsureSuccess("inspect the sandbox container", result);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement[0];
        var hostConfig = root.GetProperty("HostConfig");
        var nanoCpus = hostConfig.GetProperty("NanoCpus").GetInt64();
        var storageSize = hostConfig.GetProperty("StorageOpt").GetProperty("size").GetString();
        return new SandboxInspection(
            nanoCpus / 1_000_000_000m,
            hostConfig.GetProperty("Memory").GetInt64(),
            long.Parse(storageSize ?? "0", CultureInfo.InvariantCulture),
            hostConfig.GetProperty("PidsLimit").GetInt32(),
            hostConfig.GetProperty("ReadonlyRootfs").GetBoolean(),
            hostConfig.GetProperty("NetworkMode").GetString() ?? string.Empty);
    }

    private async Task RemoveManagedResourceAsync(
        string resourceType,
        string resourceName,
        IReadOnlyList<string> removalArguments,
        CancellationToken cancellationToken)
    {
        if (!resourceName.StartsWith("harness-", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Cleanup refused a resource without the harness- prefix.");
        }

        var inspectionArguments = resourceType is "image" or "container"
            ? new[] { resourceType, "inspect", "--format", "{{index .Config.Labels \"com.harness.managed\"}}", resourceName }
            : [resourceType, "inspect", "--format", "{{index .Labels \"com.harness.managed\"}}", resourceName];
        var inspection = await RunDockerAsync(inspectionArguments, cancellationToken);
        EnsureSuccess($"inspect managed {resourceType} {resourceName}", inspection);
        if (!string.Equals(inspection.StandardOutput.Trim(), "true", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Cleanup refused a resource without com.harness.managed=true.");
        }

        EnsureSuccess(
            $"remove managed {resourceType} {resourceName}",
            await RunDockerAsync(removalArguments, cancellationToken));
    }

    private async Task<IReadOnlyList<string>> ListNamesAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var result = await RunDockerAsync(arguments, cancellationToken);
        EnsureSuccess("list managed Docker resources", result);
        return result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(name => name != "<none>:<none>")
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private async Task<DockerCommandResult> RunDockerAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _dockerExecutable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            // Valores entregues ao processo cliente (o argv levou só `--env NOME`): é assim
            // que uma credencial chega ao contêiner sem aparecer na tabela de processos.
            foreach (var entry in environment)
            {
                startInfo.Environment[entry.Key] = entry.Value;
            }
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Docker CLI did not start.");
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return new DockerCommandResult(process.ExitCode, await standardOutput, await standardError);
    }

    private static void ValidateRequest(SandboxRunRequest request)
    {
        ValidateAttemptId(request.AttemptId);
        ValidateImageName(request.ImageName);
        ValidateResourceLimits(
            request.ExecutionRoot,
            request.WorktreePath,
            request.CpuLimit,
            request.MemoryBytes,
            request.WritableDiskBytes,
            request.PidsLimit,
            nameof(request));
    }

    private static void ValidateProcessRequest(SandboxProcessRequest request)
    {
        ValidateAttemptId(request.AttemptId);
        ValidateImageName(request.AgentImageName);
        ValidateImageName(request.ProxyImageName);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProxyCommand);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ContainerExecutable);
        if (request.AccountConfigHomePath is { Length: > 0 } configHome &&
            !Directory.Exists(configHome))
        {
            throw new DirectoryNotFoundException(
                $"The account config home to mount was not found: {configHome}");
        }

        if (request.ContainerEnvironment is { } environment)
        {
            foreach (var name in environment.Keys)
            {
                if (!EnvironmentVariableName().IsMatch(name))
                {
                    throw new ArgumentException(
                        $"Invalid container environment variable name: {name}",
                        nameof(request));
                }
            }
        }

        ValidateResourceLimits(
            request.ExecutionRoot,
            request.WorktreePath,
            request.CpuLimit,
            request.MemoryBytes,
            request.WritableDiskBytes,
            request.PidsLimit,
            nameof(request));
    }

    private static void ValidateResourceLimits(
        string executionRootValue,
        string worktreePathValue,
        decimal cpuLimit,
        long memoryBytes,
        long writableDiskBytes,
        int pidsLimit,
        string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionRootValue);
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePathValue);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(cpuLimit, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(memoryBytes, 16 * 1024 * 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(writableDiskBytes, 4 * 1024 * 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(pidsLimit, 16);

        var executionRoot = Path.GetFullPath(executionRootValue);
        var worktree = Path.GetFullPath(worktreePathValue);
        var relative = Path.GetRelativePath(executionRoot, worktree);
        if (relative == ".." ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            Path.IsPathRooted(relative) ||
            worktree.Contains(',') ||
            !Directory.Exists(worktree))
        {
            throw new ArgumentException(
                "The worktree must be an existing path under the execution root.",
                parameterName);
        }
    }

    private static void ValidateAttemptId(string attemptId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attemptId);
        if (!SafeSuffix().IsMatch(attemptId))
        {
            throw new ArgumentException("Attempt id must contain 6-24 lowercase letters, digits or hyphens.", nameof(attemptId));
        }
    }

    private static void ValidateImageName(string imageName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imageName);
        if (!imageName.StartsWith("harness-", StringComparison.Ordinal) || imageName[0] == '-')
        {
            throw new ArgumentException("Managed image tags must use the harness- prefix.", nameof(imageName));
        }
    }

    private static string FindDockerExecutable()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, "docker");
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        throw new FileNotFoundException("Docker CLI was not found on PATH.");
    }

    private static void EnsureSuccess(string operation, DockerCommandResult result)
    {
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Docker could not {operation} (exit {result.ExitCode}): {result.StandardError.Trim()}");
        }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{5,23}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeSuffix();

    private sealed record DockerCommandResult(int ExitCode, string StandardOutput, string StandardError);

    private sealed record SandboxInspection(
        decimal CpuLimit,
        long MemoryBytes,
        long WritableDiskBytes,
        int PidsLimit,
        bool RootFilesystemReadOnly,
        string NetworkMode);

    private sealed class DockerSandboxProcessSession(
        DockerSandboxProvider owner,
        string attemptId,
        SandboxProcessPlan processPlan) : ISandboxProcessSession
    {
        private int _disposed;

        public SandboxProcessPlan ProcessPlan { get; } = processPlan;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                await owner.CleanupAsync(attemptId, CancellationToken.None);
            }
        }
    }
}
