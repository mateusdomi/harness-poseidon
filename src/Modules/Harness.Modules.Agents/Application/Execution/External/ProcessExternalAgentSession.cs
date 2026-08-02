using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;

namespace Harness.Modules.Agents.Application.Execution.External;

/// <summary>
/// Traduz uma linha da saída do executor em eventos e acumula o estado final do turno.
/// Cada CLI real tem seu próprio parser; nenhum campo é adivinhado.
/// </summary>
internal interface IExternalAgentOutputParser
{
    string? SessionId { get; }

    string? FinalMessage { get; }

    ExternalAgentUsage? Usage { get; }

    string? FailureCode { get; }

    IEnumerable<ExternalAgentEvent> ParseLine(string line);

    /// <summary>
    /// Observa uma linha de STDERR apenas para classificar falha (ex.: sentinela de
    /// autenticação do `agy`, que ele imprime em stderr e ainda assim sai com código 0).
    /// Não emite evento — o canal de eventos é single-writer, alimentado só pelo stdout —
    /// e só pode tocar <see cref="FailureCode"/>, lido após a junção dos dois fluxos.
    /// </summary>
    void ObserveErrorLine(string line)
    {
    }

    /// <summary>Fechamento após o fim do processo (ex.: ler o arquivo de última mensagem).</summary>
    void Complete();
}

/// <summary>
/// Sessão viva de um executor externo hospedado como subprocesso (CA-4).
///
/// O prompt é entregue por STDIN — nunca em argumento — de modo que não apareça na tabela
/// de processos. Toda saída passa por <see cref="ExternalAgentRedaction"/> antes de virar
/// evento. `DisposeAsync` encerra a ÁRVORE de processos e remove os temporários, portanto
/// nenhuma sessão deixa processo órfão.
/// </summary>
internal sealed class ProcessExternalAgentSession : IExternalAgentSession
{
    // O canal é a visão AO VIVO e é limitado de propósito: um consumidor lento não pode
    // fazer o Host crescer sem limite. O registro autoritativo do turno é o resultado
    // acumulado, não o canal.
    private const int LiveEventCapacity = 1024;
    private const int MaxRetainedDeltas = 2000;
    private const int MaxRetainedErrorLines = 50;

    private readonly Process _process;
    private readonly IExternalAgentOutputParser _parser;
    private readonly string _executorId;
    private readonly string _alias;
    private readonly IReadOnlyList<string> _temporaryPaths;
    private readonly TimeSpan _timeout;
    private readonly Channel<ExternalAgentEvent> _events;
    private readonly List<string> _deltas = [];
    private readonly Queue<string> _errorLines = new();
    private readonly CancellationTokenSource _timeoutSource = new();
    private readonly SemaphoreSlim _collectGate = new(1, 1);
    private readonly long _startedTimestamp;

    private Task? _pump;
    private ExternalAgentRunResult? _result;
    private bool _cancelled;
    private bool _timedOut;
    private bool _disposed;

    internal ProcessExternalAgentSession(
        string runId,
        Process process,
        IExternalAgentOutputParser parser,
        string executorId,
        string alias,
        IReadOnlyList<string> temporaryPaths,
        TimeSpan timeout)
    {
        RunId = runId;
        _process = process;
        _parser = parser;
        _executorId = executorId;
        _alias = alias;
        _temporaryPaths = temporaryPaths;
        _timeout = timeout;
        _startedTimestamp = Stopwatch.GetTimestamp();
        _events = Channel.CreateBounded<ExternalAgentEvent>(
            new BoundedChannelOptions(LiveEventCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true,
            });
    }

    public string RunId { get; }

    public string? SessionId => _parser.SessionId;

    public int ProcessId { get; private set; }

