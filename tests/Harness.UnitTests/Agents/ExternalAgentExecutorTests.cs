using System.Diagnostics;
using Harness.Modules.Agents.Application.Accounts;
using Harness.Modules.Agents.Application.Execution.External;
using Harness.Modules.Agents.Contracts;

namespace Harness.UnitTests.Agents;

/// <summary>
/// CA-4: adapters reais de Claude Code e Codex. As provas aqui não gastam cota: elas
/// asseguram os ARGUMENTOS exatos (nenhuma flag inventada), o isolamento por conta, a
/// recusa de segredo em argv e o prompt fora da linha de comando.
/// </summary>
public sealed class ExternalAgentExecutorTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"harness-external-{Guid.NewGuid():N}");

    private readonly string _workspace = Path.Combine(
        Path.GetTempPath(), $"harness-workspace-{Guid.NewGuid():N}");

    public ExternalAgentExecutorTests() => Directory.CreateDirectory(_workspace);

    private static AgentAccountContract Account(string alias, string executorId) =>
        new(alias, "openai", executorId, $"keychain://poseidon/{alias}", $"confighome://{alias}",
            ["frontend-specialist"], ["frontend/**", "docs/frontend/**"],
            AgentAccountState.Available, AgentAccountHealth.Unknown,
            1, 0, null, null, null, null, null, 100);

    private static AccountProfileHandle Provision(
        AccountProfileProvisioner provisioner, string alias, string executorId) =>
        provisioner.Ensure(
            Account(alias, executorId), ExecutorCatalog.Find(executorId)!, Now);

    private ExternalAgentRunRequest Request(
        AccountProfileHandle handle,
        ExternalAgentAccess access = ExternalAgentAccess.Workspace,
        string? resume = null,
        string? model = null,
        string? effort = null) =>
        new()
        {
            Alias = handle.Layout.Alias,
            Prompt = "implemente a fatia",
            WorkingDirectory = _workspace,
            Profile = handle.Layout,
            Access = access,
            ResumeSessionId = resume,
            Model = model,
            Effort = effort,
        };

    private static List<string> Arguments(
        ProcessExternalAgentExecutor executor, ExternalAgentRunRequest request) =>
        [.. executor.BuildArguments(
            request, new ExternalAgentRunContext("run-test", Path.Combine(Path.GetTempPath(), "last.txt")))];

    [Fact]
    public void CodexActorRunsWorkspaceWriteAndReadsThePromptFromStandardInput()
    {
        var provisioner = new AccountProfileProvisioner(_root);
        var handle = Provision(provisioner, "worker-codex-frontend", ExecutorCatalog.Codex);
        var executor = CodexExternalAgentExecutor.Create(provisioner);

        var arguments = Arguments(executor, Request(handle));

        Assert.Equal("codex", executor.Profile.Command);
        Assert.Equal("exec", arguments[0]);
        Assert.Contains("--json", arguments);
        Assert.Contains("--skip-git-repo-check", arguments);
        Assert.Equal("workspace-write", arguments[arguments.IndexOf("--sandbox") + 1]);
        Assert.Equal(_workspace, arguments[arguments.IndexOf("-C") + 1]);
        Assert.Contains("--output-last-message", arguments);

        // `-` é o que faz o Codex ler o prompt do stdin; o prompt nunca entra em argv.
        Assert.Equal("-", arguments[^1]);
        Assert.DoesNotContain(arguments, argument => argument.Contains("implemente a fatia", StringComparison.Ordinal));
    }

    [Fact]
    public void CodexCriticIsReadOnlyAndResumesByTheRealSubcommand()
    {
        var provisioner = new AccountProfileProvisioner(_root);
        var handle = Provision(provisioner, "worker-codex-critic", ExecutorCatalog.Codex);
        var executor = CodexExternalAgentExecutor.Create(provisioner);

        var arguments = Arguments(
            executor,
            Request(handle, ExternalAgentAccess.ReadOnly, resume: "019f8637-2252-7c21-9fd4-e87af6dc6dee"));

        Assert.Equal("read-only", arguments[arguments.IndexOf("--sandbox") + 1]);
        Assert.Equal("resume", arguments[1]);
        Assert.Equal("019f8637-2252-7c21-9fd4-e87af6dc6dee", arguments[2]);
    }

    [Fact]
    public void ClaudeActorAcceptsEditsWhileTheCriticHasNoWriteToolAtAll()
    {
        var provisioner = new AccountProfileProvisioner(_root);
        var chief = Provision(provisioner, "chief-claude-primary", ExecutorCatalog.ClaudeCode);
        var executor = ClaudeCodeExternalAgentExecutor.ForClaudeCode(provisioner);

        var actor = Arguments(executor, Request(chief));
        Assert.Equal("acceptEdits", actor[actor.IndexOf("--permission-mode") + 1]);

        var critic = Arguments(executor, Request(chief, ExternalAgentAccess.ReadOnly));
        var tools = critic[critic.IndexOf("--tools") + 1].Split(',');
        Assert.Equal(["Read", "Grep", "Glob"], tools);
        Assert.DoesNotContain("Edit", tools);
        Assert.DoesNotContain("Write", tools);
        Assert.DoesNotContain("Bash", tools);
    }

    [Fact]
    public void ClaudeStreamingArgumentsMatchTheInstalledCliContract()
    {
        var provisioner = new AccountProfileProvisioner(_root);
        var chief = Provision(provisioner, "chief-claude-primary", ExecutorCatalog.ClaudeCode);
        var executor = ClaudeCodeExternalAgentExecutor.ForClaudeCode(provisioner);

        var arguments = Arguments(
            executor, Request(chief, resume: "2db1da57-6768-47a3-9327-81ff271f3235", effort: "high"));

        Assert.Equal(["-p", "--output-format", "stream-json", "--verbose"], arguments.Take(4));
        Assert.Equal("2db1da57-6768-47a3-9327-81ff271f3235", arguments[arguments.IndexOf("--resume") + 1]);
        Assert.Equal("high", arguments[arguments.IndexOf("--effort") + 1]);
    }

    [Fact]
    public void GlmSharesTheClaudeBinaryButNeverTheConfigHome()
    {
        var provisioner = new AccountProfileProvisioner(_root);
        var chief = Provision(provisioner, "chief-claude-primary", ExecutorCatalog.ClaudeCode);
        var glm = Provision(provisioner, "worker-glm-general", ExecutorCatalog.Glm);

        var claude = ClaudeCodeExternalAgentExecutor.ForClaudeCode(provisioner);
        var glmExecutor = ClaudeCodeExternalAgentExecutor.ForGlm(provisioner);

        Assert.Equal(claude.Profile.Command, glmExecutor.Profile.Command);

        var chiefEnvironment = provisioner.BuildEnvironment(
            chief.Layout, claude.Profile, new Dictionary<string, string?>());
        var glmEnvironment = provisioner.BuildEnvironment(
            glm.Layout, glmExecutor.Profile, new Dictionary<string, string?>());

        Assert.NotEqual(chiefEnvironment["CLAUDE_CONFIG_DIR"], glmEnvironment["CLAUDE_CONFIG_DIR"]);
    }

    [Fact]
    public async Task AnEffortValueTheInstalledCliDoesNotAcceptIsRefused()
    {
        var provisioner = new AccountProfileProvisioner(_root);
        var handle = Provision(provisioner, "worker-codex-frontend", ExecutorCatalog.Codex);
        var executor = CodexExternalAgentExecutor.Create(provisioner);

        // O Codex CLI não expõe `--effort`; passar um valor seria inventar flag.
        var exception = await Assert.ThrowsAsync<ExternalAgentException>(
            () => executor.StartAsync(Request(handle, effort: "high")));
        Assert.Equal("executor.effort_unsupported", exception.Code);
    }

    [Fact]
    public async Task AProfileFromAnotherAliasIsRefusedBeforeAnyProcessStarts()
    {
        var provisioner = new AccountProfileProvisioner(_root);
        var other = Provision(provisioner, "worker-codex-critic", ExecutorCatalog.Codex);
        var executor = CodexExternalAgentExecutor.Create(provisioner);

        var request = Request(other) with { Alias = "worker-codex-frontend" };

        var exception = await Assert.ThrowsAsync<ExternalAgentException>(
            () => executor.StartAsync(request));
        Assert.Equal("executor.profile_alias_mismatch", exception.Code);
    }

    [Fact]
    public async Task AMissingWorkingDirectoryIsRefusedInsteadOfSilentlyUsingTheProcessCwd()
    {
        var provisioner = new AccountProfileProvisioner(_root);
        var handle = Provision(provisioner, "worker-codex-frontend", ExecutorCatalog.Codex);
        var executor = CodexExternalAgentExecutor.Create(provisioner);

        var request = Request(handle) with
        {
            WorkingDirectory = Path.Combine(_workspace, "does-not-exist"),
        };

        var exception = await Assert.ThrowsAsync<ExternalAgentException>(
            () => executor.StartAsync(request));
        Assert.Equal("executor.working_directory_invalid", exception.Code);
    }

    [Theory]
    [InlineData("sk-ant-api03-AAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("ghp_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("Authorization: Bearer AAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("ANTHROPIC_AUTH_TOKEN=AAAAAAAAAAAAAAAAAAAA")]
    public void OutputThatLooksLikeASecretIsRedactedBeforeLeavingTheAdapter(string value)
    {
        Assert.True(ExternalAgentRedaction.ContainsSecret(value));
        var redacted = ExternalAgentRedaction.Redact($"o modelo imprimiu {value} no log");
        Assert.Contains(ExternalAgentRedaction.Placeholder, redacted, StringComparison.Ordinal);
        Assert.False(ExternalAgentRedaction.ContainsSecret(redacted));
    }

    [Fact]
    public void OrdinaryOutputIsNotMangledByRedaction()
    {
        const string Text = "Implementei o componente e rodei os testes: 12 passaram.";
        Assert.False(ExternalAgentRedaction.ContainsSecret(Text));
        Assert.Equal(Text, ExternalAgentRedaction.Redact(Text));
    }

    [Fact]
    public void ClaudeAuthFailureInTheResultTextBecomesTheStructuredAuthCode()
    {
        // A falta de credencial chega no TEXTO do resultado (stdout em stream-json), não no
        // erro padrão — dentro do contêiner a credencial do Keychain não existe e a CLI diz
        // exatamente isto. Sem a tradução para o código estruturado, a conta caía como
        // transitória e queimava o circuito de cards saudáveis.
        var parser = new ClaudeCodeExternalAgentExecutor.ClaudeStreamJsonParser();

        var failed = Assert.Single(parser.ParseLine(
            """{"type":"result","subtype":"error_during_execution","is_error":true,"result":"Not logged in · Please run /login"}""")
            .ToArray());

        Assert.Equal(ExternalAgentEventKind.Failed, failed.Kind);
        Assert.Equal("executor.authentication_required", parser.FailureCode);
    }

    [Fact]
    public void SandboxedExecutionRewritesHostPathsToContainerPaths()
    {
        var provisioner = new AccountProfileProvisioner(_root);
        var handle = Provision(provisioner, "worker-codex-frontend", ExecutorCatalog.Codex);
        var request = Request(handle);
        var sandbox = new ProcessExternalAgentExecutor.SandboxedCommand(
            "/usr/local/bin/docker",
            ["exec", "--interactive", "harness-sandbox-wsp-x", "sh", "-c", "b", "sh", "codex"],
            null,
            "/workspace",
            "/codex-state");

        var effective = ProcessExternalAgentExecutor.ResolveSandboxedRequest(request, sandbox);
        var lastMessage = ProcessExternalAgentExecutor.ResolveLastMessagePath(
            request, sandbox, "last-message-test.txt");

        Assert.Equal("/workspace", effective.WorkingDirectory);
        Assert.Equal("/tmp/last-message-test.txt", lastMessage);
        // Sem sandbox, nada muda — os caminhos do host seguem valendo.
        Assert.Same(request, ProcessExternalAgentExecutor.ResolveSandboxedRequest(request, null));
    }

    /// <summary>
    /// A CLI do Codex roda com sandbox própria (`--sandbox workspace-write`), cujas raízes
    /// graváveis são o diretório de trabalho, `/tmp` e `$TMPDIR`. Apontar a mensagem final
    /// para o `sessions/` do perfil — que fica fora dessas raízes — fazia a CLI avisar
    /// `Failed to write last message file … (os error 2)`, gravar vazio e o turno morrer sem
    /// token. O diretório existia: o que faltava era permissão da própria sandbox.
    /// </summary>
    [Fact]
    public void TheLastMessageFileLandsWhereTheExecutorSandboxCanActuallyWrite()
    {
        var provisioner = new AccountProfileProvisioner(_root);
        var handle = Provision(provisioner, "worker-codex-frontend", ExecutorCatalog.Codex);
        var request = Request(handle);

        var onHost = ProcessExternalAgentExecutor.ResolveLastMessagePath(
            request, null, "last-message-test.txt");

        Assert.Equal(Path.Combine(Path.GetTempPath(), "last-message-test.txt"), onHost);
        Assert.DoesNotContain(handle.Layout.SessionStorePath, onHost, StringComparison.Ordinal);
    }

    [Fact]
    public void AuthFailureWearingAContradictorySuccessEnvelopeIsStillAuth()
    {
        // Capturado ao vivo no contêiner (claude 2.0.30): a falta de credencial chega como
        // subtype=success + is_error=true + "Invalid API key · Please run /login". A regra da
        // contradição GLM prioriza o subtipo — a frase de auth precisa vir ANTES dela, ou a
        // conta vira sucesso fantasma/falha transitória para sempre.
        var parser = new ClaudeCodeExternalAgentExecutor.ClaudeStreamJsonParser();

        var failed = Assert.Single(parser.ParseLine(
            """{"type":"result","subtype":"success","is_error":true,"result":"Invalid API key · Please run /login"}""")
            .ToArray());

        Assert.Equal(ExternalAgentEventKind.Failed, failed.Kind);
        Assert.Equal("executor.authentication_required", parser.FailureCode);
    }

    [Fact]
    public void ClaudeAuthFailurePrintedOutsideTheEnvelopeBecomesTheStructuredAuthCode()
    {
        // A CLI também imprime a falta de credencial FORA do stream-json (linha solta) e sai
        // com código 1 — sem a tradução o classificador via só exit_code_1 e a conta voltava
        // como transitória a cada dois minutos.
        var parser = new ClaudeCodeExternalAgentExecutor.ClaudeStreamJsonParser();

        Assert.Empty(parser.ParseLine("Not logged in · Please run /login").ToArray());

        Assert.Equal("executor.authentication_required", parser.FailureCode);
    }

    /// <summary>
    /// A cauda de diagnóstico é o que um humano lê para saber por que o turno morreu. Um aviso
    /// tardio da CLI ("no last agent message") entrava na janela de dez linhas e empurrava
    /// para fora a linha que explicava — e o `failure_reason` gravado no banco passava a
    /// mostrar um aviso no lugar da causa.
    /// </summary>
    [Fact]
    public void TheDiagnosticKeepsTheCauseWhenALateWarningWouldPushItOut()
    {
        var lines = new List<string>
        {
            "ERROR: The 'gpt-5' model is not supported when using Codex with a ChatGPT account.",
        };
        for (var index = 0; index < 12; index++)
        {
            lines.Add($"Warning: no last agent message; wrote empty content to /tmp/last-{index}.txt");
        }

        var diagnostic = ProcessExternalAgentSession.SelectDiagnostic(lines);

        Assert.Contains("not supported when using", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutAnyErrorLineTheDiagnosticStillShowsWhatWasSeen()
    {
        var diagnostic = ProcessExternalAgentSession.SelectDiagnostic(["apenas um aviso"]);

        Assert.Equal("apenas um aviso", diagnostic);
    }

    [Fact]
    public void CodexAccountModelRefusalArrivesAsATypedErrorEventInJsonMode()
    {
        // Observado ao vivo: em modo --json o 400 do plano ChatGPT não sai em texto solto —
        // chega como evento {"type":"error","message":"…"} no JSONL. É a última das três
        // vias (stdout, stderr, evento tipado) por que a falha da conta precisa ser reconhecida.
        var parser = new CodexExternalAgentExecutor.CodexJsonlParser("/unused/last-message.txt");

        Assert.Empty(parser.ParseLine(
            """{"type":"error","message":"unexpected status 400 Bad Request: {\"detail\":\"The 'gpt-5' model is not supported when using Codex with a ChatGPT account.\"}"}""")
            .ToArray());

        Assert.Equal("executor.account_model_unsupported", parser.FailureCode);
    }

    [Fact]
    public void CodexAccountModelRefusalIsCaughtOnStandardErrorToo()
    {
        // Em modo --json os erros fatais saem no stderr, e a cauda de diagnóstico de 10 linhas
        // perde o 400 para o aviso de "last message" — a sentinela vive no parser agora.
        var parser = new CodexExternalAgentExecutor.CodexJsonlParser("/unused/last-message.txt");

        parser.ObserveErrorLine(
            "stream error: unexpected status 400 Bad Request: {\"detail\":\"The 'gpt-5-codex' " +
            "model is not supported when using Codex with a ChatGPT account.\"}; retrying 5/5");

        Assert.Equal("executor.account_model_unsupported", parser.FailureCode);
    }

    /// <summary>
    /// `turn.failed` é o ENVELOPE do fracasso, não a causa: ele chega DEPOIS do erro real.
    /// Escrito por cima, apagava a causa específica e a conta morta continuava elegível —
    /// falha genérica de turno não marca conta indisponível. Foi o que manteve a conta codex
    /// eleita por horas na prova limpa de 2026-08-03, com os cards de Arquitetura voltando a
    /// `ready` indefinidamente.
    /// </summary>
    [Fact]
    public void TheGenericTurnFailureNeverOverwritesTheCauseAlreadyObserved()
    {
        var parser = new CodexExternalAgentExecutor.CodexJsonlParser("/unused/last-message.txt");

        parser.ObserveErrorLine(
            "ERROR: The 'gpt-5' model is not supported when using Codex with a ChatGPT account.");
        var failed = parser.ParseLine("""{"type":"turn.failed"}""").Single();

        Assert.Equal("executor.account_model_unsupported", parser.FailureCode);
        Assert.Equal("executor.account_model_unsupported", failed.Code);
    }

    /// <summary>Sem causa conhecida, o envelope genérico continua sendo a resposta honesta.</summary>
    [Fact]
    public void AnUnexplainedTurnFailureStillReportsTheGenericCode()
    {
        var parser = new CodexExternalAgentExecutor.CodexJsonlParser("/unused/last-message.txt");

        Assert.Equal(
            "executor.turn_failed",
            parser.ParseLine("""{"type":"turn.failed"}""").Single().Code);
    }

    [Fact]
    public void CodexAccountModelRefusalBecomesTheAccountModelCode()
    {
        var parser = new CodexExternalAgentExecutor.CodexJsonlParser("/unused/last-message.txt");

        Assert.Empty(parser.ParseLine(
            "ERROR: unexpected status 400 Bad Request: {\"detail\":\"The 'gpt-5-codex' model " +
            "is not supported when using Codex with a ChatGPT account.\"}").ToArray());

        Assert.Equal("executor.account_model_unsupported", parser.FailureCode);
    }

    [Fact]
    public void CodexQuotaRefusalBecomesTheQuotaCode()
    {
        var parser = new CodexExternalAgentExecutor.CodexJsonlParser("/unused/last-message.txt");

        Assert.Empty(parser.ParseLine(
            "stream error: usage limit reached; retrying 5/5").ToArray());

        Assert.Equal("executor.quota_exhausted", parser.FailureCode);
    }

    [Fact]
    public void GlmContradictorySuccessEnvelopeDoesNotDiscardCompletedWork()
    {
        var parser = new ClaudeCodeExternalAgentExecutor.ClaudeStreamJsonParser();

        var events = parser.ParseLine(
            """{"type":"result","subtype":"success","is_error":true,"result":"entrega concluída"}""")
            .ToArray();

        var completed = Assert.Single(events);
        Assert.Equal(ExternalAgentEventKind.Completed, completed.Kind);
        Assert.Null(parser.FailureCode);
        Assert.Equal("entrega concluída", parser.FinalMessage);
    }

    [Fact]
    public void FinalSuccessEnvelopeClearsAnEarlierTransientResultError()
    {
        var parser = new ClaudeCodeExternalAgentExecutor.ClaudeStreamJsonParser();
        _ = parser.ParseLine(
            """{"type":"result","subtype":"error_during_execution","is_error":true}""")
            .ToArray();
        Assert.Equal("executor.result_error_during_execution", parser.FailureCode);

        var events = parser.ParseLine(
            """{"type":"result","subtype":"success","is_error":false,"result":"ok"}""")
            .ToArray();

        Assert.Equal(ExternalAgentEventKind.Completed, Assert.Single(events).Kind);
        Assert.Null(parser.FailureCode);
    }

    [Fact]
    public void ErrorSubtypeRemainsFailClosedWhenBooleanIsWrong()
    {
        var parser = new ClaudeCodeExternalAgentExecutor.ClaudeStreamJsonParser();

        var failed = Assert.Single(parser.ParseLine(
            """{"type":"result","subtype":"error_max_turns","is_error":false}""")
            .ToArray());

        Assert.Equal(ExternalAgentEventKind.Failed, failed.Kind);
        Assert.Equal("executor.result_error_max_turns", parser.FailureCode);
    }

    [Fact]
    public async Task DisposingASessionCancelsAStderrPipeHeldByAnOrphanedChild()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var childPidFile = Path.Combine(_root, $"orphan-{Guid.NewGuid():N}.pid");
        Directory.CreateDirectory(_root);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "/bin/sh",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add(
            $"sleep 30 >&2 & echo $! > {childPidFile}; exit 0");

        Assert.True(process.Start());
        var session = new ProcessExternalAgentSession(
            "run-orphaned-stderr",
            process,
            new ClaudeCodeExternalAgentExecutor.ClaudeStreamJsonParser(),
            ExecutorCatalog.ClaudeCode,
            "worker-claude-secondary",
            [],
            TimeSpan.FromMinutes(1));
        session.BeginPump();

        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
        await AssertFileAppearsAsync(childPidFile);
        var childPid = int.Parse(
            await File.ReadAllTextAsync(childPidFile),
            System.Globalization.CultureInfo.InvariantCulture);

        try
        {
            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            try
            {
                Process.GetProcessById(childPid).Kill(entireProcessTree: true);
            }
            catch (ArgumentException)
            {
                // O processo filho já encerrou.
            }
            catch (InvalidOperationException)
            {
                // O processo filho já encerrou.
            }
        }
    }

    [Fact]
    public async Task FailedProcessPreservesABoundedRedactedStderrDiagnostic()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "/bin/sh",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add("echo provider-connection-reset >&2; exit 1");

        Assert.True(process.Start());
        await using var session = new ProcessExternalAgentSession(
            "run-stderr-diagnostic",
            process,
            new ClaudeCodeExternalAgentExecutor.ClaudeStreamJsonParser(),
            ExecutorCatalog.ClaudeCode,
            "worker-claude-secondary",
            [],
            TimeSpan.FromMinutes(1));
        session.BeginPump();

        var result = await session.CollectAsync().WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(ExternalAgentRunStatus.Failed, result.Status);
        Assert.Equal("executor.exit_code_1", result.FailureCode);
        Assert.Equal("provider-connection-reset", result.FailureDiagnostic);
    }

    private static async Task AssertFileAppearsAsync(string path)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (!File.Exists(path) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        Assert.True(File.Exists(path), $"Expected child pid file at {path}.");
    }

    public void Dispose()
    {
        foreach (var path in new[] { _root, _workspace })
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
    }
}
