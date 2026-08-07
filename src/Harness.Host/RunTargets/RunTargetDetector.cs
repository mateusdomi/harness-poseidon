using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Harness.Persistence.Abstractions.RunTargets;

namespace Harness.Host.RunTargets;

public sealed class RunTargetDetector(RunTargetAgentFallback? agentFallback = null)
{
    private static readonly HashSet<string> IgnoredDirectories =
        new([".git", ".idea", ".vs", ".vscode", "bin", "obj", "node_modules", "dist", "build"], StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyList<RunTargetDefinition>> DetectAsync(
        string rootPath,
        CancellationToken cancellationToken = default) =>
        DetectAsync(rootPath, null, cancellationToken);

    public async Task<IReadOnlyList<RunTargetDefinition>> DetectAsync(
        string rootPath,
        RunTargetDetectionContext? context,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(rootPath);
        if (!Directory.Exists(root))
        {
            throw new RunTargetValidationException("The configured working directory does not exist.");
        }

        var definitions = new List<RunTargetDefinition>();
        var files = EnumerateFiles(root, 4, cancellationToken).Take(500).ToArray();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                definitions.Add(DotNet(file));
            }
            else if (string.Equals(Path.GetFileName(file), "package.json", StringComparison.OrdinalIgnoreCase) &&
                     TryNode(file, out var node))
            {
                definitions.Add(node);
            }
            else if (IsPythonEntry(file))
            {
                definitions.Add(Python(file));
            }
            else if (string.Equals(Path.GetFileName(file), "pom.xml", StringComparison.OrdinalIgnoreCase) &&
                     TryMaven(file, out var maven))
            {
                definitions.Add(maven);
            }
            else if (IsGradleBuildFile(file) && TryGradle(file, out var gradle))
            {
                definitions.Add(gradle);
            }
            else if (IsDockerfile(file) && TryDockerfile(file, out var dockerfile))
            {
                definitions.Add(dockerfile);
            }
        }

        foreach (var composeFile in files
                     .Where(IsComposeFile)
                     .GroupBy(Path.GetDirectoryName, StringComparer.Ordinal)
                     .Select(group => group.OrderBy(ComposeFilePriority).First()))
        {
            var compose = await TryComposeAsync(composeFile, cancellationToken);
            if (compose is not null) definitions.Add(compose);
        }

        var detected = definitions.GroupBy(value => value.Fingerprint, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(value => value.Name, StringComparer.Ordinal)
                .ToArray();
        if (detected.Length > 0 || context is null || agentFallback is null) return detected;
        return await agentFallback.InferAsync(root, context, cancellationToken);
    }

