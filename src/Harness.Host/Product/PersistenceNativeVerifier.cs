using System.Net.Http;
using System.Text;
using System.Text.Json;
using Harness.Modules.Workflows.Product;

namespace Harness.Host.Product;

/// <summary>
/// Prova que o dado da entrega SOBREVIVE — escrevendo pela própria API, lendo de volta, derrubando
/// a aplicação, subindo de novo e lendo outra vez.
///
/// A distinção do §12, que é a razão de este verificador existir separado do de migration: ter
/// migration prova ESTRUTURA; nada nela prova COMPORTAMENTO. Uma aplicação com dez migrations
/// perfeitas e um `List&lt;T&gt;` estático no serviço passa em migration e perde tudo no primeiro
/// reinício. O reinício no meio desta verificação é o passo que separa os dois fatos, e é o único
/// que não dá para forjar guardando dado em memória.
///
/// O §14 manda o Poseidon não assumir nome de tabela nem de domínio: ele não assume. Tudo — a rota,
/// o corpo, o identificador — sai do contrato que a própria aplicação publicou.
///
/// O §13 manda nunca rodar teste destrutivo em banco de produção: antes de escrever qualquer coisa,
/// a configuração da entrega é inspecionada, e uma conexão que não seja comprovadamente local
/// interrompe a verificação com <c>NotSupported</c>. Não verificar é ruim; escrever num banco de
/// verdade é catastrófico.
/// </summary>
public sealed class PersistenceNativeVerifier(TrustedProcessRunner runner) : ProcessProductVerifier(runner)
{
    private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromMinutes(2);

    public override ProductEvidenceKind Kind => ProductEvidenceKind.PersistenceVerified;

    public override string Name => "persistence-native";

    public override bool AppliesTo(ProjectEffectiveProfile profile) =>
        profile.Data.Required && profile.Api.Required;

    public override async Task<ProductVerificationRecord?> VerifyAsync(
        ProductVerificationContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var (project, ambiguity) = WebSurfaceLocator.LocateAspNetCore(
            context.WorkspaceRoot, SafeFind(context.WorkspaceRoot, "*.csproj"));
        if (ambiguity is not null)
        {
            return Negative(context, ambiguity);
        }

        if (project is null)
        {
            return null;
        }

        var isolation = DatabaseIsolationGuard.Inspect(context.WorkspaceRoot);
        if (!isolation.Safe)
        {
            return Unsupported(context, isolation.Reason!);
        }

        var discovery = WebSurfaceLocator.DiscoverOpenApiPaths(context.WorkspaceRoot, project);
        var discriminator = OpenApiPayloadSynthesizer.Discriminator(context.CommitSha, "persist");

        string? createdPath = null;
        string? identity = null;
        string? contract = null;

        // PRIMEIRO CICLO: subir, descobrir a rota pelo contrato, escrever e ler de volta.
        await using (var first = await DeliveryApiSession.StartAsync(
            Runner, context.WorkspaceRoot, project, ReadinessTimeout, cancellationToken))
        {
            if (!first.IsUp)
            {
                return Negative(context, $"A aplicação não subiu: {first.Failure}", first.CommandLine);
            }

            contract = await FetchContractAsync(first.Client!, first.BaseUrl!, discovery.Paths, cancellationToken);
            if (contract is null)
            {
                return Unsupported(
                    context,
                    "A aplicação não publicou contrato OpenAPI, e sem contrato o Poseidon teria de " +
                    "adivinhar rota e corpo — o que significaria escrever dado arbitrário numa " +
                    "aplicação que ele não entende.");
            }

            var target = ResourceTargetResolver.Resolve(contract, discriminator);
            if (target is null)
            {
                return Unsupported(
                    context,
                    "O contrato não declara nenhum recurso com escrita e leitura na mesma rota, ou o " +
                    "corpo exigido não pode ser sintetizado com segurança a partir do schema. " +
                    "Sem isso não há como provar persistência pela API sem inventar domínio.");
            }

            var written = await WriteAsync(
                first.Client!, first.BaseUrl!, target, cancellationToken);
            if (written.Failure is not null)
            {
                return Negative(context, written.Failure, first.CommandLine);
            }

            createdPath = target.Path;
            identity = written.Identity;

            var readBack = await ReadsBackAsync(
                first.Client!, first.BaseUrl!, target.Path, identity, discriminator, cancellationToken);
            if (!readBack.Found)
            {
                return Negative(
                    context,
                    $"O POST em {target.Path} foi aceito, mas o registro não voltou na leitura " +
                    $"seguinte ({readBack.Detail}). Aceitar escrita e não devolvê-la não é persistir.",
                    first.CommandLine);
            }
        }

        // SEGUNDO CICLO: processo NOVO. É o passo que distingue persistir de guardar em memória.
        await using var second = await DeliveryApiSession.StartAsync(
            Runner, context.WorkspaceRoot, project, ReadinessTimeout, cancellationToken);
        if (!second.IsUp)
        {
            return Negative(
                context,
                $"A aplicação não subiu no segundo ciclo, então a sobrevivência do dado não pôde " +
                $"ser verificada: {second.Failure}",
                second.CommandLine);
        }

        var survived = await ReadsBackAsync(
            second.Client!, second.BaseUrl!, createdPath!, identity, discriminator, cancellationToken);
        if (!survived.Found)
        {
            return Negative(
                context,
                $"O dado escrito em {createdPath} desapareceu depois de reiniciar a aplicação " +
                $"({survived.Detail}). A entrega grava em memória, não persiste.",
                second.CommandLine);
        }

        var cleanup = await CleanupAsync(
            second.Client!, second.BaseUrl!, createdPath!, identity, cancellationToken);

        return new ProductVerificationRecord(
            Kind,
            true,
            Name,
            $"POST {createdPath} → GET → restart → GET",
            0,
            context.CommitSha,
            DateTimeOffset.UtcNow,
            context.AttemptId,
            createdPath,
            $"Dado escrito pela API em {createdPath}, lido de volta, e ainda presente depois de a " +
            $"aplicação ser derrubada e subida de novo{(identity is null ? string.Empty : $" (id {identity})")}. " +
            $"Isolamento: {isolation.Reason}. Limpeza: {cleanup}.",
            VerificationTrustLevel.PoseidonControlled);
    }

