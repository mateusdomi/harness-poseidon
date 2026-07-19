using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Harness.Persistence.Abstractions.RunTargets;

namespace Harness.Host.RunTargets;

public sealed class RunTargetDetector
{
    private static readonly HashSet<string> IgnoredDirectories =
        new([".git", ".idea", ".vs", ".vscode", "bin", "obj", "node_modules", "dist", "build"], StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyList<RunTargetDefinition>> DetectAsync(
        string rootPath,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(rootPath);
        if (!Directory.Exists(root))
        {
            throw new RunTargetValidationException("The configured working directory does not exist.");
        }

        var definitions = new List<RunTargetDefinition>();
        foreach (var file in EnumerateFiles(root, 4, cancellationToken).Take(500))
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
        }

        return Task.FromResult<IReadOnlyList<RunTargetDefinition>>(
            definitions.GroupBy(value => value.Fingerprint, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(value => value.Name, StringComparer.Ordinal)
                .ToArray());
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
                    environment);
                return true;
            }

            if (root.TryGetProperty("scripts", out var scripts) &&
                scripts.ValueKind == JsonValueKind.Object &&
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
                    environment);
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
