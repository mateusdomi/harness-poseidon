using System.Net;
using System.Net.Http;

namespace Harness.Host.Product;

/// <summary>
/// Um encaminhador que o POSEIDON controla, posto entre a interface e a API durante a jornada.
///
/// Existe para responder a uma pergunta que nenhuma outra verificação responde: <b>a tela chamou o
/// backend de verdade?</b> Compilar prova que a tela existe; a jornada prova que ela funciona; nada
/// disso distingue uma tela que consome a API de uma que renderiza dado embutido. Como o tráfego
/// atravessa este processo, a contagem é fato constatado pelo Poseidon, não dedução.
///
/// É deliberadamente burro: repassa método, caminho, corpo e tipo de conteúdo, e conta. Não
/// interpreta payload, não reescreve resposta e não decide nada — um proxy que opinasse viraria
/// mais uma fonte de verdade sobre o que a aplicação respondeu.
/// </summary>
public sealed class ApiTrafficProbe : IAsyncDisposable
{
    private readonly HttpListener _listener;
    private readonly HttpClient _client;
    private readonly string _target;
    private readonly CancellationTokenSource _stopping = new();
    private Task? _loop;
    private int _requests;
    private int _upstreamFailures;

    private ApiTrafficProbe(HttpListener listener, HttpClient client, string target, string baseUrl)
    {
        _listener = listener;
        _client = client;
        _target = target;
        BaseUrl = baseUrl;
    }

    /// <summary>Endereço que a interface recebe como base da API.</summary>
    public string BaseUrl { get; }

    /// <summary>Quantas requisições da interface atravessaram até a API.</summary>
    public int Requests => Volatile.Read(ref _requests);

    /// <summary>Quantas não conseguiram chegar à API. Tráfego que não chegou não prova integração.</summary>
    public int UpstreamFailures => Volatile.Read(ref _upstreamFailures);

    /// <summary>
    /// Sobe o encaminhador apontando para <paramref name="targetBaseUrl"/>. Devolve nulo quando o
    /// host não permite escutar — e nesse caso a integração simplesmente não é derivada, em vez de
    /// ser assumida.
    /// </summary>
    public static ApiTrafficProbe? TryStart(string targetBaseUrl)
    {
        var port = LocalApplicationProbe.ReserveFreePort();
        var baseUrl = $"http://127.0.0.1:{port}";
        var listener = new HttpListener();
        listener.Prefixes.Add(baseUrl + "/");

        try
        {
            listener.Start();
        }
        catch (Exception exception) when (exception is HttpListenerException or PlatformNotSupportedException)
        {
            listener.Close();
            return null;
        }

        var probe = new ApiTrafficProbe(
            listener, LocalApplicationProbe.CreateClient(TimeSpan.FromSeconds(30)),
            targetBaseUrl.TrimEnd('/'), baseUrl);
        probe._loop = Task.Run(probe.PumpAsync);
        return probe;
    }

    private async Task PumpAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception exception) when (
                exception is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            _ = Task.Run(() => ForwardAsync(context));
        }
    }

    private async Task ForwardAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;

        // A interface roda numa origem e a API noutra: sem isto o navegador barra a chamada antes
        // de ela sair, e a jornada reprovaria por causa do próprio instrumento de medição.
        response.AddHeader("Access-Control-Allow-Origin", "*");
        response.AddHeader("Access-Control-Allow-Headers", "*");
        response.AddHeader("Access-Control-Allow-Methods", "GET,POST,PUT,PATCH,DELETE,OPTIONS");

        if (string.Equals(request.HttpMethod, "OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            response.StatusCode = 204;
            response.Close();
            return;
        }

        Interlocked.Increment(ref _requests);

        try
        {
            using var forwarded = new HttpRequestMessage(
                new HttpMethod(request.HttpMethod), _target + request.RawUrl);
            if (request.HasEntityBody)
            {
                using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
                var body = await reader.ReadToEndAsync();
                forwarded.Content = new StringContent(
                    body, System.Text.Encoding.UTF8, request.ContentType ?? "application/json");
            }

            using var upstream = await _client.SendAsync(forwarded, _stopping.Token);
            var payload = await upstream.Content.ReadAsByteArrayAsync(_stopping.Token);
            response.StatusCode = (int)upstream.StatusCode;
            response.ContentType = upstream.Content.Headers.ContentType?.ToString() ?? "application/json";
            await response.OutputStream.WriteAsync(payload, _stopping.Token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Interlocked.Increment(ref _upstreamFailures);
            try
            {
                response.StatusCode = 502;
            }
            catch (Exception inner) when (inner is InvalidOperationException or ObjectDisposedException)
            {
                // A resposta já foi enviada ou fechada; o contador já registrou a falha.
            }
        }
        finally
        {
            try
            {
                response.Close();
            }
            catch (Exception exception) when (
                exception is ObjectDisposedException or InvalidOperationException)
            {
                // Fechada duas vezes é inócuo; deixar de fechar travaria o cliente.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch (Exception exception) when (
            exception is ObjectDisposedException or HttpListenerException)
        {
            // Já parado.
        }

        if (_loop is not null)
        {
            try
            {
                await _loop.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            }
            catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
            {
                // O laço morre com o listener; esperar mais não muda o resultado da verificação.
            }
        }

        _client.Dispose();
        _stopping.Dispose();
    }
}
