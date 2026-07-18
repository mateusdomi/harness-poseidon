namespace Harness.SharedKernel.Time;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
