using System;
using System.Threading;
using System.Threading.Tasks;

namespace Harness.Bruna.Desktop;

public enum BrunaVisualState
{
    Idle,
    Starting,
    Working,
    Waiting,
    Error,
}

/// <summary>
/// Gerencia os estados visuais da Bruna com base na saúde do Poseidon e interações do usuário.
/// Faz polling suave no health check do Poseidon (a cada 5s) e nunca bloqueia a UI.
/// </summary>
public sealed class BrunaStateManager : IDisposable
{
    private readonly PoseidonDiscovery _discovery;
    private readonly Timer _pollingTimer;
    private BrunaVisualState _state;
    private bool _disposed;

    public event EventHandler<BrunaVisualState>? StateChanged;

    public BrunaStateManager(PoseidonDiscovery discovery, TimeSpan? pollInterval = null)
    {
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        _state = BrunaVisualState.Idle;
        var interval = pollInterval ?? TimeSpan.FromSeconds(5);
        _pollingTimer = new Timer(
            async _ => await TickAsync().ConfigureAwait(false),
            null,
            TimeSpan.Zero,
            interval);
    }

    public BrunaVisualState State => _state;

    public void SetUserInteractionState(BrunaVisualState state)
    {
        lock (_pollingTimer)
        {
            if (_disposed) return;
            UpdateState(state);
        }
    }

    public void ResetToObservedState()
    {
        lock (_pollingTimer)
        {
            if (_disposed) return;
            _ = TickAsync();
        }
    }

    private async Task TickAsync()
    {
        PoseidonInstance instance;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            instance = await _discovery.ProbeAsync(timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            instance = new PoseidonInstance(PoseidonStatus.Unhealthy, null, null, null, "Falha no probe.");
        }

        lock (_pollingTimer)
        {
            if (_disposed) return;

            var newState = instance.Status switch
            {
                PoseidonStatus.Healthy => BrunaVisualState.Idle,
                PoseidonStatus.Starting => BrunaVisualState.Starting,
                PoseidonStatus.Unhealthy => BrunaVisualState.Error,
                PoseidonStatus.Stopped => BrunaVisualState.Idle,
                _ => BrunaVisualState.Waiting,
            };

            UpdateState(newState);
        }
    }

    private void UpdateState(BrunaVisualState newState)
    {
        if (_state == newState) return;
        _state = newState;
        StateChanged?.Invoke(this, _state);
    }

    public void Dispose()
    {
        lock (_pollingTimer)
        {
            if (_disposed) return;
            _disposed = true;
            _pollingTimer.Dispose();
        }
    }
}
