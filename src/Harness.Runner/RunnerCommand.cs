using System.Globalization;
using System.Net;
using System.Text.Json;
using Harness.SharedKernel.RunnerIpc;

namespace Harness.Runner;

public static class RunnerCommand
{
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        var options = RunnerOptions.Parse(args);
        var token = (await File.ReadAllTextAsync(options.TokenFile, cancellationToken)).Trim();
        using var httpClient = new HttpClient { BaseAddress = options.HostUrl };
        var client = new RunnerIpcClient(httpClient, token);
        var results = new List<RunnerIpcSendResult>();

        for (var index = 0; index < options.MessageTypes.Count; index++)
        {
            var sequence = options.StartSequence + index;
            var type = options.MessageTypes[index];
            var message = new RunnerMessageEnvelope(
                options.RunnerId,
                options.AttemptId,
                sequence,
                $"{options.IdempotencyPrefix}:{options.AttemptId}:{sequence.ToString(CultureInfo.InvariantCulture)}",
                type,
                CreatePayload(type, sequence));
            var result = await client.SendAsync(message, cancellationToken);
            results.Add(result);
            if (!result.IsSuccess)
            {
                break;
            }
        }

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            sent = results.Count,
            successful = results.Count(result => result.IsSuccess),
            replays = results.Count(result => result.Receipt?.Replay is true),
            lastStatus = results.LastOrDefault()?.StatusCode,
            lastError = results.LastOrDefault()?.ErrorTitle,
        }));
        return results.All(result => result.IsSuccess) ? 0 : 2;
    }

    private static JsonElement CreatePayload(string type, long sequence) => type switch
    {
        RunnerMessageTypes.Heartbeat => JsonSerializer.SerializeToElement(new { status = "alive" }),
        RunnerMessageTypes.Checkpoint => JsonSerializer.SerializeToElement(new
        {
            checkpointId = $"checkpoint-{sequence.ToString(CultureInfo.InvariantCulture)}",
            commitSha = "0123456789abcdef0123456789abcdef01234567",
        }),
        RunnerMessageTypes.Completion => JsonSerializer.SerializeToElement(new { outcome = "succeeded" }),
        _ => throw new ArgumentException("Unsupported runner message type.", nameof(type)),
    };

    private sealed record RunnerOptions(
        Uri HostUrl,
        string TokenFile,
        string RunnerId,
        string AttemptId,
        long StartSequence,
        IReadOnlyList<string> MessageTypes,
        string IdempotencyPrefix)
    {
        public static RunnerOptions Parse(string[] args)
        {
            ArgumentNullException.ThrowIfNull(args);
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var index = 0; index < args.Length; index += 2)
            {
                if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new ArgumentException("Runner arguments must be --name value pairs.", nameof(args));
                }

                values.Add(args[index][2..], args[index + 1]);
            }

            var hostUrl = new Uri(Required(values, "host-url"), UriKind.Absolute);
            if (hostUrl.Scheme != Uri.UriSchemeHttp || !IsLoopback(hostUrl.Host))
            {
                throw new ArgumentException("Runner host-url must use HTTP loopback.", nameof(args));
            }

            var tokenFile = Path.GetFullPath(Required(values, "token-file"));
            if (!File.Exists(tokenFile))
            {
                throw new FileNotFoundException("Runner token file was not found.", tokenFile);
            }

            var startSequence = values.TryGetValue("start-sequence", out var sequenceText)
                ? long.Parse(sequenceText, NumberStyles.None, CultureInfo.InvariantCulture)
                : 1;
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(startSequence, 0);
            var messageTypes = values.TryGetValue("messages", out var messageText)
                ? messageText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : [RunnerMessageTypes.Heartbeat, RunnerMessageTypes.Checkpoint, RunnerMessageTypes.Completion];
            if (messageTypes.Length == 0 || messageTypes.Any(type => !RunnerMessageTypes.All.Contains(type)))
            {
                throw new ArgumentException("Runner messages contain an unsupported type.", nameof(args));
            }

            return new RunnerOptions(
                hostUrl,
                tokenFile,
                Required(values, "runner-id"),
                Required(values, "attempt-id"),
                startSequence,
                messageTypes,
                values.GetValueOrDefault("idempotency-prefix", "runner"));
        }

        private static string Required(Dictionary<string, string> values, string name) =>
            values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value
                : throw new ArgumentException($"Runner argument --{name} is required.");

        private static bool IsLoopback(string host) =>
            string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));
    }
}
