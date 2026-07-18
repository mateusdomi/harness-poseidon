namespace Harness.Host.Realtime;

public static class EventStreamName
{
    public static bool IsValid(string? stream) =>
        !string.IsNullOrWhiteSpace(stream) &&
        stream.Length <= 200 &&
        stream.Contains(':') &&
        !stream.Any(char.IsWhiteSpace);
}
