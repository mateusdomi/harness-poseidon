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
    /// <summary>
    /// Os tipos para os quais existe verificador cujo CONTEÚDO o Poseidon controla. É esta lista
    /// que eleva a barra de um requisito: registrar um verificador nativo faz o script do produto
    /// deixar de bastar para aquele tipo, sem tocar no portão nem no plano.
    /// </summary>
    public IReadOnlyDictionary<ProductEvidenceKind, string> NativeVerifiers { get; } =
        verifiers
            .Where(verifier => verifier is not ScriptedProductVerifier)
            .SelectMany(verifier => verifier.DerivedKinds
                .Prepend(verifier.Kind)
                .Select(kind => (Kind: kind, verifier.Name)))
            .GroupBy(entry => entry.Kind)
            .ToDictionary(group => group.Key, group => group.First().Name);

    /// <summary>
    /// Os tipos cobertos por verificador cujo conteúdo o PRODUTO define. Entram no plano para
    /// distinguir "requisito sem prova forte" de "requisito sem nenhum produtor" — que é a
    /// distinção do §16.
    /// </summary>
    public IReadOnlyDictionary<ProductEvidenceKind, string> ProjectControlledVerifiers { get; } =
        verifiers
            .Where(verifier => verifier is ScriptedProductVerifier)
            .GroupBy(verifier => verifier.Kind)
            .ToDictionary(group => group.Key, group => group.First().Name);

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

            // Existindo verificador nativo para o tipo, o do script SAI DE CENA. Deixar os dois
            // rodando faria a ausência de um script convencionado reprovar uma entrega cujo
            // requisito o Poseidon acabou de provar por conta própria — e o portão, que trata
            // qualquer reprovação como definitiva, não teria como distinguir as duas.
            if (verifier is ScriptedProductVerifier && NativeVerifiers.ContainsKey(verifier.Kind))
            {
                LogSupersededByNative(logger, verifier.Name, NativeVerifiers[verifier.Kind]);
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

    private static void LogSupersededByNative(ILogger? logger, string scripted, string native)
    {
        if (logger is not null)
        {
            SupersededByNative(logger, scripted, native, null);
        }
    }

    private static readonly Action<ILogger, string, string, Exception?> SupersededByNative =
        LoggerMessage.Define<string, string>(
            LogLevel.Debug,
            new EventId(4, nameof(SupersededByNative)),
            "Verificador por script {Scripted} não roda: {Native} prova o mesmo requisito com " +
            "conteúdo controlado pelo Poseidon.");

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
