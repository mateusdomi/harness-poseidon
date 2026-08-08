using System.Globalization;
using System.Text;

namespace Harness.Modules.Workflows.Product;

/// <summary>
/// Materializa o AMBIENTE EFÊMERO de uma rodada do gate de E2E: gera os segredos declarados,
/// aloca a porta de banco (se pedida) e resolve os templates <c>${VAR}</c> de <c>env</c> contra
/// esses valores. Determinístico dado o gerador e o alocador — a aleatoriedade e a alocação de
/// porta entram por delegate, para o núcleo ser testável sem tocar em rede nem em RNG real.
///
/// É a peça que tira o gate do "unavailable": antes, compose e API subiam sem senha porque os
/// segredos não podiam morar no git. Agora o produto DECLARA o que gerar, e cada rodada nasce com
/// credenciais próprias que morrem no teardown.
/// </summary>
public static class ProductE2EEnvironment
{
    /// <summary>
    /// Produz o env resolvido. <paramref name="generateSecret"/> recebe o TIPO declarado
    /// ("password", "hex32", "policyPassword", …) e devolve um valor; <paramref name="allocatePort"/>
    /// devolve uma porta livre (só chamado se o manifesto declarar <see cref="ProductE2EHarness.DbPortVar"/>).
    /// </summary>
    public static IReadOnlyDictionary<string, string> Materialize(
        ProductE2EHarness harness,
        Func<string, string> generateSecret,
        Func<int> allocatePort)
    {
        ArgumentNullException.ThrowIfNull(harness);
        ArgumentNullException.ThrowIfNull(generateSecret);
        ArgumentNullException.ThrowIfNull(allocatePort);

        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);

        // 1. Segredos gerados — cada variável ganha um valor do tipo pedido.
        if (harness.GeneratedSecrets is { Count: > 0 } secrets)
        {
            foreach (var (variable, kind) in secrets)
            {
                resolved[variable] = generateSecret(kind);
            }
        }

        // 2. Porta de banco aleatória, exposta pelo nome pedido e como ${DB_PORT}.
        if (harness.DbPortVar is { Length: > 0 } portVariable)
        {
            var port = allocatePort().ToString(CultureInfo.InvariantCulture);
            resolved[portVariable] = port;
            resolved["DB_PORT"] = port;
        }

        // 3. `env` do manifesto, com ${VAR} substituído pelos valores acima. Uma entrada de env
        //    pode referenciar um segredo gerado (ex.: a connection string usa a senha do banco) ou
        //    a porta. O que não casar fica literal — nunca explode.
        foreach (var (key, template) in harness.Env)
        {
            resolved[key] = Substitute(template, resolved);
        }

        return resolved;
    }

    private static string Substitute(string template, Dictionary<string, string> values)
    {
        if (template.IndexOf('$', StringComparison.Ordinal) < 0)
        {
            return template;
        }

        var builder = new StringBuilder(template.Length);
        for (var index = 0; index < template.Length; index++)
        {
            if (template[index] == '$' && index + 1 < template.Length && template[index + 1] == '{')
            {
                var close = template.IndexOf('}', index + 2);
                if (close > index + 2)
                {
                    var name = template[(index + 2)..close];
                    if (values.TryGetValue(name, out var value))
                    {
                        _ = builder.Append(value);
                        index = close;
                        continue;
                    }
                }
            }

            _ = builder.Append(template[index]);
        }

        return builder.ToString();
    }
}
