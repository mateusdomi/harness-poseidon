using System.Security.Cryptography;
using System.Text;

namespace Harness.Modules.Governance.Memory;

/// <summary>
/// Embedding LOCAL e determinístico por feature hashing (Fase 6): cada termo normalizado do
/// texto é somado num bucket estável (SHA-256 do termo módulo dimensão) e o vetor final é
/// L2-normalizado. É o baseline do modo pessoal — sem serviço externo, sem custo, mesmo texto
/// SEMPRE produz o mesmo vetor, então o índice é reconstruível e nunca é fonte da verdade.
///
/// Ele captura similaridade LEXICAL (termos compartilhados), não semântica profunda; o modo
/// servidor pode substituí-lo por um modelo real de embeddings atrás do mesmo
/// <c>IVectorIndex</c> sem mudar nenhum chamador.
/// </summary>
public static class DeterministicLocalEmbedding
{
    /// <summary>Dimensão fixa do vetor. Mudá-la exige reindexar (o índice é derivado).</summary>
    public const int Dimensions = 256;

    public static IReadOnlyList<float> Embed(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var vector = new float[Dimensions];
        foreach (var term in Tokenize(text))
        {
            var bucket = Bucket(term);
            vector[bucket] += 1f;
        }

        var norm = MathF.Sqrt(vector.Sum(value => value * value));
        if (norm <= 0f)
        {
            return vector;
        }

        for (var index = 0; index < vector.Length; index++)
        {
            vector[index] /= norm;
        }

        return vector;
    }

    private static IEnumerable<string> Tokenize(string text) =>
        text.ToLowerInvariant()
            .Split(
                [' ', '\t', '\r', '\n', '.', ',', ';', ':', '!', '?', '(', ')', '[', ']',
                 '{', '}', '"', '\'', '`', '/', '\\', '<', '>', '|', '#', '*', '-', '_', '='],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(term => term.Length > 1);

    private static int Bucket(string term)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(term));
        return (int)(BitConverter.ToUInt32(hash, 0) % Dimensions);
    }
}
