using System.Text.Json;

namespace Harness.SharedKernel.RunnerIpc;

public sealed record RunnerMessageEnvelope(
    string RunnerId,
    string AttemptId,
    long Sequence,
    string IdempotencyKey,
    string Type,
    JsonElement Payload);
