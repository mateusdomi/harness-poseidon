namespace Harness.Modules.Agents.Application.Execution;

/// <summary>
/// A navegação da chefe pelos ANEXOS INTEGRAIS da solicitação — Onda 0.7.
///
/// O defeito que isto fecha foi observado ao vivo em 04/08/2026, no primeiro turno real do
/// Prisma: a chefe declarou, honestamente, que só tinha recebido "o início de cada anexo" — o
/// índice vetorial guarda um resumo de 4.000 caracteres do documento inteiro, e a especificação
/// tem dezesseis seções. As seções 5–6 (metodologia), 7 (perfis), 11 (auditoria), 15 (dataset) e
/// 16 (critérios de aceite) simplesmente não existiam para ela.
///
/// O desenho: o resumo continua sendo o que entra por padrão no turno (budget é real), mas ele
/// passa a ser DECLARADO como resumo, acompanhado de um índice navegável, e a chefe ganha o meio
/// de puxar qualquer seção COMPLETA sob demanda. As seções são extraídas na hora, do arquivo
/// durável em disco (`storage_path`) — não há segunda cópia do conteúdo para dessincronizar, e
/// anexos enviados ANTES desta mudança ficam navegáveis sem re-upload.
/// </summary>
public interface IChiefAttachmentNavigator
{
    /// <summary>Índice navegável (arquivo → seções) dos anexos aceitos do projeto.</summary>
    Task<IReadOnlyList<ChiefAttachmentOutline>> ListOutlinesAsync(
        string tenantId,
        string projectId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// O conteúdo INTEGRAL de uma seção. Nulo quando o arquivo ou a seção não existem — a
    /// ausência volta declarada para a chefe, nunca preenchida com texto inventado.
    /// </summary>
    Task<string?> ReadSectionAsync(
        string tenantId,
        string projectId,
        string fileName,
        string sectionId,
        CancellationToken cancellationToken = default);
}

public sealed record ChiefAttachmentOutline(
    string FileName,
    IReadOnlyList<ChiefAttachmentSectionRef> Sections);

public sealed record ChiefAttachmentSectionRef(string Id, string Title);

/// <summary>
/// Divide um documento de texto em seções endereçáveis, sem perder um caractere: a concatenação
/// das seções na ordem reproduz o documento original byte a byte — é essa propriedade que os
/// testes verificam, porque "resumo fiel" sem prova de completude é só uma promessa.
/// </summary>
public static class AttachmentSectionizer
{
    /// <summary>
    /// Corta por títulos de nível 2 (<c>## </c>) — a granularidade das especificações em
    /// markdown ("## 5. Metodologia…"). O que vem antes do primeiro título vira o preâmbulo.
    ///
    /// TEMPLATE NÃO É CONTRATO DE ENTRADA (Dual Project Gate, Parte A): quando o documento não é
    /// markdown — um TXT colado, um levantamento com "1. OBJETIVO" ou "REQUISITOS FUNCIONAIS" em
    /// caixa alta — a divisão cai para títulos NUMERADOS ou EM CAIXA ALTA. Documento sem título
    /// nenhum é uma seção única: navegável do mesmo jeito, sem caso especial para o chamador.
    /// A propriedade de completude vale em todos os modos: a concatenação reproduz o original.
    /// </summary>
    public static IReadOnlyList<AttachmentSection> Split(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var markdown = SplitBy(text, line => line.StartsWith("## ", StringComparison.Ordinal)
            ? line[3..].Trim()
            : null);
        if (markdown.Count > 1)
        {
            return markdown;
        }

        // Fallback agnóstico: "12. TÍTULO" / "12) Título" / "TÍTULO EM CAIXA ALTA".
        var fallback = SplitBy(text, PlainHeading);
        return fallback.Count > 1 ? fallback : markdown;
    }

    /// <summary>
    /// Título de documento SEM markdown: linha numerada ("27. ENTREGA DO MVP", "3) Escopo") ou
    /// linha curta em caixa alta. Estreito de propósito — promover linha comum a título espalha
    /// o documento em seções falsas.
    /// </summary>
    private static string? PlainHeading(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length is < 4 or > 120)
        {
            return null;
        }

        if (System.Text.RegularExpressions.Regex.IsMatch(
                trimmed, @"^\d{1,3}(\.\d+)*[.)—-]\s+\S.{2,}$"))
        {
            return trimmed;
        }

        var letters = trimmed.Where(char.IsLetter).ToArray();
        return letters.Length >= 6 && letters.All(char.IsUpper) &&
            !trimmed.EndsWith('.') && !trimmed.Contains(':')
            ? trimmed
            : null;
    }

    private static List<AttachmentSection> SplitBy(
        string text, Func<string, string?> headingOf)
    {
        var sections = new List<AttachmentSection>();
        var lines = text.Split('\n');
        var buffer = new List<string>();
        string? currentTitle = null;
        var fenced = false;

        foreach (var line in lines)
        {
            // Um título dentro de cerca de código é conteúdo, não título — a spec do Prisma tem
            // pseudocódigo com comentários `#` que não podem virar seções fantasmas.
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                fenced = !fenced;
            }

            if (!fenced && headingOf(line) is { } title)
            {
                Flush(sections, currentTitle, buffer);
                currentTitle = title;
                buffer.Clear();
            }

            buffer.Add(line);
        }

        Flush(sections, currentTitle, buffer);
        return sections;
    }

    /// <summary>
    /// Prova de completude: as seções, na ordem, reproduzem o documento original.
    /// </summary>
    public static string Reassemble(IReadOnlyList<AttachmentSection> sections)
    {
        ArgumentNullException.ThrowIfNull(sections);
        return string.Join('\n', sections.Select(section => section.Content));
    }

    public static AttachmentSection? Find(
        IReadOnlyList<AttachmentSection> sections, string sectionId)
    {
        ArgumentNullException.ThrowIfNull(sections);
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionId);
        var wanted = sectionId.Trim().TrimStart('§').TrimEnd('.');
        return sections.FirstOrDefault(section =>
            string.Equals(section.Id, wanted, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(section.Title, sectionId.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private static void Flush(
        List<AttachmentSection> sections, string? title, List<string> buffer)
    {
        if (buffer.Count == 0)
        {
            return;
        }

        var content = string.Join('\n', buffer);
        if (title is null)
        {
            // Preâmbulo totalmente em branco não merece entrada no índice; em branco COM título
            // seria perda de conteúdo e é preservado no ramo de baixo.
            if (sections.Count == 0 && string.IsNullOrWhiteSpace(content))
            {
                return;
            }

            sections.Add(new AttachmentSection("preambulo", "Preâmbulo", content));
            return;
        }

        sections.Add(new AttachmentSection(DeriveId(title, sections.Count), title, content));
    }

    /// <summary>
    /// O id é o número que abre o título ("5. Metodologia…" → "5"; "4.2 Entidades" → "4.2").
    /// Título sem número recebe id posicional estável ("s7") — endereçável do mesmo jeito.
    /// </summary>
    private static string DeriveId(string title, int position)
    {
        var token = title.Split(' ', 2)[0].TrimEnd('.');
        return token.Length > 0 && token.All(c => char.IsDigit(c) || c == '.')
            ? token
            : $"s{position}";
    }
}

public sealed record AttachmentSection(string Id, string Title, string Content);
