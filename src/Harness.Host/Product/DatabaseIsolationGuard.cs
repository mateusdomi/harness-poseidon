using System.Text.Json;
using System.Text.RegularExpressions;

namespace Harness.Host.Product;

/// <summary>O veredito sobre poder escrever, ou não, no banco que a entrega vai usar.</summary>
public sealed record DatabaseIsolationVerdict(bool Safe, string? Reason);

/// <summary>
/// A trava do §13: <b>nunca executar verificação destrutiva contra banco de produção.</b>
///
/// A verificação de persistência escreve de verdade. Se a configuração da entrega apontar para um
/// servidor que não seja comprovadamente local, a coisa certa a fazer é NÃO VERIFICAR e dizer por
/// quê. O prejuízo de deixar um requisito sem prova é uma entrega barrada; o de gravar dado de
/// teste num banco de cliente não tem tamanho conhecido.
///
/// A postura é fail-closed em cima da dúvida: só é considerado seguro o que se reconhece como
/// local — <c>localhost</c>, <c>127.0.0.1</c>, <c>(local)</c>, <c>(localdb)</c>, arquivo SQLite,
/// banco em memória. Qualquer outro host, e qualquer coisa que não dê para interpretar, para.
/// </summary>
public static partial class DatabaseIsolationGuard
{
    public static DatabaseIsolationVerdict Inspect(string workspaceRoot)
    {
        var files = FileDiscovery.Find(workspaceRoot, "appsettings*.json");
        if (files.Count == 0)
        {
            // Sem configuração declarada, a aplicação define o banco em código ou por ambiente — e o
            // ambiente que ela recebe é o que o runner constrói, sem variável de conexão nenhuma.
            return new DatabaseIsolationVerdict(
                true, "nenhuma configuração de conexão declarada na entrega");
        }

        var inspected = new List<string>();
        foreach (var relative in files)
        {
            var absolute = Path.Combine(
                workspaceRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            foreach (var connection in ReadConnectionStrings(absolute))
            {
                if (!IsLocal(connection))
                {
                    return new DatabaseIsolationVerdict(
                        false,
                        $"{relative} declara uma conexão que não é comprovadamente local " +
                        $"({DescribeTarget(connection)}). Escrever dado de teste num banco que pode " +
                        "ser real é risco que nenhuma evidência compensa.");
                }

                inspected.Add(relative);
            }
        }

        return new DatabaseIsolationVerdict(
            true,
            inspected.Count == 0
                ? "nenhuma cadeia de conexão declarada nos appsettings da entrega"
                : $"todas as conexões declaradas apontam para banco local ({inspected.Count} verificadas)");
    }

    private static IEnumerable<string> ReadConnectionStrings(string path)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(path));
        }
        catch (Exception exception) when (
            exception is IOException or JsonException or UnauthorizedAccessException)
        {
            yield break;
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("ConnectionStrings", out var section) ||
                section.ValueKind != JsonValueKind.Object)
            {
                yield break;
            }

            foreach (var property in section.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String &&
                    property.Value.GetString() is { Length: > 0 } value)
                {
                    yield return value;
                }
            }
        }
    }

    /// <summary>
    /// Local de verdade, reconhecido pela forma. Tudo o que não casa com um destes é tratado como
    /// possivelmente real — inclusive o que não dá para interpretar.
    /// </summary>
    private static bool IsLocal(string connection)
    {
        var value = connection.ToLowerInvariant();

        if (value.Contains(":memory:", StringComparison.Ordinal) ||
            value.Contains("mode=memory", StringComparison.Ordinal))
        {
            return true;
        }

        // SQLite por arquivo: o banco é um arquivo da própria worktree.
        if (value.Contains("data source=", StringComparison.Ordinal) &&
            !value.Contains("server=", StringComparison.Ordinal) &&
            !value.Contains("initial catalog=", StringComparison.Ordinal) &&
            (value.Contains(".db", StringComparison.Ordinal) ||
                value.Contains(".sqlite", StringComparison.Ordinal)))
        {
            return true;
        }

        var host = HostPattern().Match(value);
        return host.Success && IsLocalHost(host.Groups[1].Value);
    }

    /// <summary>
    /// Reduz o destino ao HOST e decide se ele é local.
    ///
    /// As formas variam por fornecedor e todas apareceram em entregas reais:
    /// <c>localhost</c>, <c>.\SQLEXPRESS</c>, <c>(localdb)\MSSQLLocalDB</c>, <c>127.0.0.1,1433</c>
    /// e, no Oracle, o EZ-connect <c>127.0.0.1:1521/FREEPDB1</c> — este último foi encontrado no
    /// preflight do Prisma barrando a verificação de persistência de uma conexão que era local.
    /// Tratar host local como remoto custa um requisito sem prova; o inverso custaria escrita em
    /// banco de produção, e por isso tudo o que não se reconhece continua sendo tratado como remoto.
    /// </summary>
    private static bool IsLocalHost(string target)
    {
        var host = target.Trim();

        // Descritor TNS completo: o host está dentro de `(HOST=...)`.
        var descriptor = TnsHostPattern().Match(host);
        if (descriptor.Success)
        {
            host = descriptor.Groups[1].Value;
        }

        // EZ-connect do Oracle: `host:porta/serviço`. Também cobre `host/serviço` sem porta.
        var slash = host.IndexOf('/', StringComparison.Ordinal);
        if (slash >= 0)
        {
            host = host[..slash];
        }

        // Instância nomeada do SQL Server (`.\SQLEXPRESS`) e porta por vírgula (`127.0.0.1,1433`).
        host = host.Split('\\')[0].Split(',')[0];

        // Porta por dois-pontos, preservando IPv6 entre colchetes.
        if (!host.StartsWith('[') && host.Count(character => character == ':') == 1)
        {
            host = host[..host.IndexOf(':', StringComparison.Ordinal)];
        }

        host = host.Trim().Trim('[', ']');
        return host is "localhost" or "127.0.0.1" or "::1" or "0.0.0.0" or "(local)" or "." ||
            host.StartsWith("(localdb)", StringComparison.Ordinal) ||
            host.EndsWith(".localhost", StringComparison.Ordinal);
    }

    private static string DescribeTarget(string connection)
    {
        var match = HostPattern().Match(connection.ToLowerInvariant());
        return match.Success ? $"destino `{match.Groups[1].Value.Trim()}`" : "destino não interpretável";
    }

    [GeneratedRegex(@"(?:server|host|data source|datasource)\s*=\s*([^;]+)", RegexOptions.CultureInvariant)]
    private static partial Regex HostPattern();

    [GeneratedRegex(@"\(\s*host\s*=\s*([^)\s]+)", RegexOptions.CultureInvariant)]
    private static partial Regex TnsHostPattern();
}
