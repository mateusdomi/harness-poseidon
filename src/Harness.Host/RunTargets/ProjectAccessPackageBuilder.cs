using Harness.Persistence.Abstractions.Projects;
using Harness.Persistence.Abstractions.RunTargets;

namespace Harness.Host.RunTargets;

public sealed record AccessCheck(string Name, string? Url, bool Healthy, string Detail);

public sealed record AccessPackage(
    IReadOnlyList<AccessCheck> Checks,
    bool AllHealthy,
    string? AccessDocument,
    string Markdown);

/// <summary>
/// O PACOTE DE ACESSO de um produto: URLs de tela e API TESTADAS antes de serem entregues, mais
/// as instruções de acesso que o próprio produto documenta (`docs/ACESSO.md`).
///
/// Existe por exigência do dono (2026-08-07): entregar uma URL sem testar o caminho inteiro é
/// defeito básico — ele abriu a tela, a API estava fora do ar e o console encheu de erro. A
/// regra passa a ser: o Poseidon verifica ANTES de entregar; o que reprova chega junto com a
/// correção já encaminhada; o que não foi verificado é declarado, nunca omitido.
/// </summary>
public static class ProjectAccessPackageBuilder
{
    public static async Task<AccessPackage> BuildAsync(
        ProjectRecord project,
        IReadOnlyList<RunTargetRecord> targets,
        HttpClient http,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(targets);

        var checks = new List<AccessCheck>();
        var front = targets.FirstOrDefault(target => target.UserFacing);
        var api = targets.FirstOrDefault(target =>
            !target.UserFacing &&
            target.Name.Contains(".Api", StringComparison.OrdinalIgnoreCase));

        checks.Add(front is null
            ? new AccessCheck(
                "Tela do produto", null, false,
                "Nenhum serviço de interface foi detectado no repositório do produto.")
            : await ProbeAsync(http, "Tela do produto", front, ["/"], cancellationToken));
        checks.Add(api is null
            ? new AccessCheck(
                "API do produto", null, false,
                "Nenhum serviço de API foi detectado no repositório do produto.")
            : await ProbeAsync(http, "API do produto", api, ["/health", "/swagger", "/"], cancellationToken));

        string? accessDocument = null;
        if (project is { RepositoryProvider: "local", RepositoryUrl.Length: > 0 })
        {
            var path = Path.Combine(project.RepositoryUrl, "docs", "ACESSO.md");
            if (File.Exists(path))
            {
                accessDocument = await File.ReadAllTextAsync(path, cancellationToken);
            }
        }

        var allHealthy = checks.All(check => check.Healthy);
        return new AccessPackage(checks, allHealthy, accessDocument, Compose(project, checks, accessDocument));
    }

    private static async Task<AccessCheck> ProbeAsync(
        HttpClient http,
        string name,
        RunTargetRecord target,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(target.State, "running", StringComparison.OrdinalIgnoreCase))
        {
            return new AccessCheck(
                name, target.Url, false,
                $"O serviço \"{target.Name}\" está {target.State} — precisa ser iniciado (tela Executar Projeto).");
        }

        if (target.Url is not { Length: > 0 } baseUrl)
        {
            return new AccessCheck(name, null, false, $"O serviço \"{target.Name}\" não tem URL registrada.");
        }

        foreach (var path in paths)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                using var response = await http.GetAsync(
                    new Uri(new Uri(baseUrl), path), HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if ((int)response.StatusCode < 500)
                {
                    return new AccessCheck(
                        name, baseUrl, true,
                        $"Respondeu HTTP {(int)response.StatusCode} em {path}.");
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException ||
                                              !cancellationToken.IsCancellationRequested)
            {
                // Tenta o próximo caminho; o veredito final é do conjunto.
            }
        }

        return new AccessCheck(
            name, baseUrl, false,
            $"Nenhuma resposta utilizável em {string.Join(", ", paths)} — o serviço não está de pé de verdade.");
    }

    private static string Compose(
        ProjectRecord project,
        IReadOnlyList<AccessCheck> checks,
        string? accessDocument)
    {
        var lines = new List<string>
        {
            $"🔑 **Pacote de acesso — {project.Name}** (verificado agora, não presumido)",
            string.Empty,
        };
        foreach (var check in checks)
        {
            var mark = check.Healthy ? "✅" : "❌";
            lines.Add(check.Url is { Length: > 0 }
                ? $"{mark} {check.Name}: {check.Url} — {check.Detail}"
                : $"{mark} {check.Name}: {check.Detail}");
        }

        lines.Add(string.Empty);
        if (accessDocument is { Length: > 0 })
        {
            lines.Add("**Como acessar (documentado pelo produto em `docs/ACESSO.md`):**");
            lines.Add(string.Empty);
            lines.Add(accessDocument.Trim());
        }
        else
        {
            lines.Add(
                "❌ O produto ainda não entregou `docs/ACESSO.md` (URLs, credenciais de " +
                "demonstração e passo a passo de login). Isso é exigência de entrega e será " +
                "cobrado do executor no próximo objetivo.");
        }

        lines.Add(string.Empty);
        lines.Add(
            "_O que esta verificação cobre: serviço de pé e respondendo em HTTP. O que ela NÃO " +
            "cobre (e é coberto pela validação de produto de cada objetivo): fluxo de login no " +
            "navegador, console limpo e jornada ponta a ponta._");
        return string.Join("\n", lines);
    }
}
