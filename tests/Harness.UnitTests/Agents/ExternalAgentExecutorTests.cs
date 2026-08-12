using System.Diagnostics;
using Harness.Modules.Agents.Application.Accounts;
using Harness.Modules.Agents.Application.Execution;
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
    public void CodexActorRunsWithGitWriteCapabilityAndReadsThePromptFromStandardInput()
    {
        var provisioner = new AccountProfileProvisioner(_root);
        var handle = Provision(provisioner, "worker-codex-frontend", ExecutorCatalog.Codex);
        var executor = CodexExternalAgentExecutor.Create(provisioner);

        var arguments = Arguments(executor, Request(handle));

        Assert.Equal("codex", executor.Profile.Command);
        Assert.Equal("exec", arguments[0]);
        Assert.Contains("--json", arguments);
        Assert.Contains("--skip-git-repo-check", arguments);
        Assert.Equal("danger-full-access", arguments[arguments.IndexOf("--sandbox") + 1]);
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
        var resumeIndex = arguments.IndexOf("resume");
        Assert.True(resumeIndex > arguments.IndexOf("--output-last-message"));
        Assert.True(resumeIndex > arguments.IndexOf("-C"));
        Assert.True(resumeIndex > arguments.IndexOf("--sandbox"));
        Assert.Equal("019f8637-2252-7c21-9fd4-e87af6dc6dee", arguments[resumeIndex + 1]);
        Assert.Equal("-", arguments[^1]);
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

        // "turbo" não existe em nenhum nível aceito pela CLI instalada — recusa fail-closed,
        // nunca um argv inventado nem um descarte silencioso.
        var exception = await Assert.ThrowsAsync<ExternalAgentException>(
            () => executor.StartAsync(Request(handle, effort: "turbo")));
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

    /// <summary>
    /// Processo vivo NÃO prova trabalho. Observado ao vivo em 2026-08-03: a CLI recebeu 429 de
    /// cota, caiu no modo não-streaming e ficou dezesseis minutos em `epoll` — sem UMA conexão
    /// ao modelo e sem UMA linha de saída. O único fim possível era o timeout de trinta
    /// minutos, classificado como transitório: tudo de novo, na mesma conta morta.
    /// </summary>
    [Fact]
    public async Task ASilentProcessIsEndedAsStalledInsteadOfHoldingTheAttempt()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var process = StartShell("sleep 120");
        await using var session = new ProcessExternalAgentSession(
            "run-no-progress",
            process,
            new ClaudeCodeExternalAgentExecutor.ClaudeStreamJsonParser(),
            ExecutorCatalog.ClaudeCode,
            "worker-glm-general",
            [],
            TimeSpan.FromMinutes(30),
            TimeSpan.FromSeconds(1));
        session.BeginPump();

        var result = await session.CollectAsync().WaitAsync(TimeSpan.FromSeconds(20));

        // Falha, e com código PRÓPRIO: cancelamento diria "alguém mandou parar" e o timeout
        // diria "demorou demais". O que houve foi silêncio.
        Assert.Equal(ExternalAgentRunStatus.Failed, result.Status);
        Assert.Equal("executor.no_progress", result.FailureCode);
    }

    /// <summary>Quem fala continua vivo: o vigia mede SILÊNCIO, não duração.</summary>
    [Fact]
    public async Task AProcessThatKeepsTalkingIsNeverTreatedAsStalled()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var process = StartShell(
            "i=0; while [ $i -lt 6 ]; do echo tick; sleep 0.5; i=$((i+1)); done");
        await using var session = new ProcessExternalAgentSession(
            "run-progress",
            process,
            new ClaudeCodeExternalAgentExecutor.ClaudeStreamJsonParser(),
            ExecutorCatalog.ClaudeCode,
            "worker-glm-general",
            [],
            TimeSpan.FromMinutes(30),
            TimeSpan.FromSeconds(2));
        session.BeginPump();

        var result = await session.CollectAsync().WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(ExternalAgentRunStatus.Completed, result.Status);
    }

    /// <summary>
    /// Quando a CLI não escreve o motivo em lugar nenhum que o host leia, o motivo é buscado
    /// no log DELA. Sem isso, `run.timeout` chega ao humano como resposta final e a linha que
    /// dizia "cota semanal esgotada" morre dentro do contêiner.
    /// </summary>
    [Fact]
    public async Task WhenTheCliSaysNothingTheCauseIsFetchedFromItsOwnLog()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var process = StartShell("exit 1");
        await using var session = new ProcessExternalAgentSession(
            "run-probe",
            process,
            new ClaudeCodeExternalAgentExecutor.ClaudeStreamJsonParser(),
            ExecutorCatalog.ClaudeCode,
            "worker-glm-general",
            [],
            TimeSpan.FromMinutes(1),
            default,
            (_, _) => Task.FromResult<string?>(
                "[ERROR] 429 rate_limit_error: Weekly/Monthly Limit Exhausted"));
        session.BeginPump();

        var result = await session.CollectAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(ExternalAgentRunStatus.Failed, result.Status);
        Assert.Contains("Weekly/Monthly Limit Exhausted", result.FailureDiagnostic);
    }

    /// <summary>O que a CLI já explicou tem precedência: a sonda é o último recurso.</summary>
    [Fact]
    public async Task TheProbeNeverOverwritesACauseTheCliAlreadyPrinted()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var process = StartShell("echo 'error: provider-connection-reset' >&2; exit 1");
        await using var session = new ProcessExternalAgentSession(
            "run-probe-not-needed",
            process,
            new ClaudeCodeExternalAgentExecutor.ClaudeStreamJsonParser(),
            ExecutorCatalog.ClaudeCode,
            "worker-glm-general",
            [],
            TimeSpan.FromMinutes(1),
            default,
            (_, _) => Task.FromResult<string?>("cauda do log que não deveria ser lida"));
        session.BeginPump();

        var result = await session.CollectAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("error: provider-connection-reset", result.FailureDiagnostic);
    }

    private static Process StartShell(string script)
    {
        var process = new Process
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
        process.StartInfo.ArgumentList.Add(script);
        Assert.True(process.Start());
        return process;
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

    /// <summary>
    /// O GLM é o MESMO binário do Claude Code apontado a outro endpoint por ambiente, e o
    /// operador carrega esse ambiente no processo do Host. Bastava uma dessas variáveis
    /// sobreviver para a conta Claude — autenticada e com cota — passar a falar com o endpoint
    /// do GLM: a própria CLI avisa que "another auth source takes precedence over your
    /// claude.ai login", e o run morria com a cota semanal do GLM. Medido em 03/08/2026.
    ///
    /// Não copiar não bastava: é preciso ZERAR, para que nenhum caminho de herança vença.
    /// </summary>
    [Theory]
    [InlineData("ANTHROPIC_BASE_URL")]
    [InlineData("ANTHROPIC_AUTH_TOKEN")]
    [InlineData("ANTHROPIC_API_KEY")]
    [InlineData("ANTHROPIC_DEFAULT_OPUS_MODEL")]
    public void AClaudeAccountIsNeverRedirectedToAnotherProvidersEndpoint(string variable)
    {
        var provisioner = new AccountProfileProvisioner(_root);
        var handle = Provision(provisioner, "chief-claude-primary", ExecutorCatalog.ClaudeCode);

        var environment = provisioner.BuildEnvironment(
            handle.Layout,
            ExecutorCatalog.Find(ExecutorCatalog.ClaudeCode)!,
            new Dictionary<string, string?>
            {
                [variable] = "https://api.z.ai/api/anthropic",
                ["HOME"] = "/Users/dono",
                ["USER"] = "dono",
            });

        Assert.True(environment.ContainsKey(variable), $"{variable} precisa ser zerada, não omitida.");
        Assert.Equal(string.Empty, environment[variable]);
    }

    /// <summary>
    /// E o contrapeso: o GLM DECLARA essas variáveis no allowlist porque é assim que ele
    /// funciona. Zerá-las nele quebraria a conta em vez de protegê-la.
    /// </summary>
    [Fact]
    public void TheAccountThatDeclaresTheRedirectStillReceivesIt()
    {
        var provisioner = new AccountProfileProvisioner(_root);
        var handle = Provision(provisioner, "worker-glm-general", ExecutorCatalog.Glm);

        var environment = provisioner.BuildEnvironment(
            handle.Layout,
            ExecutorCatalog.Find(ExecutorCatalog.Glm)!,
            new Dictionary<string, string?>
            {
                ["ANTHROPIC_BASE_URL"] = "https://api.z.ai/api/anthropic",
                ["HOME"] = "/Users/dono",
                ["USER"] = "dono",
            });

        Assert.Equal("https://api.z.ai/api/anthropic", environment["ANTHROPIC_BASE_URL"]);
    }

    /// <summary>
    /// O limite de SESSÃO da assinatura veste o mesmo disfarce que a falta de credencial: a
    /// CLI não emite código de cota — escreve "You've hit your session limit · resets 11:30am"
    /// como texto do assistente, marca `is_error` e sai com 1. Sem sentinela isso virava
    /// `exit_code_1`, que a classificação lê como TRANSITÓRIO — a conta seguia "disponível",
    /// voltava à eleição a cada rodada e a esteira batia numa parede que só o relógio abre.
    /// Medido em 03/08/2026: uma hora e meia sem um único token.
    /// </summary>
    [Fact]
    public void TheSubscriptionSessionLimitIsQuotaAndNotATransientFailure()
    {
        var parser = new ClaudeCodeExternalAgentExecutor.ClaudeStreamJsonParser();

        var failed = parser.ParseLine(
            """{"type":"result","subtype":"error","is_error":true,"result":"You've hit your session limit · resets 11:30am (America/Sao_Paulo)"}""")
            .Single();

        Assert.Equal("executor.quota_exhausted", parser.FailureCode);
        Assert.Equal(ExternalAgentEventKind.Failed, failed.Kind);
    }

    /// <summary>
    /// E a cota tem de sobreviver à travessia até a política: é ela que manda a conta para
    /// cooldown em vez de reelegê-la na rodada seguinte.
    /// </summary>
    [Fact]
    public void ASessionLimitSendsTheAccountToCooldownInsteadOfBackToTheElection()
    {
        var outcome = AgentRunOutcomeClassifier.Classify(
            ExternalAgentRunStatus.Failed, "executor.quota_exhausted");

        Assert.Equal(AgentRunOutcomeKind.QuotaExhausted, outcome.Kind);
        Assert.NotNull(outcome.SuggestedCooldown);
    }

    /// <summary>
    /// O contrapeso: "limit" sozinho aparece em trabalho legítimo sobre limites, e um falso
    /// positivo tira uma conta boa da eleição até o relógio virar.
    /// </summary>
    [Fact]
    public void TalkingAboutLimitsIsNotHittingOne()
    {
        var parser = new ClaudeCodeExternalAgentExecutor.ClaudeStreamJsonParser();

        _ = parser.ParseLine(
            """{"type":"result","subtype":"success","is_error":false,"result":"Documentei o rate limit da API e o limite de upload."}""")
            .ToArray();

        Assert.Null(parser.FailureCode);
    }

    /// <summary>
    /// O defeito que a taxonomia tipada existe para matar: o classificador fazia
    /// <c>code.Contains(sinal)</c> sobre o NOSSO PRÓPRIO código interno, então
    /// <c>executor.exit_code_1</c> casava com o sinal <c>"exit_code"</c> e virava
    /// "transitório". Foi assim que uma conta com a sessão esgotada seguiu elegível por uma
    /// hora e meia em 2026-08-03, batendo na mesma parede a cada rodada.
    ///
    /// Com o tipo declarado pelo adaptador, o texto do código deixa de decidir qualquer coisa.
    /// </summary>
    [Fact]
    public void TheDeclaredKindDecidesEvenWhenTheCodeTextWouldSaySomethingElse()
    {
        var outcome = AgentRunOutcomeClassifier.Classify(
            ExternalAgentRunStatus.Failed,
            ExternalFailureKind.QuotaExhausted,
            // O texto continua sendo o que a CLI deu, e continua enganando a heurística legada.
            failureCode: "executor.exit_code_1");

        Assert.Equal(AgentRunOutcomeKind.QuotaExhausted, outcome.Kind);
        Assert.NotNull(outcome.SuggestedCooldown);

        // Prova de que o texto sozinho ainda erraria — é exatamente o defeito medido.
        var legacy = AgentRunOutcomeClassifier.Classify(
            ExternalAgentRunStatus.Failed, "executor.exit_code_1");
        Assert.Equal(AgentRunOutcomeKind.Transient, legacy.Kind);
    }

    /// <summary>
    /// Adaptador que ainda não classifica não pode quebrar: <c>Unknown</c> cai na heurística
    /// legada, que é o que mantém o executor não migrado funcionando durante a transição.
    /// </summary>
    [Fact]
    public void AnAdapterThatDoesNotClassifyStillFallsBackToTheLegacyReading()
    {
        var outcome = AgentRunOutcomeClassifier.Classify(
            ExternalAgentRunStatus.Failed,
            ExternalFailureKind.Unknown,
            "executor.quota_exhausted");

        Assert.Equal(AgentRunOutcomeKind.QuotaExhausted, outcome.Kind);
    }

    /// <summary>
    /// E o caminho ponta a ponta: a frase da CLI vira TIPO no adaptador, e o tipo é o que
    /// atravessa. O núcleo nunca mais precisa conhecer a frase.
    /// </summary>
    [Fact]
    public void TheSessionLimitPhraseBecomesATypeAtTheAdapterBoundary()
    {
        var parser = new ClaudeCodeExternalAgentExecutor.ClaudeStreamJsonParser();

        _ = parser.ParseLine(
            """{"type":"result","subtype":"error","is_error":true,"result":"You've hit your session limit · resets 11:30am"}""")
            .ToArray();

        Assert.Equal(ExternalFailureKind.QuotaExhausted, parser.FailureKind);
        Assert.Equal(
            AgentRunOutcomeKind.QuotaExhausted,
            AgentRunOutcomeClassifier.Classify(
                ExternalAgentRunStatus.Failed, parser.FailureKind, parser.FailureCode).Kind);
    }

    /// <summary>
    /// O ELO que faltava, e que teria feito a taxonomia inteira não valer nada: o tipo
    /// reconhecido pelo parser precisa chegar ao RESULTADO do run. Enquanto a sessão não o
    /// copiava, o campo existia no contrato, chegava sempre <c>Unknown</c> a quem decide, e a
    /// decisão continuava saindo da leitura de texto — exatamente o defeito que a taxonomia
    /// existe para matar. É a lição do OPS-024: conferir cada elo até o estado final, não parar
    /// no primeiro consertado.
    /// </summary>
    [Fact]
    public async Task TheKindRecognizedByTheParserReachesTheRunResult()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // A CLI anuncia o limite de sessão e sai com código 1 — o mesmo par que produziu o laço
        // de uma hora e quarenta em 2026-08-03.
        using var process = StartShell(
            """echo '{"type":"result","subtype":"error","is_error":true,"result":"You'"'"'ve hit your session limit · resets 11:30am"}'; exit 1""");
        await using var session = new ProcessExternalAgentSession(
            "run-kind-crosses-the-boundary",
            process,
            new ClaudeCodeExternalAgentExecutor.ClaudeStreamJsonParser(),
            ExecutorCatalog.ClaudeCode,
            "worker-claude-secondary",
            [],
            TimeSpan.FromMinutes(1));
        session.BeginPump();

        var result = await session.CollectAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(ExternalAgentRunStatus.Failed, result.Status);
        Assert.Equal(ExternalFailureKind.QuotaExhausted, result.FailureKind);
        Assert.Equal(
            AgentRunOutcomeKind.QuotaExhausted,
            AgentRunOutcomeClassifier.Classify(
                result.Status, result.FailureKind, result.FailureCode, result.FailureDiagnostic).Kind);
    }

    /// <summary>
    /// O envelope de quem PAROU vence o parser: cancelamento é coisa nossa, e o adaptador não
    /// tem como saber. Sem isto, uma parada do Host durante uma falha de conta chegaria como
    /// falha da conta — a família de misattribuição de culpa que já custou cards saudáveis.
    /// </summary>
    [Fact]
    public async Task CancellingTheSessionKeepsTheCancelledKindEvenIfTheParserSawAFailure()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var process = StartShell(
            """echo '{"type":"result","subtype":"error","is_error":true,"result":"You'"'"'ve hit your session limit"}'; sleep 30""");
        await using var session = new ProcessExternalAgentSession(
            "run-cancel-wins",
            process,
            new ClaudeCodeExternalAgentExecutor.ClaudeStreamJsonParser(),
            ExecutorCatalog.ClaudeCode,
            "worker-claude-secondary",
            [],
            TimeSpan.FromMinutes(1));
        session.BeginPump();

        await session.CancelAsync();
        var result = await session.CollectAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(ExternalAgentRunStatus.Cancelled, result.Status);
        Assert.Equal(ExternalFailureKind.Cancelled, result.FailureKind);
    }
}
