using Harness.Host.Execution;

namespace Harness.UnitTests.Execution;

public sealed class IsolationStatusEndpointsTests
{
    private static readonly IsolatedExecutionSettings Docker = new() { Mode = IsolatedExecutionMode.Docker };

    [Fact]
    public void DockerWithRuntimeAndImageIsProtected()
    {
        var status = IsolationStatusEndpoints.Evaluate(Docker, () => true, _ => true);

        Assert.Equal("docker", status.Mode);
        Assert.True(status.ContainerRuntimeReady);
        Assert.True(status.AgentImageReady);
        Assert.True(status.Protected);
        Assert.Null(status.Message);
    }

    [Fact]
    public void DockerWithoutRuntimeIsNotProtectedAndSaysWhatToDo()
    {
        var status = IsolationStatusEndpoints.Evaluate(Docker, () => false, _ => true);

        Assert.False(status.Protected);
        Assert.False(status.AgentImageReady);
        Assert.Equal(ContainerRuntimeProbe.Message, status.Message);
    }

    [Fact]
    public void DockerWithoutImageIsNotProtectedAndNamesTheMissingImage()
    {
        var status = IsolationStatusEndpoints.Evaluate(Docker, () => true, _ => false);

        Assert.True(status.ContainerRuntimeReady);
        Assert.False(status.Protected);
        Assert.Equal(ContainerRuntimeProbe.MissingImage(Docker.AgentImageName), status.Message);
    }

    [Theory]
    [InlineData(IsolatedExecutionMode.Disabled, "disabled")]
    [InlineData(IsolatedExecutionMode.Fake, "fake")]
    public void NonDockerModesAreNeverReportedAsProtected(IsolatedExecutionMode mode, string expected)
    {
        var probed = false;
        var status = IsolationStatusEndpoints.Evaluate(
            new IsolatedExecutionSettings { Mode = mode },
            () => probed = true,
            _ => true);

        Assert.Equal(expected, status.Mode);
        Assert.False(status.Protected);
        Assert.False(probed);
    }
}
