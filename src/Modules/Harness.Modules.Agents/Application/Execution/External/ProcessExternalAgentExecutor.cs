using System.Diagnostics;
using System.Globalization;
using Harness.Modules.Agents.Application.Accounts;
using Harness.Modules.Agents.Contracts;

namespace Harness.Modules.Agents.Application.Execution.External;

/// <summary>
/// Base dos adapters que hospedam uma CLI real como subprocesso (CA-4).
///
/// Responsabilidades comuns: probe, montagem do ambiente a partir do perfil ISOLADO da
/// conta (CA-3), recusa de segredo em argumento, entrega do prompt por stdin e criação da
/// sessão. A tradução da saída é do adapter concreto.
/// </summary>
public abstract class ProcessExternalAgentExecutor : IExternalAgentExecutor
{
    /// <summary>Cauda lida do log da CLI: o bastante para conter a causa, pouco para virar log.</summary>
    private const int DiagnosticTailLines = 40;

    private readonly AccountProfileProvisioner _profiles;
    private readonly ExecutorProbe _probe;
    private SandboxedCommand? _sandbox;

    protected ProcessExternalAgentExecutor(
        ExecutorProfile profile, AccountProfileProvisioner profiles, ExecutorProbe? probe = null)
    {
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _probe = probe ?? new ExecutorProbe();
    }

    /// <summary>
    /// Comando de sandbox resolvido: executável do host (o cliente <c>docker</c>), argumentos de
    /// prefixo (o <c>docker exec …</c> completo), os valores de ambiente que o contêiner herda
    /// por <c>--env NOME</c> e os caminhos DE DENTRO do contêiner. Primitivos em vez do tipo do
    /// plano para o módulo de agentes não depender do módulo de execução.
    /// </summary>
    public sealed record SandboxedCommand(
        string HostExecutablePath,
        IReadOnlyList<string> ExecutablePrefixArguments,
        IReadOnlyDictionary<string, string>? ContainerEnvironment,
        /// <summary>O workdir do agente no contêiner (a worktree montada).</summary>
        string AgentWorkingDirectory,
        /// <summary>O volume de estado gravável do contêiner.</summary>
        string ContainerStateDirectory,
        /// <summary>Nome do contêiner do agente, para ler diagnóstico de dentro dele.</summary>
        string? ContainerName = null);

