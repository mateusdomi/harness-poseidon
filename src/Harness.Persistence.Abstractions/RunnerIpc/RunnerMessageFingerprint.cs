using System.Security.Cryptography;
using System.Text.Json;
using Harness.SharedKernel.RunnerIpc;

namespace Harness.Persistence.Abstractions.RunnerIpc;

public static class RunnerMessageFingerprint
{
    public static string Compute(RunnerMessageEnvelope message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(message)));
    }
}
