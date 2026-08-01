using Harness.Modules.Delivery.Application;

namespace Harness.UnitTests.Delivery;

/// <summary>
/// A leitura de NEGÓCIO de uma aprovação.
///
/// O contrato previa `businessTitle` e `businessDescription` desde sempre, e o frontend depende
/// dos dois para liberar a decisão no modo Negócio: sem eles a tela mostra "propósito
/// indisponível" e BLOQUEIA a resolução. Nenhum código do backend os preenchia — eram `null`
/// cravados no mapeamento —, então o dono nunca conseguia aprovar nada, inclusive o Termo de
/// Aceite, que é o gate humano da Fase 7. O botão existia e a decisão não passava.
/// </summary>
public sealed class ApprovalBusinessProjectionTests
{
    [Fact]
    public void AGateApprovalAsksTheOwnerToReleaseTheNextStage()
    {
        var purpose = ApprovalBusinessProjection.Create(
            "Aprovar gate da fase de testes",
            "Todos os cenários passaram e não há defeito bloqueante.",
            gateId: "01ARZ3NDEKTSV4RRFFQ69G5FAV",
            documentId: null,
            taskId: null,
            subjectName: null);

        Assert.NotNull(purpose);
        Assert.Contains("Liberar a próxima etapa", purpose!.Title, StringComparison.Ordinal);

        // A descrição diz o que ACONTECE ao aprovar e o que fazer se não estiver certo — sem isso
        // o dono decide sem saber a consequência.
        Assert.Contains("segue para a etapa seguinte", purpose.Description, StringComparison.Ordinal);
        Assert.Contains("reprove", purpose.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void ADocumentApprovalSaysWhatApprovingMakesTrue()
    {
        var purpose = ApprovalBusinessProjection.Create(
            "Aprovar PRD", "O documento descreve objetivos e não-objetivos.",
            gateId: null, documentId: "01ARZ3NDEKTSV4RRFFQ69G5FAV", taskId: null, subjectName: null);

        Assert.NotNull(purpose);
        Assert.Contains("Aprovar o documento", purpose!.Title, StringComparison.Ordinal);
        Assert.Contains("passa a valer como decisão", purpose.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void ATaskApprovalConfirmsADelivery()
    {
        var purpose = ApprovalBusinessProjection.Create(
            "Aceitar entrega da tela de login", "A tela está publicada no ambiente de homologação.",
            gateId: null, documentId: null, taskId: "01ARZ3NDEKTSV4RRFFQ69G5FAV", subjectName: null);

        Assert.NotNull(purpose);
        Assert.Contains("Confirmar a entrega", purpose!.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutSubjectOrDetailThereIsNoQuestionToAsk()
    {
        // Fabricar uma frase genérica ("Aprovar item") daria ao dono a impressão de que ele
        // entendeu o que decidiu. Nulo é honesto: a tela bloqueia, e o bloqueio está certo.
        Assert.Null(ApprovalBusinessProjection.Create(
            null, null, null, null, null, null));
        Assert.Null(ApprovalBusinessProjection.Create(
            "   ", "   ", null, null, null, null));
    }

    [Fact]
    public void TheTitleAloneIsEnoughWhenThereIsNoDescription()
    {
        // Uma aprovação com título e sem descrição ainda é decidível: o título vira o detalhe.
        // Recusar aqui bloquearia o dono por uma ausência que não o impede de entender.
        var purpose = ApprovalBusinessProjection.Create(
            "Aprovar publicação da versão 1.2", null, null, null, null, null);

        Assert.NotNull(purpose);
        Assert.Contains("Aprovar publicação da versão 1.2", purpose!.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOwnerNeverReadsTechnicalIdentifiers()
    {
        // O léxico do modo Negócio proíbe id, gate e nome de componente. A projeção usa os ids
        // apenas para escolher a PERGUNTA — nunca os repete ao dono.
        var purpose = ApprovalBusinessProjection.Create(
            "Aprovar entrega", "Pronto para revisão.",
            gateId: "01ARZ3NDEKTSV4RRFFQ69G5FAV", documentId: null, taskId: null, subjectName: null);

        Assert.NotNull(purpose);
        Assert.DoesNotContain("01ARZ3", purpose!.Title, StringComparison.Ordinal);
        Assert.DoesNotContain("01ARZ3", purpose.Description, StringComparison.Ordinal);
    }
}
