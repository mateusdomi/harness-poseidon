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

    protected ProcessExternalAgentExecutor(
        ExecutorProfile profile, AccountProfileProvisioner profiles, ExecutorProbe? probe = null)
    {
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _probe = probe ?? new ExecutorProbe();
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

        var context = new ExternalAgentRunContext(
            RunId: $"run-{Guid.NewGuid():N}",
            LastMessagePath: Path.Combine(
                request.Profile.SessionStorePath, $"last-message-{Guid.NewGuid():N}.txt"));

        var arguments = BuildArguments(request, context);
        GuardArguments(arguments);

        var promptAsArgument = PromptDelivery == ExternalPromptDelivery.PositionalArgument;

        var startInfo = new ProcessStartInfo
        {
            FileName = Profile.Command,
            WorkingDirectory = request.WorkingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

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
        foreach (var entry in _profiles.BuildEnvironment(request.Profile, Profile))
        {
            startInfo.Environment[entry.Key] = entry.Value;
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
