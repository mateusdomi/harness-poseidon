using Harness.Modules.Delivery.Application;
using Harness.Modules.Delivery.Contracts;

namespace Harness.UnitTests.Delivery;

/// <summary>
/// DEL-04 — a máquina de estados de aprovação: draft → approved → sent, sempre avançando. Aprovar exige
/// rascunho; enviar exige aprovação humana prévia. Cada recusa é tipada com um código estável.
/// </summary>
public sealed class DeliveryReportStateMachineTests
{
    [Fact]
    public void DraftCanBeApproved()
    {
        var transition = DeliveryReportStateMachine.CanApprove(DeliveryReportStatus.Draft);
        Assert.True(transition.Allowed);
        Assert.Null(transition.Code);
    }

    [Theory]
    [InlineData(DeliveryReportStatus.Approved, "report_already_approved")]
    [InlineData(DeliveryReportStatus.Sent, "report_already_sent")]
    public void ApprovingANonDraftIsRejected(DeliveryReportStatus status, string code)
    {
        var transition = DeliveryReportStateMachine.CanApprove(status);
        Assert.False(transition.Allowed);
        Assert.Equal(code, transition.Code);
    }

    [Fact]
    public void ApprovedCanBeSent()
    {
        var transition = DeliveryReportStateMachine.CanSend(DeliveryReportStatus.Approved);
        Assert.True(transition.Allowed);
    }

    [Fact]
    public void SendingBeforeApprovalIsRejectedAsNotApproved()
    {
        var transition = DeliveryReportStateMachine.CanSend(DeliveryReportStatus.Draft);
        Assert.False(transition.Allowed);
        Assert.Equal("report_not_approved", transition.Code);
    }

    [Fact]
    public void SendingAnAlreadySentReportIsRejected()
    {
        var transition = DeliveryReportStateMachine.CanSend(DeliveryReportStatus.Sent);
        Assert.False(transition.Allowed);
        Assert.Equal("report_already_sent", transition.Code);
    }
}
