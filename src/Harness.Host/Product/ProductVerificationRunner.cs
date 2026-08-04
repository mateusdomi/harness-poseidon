using Harness.Modules.Workflows.Product;

namespace Harness.Host.Product;

/// <summary>
/// Executa o PLANO de verificação: para cada evidência que o perfil exige, chama o verificador
/// registrado que sabe produzi-la.
///
/// Duas regras que este componente existe para garantir:
///
/// 1. <b>o plano manda</b> — nenhum verificador roda por iniciativa própria, e nenhum agente
///    escolhe o que será verificado. O que roda é o que o perfil efetivo exige;
/// 2. <b>ausência nunca vira sucesso</b> — quando um verificador não tem o que verificar, ele não
///    aparece no conjunto, e o gate lê a ausência como reprovação. Preencher o vazio com um
///    resultado positivo seria fabricar a evidência que este trabalho inteiro existe para impedir.
/// </summary>
public sealed class ProductVerificationRunner(
    IReadOnlyList<IProductVerifier> verifiers,
    ILogger<ProductVerificationRunner>? logger = null)
{
    public async Task<IReadOnlyList<ProductVerificationRecord>> RunAsync(
        ProductVerificationContext context,
        ProductVerificationPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);

        var required = plan.Required.ToHashSet();
        var records = new List<ProductVerificationRecord>();

        foreach (var verifier in verifiers)
        {
            if (!required.Contains(verifier.Kind) || !verifier.AppliesTo(context.Profile))
            {
                continue;
            }

            try
            {
                var record = await verifier.VerifyAsync(context, cancellationToken);
                if (record is not null)
                {
                    records.Add(record);
                    LogVerified(logger, verifier.Name, record.Succeeded, record.ExitCode);
                }
                else
                {
                    LogNothingToVerify(logger, verifier.Name);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Verificador que explode não pode derrubar a esteira NEM aprovar por omissão: a
                // falha vira evidência negativa, com o tipo do erro no diagnóstico.
                records.Add(new ProductVerificationRecord(
                    verifier.Kind, false, verifier.Name, "(verificador falhou)", -1,
                    context.CommitSha, DateTimeOffset.UtcNow, context.AttemptId, ".",
                    $"O verificador lançou {exception.GetType().Name}."));
                LogVerifierFailed(logger, verifier.Name, exception);
            }
        }

        return records;
    }

    private static void LogVerified(ILogger? logger, string verifier, bool succeeded, int exitCode)
    {
        if (logger is not null)
        {
            Verified(logger, verifier, succeeded ? "passou" : "reprovou", exitCode, null);
        }
    }

    private static void LogNothingToVerify(ILogger? logger, string verifier)
    {
        if (logger is not null)
        {
            NothingToVerify(logger, verifier, null);
        }
    }

    private static void LogVerifierFailed(ILogger? logger, string verifier, Exception exception)
    {
        if (logger is not null)
        {
            VerifierFailed(logger, verifier, exception);
        }
    }

    private static readonly Action<ILogger, string, string, int, Exception?> Verified =
        LoggerMessage.Define<string, string, int>(
            LogLevel.Information,
            new EventId(1, nameof(Verified)),
            "Verificador {Verifier} {Result} (exit {ExitCode}).");

    private static readonly Action<ILogger, string, Exception?> NothingToVerify =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(2, nameof(NothingToVerify)),
            "Verificador {Verifier} aplicável ao perfil não encontrou o que verificar na entrega; " +
            "a evidência fica AUSENTE e o portão a lê como reprovação.");

    private static readonly Action<ILogger, string, Exception?> VerifierFailed =
        LoggerMessage.Define<string>(
            LogLevel.Error,
            new EventId(3, nameof(VerifierFailed)),
            "Verificador {Verifier} lançou exceção; registrado como evidência negativa.");
}
