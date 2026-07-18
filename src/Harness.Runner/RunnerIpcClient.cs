using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.SharedKernel.RunnerIpc;

namespace Harness.Runner;

public sealed class RunnerIpcClient(HttpClient httpClient, string token)
{
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly AuthenticationHeaderValue _authorization =
        new("Bearer", string.IsNullOrWhiteSpace(token) ? throw new ArgumentException("Token is required.", nameof(token)) : token);

    public async Task<RunnerIpcSendResult> SendAsync(
        RunnerMessageEnvelope message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/internal/runner/messages")
        {
            Content = JsonContent.Create(message),
        };
        request.Headers.Authorization = _authorization;
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            var receipt = await response.Content.ReadFromJsonAsync<RunnerMessageReceipt>(cancellationToken);
            return new RunnerIpcSendResult((int)response.StatusCode, receipt, null);
        }

        var errorTitle = await ReadProblemTitleAsync(response, cancellationToken);
        return new RunnerIpcSendResult((int)response.StatusCode, null, errorTitle);
    }

    private static async Task<string?> ReadProblemTitleAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return document.RootElement.TryGetProperty("title", out var title) ? title.GetString() : null;
    }
}

public sealed record RunnerIpcSendResult(int StatusCode, RunnerMessageReceipt? Receipt, string? ErrorTitle)
{
    public bool IsSuccess => Receipt is not null;
}