    private static RunTargetDefinition DotNet(string projectFile)
    {
        var directory = Path.GetDirectoryName(projectFile)!;
        var port = FreePort();
        var url = $"http://127.0.0.1:{port}";
        var dotnet = ResolveDotNet();
        return new(
            Fingerprint("dotnet", projectFile),
            $"{Path.GetFileNameWithoutExtension(projectFile)} (.NET)",
            "http",
            url,
            port,
            directory,
            dotnet,
            ["run", "--project", projectFile, "--no-launch-profile", "--", "--urls", url],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ASPNETCORE_URLS"] = url,
                ["DOTNET_NOLOGO"] = "1",
            });
    }

    /// <summary>
    /// Pacotes que EXISTEM para servir interface web. É a evidência de que este serviço é a
    /// tela que o cliente abre — não uma adivinhação por nome de pasta.
    /// </summary>
    private static readonly HashSet<string> UserInterfacePackages = new(
        [
            "vite", "next", "react-scripts", "@angular/cli", "nuxt", "astro",
            "@sveltejs/kit", "@vue/cli-service", "parcel", "expo", "remix",
            "@remix-run/dev", "gatsby",
        ],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Verdadeiro quando o MANIFESTO prova que o serviço entrega interface ao usuário: declara
    /// um framework de tela (<see cref="UserInterfacePackages"/>) ou serve um `index.html`
    /// próprio. Sem prova, devolve falso — e o modo Negócio prefere dizer que não sabe qual é a
    /// tela do cliente a eleger uma ao acaso (D8).
    /// </summary>
    private static bool ServesUserInterface(JsonElement packageRoot, string directory)
    {
        foreach (var section in new[] { "dependencies", "devDependencies" })
        {
            if (packageRoot.TryGetProperty(section, out var node) &&
                node.ValueKind == JsonValueKind.Object &&
                node.EnumerateObject().Any(property => UserInterfacePackages.Contains(property.Name)))
            {
                return true;
            }
        }

        return File.Exists(Path.Combine(directory, "index.html"));
    }

    private static bool TryNode(string packageFile, out RunTargetDefinition definition)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(packageFile));
            var root = document.RootElement;
            var directory = Path.GetDirectoryName(packageFile)!;
            var name = root.TryGetProperty("name", out var nameNode) && nameNode.ValueKind == JsonValueKind.String
                ? nameNode.GetString()
                : Path.GetFileName(directory);
            var port = FreePort();
            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["PORT"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            };
            var servesUi = ServesUserInterface(root, directory);
            var hasScript = root.TryGetProperty("scripts", out var scripts) &&
                scripts.ValueKind == JsonValueKind.Object;

            // Uma tela de cliente sobe pelo script de desenvolvimento: é o caminho que serve a
            // interface sem exigir build prévio. Só entra aqui com evidência de interface — o
            // `dev` de um pacote de backend continua fora do manifesto.
            if (servesUi && hasScript &&
                scripts.TryGetProperty("dev", out var dev) &&
                dev.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(dev.GetString()))
            {
                definition = new(
                    Fingerprint("npm-dev", packageFile),
                    $"{name} (npm run dev)",
                    "http",
                    $"http://127.0.0.1:{port}",
                    port,
                    directory,
                    "/usr/bin/env",
                    // `--port`/`--host` explícitos depois do `--`: Vite (e os dev servers da
                    // família — TanStack Start, Astro, Nuxt) IGNORA a env PORT e sobe na porta
                    // do próprio config — a URL registrada apontava para uma porta e o serviço
                    // vivia em outra (observado ao vivo com os frontends Lovable, 2026-08-07).
                    ["npm", "run", "dev", "--", "--port", port.ToString(CultureInfo.InvariantCulture), "--host", "127.0.0.1"],
                    environment,
                    UserFacing: true);
                return true;
            }

            var entry = root.TryGetProperty("main", out var main) && main.ValueKind == JsonValueKind.String
                ? main.GetString()
                : null;
            entry = string.IsNullOrWhiteSpace(entry) ? "server.js" : entry;
            var entryPath = Path.GetFullPath(Path.Combine(directory, entry!));
            if (entryPath.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                File.Exists(entryPath))
            {
                definition = new(
                    Fingerprint("node", packageFile),
                    $"{name} (Node)",
                    "http",
                    $"http://127.0.0.1:{port}",
                    port,
                    directory,
                    "/usr/bin/env",
                    ["node", entryPath],
                    environment,
                    UserFacing: servesUi);
                return true;
            }

            if (hasScript &&
                scripts.TryGetProperty("start", out var start) &&
                start.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(start.GetString()))
            {
                definition = new(
                    Fingerprint("npm-start", packageFile),
                    $"{name} (npm start)",
                    "http",
                    $"http://127.0.0.1:{port}",
                    port,
                    directory,
                    "/usr/bin/env",
                    ["npm", "start"],
                    environment,
                    UserFacing: servesUi);
                return true;
            }

            definition = null!;
            return false;
        }
        catch (JsonException)
        {
            definition = null!;
            return false;
        }
    }

    // Java (camada de manifesto): registra apenas apps Spring Boot runnable — a heurística
    // exige o plugin/starter no manifesto para não criar alvo que não sobe com URL/health.
    private static bool TryMaven(string pomFile, out RunTargetDefinition definition)
    {
        try
        {
            var content = File.ReadAllText(pomFile);
            if (!content.Contains("spring-boot", StringComparison.OrdinalIgnoreCase))
            {
                definition = null!;
                return false;
            }

            var directory = Path.GetDirectoryName(pomFile)!;
            var port = FreePort();
            var wrapper = Path.Combine(directory, "mvnw");
            var hasWrapper = File.Exists(wrapper);
            var executable = hasWrapper ? wrapper : "/usr/bin/env";
            var arguments = hasWrapper
                ? new[] { "-q", "spring-boot:run", $"-Dspring-boot.run.arguments=--server.port={port}" }
                : new[] { "mvn", "-q", "spring-boot:run", $"-Dspring-boot.run.arguments=--server.port={port}" };
            definition = new(
                Fingerprint("maven", pomFile),
                $"{Path.GetFileName(directory)} (Maven)",
                "http",
                $"http://127.0.0.1:{port}",
                port,
                directory,
                executable,
                arguments,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["SERVER_PORT"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
            return true;
        }
        catch (IOException)
        {
            definition = null!;
            return false;
        }
    }

    private static bool IsGradleBuildFile(string file)
    {
        var name = Path.GetFileName(file);
        return string.Equals(name, "build.gradle", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "build.gradle.kts", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGradle(string buildFile, out RunTargetDefinition definition)
    {
        try
        {
            var content = File.ReadAllText(buildFile);
            if (!content.Contains("org.springframework.boot", StringComparison.OrdinalIgnoreCase) &&
                !content.Contains("spring-boot", StringComparison.OrdinalIgnoreCase))
            {
                definition = null!;
                return false;
            }

            var directory = Path.GetDirectoryName(buildFile)!;
            var port = FreePort();
            var wrapper = Path.Combine(directory, "gradlew");
            var hasWrapper = File.Exists(wrapper);
            var executable = hasWrapper ? wrapper : "/usr/bin/env";
            var arguments = hasWrapper
                ? new[] { "bootRun", $"--args=--server.port={port}" }
                : new[] { "gradle", "bootRun", $"--args=--server.port={port}" };
            definition = new(
                Fingerprint("gradle", buildFile),
                $"{Path.GetFileName(directory)} (Gradle)",
                "http",
                $"http://127.0.0.1:{port}",
                port,
                directory,
                executable,
                arguments,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["SERVER_PORT"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
            return true;
        }
        catch (IOException)
        {
            definition = null!;
            return false;
        }
    }

    private static bool IsPythonEntry(string file)
    {
        var name = Path.GetFileName(file);
        return string.Equals(name, "main.py", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "app.py", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDockerfile(string file) =>
        string.Equals(Path.GetFileName(file), "Dockerfile", StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileName(file).StartsWith("Dockerfile.", StringComparison.OrdinalIgnoreCase);

    private static bool TryDockerfile(string dockerfile, out RunTargetDefinition definition)
    {
        try
        {
            var containerPort = File.ReadLines(dockerfile)
                .Select(line => line.Trim())
                .Where(line => line.StartsWith("EXPOSE ", StringComparison.OrdinalIgnoreCase))
                .SelectMany(line => line[7..].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Select(TryContainerPort)
                .FirstOrDefault(port => port is not null);
            if (containerPort is null)
            {
                definition = null!;
                return false;
            }

            var directory = Path.GetDirectoryName(dockerfile)!;
            var hostPort = FreePort();
            definition = new(
                Fingerprint("dockerfile", dockerfile),
                $"{Path.GetFileName(directory)} (Dockerfile)",
                "http",
                $"http://127.0.0.1:{hostPort}",
                hostPort,
                directory,
                ResolveDocker(),
                [],
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["HARNESS_RUN_DOCKER_MODE"] = "dockerfile",
                    ["HARNESS_RUN_DOCKER_FILE"] = Path.GetFullPath(dockerfile),
                    ["HARNESS_RUN_CONTAINER_PORT"] = containerPort.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
            return true;
        }
        catch (IOException)
        {
            definition = null!;
            return false;
        }
    }

    private static bool IsComposeFile(string file)
    {
        var name = Path.GetFileName(file);
        return name.Equals("compose.yaml", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("compose.yml", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("docker-compose.yaml", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("docker-compose.yml", StringComparison.OrdinalIgnoreCase);
    }

    private static int ComposeFilePriority(string file) =>
        Path.GetFileName(file).ToLowerInvariant() switch
        {
            "compose.yaml" => 0,
            "compose.yml" => 1,
            "docker-compose.yaml" => 2,
            _ => 3,
        };

    private static async Task<RunTargetDefinition?> TryComposeAsync(
        string composeFile,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await RunAsync(
                ResolveDocker(),
                ["compose", "--file", composeFile, "config", "--format", "json"],
                Path.GetDirectoryName(composeFile)!,
                cancellationToken);
            if (result.ExitCode != 0) return null;

            using var document = JsonDocument.Parse(result.StandardOutput);
            if (!document.RootElement.TryGetProperty("services", out var services) ||
                services.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            string? selectedService = null;
            int? containerPort = null;
            foreach (var service in services.EnumerateObject().OrderBy(value => value.Name, StringComparer.Ordinal))
            {
                if (service.Value.TryGetProperty("ports", out var ports) &&
                    ports.ValueKind == JsonValueKind.Array && ports.GetArrayLength() > 0)
                {
                    // Existing host publications could collide with another project. The managed
                    // runtime only accepts container-only ports and publishes a free loopback port.
                    return null;
                }

                if (selectedService is null &&
                    service.Value.TryGetProperty("expose", out var exposed) &&
                    exposed.ValueKind == JsonValueKind.Array)
                {
                    containerPort = exposed.EnumerateArray()
                        .Select(value => value.ValueKind == JsonValueKind.String ? TryContainerPort(value.GetString()) : null)
                        .FirstOrDefault(value => value is not null);
                    if (containerPort is not null) selectedService = service.Name;
                }
            }

            if (selectedService is null || containerPort is null) return null;
            var directory = Path.GetDirectoryName(composeFile)!;
            var hostPort = FreePort();
            return new(
                Fingerprint("compose", directory),
                $"{Path.GetFileName(directory)} (Compose)",
                "http",
                $"http://127.0.0.1:{hostPort}",
                hostPort,
                directory,
                ResolveDocker(),
                [],
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["HARNESS_RUN_DOCKER_MODE"] = "compose",
                    ["HARNESS_RUN_DOCKER_FILE"] = Path.GetFullPath(composeFile),
                    ["HARNESS_RUN_COMPOSE_SERVICE"] = selectedService,
                    ["HARNESS_RUN_CONTAINER_PORT"] = containerPort.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static int? TryContainerPort(string? value)
    {
        var token = value?.Split('/', 2, StringSplitOptions.TrimEntries)[0];
        return int.TryParse(token, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var port) &&
            port is > 0 and <= 65535
                ? port
                : null;
    }

    private static async Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var start = new System.Diagnostics.ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)
            ?? throw new RunTargetValidationException("The Docker CLI could not be started.");
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return new(process.ExitCode, await standardOutput, await standardError);
    }

    private static string ResolveDocker()
    {
        var configured = Environment.GetEnvironmentVariable("HARNESS_DOCKER_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        foreach (var candidate in new[] { "/usr/local/bin/docker", "/opt/homebrew/bin/docker", "/usr/bin/docker" })
            if (File.Exists(candidate)) return candidate;
        return "docker";
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

    private static RunTargetDefinition Python(string entryFile)
    {
        var directory = Path.GetDirectoryName(entryFile)!;
        var port = FreePort();
        return new(
            Fingerprint("python", entryFile),
            $"{Path.GetFileName(directory)} (Python)",
            "http",
            $"http://127.0.0.1:{port}",
            port,
            directory,
            "/usr/bin/env",
            ["python3", entryFile],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["PORT"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["PYTHONUNBUFFERED"] = "1",
            });
    }

    private static IEnumerable<string> EnumerateFiles(string root, int maximumDepth, CancellationToken token)
    {
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((root, 0));
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var current = pending.Pop();
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(current.Path); }
            catch (UnauthorizedAccessException) { continue; }
            foreach (var file in files) yield return file;
            if (current.Depth >= maximumDepth) continue;
            IEnumerable<string> directories;
            try { directories = Directory.EnumerateDirectories(current.Path); }
            catch (UnauthorizedAccessException) { continue; }
            foreach (var directory in directories)
            {
                var info = new DirectoryInfo(directory);
                if (!IgnoredDirectories.Contains(info.Name) && !info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    pending.Push((directory, current.Depth + 1));
            }
        }
    }

    private static string ResolveDotNet()
    {
        var configured = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        var repositoryTool = Path.Combine(Directory.GetCurrentDirectory(), "tools", "backend", "dotnet.sh");
        return File.Exists(repositoryTool) ? repositoryTool : "dotnet";
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    private static string Fingerprint(string runtime, string path) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{runtime}\n{Path.GetFullPath(path)}")));
}
