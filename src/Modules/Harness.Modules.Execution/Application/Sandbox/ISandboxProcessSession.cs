namespace Harness.Modules.Execution.Application.Sandbox;

public interface ISandboxProcessSession : IAsyncDisposable
{
    SandboxProcessPlan ProcessPlan { get; }
}
