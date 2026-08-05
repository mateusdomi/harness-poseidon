using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Harness.SharedKernel.Graph;

namespace Harness.Modules.Workflows.Product.Graph;

/// <summary>
/// O trabalho de revalidação que uma propagação de STALE exige — derivado, nunca escrito por
/// modelo (Onda 2.2). Mesma filosofia do <c>ProductGapCorrections</c>: a impressão digital é do
/// PAR causa+versão, então a mesma mudança nunca gera dois cards, e uma mudança NOVA no mesmo nó
/// (versão diferente) gera trabalho novo — porque é um problema novo.
/// </summary>
public sealed record GraphRevalidationWork(
    string Fingerprint,
    string Title,
    string Instruction);

public static class GraphRevalidationWorks
{
    /// <summary>Prefixo fixo no código: é a chave de idempotência no board.</summary>
    public const string TitlePrefix = "REVALIDACAO/GRAFO";

    /// <summary>
    /// Deriva o card de revalidação de uma propagação. Propagação vazia não produz NADA — o
    /// mecanismo existe para revalidar premissa invalidada, não para gerar trabalho.
    /// </summary>
    public static GraphRevalidationWork? From(
        GraphNode cause,
        IReadOnlyList<ImpactedNode> impacted,
        IReadOnlyDictionary<string, GraphNode> nodesById)
    {
        ArgumentNullException.ThrowIfNull(cause);
        ArgumentNullException.ThrowIfNull(impacted);
        ArgumentNullException.ThrowIfNull(nodesById);

        var blocking = impacted
            .Where(node => node.Classification != ImpactClassification.Possible)
            .ToArray();
        if (blocking.Length == 0)
        {
            return null;
        }

        var fingerprint = Fingerprint(cause.Id, cause.Version);
        var affected = string.Join(
            '\n',
            blocking.Select(node =>
            {
                var title = nodesById.TryGetValue(node.NodeId, out var known)
                    ? known.Title
                    : node.NodeId;
                return $"- {title} ({node.NodeId}, {node.Classification}) — caminho: " +
                    string.Join(" → ", node.Path);
            }));

        return new GraphRevalidationWork(
            fingerprint,
            $"{TitlePrefix} {fingerprint} — revalidar após mudança em \"{cause.Title}\"",
            $"""
            A fonte "{cause.Title}" ({cause.Id}) mudou para a versão {cause.Version.ToString(CultureInfo.InvariantCulture)}.
            Os itens abaixo foram construídos sobre a premissa ANTERIOR e estão marcados como
            STALE — eles não foram apagados nem revertidos; precisam de REVALIDAÇÃO:

            {affected}

            Para cada item: confirme se ele continua válido sob a nova versão da fonte. Se sim,
            registre a evidência da confirmação; se não, produza a correção. A aprovação desta
            revalidação limpa o STALE dos itens confirmados.
            """);
    }

    /// <summary>Doze caracteres de SHA-256 sobre causa+versão.</summary>
    public static string Fingerprint(string causeNodeId, int causeVersion)
    {
        var hash = SHA256.HashData(
            Encoding.UTF8.GetBytes($"{causeNodeId}|{causeVersion.ToString(CultureInfo.InvariantCulture)}"));
        return Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }
}
