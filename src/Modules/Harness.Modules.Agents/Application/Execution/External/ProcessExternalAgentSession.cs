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
/// Última tentativa de descobrir POR QUE um turno morreu, quando a saída padrão não disse.
/// Recebe o identificador da sessão da CLI (é por ele que a CLI nomeia o log dela) e devolve
/// a cauda já redigida, ou nulo quando não há nada a dizer.
/// </summary>
internal delegate Task<string?> ExternalDiagnosticProbe(
    string? sessionId, CancellationToken cancellationToken);

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

    private static readonly TimeSpan ProgressPollInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DiagnosticProbeTimeout = TimeSpan.FromSeconds(15);

    private readonly Process _process;
    private readonly IExternalAgentOutputParser _parser;
    private readonly string _executorId;
    private readonly string _alias;
    private readonly IReadOnlyList<string> _temporaryPaths;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _noProgressTimeout;
    private readonly ExternalDiagnosticProbe? _diagnosticProbe;
    private readonly Channel<ExternalAgentEvent> _events;
    private readonly List<string> _deltas = [];
    private readonly Queue<string> _errorLines = new();
    private readonly CancellationTokenSource _timeoutSource = new();
    private readonly SemaphoreSlim _collectGate = new(1, 1);
    private readonly long _startedTimestamp;

    private Task? _pump;
    private Task? _progressWatch;
    private long _lastProgressTimestamp;
    private ExternalAgentRunResult? _result;
    private bool _cancelled;
    private bool _timedOut;
    private bool _stalled;
    private bool _disposed;

    internal ProcessExternalAgentSession(
        string runId,
        Process process,
        IExternalAgentOutputParser parser,
        string executorId,
        string alias,
        IReadOnlyList<string> temporaryPaths,
        TimeSpan timeout,
        TimeSpan noProgressTimeout = default,
        ExternalDiagnosticProbe? diagnosticProbe = null)
    {
        RunId = runId;
        _process = process;
        _parser = parser;
        _executorId = executorId;
        _alias = alias;
        _temporaryPaths = temporaryPaths;
        _timeout = timeout;
        _noProgressTimeout = noProgressTimeout;
        _diagnosticProbe = diagnosticProbe;
        _startedTimestamp = Stopwatch.GetTimestamp();
        _lastProgressTimestamp = _startedTimestamp;
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
        _lastProgressTimestamp = Stopwatch.GetTimestamp();
        _timeoutSource.CancelAfter(_timeout);
        _pump = Task.Run(PumpAsync);
        if (_noProgressTimeout > TimeSpan.Zero)
        {
            _progressWatch = Task.Run(WatchProgressAsync);
        }
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
            // A ordem importa: encerrar por silêncio passa por StopAsync, que marca
            // `_cancelled`. Sem testar `_stalled` primeiro, o travamento seria contado como
            // cancelamento — o motivo REAL apagado pelo envelope do encerramento.
            var status = _stalled
                ? ExternalAgentRunStatus.Failed
                : _cancelled
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
                // Um código específico do parser vale mais que o genérico do silêncio.
                _ when _stalled => _parser.FailureCode ?? "executor.no_progress",
                _ => _parser.FailureCode ??
                    $"executor.exit_code_{exitCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}",
            };

            // Timeout e travamento também precisam de causa. Enquanto só a falha "normal"
            // carregava diagnóstico, um turno morto por silêncio chegava ao humano como
            // `executor.timeout` puro — e a linha que dizia "cota semanal esgotada" ficava
            // dentro do contêiner, sem ninguém para lê-la.
            var diagnostic = status is ExternalAgentRunStatus.Failed or ExternalAgentRunStatus.TimedOut
                ? await ResolveDiagnosticAsync(cancellationToken)
                : null;

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
                (long)Stopwatch.GetElapsedTime(_startedTimestamp).TotalMilliseconds)
            {
                FailureDiagnostic = diagnostic,
            };
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
        if (_progressWatch is not null)
        {
            try
            {
                await _progressWatch;
            }
            catch (OperationCanceledException)
            {
                // Vigia encerrado junto com o turno.
            }
        }

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

    private string? BuildFailureDiagnostic() => SelectDiagnostic([.. _errorLines]);

    /// <summary>
    /// Diagnóstico da falha: primeiro o que a CLI escreveu em stderr; quando isso não explica
    /// nada, o que ela escreveu no log DELA.
    ///
    /// A segunda fonte existe porque o Claude Code 2.0.30 em modo `-p` não manda uma linha
    /// sequer para stderr: o 429 de cota vai só para o arquivo de depuração da sessão, dentro
    /// do contêiner. Sem lê-lo, a causa nunca sai de lá.
    /// </summary>
    private async Task<string?> ResolveDiagnosticAsync(CancellationToken cancellationToken)
    {
        var observed = BuildFailureDiagnostic();
        if (_diagnosticProbe is null || (observed is not null && IsError(observed)))
        {
            return observed;
        }

        string? probed = null;
        try
        {
            // Teto curto: diagnóstico não pode segurar a coleta do turno.
            probed = await _diagnosticProbe(_parser.SessionId, cancellationToken)
                .WaitAsync(DiagnosticProbeTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            // Sem diagnóstico extra: registrado por ausência.
        }
        catch (OperationCanceledException)
        {
            // Coleta cancelada; o observado já basta.
        }

        if (string.IsNullOrWhiteSpace(probed))
        {
            return observed;
        }

        return observed is null ? probed : $"{observed} | {probed}";
    }

    private async Task WatchProgressAsync()
    {
        var poll = _noProgressTimeout < ProgressPollInterval ? _noProgressTimeout : ProgressPollInterval;
        try
        {
            while (!_timeoutSource.IsCancellationRequested)
            {
                await Task.Delay(poll, _timeoutSource.Token);
                if (!IsRunning || _cancelled)
                {
                    return;
                }

                if (Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastProgressTimestamp)) < _noProgressTimeout)
                {
                    continue;
                }

                // Marcar ANTES de parar: StopAsync marca `_cancelled`, e quem lê o resultado
                // precisa distinguir "alguém cancelou" de "a CLI emudeceu".
                _stalled = true;
                await StopAsync(CancellationToken.None);
                return;
            }
        }
        catch (OperationCanceledException)
        {
            // O turno acabou por outro caminho.
        }
    }

    private void MarkProgress() =>
        Interlocked.Exchange(ref _lastProgressTimestamp, Stopwatch.GetTimestamp());

    /// <summary>Escolha das linhas que explicam a falha. Interno para ser verificável sem processo.</summary>
    internal static string? SelectDiagnostic(IReadOnlyCollection<string> errorLines)
    {
        const int maximumLength = 1200;
        if (errorLines.Count == 0)
        {
            return null;
        }

        var distinct = errorLines
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        // As últimas linhas nem sempre são as que explicam. Um aviso tardio da CLI ("no last
        // agent message") entrava na janela e EMPURRAVA para fora a linha que dizia por que o
        // turno morreu — o humano lia um aviso onde deveria ler a causa. Erro tem precedência
        // sobre aviso; entre erros, a ordem original é preservada.
        var errors = distinct.Where(IsError).ToArray();
        var selected = errors.Length > 0
            ? errors.TakeLast(10).Concat(distinct.Where(line => !IsError(line)).TakeLast(2))
            : distinct.TakeLast(10);

        var diagnostic = string.Join(" | ", selected);
        return diagnostic.Length <= maximumLength
            ? diagnostic
            : diagnostic[..maximumLength];
    }

    /// <summary>Linha que descreve falha, e não progresso. Deliberadamente ampla: o custo de
    /// classificar um aviso como erro é ruído no diagnóstico; o de perder o erro é o humano
    /// lendo a linha errada.</summary>
    private static bool IsError(string line) =>
        line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("not supported", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("usage limit", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("denied", StringComparison.OrdinalIgnoreCase);

    private async Task PumpAsync()
    {
        var errorTask = Task.Run(ReadStandardErrorAsync);
        try
        {
            while (await _process.StandardOutput.ReadLineAsync(_timeoutSource.Token) is { } line)
            {
                MarkProgress();
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
                MarkProgress();

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
