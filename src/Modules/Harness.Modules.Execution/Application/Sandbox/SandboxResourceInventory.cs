namespace Harness.Modules.Execution.Application.Sandbox;

public sealed record SandboxResourceInventory(
    IReadOnlyList<string> Containers,
    IReadOnlyList<string> Networks,
    IReadOnlyList<string> Volumes,
    IReadOnlyList<string> Images)
{
    public bool IsEmpty =>
        Containers.Count == 0 && Networks.Count == 0 && Volumes.Count == 0 && Images.Count == 0;
}
