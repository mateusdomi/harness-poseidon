using System.Text.Json;

var line = await Console.In.ReadLineAsync();
if (line is null) return 2;
var request = JsonSerializer.Deserialize<FakeRequest>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web));
if (request is null || request.Type != "execute") return 3;
var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
await Console.Out.WriteLineAsync(JsonSerializer.Serialize(
    new FakeHeartbeat("1.0.0", "heartbeat", request.RequestId, DateTimeOffset.UtcNow), options));
if (args.Contains("--fake-hang", StringComparer.Ordinal))
{
    await Task.Delay(TimeSpan.FromMinutes(5));
    return 0;
}

if (args.Contains("--fake-invalid", StringComparer.Ordinal))
{
    await Console.Out.WriteLineAsync("{\"schemaVersion\":\"1.0.0\",\"type\":\"unknown\",\"requestId\":\"invalid\"}");
    return 0;
}

if (args.Contains("--fake-invalid-schema", StringComparer.Ordinal))
{
    await Console.Out.WriteLineAsync(
        $"{{\"schemaVersion\":\"1.0.0\",\"type\":\"chunk\",\"requestId\":\"{request.RequestId}\",\"text\":\"invalid\",\"unexpected\":true}}");
    return 0;
}

await Console.Out.WriteLineAsync(JsonSerializer.Serialize(
    new FakeChunk("1.0.0", "chunk", request.RequestId, "deterministic fake chunk"), options));
await Console.Out.WriteLineAsync(JsonSerializer.Serialize(
    new FakeResult(
        "1.0.0",
        "result",
        request.RequestId,
        "omp-fake-session",
        "omp-fake-turn",
        "{\"response\":\"OMP fake response\",\"demands\":[]}",
        1), options));
return 0;

internal sealed record FakeRequest(string SchemaVersion, string Type, string RequestId);
internal sealed record FakeHeartbeat(string SchemaVersion, string Type, string RequestId, DateTimeOffset Timestamp);
internal sealed record FakeChunk(string SchemaVersion, string Type, string RequestId, string Text);
internal sealed record FakeResult(string SchemaVersion, string Type, string RequestId, string SessionId, string TurnId, string StructuredOutput, long DurationMs);
