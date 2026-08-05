using Harness.Modules.Agents.Application.Execution;
using Harness.Persistence.Abstractions.Coordination;
using Harness.Persistence.Abstractions.WorkChain;

namespace Harness.Host.WorkBoard;

/// <summary>
/// A implementação real da navegação de anexos da chefe (Onda 0.7): resolve as solicitações do
/// projeto, lê o arquivo DURÁVEL de cada anexo aceito (o mesmo <c>storage_path</c> gravado no
/// intake) e seciona o texto na hora.
///
/// Secionar sob demanda — em vez de indexar seções numa segunda estrutura — é decisão de
/// arquitetura: o arquivo em disco é a única cópia do conteúdo, então não existe dessincronização
/// possível, e anexos enviados ANTES desta mudança (a especificação do Prisma, por exemplo) ficam
/// navegáveis sem re-upload. O custo é reler o arquivo por acesso, e ele é aceitável: anexo tem
/// teto de tamanho no intake e turno de conversa não é caminho quente.
/// </summary>
public sealed class SolicitationAttachmentNavigator(
    IWorkBoardStore board,
    ISolicitationAttachmentStore attachments,
    SolicitationAttachmentStorage storage) : IChiefAttachmentNavigator
{
    private readonly SolicitationAttachmentStorage _storage =
        storage ?? throw new ArgumentNullException(nameof(storage));

    private readonly IWorkBoardStore _board =
        board ?? throw new ArgumentNullException(nameof(board));
    private readonly ISolicitationAttachmentStore _attachments =
        attachments ?? throw new ArgumentNullException(nameof(attachments));

    /// <summary>
    /// Anexo maior que isto não é navegado por seção — é um teto de proteção do processo, bem
    /// acima do limite do intake, nunca o caminho normal.
    /// </summary>
    private const long MaxNavigableBytes = 20 * 1024 * 1024;

    public async Task<IReadOnlyList<ChiefAttachmentOutline>> ListOutlinesAsync(
        string tenantId,
        string projectId,
        CancellationToken cancellationToken = default)
    {
        var outlines = new List<ChiefAttachmentOutline>();
        foreach (var record in await ListAttachmentsAsync(tenantId, projectId, cancellationToken))
        {
            var text = await TryReadTextAsync(record, cancellationToken);
            if (text is null)
            {
                continue;
            }

            var sections = AttachmentSectionizer.Split(text);
            outlines.Add(new ChiefAttachmentOutline(
                record.FileName,
                [.. sections.Select(section =>
                    new ChiefAttachmentSectionRef(section.Id, section.Title))]));
        }

        return outlines;
    }

    public async Task<string?> ReadSectionAsync(
        string tenantId,
        string projectId,
        string fileName,
        string sectionId,
        CancellationToken cancellationToken = default)
    {
        foreach (var record in await ListAttachmentsAsync(tenantId, projectId, cancellationToken))
        {
            if (!string.Equals(record.FileName, fileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var text = await TryReadTextAsync(record, cancellationToken);
            if (text is null)
            {
                continue;
            }

            var section = AttachmentSectionizer.Find(AttachmentSectionizer.Split(text), sectionId);
            if (section is not null)
            {
                return section.Content;
            }
        }

        return null;
    }

    /// <summary>
    /// Anexos ACEITOS do projeto, mais recentes primeiro — no duplo upload do mesmo nome, a
    /// versão nova é a que vale para a conversa.
    /// </summary>
    private async Task<IReadOnlyList<SolicitationAttachmentRecord>> ListAttachmentsAsync(
        string tenantId, string projectId, CancellationToken cancellationToken)
    {
        var solicitations = await _board.ListSolicitationsAsync(
            tenantId, projectId, afterId: null, limit: 200, cancellationToken);
        var records = new List<SolicitationAttachmentRecord>();
        foreach (var solicitation in solicitations)
        {
            records.AddRange(await _attachments.ListAsync(
                tenantId, solicitation.Id, cancellationToken));
        }

        return [.. records
            .Where(record => string.Equals(record.State, "accepted", StringComparison.Ordinal))
            .OrderByDescending(record => record.CreatedAt)];
    }

    /// <summary>
    /// Lê o conteúdo como texto UTF-8, ou nulo quando o arquivo é binário (um ZIP de frontend
    /// não tem seção), grande demais ou não está mais no disco. Ausência é ausência declarada
    /// no chamador — nunca texto inventado.
    /// </summary>
    private async Task<string?> TryReadTextAsync(
        SolicitationAttachmentRecord record, CancellationToken cancellationToken)
    {
        try
        {
            // O storage_path é RELATIVO à raiz de anexos: a resolução canônica (com
            // confinamento) é da SolicitationAttachmentStorage — nunca File.Exists no relativo,
            // que falha silenciosamente dependendo do working directory do processo.
            var info = new FileInfo(_storage.Resolve(record.StoragePath));
            if (!info.Exists || info.Length > MaxNavigableBytes)
            {
                return null;
            }

            var bytes = await File.ReadAllBytesAsync(info.FullName, cancellationToken);
            if (Array.IndexOf(bytes, (byte)0) >= 0)
            {
                return null;
            }

            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
