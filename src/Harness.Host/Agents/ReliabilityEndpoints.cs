using Harness.Host.Profiles;
using Harness.Modules.Coordination.Application;
using Harness.Persistence.Abstractions.Coordination;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.Providers;
using Harness.SharedKernel.Identifiers;

namespace Harness.Host.Agents;

/// <summary>
/// Painel de confiabilidade da equipe (B1+B12/F16) — modo Técnico.
///
/// Duas leituras que só existem juntas: <b>onde</b> o sistema erra (distribuição MAST) e <b>quem</b>
/// resolve com quantas rodadas (pass@k por par conta+modelo+tipo de card). Uma sem a outra engana:
/// distribuição sem pass@k diz que há falha de verificação e não diz de quem; pass@k sem
/// distribuição diz que um par acerta pouco e não diz se o problema é o par ou o enunciado.
///
/// Nada aqui aparece no modo Negócio: o léxico §2 proíbe conta, modelo e taxa técnica para o dono
/// leigo. É informação de quem opera a plataforma.
/// </summary>
public static class ReliabilityEndpoints
{
    public static IEndpointRouteBuilder MapReliability(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/projects/{projectId}").WithTags("agents");
        group.MapGet("/reliability", GetAsync)
            .Produces<ProjectReliabilityContract>()
            .ProducesProblem(400)
            .ProducesProblem(401);
        return endpoints;
    }

    private static async Task<IResult> GetAsync(
        string projectId,
        int? k,
        HttpRequest request,
        ILocalProfileStore profiles,
        IMastClassificationStore mast,
        IModelInvocationStore invocations,
        CancellationToken token)
    {
        if (!UlidValue.TryParse(projectId, out _))
        {
            return Results.Problem(
                statusCode: 400, title: "invalid_project_id", detail: "Project ID must be a ULID.");
        }

        var rounds = k ?? 3;
        if (rounds is < 1 or > 10)
        {
            return Results.Problem(
                statusCode: 400, title: "invalid_k", detail: "k must be between 1 and 10.");
        }

        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null)
        {
            return Results.Problem(
                statusCode: 401,
                title: "local_session_required",
                detail: "A local profile session is required.");
        }

        var classifications = await mast.ListByProjectAsync(profile.TenantId, projectId, token);
        var distribution = MastDistribution.From(classifications.Select(item => item.FailureModeCode));

        // pass@k sai do histórico REAL de invocações: cada tentativa de um card, na ordem em que
        // aconteceu, com o desfecho registrado. A ordem define o número da rodada — não há campo
        // "tentativa nº" e inventá-lo por contagem global misturaria cards diferentes.
        var history = new List<AttemptOutcome>();
        foreach (var group in classifications
            .Select(item => item.TaskId)
            .Distinct(StringComparer.Ordinal))
        {
            var taskInvocations = await invocations.GetTaskInvocationsAsync(
                profile.TenantId, group, token);
            var ordered = taskInvocations.OrderBy(item => item.InvokedAt).ToArray();
            for (var index = 0; index < ordered.Length; index++)
            {
                var invocation = ordered[index];
                history.Add(new AttemptOutcome(
                    new CapabilityPair(invocation.AccountAlias, invocation.Model, "implementation"),
                    invocation.WorkTaskId,
                    index + 1,
                    invocation.Outcome.Contains("success", StringComparison.OrdinalIgnoreCase)));
            }
        }

        var measurements = history
            .Select(outcome => outcome.Pair)
            .Distinct()
            .Select(pair => PassAtKPolicy.Measure(pair, history, rounds))
            .ToArray();

        var ranked = PassAtKPolicy
            .Rank(measurements)
            .Select(measurement => new CapabilityMeasurementContract(
                measurement.Pair.AccountAlias,
                measurement.Pair.ModelTier,
                measurement.Pair.CardType,
                measurement.K,
                measurement.TasksObserved,
                measurement.PassAt1,
                measurement.PassAtK,
                PassAtKPolicy.RecommendMaxRounds(measurement, rounds)))
            .ToArray();

        return Results.Ok(new ProjectReliabilityContract(
            projectId,
            classifications.Count,
            distribution.CountByCategory.ToDictionary(
                entry => entry.Key.ToString().ToLowerInvariant(),
                entry => entry.Value,
                StringComparer.Ordinal),
            MastAdvice.Recommend(distribution),
            ranked));
    }
}

/// <param name="Advice">
/// O que a distribuição recomenda fazer. Concentração em especificação significa que decompor mais
/// não ajuda — o enunciado precisa mudar; em verificação, aprofundar a revisão ajuda; em
/// desalinhamento, o problema é o número de agentes e a fronteira entre eles.
/// </param>
public sealed record ProjectReliabilityContract(
    string ProjectId,
    int ClassifiedAttempts,
    IReadOnlyDictionary<string, int> FailureModesByCategory,
    string Advice,
    IReadOnlyList<CapabilityMeasurementContract> Capabilities);

public sealed record CapabilityMeasurementContract(
    string AccountAlias,
    string ModelTier,
    string CardType,
    int K,
    int TasksObserved,
    double PassAt1,
    double PassAtK,
    int RecommendedMaxRounds);
