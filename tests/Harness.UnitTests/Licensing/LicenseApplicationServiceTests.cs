using Harness.Modules.Licensing.Application;
using Harness.Modules.Licensing.Contracts;

namespace Harness.UnitTests.Licensing;

public sealed class LicenseApplicationServiceTests
{
    [Fact]
    public void ActivationAndTemporalStatesAreDeterministic()
    {
        var now = new DateTimeOffset(2026, 7, 18, 22, 0, 0, TimeSpan.Zero);
        Assert.Equal("ABCD-1234-EFGH-5678", LicenseApplicationService.ValidateActivation(
            new ActivateLicenseRequest("abcd-1234-efgh-5678")));
        Assert.Throws<ArgumentException>(() => LicenseApplicationService.ValidateActivation(
            new ActivateLicenseRequest("invalid")));
        Assert.Equal("unlicensed", LicenseApplicationService.ResolveState(
            "unlicensed", false, null, null, now));
        Assert.Equal("active", LicenseApplicationService.ResolveState(
            "active", false, now.AddDays(1), now.AddDays(15), now));
        Assert.Equal("offline", LicenseApplicationService.ResolveState(
            "active", true, now.AddDays(1), now.AddDays(15), now));
        Assert.Equal("gracePeriod", LicenseApplicationService.ResolveState(
            "active", false, now.AddSeconds(-1), now.AddDays(1), now));
        Assert.Equal("expired", LicenseApplicationService.ResolveState(
            "active", false, now.AddDays(-15), now.AddSeconds(-1), now));
    }
}
