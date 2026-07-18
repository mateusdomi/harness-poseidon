using System.Security.Cryptography;
using System.Text.Json;

namespace Harness.Persistence.Abstractions.DurableExecution;

public static class DurableCommandHash
{
    public static string Compute<T>(T command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(command)));
    }
}
