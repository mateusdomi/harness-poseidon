namespace Harness.SharedKernel.Results;

public sealed record ErrorDescriptor(string Code, string MessageKey)
{
    public static ErrorDescriptor None { get; } = new(string.Empty, string.Empty);
}
