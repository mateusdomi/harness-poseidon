namespace Harness.Host.Product;

/// <summary>
/// A licença para fazer trabalho PESADO — compilar, rodar suíte, subir aplicação, abrir navegador.
///
/// Por que isto passou a ser necessário: até a Fase 5, verificar era ler arquivo. Hoje uma única
/// avaliação de entrega pode disparar dois <c>dotnet build</c>, um <c>dotnet test</c>, um build de
/// frontend, duas subidas da API, um servidor de interface e um navegador. Duas avaliações
/// simultâneas na mesma máquina não somam — elas competem, e a máquina desta operação já travou por
/// isso antes, com sete agentes e 35 GB.
///
/// O que este tipo NÃO é: um gerenciador de recursos. Não mede memória, não prioriza, não escalona
/// e não conhece tipos de trabalho. É um contador com teto, e a decisão deliberada é que ele
/// continue sendo só isso até o Golden Run mostrar o que realmente falta.
///
/// <b>Trabalho leve não passa por aqui.</b> Ler um arquivo, resolver um perfil, consultar o ledger
/// e decidir um portão continuam correndo sem licença — enfileirá-los atrás de um build seria
/// transformar uma trava de segurança num gargalo.
/// </summary>
public sealed class HeavyWorkPermit : IDisposable
{
    private readonly SemaphoreSlim _gate;
    private readonly object _sync = new();
    private readonly List<(long Sequence, int Priority, TaskCompletionSource<bool> Waiter, string Label)> _queue = [];
    private readonly Dictionary<long, string> _inFlight = [];
    private long _sequence;
    private int _held;

    public HeavyWorkPermit(int maxConcurrent = 1)
    {
        // Zero travaria a fábrica inteira em silêncio: nenhuma verificação jamais rodaria, e o
        // sintoma seria "tudo parado" sem erro nenhum. O piso de 1 é o que impede um valor de
        // configuração errado de virar um sistema morto.
        MaxConcurrent = Math.Max(1, maxConcurrent);
        _gate = new SemaphoreSlim(MaxConcurrent, MaxConcurrent);
    }

    public int MaxConcurrent { get; }

    /// <summary>Quantas operações pesadas estão em curso AGORA.</summary>
    public int InFlight => Volatile.Read(ref _held);

    /// <summary>Quantas licenças ainda podem ser tomadas sem esperar.</summary>
    public int Available => _gate.CurrentCount;

    /// <summary>Quantas operações estão na FILA, aguardando recurso. Espera é estado, não erro.</summary>
    public int Waiting
    {
        get { lock (_sync) { return _queue.Count; } }
    }

    /// <summary>
    /// A fotografia da fila e do que está rodando — o "estado visível" da Onda 0.5. Um card
    /// aguardando recurso aparece AQUI com o rótulo dele, em vez de sumir num await anônimo.
    /// </summary>
    public (IReadOnlyList<string> Running, IReadOnlyList<string> Queued) Snapshot()
    {
        lock (_sync)
        {
            return (
                [.. _inFlight.Values],
                [.. _queue.OrderByDescending(item => item.Priority).ThenBy(item => item.Sequence)
                    .Select(item => item.Label)]);
        }
    }

    /// <summary>
    /// Toma a licença, esperando se preciso. <paramref name="priority"/> maior fura a fila —
    /// prioridade decide QUEM entra quando abre vaga; dentro da mesma prioridade vale a ordem de
    /// chegada, porque inanição de quem chegou primeiro é o defeito clássico de fila por
    /// prioridade e ninguém o percebe até o card barato esperar uma noite inteira.
    /// O descarte devolve — SEMPRE, inclusive quando a operação explode ou estoura o tempo.
    /// </summary>
    public async Task<Lease> AcquireAsync(
        CancellationToken cancellationToken, int priority = 0, string label = "(sem rótulo)")
    {
        TaskCompletionSource<bool>? waiter = null;
        long ticket;
        lock (_sync)
        {
            ticket = ++_sequence;

            // Vaga livre e ninguém com prioridade maior esperando: entra direto.
            if (_gate.CurrentCount > 0 && _queue.Count == 0)
            {
                // consumo síncrono garantido: CurrentCount > 0 dentro do lock e todo consumo passa
                // por aqui ou pelo Release abaixo, ambos sob o mesmo lock.
                _gate.Wait(0);
                _held++;
                _inFlight[ticket] = label;
                return new Lease(this, ticket);
            }

            waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Add((ticket, priority, waiter, label));
        }

        using var registration = cancellationToken.Register(() =>
        {
            lock (_sync)
            {
                var index = _queue.FindIndex(item => item.Sequence == ticket);
                if (index >= 0)
                {
                    _queue.RemoveAt(index);
                    waiter.TrySetCanceled(cancellationToken);
                }
            }
        });

        await waiter.Task;
        return new Lease(this, ticket);
    }

    private void Release(long ticket)
    {
        lock (_sync)
        {
            _inFlight.Remove(ticket);

            // Há fila: a vaga vai DIRETO para o próximo por prioridade (maior primeiro, FIFO no
            // empate), sem passar pelo semáforo — entregar via semáforo deixaria a ordem por
            // conta do acaso do agendador.
            if (_queue.Count > 0)
            {
                var next = _queue
                    .OrderByDescending(item => item.Priority)
                    .ThenBy(item => item.Sequence)
                    .First();
                _queue.RemoveAll(item => item.Sequence == next.Sequence);
                _inFlight[next.Sequence] = next.Label;
                next.Waiter.TrySetResult(true);
                return;
            }

            _held--;
            _gate.Release();
        }
    }

    /// <summary>A licença tomada. Descartar é devolver; descartar duas vezes é inócuo.</summary>
    public sealed class Lease(HeavyWorkPermit permit, long ticket) : IDisposable
    {
        private bool _returned;

        public void Dispose()
        {
            if (_returned)
            {
                return;
            }

            _returned = true;
            permit.Release(ticket);
        }
    }

    public void Dispose() => _gate.Dispose();
}
