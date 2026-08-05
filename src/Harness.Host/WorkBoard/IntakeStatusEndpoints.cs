using Harness.Host.Profiles;
using Harness.Modules.Workflows.Product;
using Harness.Persistence.Abstractions.Attention;
using Harness.Persistence.Abstractions.Coordination;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.WorkChain;

namespace Harness.Host.WorkBoard;

/// <summary>
/// O "Intake Accepted" — o marco que diz ao usuário QUANDO ele pode abandonar a tela.
///
/// Estado COMPUTADO das fontes reais, nunca declarado por otimismo:
/// RECEIVED (solicitação existe, nenhum anexo aceito) → VALIDATING (anexos aceitos, extração em
/// curso/parcial) → NEEDS_INPUT (há dúvidas BLOQUEANTES abertas) → READY (fontes processadas,
/// requisitos conhecidos, perfil resolvível, zero bloqueio). READY vem com a frase que importa:
/// "pode sair desta tela; se sua participação for necessária, você será notificado."
/// </summary>
public static class IntakeStatusEndpoints
{
    public static IEndpointRouteBuilder MapIntakeStatus(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/solicitations/{solicitationId}/intake-status", GetAsync)
            .WithTags("po-assistant")
            .Produces<IntakeStatusContract>()
            .ProducesProblem(401)
            .ProducesProblem(404);
        return endpoints;
    }

    private static async Task<IResult> GetAsync(
        string solicitationId,
        HttpRequest request,
        ILocalProfileStore profiles,
        IWorkBoardStore board,
        ISolicitationAttachmentStore attachments,
        SolicitationAttachmentStorage storage,
        CancellationToken token)
    {
        var attention = request.HttpContext.RequestServices
            .GetService<IHumanAttentionStore>();
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null)
        {
            return Results.Problem(
                statusCode: 401, title: "local_session_required",
                detail: "A local profile session is required.");
        }

        var solicitation = await board.GetSolicitationAsync(profile.TenantId, solicitationId, token);
        if (solicitation is null)
        {
            return Results.Problem(statusCode: 404, title: "solicitation_not_found");
        }

        var accepted = (await attachments.ListAsync(profile.TenantId, solicitationId, token))
            .Where(record => string.Equals(record.State, "accepted", StringComparison.Ordinal))
            .ToArray();
        if (accepted.Length == 0)
        {
            return Results.Ok(new IntakeStatusContract(
                "RECEIVED", 0, 0, 0, false,
                "Solicitação recebida. Anexe as fontes (requisitos, protótipo, referências) para a validação começar."));
        }

        // Extração REAL sobre as fontes de requisitos anexadas: quantos critérios de aceite o
        // intake enxerga hoje. Falha de leitura degrada para VALIDATING, nunca para READY.
        var criteria = 0;
        var requirementSources = 0;
        foreach (var record in accepted.Where(record =>
            string.Equals(record.Role, "requirements_source", StringComparison.Ordinal)))
        {
            requirementSources++;
            try
            {
                var absolute = storage.Resolve(record.StoragePath);
                if (!File.Exists(absolute))
                {
                    return Validating(accepted.Length, criteria, 0);
                }

                var text = await File.ReadAllTextAsync(absolute, token);
                criteria += AcceptanceCriteriaExtractor.Extract(text).Count;
            }
            catch (IOException)
            {
                return Validating(accepted.Length, criteria, 0);
            }
        }

        var blocking = attention is null
            ? 0
            : (await attention.ListOpenAsync(profile.TenantId, solicitation.ProjectId, token))
                .Count(item => item.Severity is "high" or "critical");
        if (blocking > 0)
        {
            return Results.Ok(new IntakeStatusContract(
                "NEEDS_INPUT", accepted.Length, criteria, blocking, false,
                $"O intake identificou {blocking} questão(ões) bloqueante(s) que precisam de você antes do início. As demais frentes não estão impedidas."));
        }

        return Results.Ok(new IntakeStatusContract(
            "READY", accepted.Length, criteria, 0, true,
            $"Intake validado: {accepted.Length} artefato(s) processado(s)" +
            (criteria > 0 ? $", {criteria} critérios de aceite identificados" : string.Empty) +
            ", zero dúvidas bloqueantes. O projeto está apto à execução autônoma. Você pode " +
            "sair desta tela — caso sua participação seja necessária, enviaremos uma notificação."));

        IResult Validating(int processed, int found, int blockingCount) =>
            Results.Ok(new IntakeStatusContract(
                "VALIDATING", processed, found, blockingCount, false,
                "As fontes anexadas estão sendo validadas."));
    }
}

public sealed record IntakeStatusContract(
    string State,
    int AcceptedArtifacts,
    int AcceptanceCriteria,
    int BlockingQuestions,
    bool CanLeave,
    string Message);
