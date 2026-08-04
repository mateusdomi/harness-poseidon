using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace Harness.Host.Product;

/// <summary>
/// Como o Poseidon descobre uma porta livre e espera a aplicação da entrega ficar de pé.
///
/// A regra do §8: <b>sleep fixo não é readiness</b>. Dormir cinco segundos e assumir que subiu
/// produz duas falhas silenciosas — a máquina lenta reprova uma entrega correta, e a máquina rápida
/// deixa de esperar o suficiente numa entrega que só demora. O que vale é o fato: a porta responde.
/// </summary>
public static class LocalApplicationProbe
{
    /// <summary>
    /// Uma porta que o sistema operacional acabou de dizer que está livre.
    ///
    /// Há uma corrida inerente entre soltar a porta e a aplicação tomá-la; ela é aceitável porque a
    /// alternativa (fixar porta) colide com QUALQUER outra verificação em paralelo, e o desfecho de
    /// uma colisão aqui é falha de readiness com diagnóstico, não aprovação falsa.
    /// </summary>
    public static int ReserveFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    /// Espera a aplicação responder em <paramref name="url"/>.
    ///
    /// QUALQUER resposta HTTP conta como pronta — inclusive 404. O que se está provando aqui é que
    /// o servidor subiu e atende, não que aquela rota existe; exigir 200 numa rota adivinhada
    /// reprovaria aplicações corretas que simplesmente não publicam raiz.
    ///
    /// Desiste cedo quando o processo morre: continuar esperando um processo morto é gastar o teto
    /// de tempo inteiro para chegar ao mesmo lugar com um diagnóstico pior.
    /// </summary>
    public static async Task<bool> WaitForHttpAsync(
        HttpClient client,
        string url,
        TrustedProcessHandle process,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(process);

        var deadline = DateTimeOffset.UtcNow + timeout;
        var delay = TimeSpan.FromMilliseconds(150);

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!process.IsRunning)
            {
                return false;
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                using var response = await client.SendAsync(request, cancellationToken);
                return true;
            }
            catch (Exception exception) when (
                exception is HttpRequestException or TaskCanceledException or SocketException)
            {
                // Ainda não atende. É o caso comum durante a subida.
            }

            await Task.Delay(delay, cancellationToken);
            if (delay < TimeSpan.FromSeconds(1))
            {
                delay += TimeSpan.FromMilliseconds(150);
            }
        }

        return false;
    }

    /// <summary>
    /// Cliente HTTP para falar com a aplicação da entrega. Sem proxy e sem redirecionamento
    /// automático: o que se mede é o que aquela porta devolveu, não o que outra devolveu depois.
    /// </summary>
    public static HttpClient CreateClient(TimeSpan timeout) =>
        new(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
        {
            Timeout = timeout,
        };
}
