using Harness.Modules.Execution.Infrastructure.Git;
using Harness.Persistence.Abstractions.Projects;

namespace Harness.Host.Workflows;

/// <summary>
/// De onde sai a OPINIÃO de um conselheiro.
///
/// A política do conselho lê a opinião do "resumo da tentativa" (`work_attempts.summary`). Esse
/// campo existe no contrato e NENHUM escritor o preenche: no banco inteiro desta instalação não
/// havia um único resumo não nulo. Consequência silenciosa: todo assento voltava sem parecer, o
/// conselho reunia zero opiniões e a transição da fase 4 respondia `council.incomplete` para
/// sempre — com os seis pareceres já escritos, revisados e entregues.
///
/// O parecer real é o ARTEFATO: `docs/conselho/&lt;persona&gt;-ciclo-&lt;n&gt;.md`, escrito sob claim
/// estreito e commitado na branch da tentativa. É ele que a instrução exige, é ele que o revisor
/// leu e é ele que sobrevive ao fim da sessão do executor — ao contrário da mensagem final, que
/// só existe enquanto o processo está vivo.
/// </summary>
public interface ICouncilOpinionArtifactReader
{
    /// <summary>
    /// Devolve o corpo do parecer entregue, ou <see langword="null"/> quando não há artefato
    /// legível. Nunca lança: falha de leitura vira ausência de opinião, e a ausência é
    /// registrada por quem chama — o conselho não pode cair porque o git recusou um `show`.
    /// </summary>
    Task<string?> ReadAsync(
        ProjectRecord project,
        string attemptId,
        string personaKey,
        int cycle,
        CancellationToken cancellationToken);
}

/// <summary>
/// Leitura do parecer na branch da tentativa, com a mesma fronteira de raiz controlada usada
/// pela publicação de documentos aprovados.
/// </summary>
public sealed class GitCouncilOpinionArtifactReader(string controlledRoot) : ICouncilOpinionArtifactReader
{
    private const string CouncilDirectory = "docs/conselho/";

    private readonly string _controlledRoot = string.IsNullOrWhiteSpace(controlledRoot)
        ? throw new ArgumentException("A raiz controlada é obrigatória.", nameof(controlledRoot))
        : Path.GetFullPath(controlledRoot);

    /// <summary>Caminho canônico do parecer — o mesmo que a instrução do conselho exige.</summary>
    public static string ParecerPath(string personaKey, int cycle) =>
        $"{CouncilDirectory}{personaKey}-ciclo-{cycle}.md";

    public async Task<string?> ReadAsync(
        ProjectRecord project,
        string attemptId,
        string personaKey,
        int cycle,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (string.IsNullOrWhiteSpace(project.RepositoryUrl) ||
            string.IsNullOrWhiteSpace(attemptId) ||
            string.IsNullOrWhiteSpace(personaKey))
        {
            return null;
        }

        var branch = $"task/agent-run-{attemptId.ToLowerInvariant()}";
        try
        {
            using var git = await GitWorktreeManager.OpenAsync(
                Path.GetFullPath(project.RepositoryUrl), _controlledRoot, cancellationToken);

            // O conselho consolida DEPOIS que o card fecha, e fechar inclui o merge — nesse ponto
            // `diff HEAD...branch` ja e vazio, porque a base virou o proprio topo da branch.
            // Portanto a referencia publicada e a primeira fonte, e a branch e o fallback para o
            // caso em que o assento terminou sem merge (bloqueado, ou consolidacao antecipada).
            var published = await TryReadAsync(
                () => git.ReadPublishedDocumentAsync(
                    "HEAD", ParecerPath(personaKey, cycle), cancellationToken));
            if (published is not null)
            {
                return published;
            }

            var changed = await git.ListBranchChangedFilesAsync(branch, cancellationToken);
            var target = SelectParecer(changed, personaKey, cycle);
            if (target is null)
            {
                return null;
            }

            return await TryReadAsync(
                () => git.ReadDocumentFromBranchAsync(branch, target, cancellationToken));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Branch removida, repositório fora da raiz controlada, git indisponível: nada disso
            // é opinião do conselheiro. Quem chama registra a ausência como falha visível.
            return null;
        }
    }

    /// <summary>
    /// Arquivo ausente na referência é resposta legítima ("ainda não está publicado"), não erro:
    /// o leitor tenta a próxima fonte em vez de derrubar a consolidação do conselho.
    /// </summary>
    private static async Task<string?> TryReadAsync(Func<Task<string>> read)
    {
        try
        {
            var body = await read();
            return string.IsNullOrWhiteSpace(body) ? null : body;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// O caminho exato vale primeiro. O fallback só aceita UM markdown em `docs/conselho/`:
    /// dois arquivos são ambiguidade, e escolher um seria inventar de quem é o parecer.
    /// </summary>
    internal static string? SelectParecer(
        IReadOnlyList<string> changedFiles, string personaKey, int cycle)
    {
        ArgumentNullException.ThrowIfNull(changedFiles);
        var exact = ParecerPath(personaKey, cycle);
        if (changedFiles.Any(file => string.Equals(file, exact, StringComparison.Ordinal)))
        {
            return exact;
        }

        var candidates = changedFiles
            .Where(file =>
                file.StartsWith(CouncilDirectory, StringComparison.Ordinal) &&
                file.EndsWith(".md", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }
}