    /// <summary>
    /// Liga o executor a uma sessão de sandbox já aberta: o processo hospedado passa a ser o
    /// <c>docker run</c> do plano, e os argumentos da CLI são acrescentados depois do prefixo.
    /// A autenticação e o ambiente chegam ao contêiner pela sessão (montagem somente-leitura +
    /// <c>--env</c> sem valor no argv) — nunca por argumento nem por log.
    /// </summary>
    public void AttachSandbox(SandboxedCommand sandbox)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        _sandbox = sandbox;
    }

    public ExecutorProfile Profile { get; }

    public string ExecutorId => Profile.ExecutorId;

    /// <summary>
    /// Como o prompt chega à CLI. O padrão é STDIN, de modo que o prompt não apareça na
    /// tabela de processos. Uma CLI que só aceita o prompt como argumento posicional —
    /// observado no `agy --print` — declara <see cref="ExternalPromptDelivery.PositionalArgument"/>;
    /// é uma limitação REAL do binário, não uma escolha do adapter.
    /// </summary>
    protected virtual ExternalPromptDelivery PromptDelivery => ExternalPromptDelivery.StandardInput;

    public Task<ExecutorProbeResult> ProbeAsync(CancellationToken cancellationToken = default) =>
        _probe.ProbeAsync(Profile, cancellationToken);

    /// <summary>
    /// Argumentos da CLI para este pedido. Nunca contêm o prompt nem segredo. É público de
    /// propósito: é o contrato com a CLI instalada e precisa ser verificável por teste sem
    /// gastar cota.
    /// </summary>
    public abstract IReadOnlyList<string> BuildArguments(
        ExternalAgentRunRequest request, ExternalAgentRunContext context);

    /// <summary>Parser da saída desta CLI.</summary>
    private protected abstract IExternalAgentOutputParser CreateParser(
        ExternalAgentRunRequest request, ExternalAgentRunContext context);

    /// <summary>Artefatos temporários criados para o run, removidos no cleanup.</summary>
    protected virtual IReadOnlyList<string> TemporaryPaths(ExternalAgentRunContext context) => [];

    /// <summary>
    /// Caminho do log que a PRÓPRIA CLI mantém para a sessão, relativo ao config home dela.
    /// Nulo quando a CLI não mantém um — e aí não há segunda fonte de diagnóstico.
    /// </summary>
    /// <param name="configHome">
    /// O config home VÁLIDO no ambiente onde a CLI rodou: o do host quando não há sandbox, o
    /// do contêiner quando há.
    /// </param>
    private protected virtual string? ResolveCliDiagnosticPath(string configHome, string sessionId) => null;

    /// <summary>
    /// Segunda fonte, no HOST: o transcript que a CLI mantém da própria sessão, quando ela não
    /// escreve o log de depuração.
    ///
    /// Existe porque a primeira fonte não é estável entre versões: o Claude Code 2.0.30
    /// escrevia o motivo em <c>debug/&lt;sessão&gt;.txt</c> e o 2.1.220 não escreve — o arquivo
    /// simplesmente não existe. O motivo continuava registrado, em outro lugar, e a sonda
    /// olhava para o lugar antigo e devolvia nada; o turno morria como
    /// <c>executor.exit_code_1</c>, que a classificação lê como transitório, e o card era
    /// redespachado contra a mesma parede indefinidamente.
    ///
    /// Devolve <see langword="null"/> quando o executor não tem transcript próprio.
    /// </summary>
    private protected virtual string? ResolveCliTranscriptPath(string configHome, string sessionId) => null;

    /// <summary>
    /// Traduz as linhas cruas de uma fonte de diagnóstico em texto legível. O padrão é a
    /// identidade; um transcript estruturado precisa extrair a mensagem de dentro do envelope.
    /// </summary>
    private protected virtual IReadOnlyList<string> ExtractDiagnosticLines(
        string path, IReadOnlyList<string> rawLines) => rawLines;

    /// <summary>
    /// Sonda de última instância: lê a cauda do log da CLI quando o turno morreu sem dizer por
    /// quê. Dentro da sandbox isso exige entrar no contêiner — o arquivo mora em volume, sem
    /// caminho no host.
    /// </summary>
    private ExternalDiagnosticProbe? CreateDiagnosticProbe(ExternalAgentRunRequest request)
    {
        var configHome = _sandbox?.ContainerStateDirectory ?? request.Profile.ConfigHomePath;
        if (ResolveCliDiagnosticPath(configHome, "probe") is null)
        {
            return null;
        }

        var containerName = _sandbox?.ContainerName;
        var dockerPath = _sandbox?.HostExecutablePath;
        return async (sessionId, cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                // Sem identificador de sessão não há arquivo a ler: a CLI nomeia o log por ele.
                return null;
            }

            var path = ResolveCliDiagnosticPath(configHome, sessionId);
            if (path is null)
            {
                return null;
            }

            var lines = containerName is null || dockerPath is null
                ? ReadHostTail(path)
                : await ReadContainerTailAsync(dockerPath, containerName, path, cancellationToken);
            lines = ExtractDiagnosticLines(path, lines);

            // A primeira fonte vazia não é resposta: ela some entre versões da CLI. O transcript
            // fica no host, então esta segunda tentativa só existe fora do contêiner — dentro
            // dele o volume não tem caminho de host para varrer.
            if (lines.Count == 0 && containerName is null &&
                ResolveCliTranscriptPath(configHome, sessionId) is { } transcriptPath)
            {
                lines = ExtractDiagnosticLines(transcriptPath, ReadHostTail(transcriptPath));
            }

            return lines.Count == 0
                ? null
                : ProcessExternalAgentSession.SelectDiagnostic(
                    [.. lines.Select(ExternalAgentRedaction.Redact)]);
        };
    }

    private static IReadOnlyList<string> ReadHostTail(string path)
    {
        try
        {
            return File.Exists(path) ? [.. File.ReadLines(path).TakeLast(DiagnosticTailLines)] : [];
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static async Task<IReadOnlyList<string>> ReadContainerTailAsync(
        string dockerPath, string containerName, string path, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = dockerPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(containerName);
        startInfo.ArgumentList.Add("tail");
        startInfo.ArgumentList.Add("-n");
        startInfo.ArgumentList.Add(DiagnosticTailLines.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(path);

        try
        {
            using var probe = Process.Start(startInfo);
            if (probe is null)
            {
                return [];
            }

            var output = await probe.StandardOutput.ReadToEndAsync(cancellationToken);
            await probe.WaitForExitAsync(cancellationToken);
            return probe.ExitCode == 0
                ? output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : [];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // O contêiner já pode ter sido removido junto com a tentativa: diagnóstico é
            // best-effort e nunca derruba a coleta do turno.
            return [];
        }
    }

    public async Task<IExternalAgentSession> StartAsync(
        ExternalAgentRunRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);

        var effectiveRequest = ResolveSandboxedRequest(request, _sandbox);
        var context = new ExternalAgentRunContext(
            RunId: $"run-{Guid.NewGuid():N}",
            LastMessagePath: ResolveLastMessagePath(
                request, _sandbox, $"last-message-{Guid.NewGuid():N}.txt"));

        var arguments = BuildArguments(effectiveRequest, context);
        GuardArguments(arguments);

        var promptAsArgument = PromptDelivery == ExternalPromptDelivery.PositionalArgument;

        var startInfo = new ProcessStartInfo
        {
            FileName = _sandbox?.HostExecutablePath ?? Profile.Command,
            WorkingDirectory = request.WorkingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        if (_sandbox is { } sandbox)
        {
            foreach (var argument in sandbox.ExecutablePrefixArguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (promptAsArgument)
        {
            // A CLI (agy) só lê o prompt como argumento posicional. O prompt é a INSTRUÇÃO,
            // não uma credencial, e passa pelo mesmo guard estrutural que recusa qualquer
            // token com forma de segredo antes de tocar o argv.
            if (ExternalAgentRedaction.ContainsSecret(request.Prompt))
            {
                throw new ExternalAgentException("executor.secret_in_prompt");
            }

            startInfo.ArgumentList.Add(request.Prompt);
        }

        // Ambiente ZERADO e remontado pela allowlist do perfil isolado: a conta nunca herda
        // credencial de outra conta nem variável não declarada.
        startInfo.Environment.Clear();
        if (_sandbox is not null)
        {
            // Sandbox: o processo hospedado é o CLIENTE docker. Ele precisa do mínimo de
            // identidade para rodar (PATH/HOME) e dos valores que o prefixo declarou como
            // `--env NOME` — o docker os encaminha ao contêiner. A montagem do ambiente por
            // allowlist do perfil não se aplica aqui: quem define o ambiente do contêiner é
            // a sessão de sandbox, no momento em que ela é aberta.
            foreach (var inherited in new[] { "PATH", "HOME", "USER", "LANG", "DOCKER_HOST" })
            {
                var inheritedValue = Environment.GetEnvironmentVariable(inherited);
                if (!string.IsNullOrEmpty(inheritedValue))
                {
                    startInfo.Environment[inherited] = inheritedValue;
                }
            }

            foreach (var entry in _sandbox.ContainerEnvironment ??
                (IReadOnlyDictionary<string, string>)new Dictionary<string, string>())
            {
                startInfo.Environment[entry.Key] = entry.Value;
            }
        }
        else
        {
            foreach (var entry in _profiles.BuildEnvironment(request.Profile, Profile))
            {
                startInfo.Environment[entry.Key] = entry.Value;
            }
        }

        // Diagnóstico de ambiente por CHAVE, nunca por valor. Uma falha de autenticação que só
        // acontece quando o Host lança o processo — e não quando um humano roda o mesmo comando
        // — é indistinguível de credencial ausente sem saber o que o filho recebeu.
        Console.Error.WriteLine(
            $"[executor-env] {ExecutorId}/{request.Alias}: " +
            string.Join(",", startInfo.Environment.Keys.Order(StringComparer.Ordinal)));

        var process = Process.Start(startInfo)
            ?? throw new ExternalAgentException("executor.start_failed");

        var session = new ProcessExternalAgentSession(
            context.RunId,
            process,
            CreateParser(request, context),
            ExecutorId,
            request.Alias,
            TemporaryPaths(context),
            request.Timeout,
            request.NoProgressTimeout,
            CreateDiagnosticProbe(request));
        session.BeginPump();

        try
        {
            if (!promptAsArgument)
            {
                // O prompt vai por STDIN: em argumento ele apareceria na tabela de processos.
                await process.StandardInput.WriteAsync(request.Prompt.AsMemory(), cancellationToken);
                await process.StandardInput.FlushAsync(cancellationToken);
            }

            // Sempre fechar a entrada: uma CLI que lê o prompt do argv não pode ficar
            // aguardando EOF de um stdin que nunca chega.
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            await session.StopAsync(CancellationToken.None);
            throw new ExternalAgentException("executor.prompt_write_failed");
        }

        return session;
    }

    /// <summary>
    /// Com sandbox, os caminhos que a CLI recebe são os DE DENTRO do contêiner: a worktree é o
    /// workdir do plano. Um caminho do host não existe lá dentro (observado ao vivo: o codex
    /// falhou o turno tentando gravar a mensagem final no session store do host).
    /// </summary>
    internal static ExternalAgentRunRequest ResolveSandboxedRequest(
        ExternalAgentRunRequest request, SandboxedCommand? sandbox) =>
        sandbox is null ? request : request with { WorkingDirectory = sandbox.AgentWorkingDirectory };

    /// <summary>
    /// O arquivo de mensagem final mora no volume de estado do contêiner quando há sandbox. O
    /// parser do host não o encontra e cai no fallback do stream, que já carrega a mensagem.
    /// </summary>
    /// <summary>
    /// Onde a CLI grava a mensagem final.
    ///
    /// Precisa ser um caminho que a CLI possa ESCREVER — e o Codex roda com política própria
    /// de escrita, cujas raízes graváveis podem variar por modo de sandbox. O destino anterior
    /// era o `sessions/` do perfil isolado, que fica
    /// fora dessas raízes: a CLI avisava `Failed to write last message file … (os error 2)`,
    /// gravava conteúdo vazio e o turno morria sem produzir token (2026-08-03, cards SAD,
    /// Observabilidade e C4 da prova limpa).
    ///
    /// O diagnóstico registrado na época — "o perfil isolado não cria o diretório" — não se
    /// sustentou: o `sessions/` existia em disco desde antes das falhas, com data anterior a
    /// elas. O que faltava era permissão da sandbox do próprio executor, não o diretório.
    ///
    /// Temporário é o lugar certo também por natureza: o arquivo é lido e apagado no mesmo
    /// run, e o `sessions/` guarda sessão, não sobra de turno.
    /// </summary>
    internal static string ResolveLastMessagePath(
        ExternalAgentRunRequest request, SandboxedCommand? sandbox, string fileName) =>
        sandbox is null
            ? Path.Combine(Path.GetTempPath(), fileName)
            : $"/tmp/{fileName}";

    private void Validate(ExternalAgentRunRequest request)
    {
        AgentAccountRegistry.ValidateAlias(request.Alias);
        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            throw new ExternalAgentException("executor.prompt_required");
        }

        if (!Path.IsPathRooted(request.WorkingDirectory) || !Directory.Exists(request.WorkingDirectory))
        {
            throw new ExternalAgentException("executor.working_directory_invalid");
        }

        if (!string.Equals(request.Profile.Alias, request.Alias, StringComparison.Ordinal))
        {
            throw new ExternalAgentException("executor.profile_alias_mismatch");
        }

        if (request.Effort is { Length: > 0 } effort &&
            !Profile.Capabilities.EffortValues.Contains(effort, StringComparer.OrdinalIgnoreCase))
        {
            // Nunca passar um valor de effort que a CLI instalada não aceita.
            throw new ExternalAgentException("executor.effort_unsupported");
        }
    }

    /// <summary>
    /// Nenhum argumento pode aparentar segredo. Um token em argv vaza para a tabela de
    /// processos, para o log do shell e para qualquer captura de comando.
    /// </summary>
    private static void GuardArguments(IReadOnlyList<string> arguments)
    {
        if (arguments.Any(ExternalAgentRedaction.ContainsSecret))
        {
            throw new ExternalAgentException("executor.secret_in_arguments");
        }
    }
}

/// <summary>Identidade e artefatos de um run externo.</summary>
public sealed record ExternalAgentRunContext(string RunId, string LastMessagePath);