    private static async Task<string?> FetchContractAsync(
        HttpClient client, string baseUrl, IReadOnlyList<string> candidates, CancellationToken cancellationToken)
    {
        foreach (var candidate in candidates)
        {
            try
            {
                using var response = await client.GetAsync(baseUrl + candidate, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (body.TrimStart().StartsWith('{'))
                {
                    return body;
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                // Rota que não responde não é o contrato; segue para a próxima candidata.
            }
        }

        return null;
    }

    private static async Task<(string? Identity, string? Failure)> WriteAsync(
        HttpClient client, string baseUrl, ResourceTarget target, CancellationToken cancellationToken)
    {
        try
        {
            using var content = new StringContent(
                target.Payload.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(baseUrl + target.Path, content, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return (null,
                    $"A escrita em {target.Path} foi recusada: HTTP {(int)response.StatusCode}. " +
                    $"Corpo enviado a partir do schema declarado; resposta: {Short(body)}");
            }

            return (ExtractIdentity(body), null);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return (null, $"A escrita em {target.Path} falhou: {exception.GetType().Name}.");
        }
    }

    /// <summary>
    /// Procura o registro escrito. Por id quando a aplicação devolveu um; senão pela marca que a
    /// síntese deixou no conteúdo — nunca por campo de domínio, que o Poseidon não conhece.
    /// </summary>
    private static async Task<(bool Found, string Detail)> ReadsBackAsync(
        HttpClient client,
        string baseUrl,
        string path,
        string? identity,
        string discriminator,
        CancellationToken cancellationToken)
    {
        var attempts = identity is null
            ? (string[])[path]
            : [$"{path.TrimEnd('/')}/{identity}", path];

        var reasons = new List<string>();
        foreach (var candidate in attempts)
        {
            try
            {
                using var response = await client.GetAsync(baseUrl + candidate, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    reasons.Add($"GET {candidate} → HTTP {(int)response.StatusCode}");
                    continue;
                }

                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if ((identity is not null && body.Contains($"\"{identity}\"", StringComparison.Ordinal)) ||
                    body.Contains(identity ?? " ", StringComparison.Ordinal) ||
                    body.Contains(discriminator, StringComparison.Ordinal) ||
                    body.Contains(OpenApiPayloadSynthesizer.Marker, StringComparison.Ordinal))
                {
                    return (true, $"GET {candidate} devolveu o registro escrito");
                }

                reasons.Add($"GET {candidate} → 200 sem o registro");
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                reasons.Add($"GET {candidate} → {exception.GetType().Name}");
            }
        }

        return (false, string.Join("; ", reasons));
    }

    /// <summary>
    /// Remove o que a verificação escreveu, quando a aplicação declara como. Falhar aqui não
    /// invalida a prova de persistência — mas fica dito, porque deixar resíduo em silêncio numa
    /// árvore que alguém vai inspecionar é uma surpresa desnecessária.
    /// </summary>
    private static async Task<string> CleanupAsync(
        HttpClient client, string baseUrl, string path, string? identity, CancellationToken cancellationToken)
    {
        if (identity is null)
        {
            return "não executada (a aplicação não devolveu identificador)";
        }

        try
        {
            using var response = await client.DeleteAsync(
                $"{baseUrl}{path.TrimEnd('/')}/{identity}", cancellationToken);
            return response.IsSuccessStatusCode
                ? "registro de teste removido"
                : $"remoção recusada (HTTP {(int)response.StatusCode}); o registro de teste permanece";
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return $"remoção falhou ({exception.GetType().Name}); o registro de teste permanece";
        }
    }

    private static string? ExtractIdentity(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!string.Equals(property.Name, "id", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.Number => property.Value.GetRawText(),
                    _ => null,
                };
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Short(string value) =>
        value.Length <= 300 ? value : value[..300] + "…";

    private ProductVerificationRecord Negative(
        ProductVerificationContext context, string reason, string? command = null) =>
        new(Kind, false, Name, command ?? "persistence-native", -1, context.CommitSha,
            DateTimeOffset.UtcNow, context.AttemptId, ".", reason,
            VerificationTrustLevel.PoseidonControlled);

    /// <summary>
    /// O requisito INCIDE e o Poseidon não sabe prová-lo com segurança nesta entrega. Reprova, como
    /// qualquer outra ausência de prova — mas o motivo diz que o buraco é da plataforma, não do
    /// produto. Chamar isso de "não aplicável" seria dispensar o produto de um requisito real.
    /// </summary>
    private ProductVerificationRecord Unsupported(ProductVerificationContext context, string reason) =>
        new(Kind, false, Name, "persistence-native", -1, context.CommitSha,
            DateTimeOffset.UtcNow, context.AttemptId, ".",
            $"{VerificationOutcomeKind.NotSupported}: {reason}",
            VerificationTrustLevel.PoseidonControlled);
}
