using Harness.Host.Workflows;

namespace Harness.UnitTests.Workflows;

/// <summary>
/// DEL-07: o workflow de entrega técnica é um template CANÔNICO de 11 fases (Recebimento → Revisão de
/// benefícios) com portões (gates) e documentos obrigatórios por fase. Reusa o módulo de Workflows —
/// aqui provamos apenas o SHAPE do template canônico.
/// </summary>
public sealed class TechnicalDeliveryWorkflowTemplateTests
{
    [Fact]
    public void HasTheElevenCanonicalPhasesInOrder()
    {
        var template = CanonicalWorkflowTemplates.TechnicalDeliveryTemplate;

        Assert.Equal(CanonicalWorkflowTemplates.TechnicalDeliveryKey, template.Key);
        Assert.Equal(
            [
                "Recebimento", "Baseline", "Planejamento", "Execução acompanhada", "Prontidão homolog",
                "Homologação", "Prontidão prod", "Produção", "Estabilização", "Encerramento",
                "Revisão de benefícios",
            ],
            template.Phases);
    }

    [Fact]
    public void EveryPhaseHasAtLeastOneMandatoryDocument()
    {
        var template = CanonicalWorkflowTemplates.TechnicalDeliveryTemplate;

        Assert.All(template.Phases, phase =>
        {
            Assert.True(
                template.DocumentsByPhase.TryGetValue(phase, out var documents) && documents.Count > 0,
                $"Phase '{phase}' must declare at least one mandatory document.");
        });
    }

    [Fact]
    public void KeyDecisionAndReadinessPhasesAreGated()
    {
        var template = CanonicalWorkflowTemplates.TechnicalDeliveryTemplate;

        foreach (var gated in new[]
                 {
                     "Baseline", "Planejamento", "Prontidão homolog", "Homologação", "Prontidão prod",
                     "Produção", "Encerramento", "Revisão de benefícios",
                 })
        {
            Assert.True(template.GatesByPhase.ContainsKey(gated), $"Phase '{gated}' must be gated.");
            Assert.NotEmpty(template.GatesByPhase[gated]);
        }

        // Todo portão referencia uma fase existente.
        Assert.All(template.GatesByPhase.Keys, phase => Assert.Contains(phase, template.Phases));
    }

    [Fact]
    public void IsRegisteredInTheCanonicalCatalog()
    {
        Assert.Contains(
            CanonicalWorkflowTemplates.All,
            template => template.Key == CanonicalWorkflowTemplates.TechnicalDeliveryKey);
    }
}
