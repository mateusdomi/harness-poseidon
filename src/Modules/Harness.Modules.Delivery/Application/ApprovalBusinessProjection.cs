namespace Harness.Modules.Delivery.Application;

/// <summary>
/// Traduz uma aprovação para o LÉXICO DE NEGÓCIO — o que o dono lê antes de decidir.
///
/// O contrato já previa <c>businessTitle</c> e <c>businessDescription</c>, e o frontend depende
/// deles: sem os dois, a visão de negócio mostra "propósito indisponível" e **bloqueia a
/// resolução**. Nenhum código do backend os preenchia, então o dono nunca conseguia aprovar nada
/// no modo Negócio — inclusive o Termo de Aceite, que é o gate humano da Fase 7. A tela existia, o
/// botão existia, e a decisão não passava.
///
/// A tradução não inventa conteúdo: usa o que já está na aprovação (título técnico, descrição, o
/// que está sendo decidido) e o reescreve na forma de uma pergunta que um leigo consegue
/// responder. Quando não há material suficiente, devolve <see langword="null"/> — e aí o bloqueio
/// é correto, porque pedir decisão sobre o que não se sabe explicar é pior do que não pedir.
/// </summary>
public static class ApprovalBusinessProjection
{
    /// <summary>O que o dono precisa decidir, em uma frase.</summary>
    public sealed record BusinessPurpose(string Title, string Description);

    /// <summary>
    /// Constrói a projeção. <paramref name="gateId"/>, <paramref name="documentId"/> e
    /// <paramref name="taskId"/> dizem a NATUREZA da decisão — e é ela que muda a pergunta:
    /// aprovar um documento não é a mesma coisa que liberar uma etapa.
    /// </summary>
    public static BusinessPurpose? Create(
        string? title,
        string? description,
        string? gateId,
        string? documentId,
        string? taskId,
        string? subjectName)
    {
        var subject = FirstNonEmpty(subjectName, title);
        if (subject is null)
        {
            // Sem nem título nem assunto não há o que perguntar. Fabricar uma frase genérica
            // ("Aprovar item") daria ao dono a impressão de que ele entendeu o que decidiu.
            return null;
        }

        var detail = FirstNonEmpty(description, title);
        if (detail is null)
        {
            return null;
        }

        return gateId is { Length: > 0 }
            ? new BusinessPurpose(
                $"Liberar a próxima etapa: {subject}",
                $"{detail} Ao aprovar, o trabalho segue para a etapa seguinte. Se algo ainda não está certo, reprove e diga o que falta.")
            : documentId is { Length: > 0 }
                ? new BusinessPurpose(
                    $"Aprovar o documento: {subject}",
                    $"{detail} Ao aprovar, este documento passa a valer como decisão do projeto.")
                : taskId is { Length: > 0 }
                    ? new BusinessPurpose(
                        $"Confirmar a entrega: {subject}",
                        $"{detail} Ao aprovar, esta entrega é considerada aceita.")
                    : new BusinessPurpose(
                        $"Decidir sobre: {subject}",
                        $"{detail} Sua decisão define como o projeto segue a partir daqui.");
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            var trimmed = value?.Trim();
            if (!string.IsNullOrEmpty(trimmed))
            {
                return trimmed;
            }
        }

        return null;
    }
}
