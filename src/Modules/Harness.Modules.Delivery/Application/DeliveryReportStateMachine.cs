using Harness.Modules.Delivery.Contracts;

namespace Harness.Modules.Delivery.Application;

/// <summary>
/// DEL-04 — a máquina de estados de APROVAÇÃO do relatório. Um relatório é um snapshot imutável cujo
/// ciclo de vida só avança: <c>draft → approved → sent</c>. Aprovar exige estar em rascunho; ENVIAR
/// exige APROVAÇÃO HUMANA prévia — enviar um relatório não aprovado é uma RECUSA tipada (nunca lança).
/// Componente PURO: valida transições e devolve um resultado tipado com um código estável de erro.
/// </summary>
public static class DeliveryReportStateMachine
{
    /// <summary>Código estável quando a transição é recusada; nulo quando permitida.</summary>
    public sealed record Transition(bool Allowed, string? Code, string? Detail)
    {
        public static Transition Ok() => new(true, null, null);

        public static Transition Reject(string code, string detail) => new(false, code, detail);
    }

    /// <summary>Aprovação: só a partir de <c>draft</c>.</summary>
    public static Transition CanApprove(DeliveryReportStatus current) => current switch
    {
        DeliveryReportStatus.Draft => Transition.Ok(),
        DeliveryReportStatus.Approved => Transition.Reject(
            "report_already_approved", "The report has already been approved."),
        DeliveryReportStatus.Sent => Transition.Reject(
            "report_already_sent", "A sent report cannot be approved again."),
        _ => Transition.Reject("report_invalid_state", "Unknown report state."),
    };

    /// <summary>Envio: só a partir de <c>approved</c> (aprovação humana é obrigatória).</summary>
    public static Transition CanSend(DeliveryReportStatus current) => current switch
    {
        DeliveryReportStatus.Approved => Transition.Ok(),
        DeliveryReportStatus.Draft => Transition.Reject(
            "report_not_approved", "The report must be approved by a human before it can be sent."),
        DeliveryReportStatus.Sent => Transition.Reject(
            "report_already_sent", "The report has already been sent."),
        _ => Transition.Reject("report_invalid_state", "Unknown report state."),
    };
}