    public bool IsRunning
    {
        get
        {
            try
            {
                return !_process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    internal void BeginPump()
    {
        ProcessId = _process.Id;
        _timeoutSource.CancelAfter(_timeout);
        _pump = Task.Run(PumpAsync);
    }

    public IAsyncEnumerable<ExternalAgentEvent> StreamAsync(
        CancellationToken cancellationToken = default) =>
        _events.Reader.ReadAllAsync(cancellationToken);

    public async Task<ExternalAgentRunResult> CollectAsync(
        CancellationToken cancellationToken = default)
    {
        await _collectGate.WaitAsync(cancellationToken);
        try
        {
            if (_result is not null)
            {
                return _result;
            }

            if (_pump is not null)
            {
                await _pump.WaitAsync(cancellationToken);
            }

            _parser.Complete();

            var exitCode = TryGetExitCode();
            var status = _cancelled
                ? ExternalAgentRunStatus.Cancelled
                : _timedOut
                    ? ExternalAgentRunStatus.TimedOut
                    : _parser.FailureCode is not null || exitCode is not 0
                        ? ExternalAgentRunStatus.Failed
                        : ExternalAgentRunStatus.Completed;

            var failureCode = status switch
            {
                ExternalAgentRunStatus.Completed => null,
                ExternalAgentRunStatus.Cancelled => "executor.cancelled",
                ExternalAgentRunStatus.TimedOut => "executor.timeout",
                _ => _parser.FailureCode ??
                    $"executor.exit_code_{exitCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}",
            };

            _result = new ExternalAgentRunResult(
                _executorId,
                _alias,
                _parser.SessionId,
                status,
                ExternalAgentRedaction.Redact(_parser.FinalMessage ?? string.Empty),
                _deltas,
                _parser.Usage,
                exitCode,
                failureCode,
                (long)Stopwatch.GetElapsedTime(_startedTimestamp).TotalMilliseconds);
            return _result;
        }
        finally
        {
            _collectGate.Release();
        }
    }

    public async Task CancelAsync(CancellationToken cancellationToken = default)
    {
        _cancelled = true;
        if (!IsRunning)
        {
            return;
        }

        try
        {
            _process.StandardInput.Close();
        }
        catch (ObjectDisposedException)
        {
            // A entrada já havia sido fechada após o envio do prompt.
        }
        catch (IOException)
        {
            // O processo já encerrou a leitura.
        }

        try
        {
            await _process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }
        catch (TimeoutException)
        {
            await StopAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await StopAsync(CancellationToken.None);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        _cancelled = true;
        try
        {
            // O processo direto pode já ter saído enquanto um neto ainda mantém stdout/stderr
            // abertos. Cancelar os leitores é obrigatório: matar apenas o PID não fecha um pipe
            // herdado e fazia DisposeAsync aguardar para sempre após o timeout do critic.
            _timeoutSource.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return Task.CompletedTask;
        }

        try
        {
            if (!_process.HasExited)
            {
                // A árvore inteira: um filho do CLI não pode sobreviver à sessão.
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // O processo já terminou entre a checagem e o kill.
        }
        catch (NotSupportedException)
        {
            // Plataforma sem suporte a árvore: o processo direto já foi encerrado.
        }

        return Task.CompletedTask;
    }

    public Task CleanupAsync(CancellationToken cancellationToken = default)
    {
        foreach (var path in _temporaryPaths)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                else if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch (IOException)
            {
                // Artefato já removido ou em uso; a limpeza é best-effort e idempotente.
            }
            catch (UnauthorizedAccessException)
            {
                // Sem permissão para remover: registrado por ausência, nunca por segredo.
            }
        }

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAsync(CancellationToken.None);
        if (_pump is not null)
        {
            try
            {
                await _pump;
            }
            catch (OperationCanceledException)
            {
                // Encerramento forçado durante o descarte.
            }
        }

        await CleanupAsync(CancellationToken.None);
        _timeoutSource.Dispose();
        _collectGate.Dispose();
        _process.Dispose();
    }

    /// <summary>Últimas linhas de stderr, redigidas — usadas apenas para diagnóstico.</summary>
    internal IReadOnlyList<string> CapturedStandardError => [.. _errorLines];

    private async Task PumpAsync()
    {
        var errorTask = Task.Run(ReadStandardErrorAsync);
        try
        {
            while (await _process.StandardOutput.ReadLineAsync(_timeoutSource.Token) is { } line)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                foreach (var @event in _parser.ParseLine(line))
                {
                    if (@event.Kind == ExternalAgentEventKind.Delta &&
                        @event.Text is { Length: > 0 } text &&
                        _deltas.Count < MaxRetainedDeltas)
                    {
                        _deltas.Add(text);
                    }

                    await _events.Writer.WriteAsync(@event, CancellationToken.None);
                }
            }

            await _process.WaitForExitAsync(_timeoutSource.Token);
        }
        catch (OperationCanceledException)
        {
            _timedOut = !_cancelled;
            await StopAsync(CancellationToken.None);
        }
        catch (IOException)
        {
            // O canal foi fechado pelo encerramento do processo.
        }
        finally
        {
            await errorTask;
            _events.Writer.TryComplete();
        }
    }

    private async Task ReadStandardErrorAsync()
    {
        try
        {
            while (await _process.StandardError.ReadLineAsync(_timeoutSource.Token) is { } line)
            {
                // O parser inspeciona a linha CRUA para classificar falha (sentinela de auth);
                // apenas depois ela é redigida para diagnóstico. Nenhum evento é emitido aqui.
                _parser.ObserveErrorLine(line);
                _errorLines.Enqueue(ExternalAgentRedaction.Redact(line));
                if (_errorLines.Count > MaxRetainedErrorLines)
                {
                    _errorLines.Dequeue();
                }
            }
        }
        catch (IOException)
        {
            // Canal fechado no encerramento.
        }
        catch (OperationCanceledException)
        {
            // Encerramento forçado.
        }
    }

    private int? TryGetExitCode()
    {
        try
        {
            return _process.HasExited ? _process.ExitCode : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
