using System.Security.Cryptography;
using System.Text;

namespace Harness.Host.Ipc;

public sealed class RunnerIpcToken
{
    private readonly byte[] _hash;

    public RunnerIpcToken(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length < 32)
        {
            throw new ArgumentException("Runner IPC tokens must contain at least 32 characters.", nameof(value));
        }

        _hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
    }

    public static RunnerIpcToken Create()
    {
        var value = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        return new RunnerIpcToken(value);
    }

    public bool Matches(string candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var candidateHash = SHA256.HashData(Encoding.UTF8.GetBytes(candidate));
        return CryptographicOperations.FixedTimeEquals(_hash, candidateHash);
    }

    public override string ToString() => "[redacted]";
}
