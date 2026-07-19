using System.Globalization;
using Harness.Modules.Agents.Application.Execution;
using Harness.Modules.Agents.Infrastructure.CodexCli;

namespace Harness.IntegrationTests.Agents;

public sealed class CodexCliAgentExecutorProtocolTests
{
    [Fact]
    public async Task ExecutorConsumesStructuredTurnOverCurrentAppServerProtocol()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "poc-artifacts",
            "codex-protocol",
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        var worktree = Path.Combine(root, "worktree");
        var state = Path.Combine(root, "state");
        var executable = Path.Combine(root, "fake-codex");
        Directory.CreateDirectory(worktree);
        Directory.CreateDirectory(state);

        try
        {
            await File.WriteAllTextAsync(executable, FakeAppServerScript, timeout.Token);
            File.SetUnixFileMode(
                executable,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var proof = new CodexCliExternalSandboxProof(true, true, true, true);
            var executor = new CodexCliAgentExecutor(
                proof,
                _ => new CodexCliAppServerOptions(
                    executable,
                    root,
                    worktree,
                    state,
                    TimeSpan.FromMilliseconds(100)));

            var result = await executor.ExecuteAsync(
                new AgentExecutionRequest(
                    "01ARZ3NDEKTSV4RRFFQ69G5FAV",
                    "01ARZ3NDEKTSV4RRFFQ69G5FAW",
                    "01ARZ3NDEKTSV4RRFFQ69G5FAX",
                    "01ARZ3NDEKTSV4RRFFQ69G5FAY",
                    "Return the fixture result.",
                    "{}",
                    worktree),
                timeout.Token);

            Assert.Equal("codex-cli", result.Executor);
            Assert.Equal("thr_fixture", result.SessionId);
            Assert.Equal("turn_fixture", result.TurnId);
            Assert.Equal(["{\"response\":\"Fixture complete.\",\"demands\":[]}"], result.Chunks);
            Assert.Equal("Fixture complete.", ChiefTurnOutputContract.Parse(result.StructuredOutput).Response);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task AppServerSupportsLauncherPrefixAndContainerWorkingDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "poc-artifacts",
            "codex-launcher",
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        var worktree = Path.Combine(root, "worktree");
        var state = Path.Combine(root, "state");
        var launcher = Path.Combine(root, "launcher");
        var appServer = Path.Combine(root, "fake-codex");
        var captured = Path.Combine(root, "arguments.txt");
        Directory.CreateDirectory(worktree);
        Directory.CreateDirectory(state);

        try
        {
            await File.WriteAllTextAsync(appServer, FakeAppServerScript, timeout.Token);
            await File.WriteAllTextAsync(
                launcher,
                "#!/bin/sh\ncapture=\"$1\"\nshift\nprintf '%s\\n' \"$@\" > \"$capture\"\nexec \"$@\"\n",
                timeout.Token);
            File.SetUnixFileMode(appServer, ExecutableMode);
            File.SetUnixFileMode(launcher, ExecutableMode);

            var options = new CodexCliAppServerOptions(
                launcher,
                root,
                worktree,
                state,
                TimeSpan.FromMilliseconds(100),
                [captured, appServer],
                "/workspace");
            await using var server = await CodexCliAppServer.StartAsync(
                options,
                cancellationToken: timeout.Token);
            var thread = await server.StartThreadAsync(
                ephemeral: true,
                cancellationToken: timeout.Token);

            Assert.Equal("thr_fixture", thread.ThreadId);
            Assert.Equal(
                [appServer, "app-server", "--listen", "stdio://", "--strict-config"],
                await File.ReadAllLinesAsync(captured, timeout.Token));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private const UnixFileMode ExecutableMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private const string FakeAppServerScript =
        """
        #!/bin/sh
        turn_count=0
        while IFS= read -r line
        do
          case "$line" in
            *'"method":"initialize"'*)
              echo '{"id":1,"result":{"userAgent":"fixture","platformFamily":"unix","platformOs":"fixture"}}'
              ;;
            *'"method":"thread/start"'*)
              echo '{"id":2,"result":{"thread":{"id":"thr_fixture","ephemeral":false}}}'
              ;;
            *'"method":"turn/start"'*)
              turn_count=$((turn_count + 1))
              if [ "$turn_count" -eq 1 ]; then
                echo '{"id":3,"result":{"turn":{"id":"turn_invalid","items":[],"status":"inProgress"}}}'
                echo '{"method":"item/completed","params":{"threadId":"thr_fixture","turnId":"turn_invalid","completedAtMs":1,"item":{"id":"item_invalid","type":"agentMessage","text":"{\"response\":\"Invalid fixture.\",\"demands\":[],\"unknown\":true}"}}}'
                echo '{"method":"turn/completed","params":{"threadId":"thr_fixture","turn":{"id":"turn_invalid","items":[],"status":"completed"}}}'
              else
                echo '{"id":4,"result":{"turn":{"id":"turn_fixture","items":[],"status":"inProgress"}}}'
                echo '{"method":"item/agentMessage/delta","params":{"threadId":"thr_fixture","turnId":"turn_fixture","itemId":"item_fixture","delta":"{\"response\":\"Fixture complete.\",\"demands\":[]}"}}'
                echo '{"method":"item/completed","params":{"threadId":"thr_fixture","turnId":"turn_fixture","completedAtMs":1,"item":{"id":"item_fixture","type":"agentMessage","text":"{\"response\":\"Fixture complete.\",\"demands\":[]}"}}}'
                echo '{"method":"turn/completed","params":{"threadId":"thr_fixture","turn":{"id":"turn_fixture","items":[],"status":"completed"}}}'
              fi
              ;;
          esac
        done
        """;
}
