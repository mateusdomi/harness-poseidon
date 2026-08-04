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

    /// <summary>
    /// Toma a licença, esperando se preciso. O descarte devolve — e devolve SEMPRE, inclusive
    /// quando a operação explode ou estoura o tempo: uma licença presa por exceção pararia toda
    /// verificação seguinte, e o sintoma seria indistinguível de uma fila legítima.
    /// </summary>
    public async Task<Lease> AcquireAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        Interlocked.Increment(ref _held);
        return new Lease(this);
    }

    private void Release()
    {
        Interlocked.Decrement(ref _held);
        _gate.Release();
    }

    /// <summary>A licença tomada. Descartar é devolver; descartar duas vezes é inócuo.</summary>
    public sealed class Lease(HeavyWorkPermit permit) : IDisposable
    {
        private bool _returned;

        public void Dispose()
        {
            if (_returned)
            {
                return;
            }

            _returned = true;
            permit.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
