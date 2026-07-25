using Harness.Host.Workflows;

namespace Harness.UnitTests.Workflows;

/// <summary>
/// DEL-07: o workflow de entrega técnica é um template CANÔNICO de 15 fases (Ideação → Revisão de
/// benefícios) com portões (gates) e documentos obrigatórios por fase. Reusa o módulo de Workflows —
/// aqui provamos apenas o SHAPE do template canônico.
/// </summary>
public sealed class TechnicalDeliveryWorkflowTemplateTests
{
    [Fact]
    public void HasTheFifteenCanonicalPhasesInOrder()
    {
        var template = CanonicalWorkflowTemplates.TechnicalDeliveryTemplate;

        Assert.Equal(CanonicalWorkflowTemplates.TechnicalDeliveryKey, template.Key);
        Assert.Equal(
            [
                "Ideação e recebimento", "Descoberta", "Requisitos", "Arquitetura", "Planejamento",
                "Implementação", "Verificação e qualidade", "Prontidão para homologação",
                "Homologação", "Prontidão para produção", "Produção", "Estabilização", "Sustentação",
                "Encerramento", "Revisão de benefícios",
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
                     "Requisitos", "Arquitetura", "Planejamento", "Verificação e qualidade",
                     "Prontidão para homologação", "Homologação", "Prontidão para produção",
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
