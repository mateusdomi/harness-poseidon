using System.Diagnostics;
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
        string ContainerStateDirectory);

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

        var process = Process.Start(startInfo)
            ?? throw new ExternalAgentException("executor.start_failed");

        var session = new ProcessExternalAgentSession(
            context.RunId,
            process,
            CreateParser(request, context),
            ExecutorId,
            request.Alias,
            TemporaryPaths(context),
            request.Timeout);
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
    /// Precisa ser um caminho que a CLI possa ESCREVER — e o Codex roda com sandbox próprio
    /// (`--sandbox workspace-write`), cujas raízes graváveis são o diretório de trabalho,
    /// `/tmp` e `$TMPDIR`. O destino anterior era o `sessions/` do perfil isolado, que fica
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
