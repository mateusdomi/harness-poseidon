using System.Collections.Concurrent;
using System.Diagnostics;
using Harness.Host.Observability;
using Harness.Modules.Agents.Application.Execution;

namespace Harness.IntegrationTests.Observability;

public sealed class AgentExecutorTelemetryTests
{
    [Fact]
    public async Task ExecutorSpanCorrelatesInvocationWithoutCapturingPayloads()
    {
        var activities = new ConcurrentQueue<Activity>();
        using var listener = Listen(activities);
        var executor = new InstrumentedAgentExecutor(new StubExecutor());
        var request = Request();

        var result = await executor.ExecuteAsync(request);

        Assert.Equal("secret structured output", result.StructuredOutput);
        var span = Assert.Single(
            activities,
            activity => activity.OperationName == "poseidon.agent.execute");
        Assert.Equal(request.TenantId, span.GetTagItem("tenant_id"));
        Assert.Equal(request.ProjectId, span.GetTagItem("project_id"));
        Assert.Equal(request.ConversationId, span.GetTagItem("conversation_id"));
        Assert.Equal(request.AgentId, span.GetTagItem("agent_id"));
        Assert.Equal("completed", span.GetTagItem("agent.result"));
        Assert.Equal("stub-executor", span.GetTagItem("agent.executor"));
        Assert.DoesNotContain(
            span.TagObjects,
            tag => tag.Value?.ToString()?.Contains("secret", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task ExecutorFailureRecordsOnlyExceptionType()
    {
        var activities = new ConcurrentQueue<Activity>();
        using var listener = Listen(activities);
        var executor = new InstrumentedAgentExecutor(new ThrowingExecutor());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => executor.ExecuteAsync(Request()));

        Assert.Equal("secret failure detail", exception.Message);
        var span = Assert.Single(
            activities,
            activity => activity.OperationName == "poseidon.agent.execute");
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal("failed", span.GetTagItem("agent.result"));
        Assert.Equal(
            typeof(InvalidOperationException).FullName,
            span.GetTagItem("error.type"));
        Assert.DoesNotContain(
            span.TagObjects,
            tag => tag.Value?.ToString()?.Contains("secret", StringComparison.Ordinal) == true);
    }

    private static ActivityListener Listen(ConcurrentQueue<Activity> activities)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == PoseidonTelemetry.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activities.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static AgentExecutionRequest Request() => new(
        "tenant-1",
        "project-1",
        "conversation-1",
        "agent-1",
        "secret instruction",
        "{\"secret\":\"digest\"}",
        "/secret/working-directory",
        Model: "model-1",
        CommunicationInstructions: "secret communication instructions");

    private sealed class StubExecutor : IAgentExecutor
    {
        public Task<AgentExecutionResult> ExecuteAsync(
            AgentExecutionRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentExecutionResult(
                "stub-executor",
                "secret-session",
                "secret-turn",
                "secret structured output",
                ["secret chunk"],
                42));
    }

    private sealed class ThrowingExecutor : IAgentExecutor
    {
        public Task<AgentExecutionResult> ExecuteAsync(
            AgentExecutionRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("secret failure detail");
    }
}
