using Harness.Modules.Workflows.Product;
using Harness.Persistence.Abstractions.Product;
using Harness.SharedKernel.Time;

namespace Harness.Host.Product;

/// <summary>
/// Motivos que este avaliador registra no portão. São códigos fechados porque viram achado
/// impeditivo e precisam ser explicáveis ao dono sem leitura de log.
/// </summary>
public static class ProductDeliveryFailures
{
    /// <summary>A Arquitetura terminou sem materializar o perfil efetivo do projeto.</summary>
    public const string ProfileMissing = "product:effective_profile_missing";

    /// <summary>O projeto tem perfil, mas a entrega não satisfaz a Definition of Done do produto.</summary>
    public const string DeliveryIncomplete = "product:definition_of_done_failed";

    /// <summary>
    /// O perfil existe mas não decidiu a modalidade: ninguém definiu que produto é este, e sem isso
    /// "pronto" não tem critério.
    /// </summary>
    public const string ProfileUndecided = "product:effective_profile_modality_unresolved";

    /// <summary>
    /// Projeto anterior ao baseline de produto: o portão do produto não incide, e o fato fica
    /// registrado. Compatibilidade legada precisa ter nome, não ser silêncio.
    /// </summary>
    public const string LegacyProjectExempt = "product:legacy_project_not_governed";
}

/// <summary>
/// Liga a execução ao portão do produto.
///
/// Responde a duas perguntas que o driver de fases não sabia fazer: a Arquitetura produziu o perfil
/// efetivo? e a entrega satisfaz o que esse perfil exige? A segunda usa evidência CONSTATADA da
/// árvore entregue, não o que o executor escreveu.
///
/// <b>Compatibilidade explícita.</b> Um projeto sem perfil persistido é anterior a este baseline: o
/// portão do produto não incide sobre ele, e isso vira um registro nomeado
/// (<see cref="ProductDeliveryFailures.LegacyProjectExempt"/>), nunca um `return Ready` escondido.
/// A janela fecha sozinha porque a Fase 3 passa a exigir o perfil de todo projeto novo.
/// </summary>
public sealed class ProductDeliveryEvaluator(
    IProjectEffectiveProfileStore profiles,
    IClock clock,
    ILogger<ProductDeliveryEvaluator>? logger = null,
    ProductVerificationRunner? verifications = null,
    ProfileDirectiveExtractor? directives = null,
    IProductEvidenceSetStore? evidenceSets = null,
    Harness.Persistence.Abstractions.Governance.IGovernanceRuntimeStore? governance = null)
{
    private readonly ProductEvidenceCollectorPipeline _pipeline = new();

    /// <summary>Ordem da fase de Arquitetura no playbook padrão.</summary>
    public const int ArchitecturePhaseOrder = 3;

    /// <summary>A partir da fase de Desenvolvimento, a entrega passa a ser cobrada.</summary>
    public const int DevelopmentPhaseOrder = 5;

    public sealed record Outcome(
        ProductDeliveryVerdict? Verdict,
        string? Failure,
        ProjectEffectiveProfileRecord? Profile,
        ProductDeliveryEvidence? Evidence = null,
        ProductVerificationPlan? Plan = null,
        string? EvidenceSetId = null,

        /// <summary>
        /// O que mudou desde a avaliação anterior deste projeto. PURAMENTE observável: nada no
        /// portão, no escalonamento ou no replanejamento lê este campo, e é assim de propósito —
        /// a política de "sem progresso" precisa ser desenhada sobre comportamento real observado,
        /// não sobre a intuição de quem escreveu o medidor.
        /// </summary>
        ProgressDelta? Progress = null);

    public async Task<Outcome> EvaluateAsync(
        string tenantId,
        string projectId,
        string? repositoryRoot,
        string commitSha,
        int phaseOrder,
        CancellationToken cancellationToken,
        string? demandText = null,
        string? attemptId = null,
        string? cardId = null)
    {
        var record = await profiles.GetCurrentAsync(tenantId, projectId, cancellationToken);

        // Fase 3 MATERIALIZA o perfil. É o artefato da Arquitetura: a partir daqui, "aderência ao
        // constraint profile" deixa de cobrar o inexistente. A resolução é determinística e
        // idempotente por conteúdo — rodar o ciclo dez vezes não cria dez versões.
        if (record is null && phaseOrder >= ArchitecturePhaseOrder &&
            !string.IsNullOrWhiteSpace(demandText))
        {
            // As decisões HUMANAS entram aqui: requisito escrito pelo usuário e ADR aprovado, lidos
            // das fontes canônicas onde já vivem. Sem esta extração o resolvedor recebia listas
            // vazias e o perfil era sempre o baseline, por mais que alguém tivesse decidido outra
            // coisa.
            var extraction = directives is null
                ? null
                : await directives.ExtractAsync(tenantId, projectId, cancellationToken);

            var materialized = await MaterializeAsync(
                tenantId, projectId,
                string.IsNullOrWhiteSpace(extraction?.DemandText) ? demandText : extraction.DemandText,
                extraction?.Directives ?? [],
                extraction?.ActiveAdrs ?? [],
                "workflow-phase-driver", cancellationToken);
            record = materialized.Profile;
            LogProfileMaterialized(logger, projectId, record.Version, record.Modality);
        }

        if (record is null)
        {
            // A janela legada, explícita: um projeto que chegou às fases executivas sem perfil e
            // sem demanda de onde resolvê-lo é anterior a este baseline. O portão do produto não
            // incide sobre ele, e o fato vira registro NOMEADO — nunca um `return Ready` escondido.
            // Ela fecha sozinha: todo projeto novo materializa o perfil na Arquitetura.
            if (phaseOrder >= DevelopmentPhaseOrder)
            {
                LogLegacyProjectExempt(logger, projectId);
                return new Outcome(null, ProductDeliveryFailures.LegacyProjectExempt, null);
            }

            // Projeto governado que chegou à Arquitetura sem perfil: o gate não fecha.
            if (phaseOrder >= ArchitecturePhaseOrder)
            {
                return new Outcome(null, ProductDeliveryFailures.ProfileMissing, null);
            }
        }

        // Perfil resolvido com modalidade indefinida é perfil que não decidiu nada: ninguém sabe
        // que produto é este, e "pronto" fica sem critério. O gate da Arquitetura reprova.
        if (record is not null &&
            string.Equals(record.Modality, nameof(ProductModality.Unspecified), StringComparison.Ordinal))
        {
            return new Outcome(null, ProductDeliveryFailures.ProfileUndecided, record);
        }

        if (phaseOrder < DevelopmentPhaseOrder)
        {
            return new Outcome(null, null, record);
        }

        var profile = ProjectEffectiveProfile.FromJson(record!.ProfileJson);
        if (profile is null)
        {
            // Perfil ilegível é fail-closed: não sabemos o que o projeto decidiu, então não temos
            // como afirmar que a entrega atende à decisão.
            return new Outcome(null, ProductDeliveryFailures.ProfileMissing, record);
        }

        if (string.IsNullOrWhiteSpace(repositoryRoot) || !Directory.Exists(repositoryRoot))
        {
            // Sem árvore para inspecionar não há como constatar nada — e "não deu para verificar"
            // nunca vira "está tudo certo".
            return new Outcome(
                ProductDeliveryGate.Evaluate(profile, [], commitSha),
                ProductDeliveryFailures.DeliveryIncomplete,
                record);
        }

        // O PLANO é derivado do perfil e auditável: dá para responder por que este projeto teve o
        // build de frontend verificado e aquele não.
        var plan = ProductVerificationPlan.From(
            profile, verifications?.NativeVerifiers, verifications?.ProjectControlledVerifiers);

        // VERIFICAÇÃO REAL. É aqui que o Poseidon deixa de acreditar e passa a constatar: os
        // verificadores executam build, testes e contrato dentro da worktree, com allowlist de
        // executável e teto de tempo, e devolvem comando, código de saída e commit.
        IReadOnlyList<ProductVerificationRecord> verified = [];
        if (verifications is not null)
        {
            verified = await verifications.RunAsync(
                new ProductVerificationContext(
                    profile, repositoryRoot, commitSha, projectId, attemptId, cardId),
                plan,
                cancellationToken);
        }

        var workspace = new FileSystemProductWorkspace(repositoryRoot, commitSha);
        var evidence = _pipeline.Collect(profile, workspace, verified);
        var verdict = ProductDeliveryGate.Evaluate(profile, evidence.Items, commitSha, plan);
        LogProductVerdict(logger, projectId, record.Version, verdict.Summary(), evidence.Summary());

        // LEDGER: o conjunto que decidiu este portão fica gravado, amarrado ao commit e à versão
        // do perfil. Sem isso, "quais evidências fizeram este projeto passar?" não tem resposta
        // depois que o ciclo termina — e a tentativa que reprovou desaparece quando a seguinte
        // passa, apagando o que a fábrica precisa para aprender.
        // TELEMETRIA DE PROGRESSO, medida antes de gravar esta avaliação — a comparação é com a
        // ANTERIOR, e gravar primeiro faria o conjunto novo comparar-se consigo mesmo.
        var progress = await MeasureProgressAsync(tenantId, projectId, verdict, cancellationToken);
        LogProgress(logger, projectId, progress.Summary());

        var evidenceSetId = await PersistEvidenceAsync(
            tenantId, projectId, record, plan, evidence, verdict, commitSha, attemptId, cardId,
            cancellationToken);

        // PONTE RECIBO → EVIDÊNCIA. O recibo do turno nasceu antes de existir portão para decidir;
        // é aqui, com a decisão tomada e o conjunto gravado, que ele passa a apontar para a prova.
        // Sem este passo, `EvidenceSetId` continuaria nulo para sempre e a trilha de auditoria
        // pararia no recibo — que é onde ela parava até agora.
        await LinkReceiptAsync(
            tenantId, projectId, attemptId, evidenceSetId, commitSha, verdict, cancellationToken);

        return new Outcome(
            verdict,
            verdict.Satisfied ? null : ProductDeliveryFailures.DeliveryIncomplete,
            record,
            evidence,
            plan,
            evidenceSetId,
            progress);
    }

    private static readonly System.Text.Json.JsonSerializerOptions TelemetryJson =
        new(System.Text.Json.JsonSerializerDefaults.Web)
        {
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        };

    /// <summary>
    /// Compara esta avaliação com a anterior DO MESMO PROJETO, lida do ledger. É o que permite
    /// distinguir 8 → 5 → 2 de 8 → 8 → 8 sem reconstruir nada à mão: a comparação é derivada dos
    /// achados já gravados, com a mesma chave estável (tipo + natureza da lacuna).
    ///
    /// Falha de leitura devolve a linha de base em vez de derrubar a avaliação: perder uma medição
    /// é ruim, travar o portão por causa de um medidor é inaceitável.
    /// </summary>
    private async Task<ProgressDelta> MeasureProgressAsync(
        string tenantId,
        string projectId,
        ProductDeliveryVerdict verdict,
        CancellationToken cancellationToken)
    {
        if (evidenceSets is null)
        {
            return ProgressTelemetry.Compare(null, verdict.Findings);
        }

        try
        {
            var previous = await evidenceSets.ListAsync(tenantId, projectId, 1, cancellationToken);
            var findings = previous.Count == 0
                ? null
                : System.Text.Json.JsonSerializer.Deserialize<ProductEvidenceFinding[]>(
                    previous[0].FindingsJson, TelemetryJson);
            return ProgressTelemetry.Compare(findings, verdict.Findings);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogProgressUnavailable(logger, projectId, exception);
            return ProgressTelemetry.Compare(null, verdict.Findings);
        }
    }

    private static void LogProgress(ILogger? logger, string projectId, string summary)
    {
        if (logger is not null)
        {
            Progress(logger, projectId, summary, null);
        }
    }

    private static void LogProgressUnavailable(ILogger? logger, string projectId, Exception exception)
    {
        if (logger is not null)
        {
            ProgressUnavailable(logger, projectId, exception);
        }
    }

    private static readonly Action<ILogger, string, string, Exception?> Progress =
        LoggerMessage.Define<string, string>(
            LogLevel.Information,
            new EventId(7, nameof(Progress)),
            "Progresso do projeto {ProjectId} desde a avaliação anterior: {Delta}");

    private static readonly Action<ILogger, string, Exception?> ProgressUnavailable =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(8, nameof(ProgressUnavailable)),
            "Não foi possível medir o progresso do projeto {ProjectId}; a avaliação seguiu sem a " +
            "métrica, que é observabilidade e nunca decide nada.");

    private static readonly System.Text.Json.JsonSerializerOptions LedgerJson =
        new(System.Text.Json.JsonSerializerDefaults.Web)
        {
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        };

    /// <summary>
    /// Grava o conjunto e devolve o id. Falha de auditoria nunca impede a DECISÃO do portão —
    /// perder o registro é ruim, travar a fábrica por causa dele é pior —, mas ela é logada.
    /// </summary>
    private async Task<string?> PersistEvidenceAsync(
        string tenantId,
        string projectId,
        ProjectEffectiveProfileRecord record,
        ProductVerificationPlan plan,
        ProductDeliveryEvidence evidence,
        ProductDeliveryVerdict verdict,
        string commitSha,
        string? attemptId,
        string? cardId,
        CancellationToken cancellationToken)
    {
        if (evidenceSets is null)
        {
            return null;
        }

        var id = Harness.SharedKernel.Identifiers.UlidValue.New(clock.UtcNow).ToString();
        try
        {
            await evidenceSets.AppendAsync(
                new ProductEvidenceSetRecord(
                    tenantId, id, projectId, null, cardId, attemptId, commitSha,
                    record.Version, record.Fingerprint, record.Modality,
                    verdict.Satisfied ? "satisfied" : "failed",
                    System.Text.Json.JsonSerializer.Serialize(plan.Steps, LedgerJson),
                    System.Text.Json.JsonSerializer.Serialize(evidence.Items, LedgerJson),
                    System.Text.Json.JsonSerializer.Serialize(verdict.Findings, LedgerJson),
                    string.Join(',', evidence.Collectors),
                    clock.UtcNow),
                cancellationToken);
            return id;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogEvidenceNotPersisted(logger, projectId, exception);
            return null;
        }
    }

    /// <summary>
    /// Liga os recibos da tentativa ao conjunto de evidências. Falhar aqui não desfaz a decisão do
    /// portão — como no ledger, perder o registro é ruim e travar a fábrica por causa dele é pior —,
    /// mas fica logado.
    /// </summary>
    private async Task LinkReceiptAsync(
        string tenantId,
        string projectId,
        string? attemptId,
        string? evidenceSetId,
        string commitSha,
        ProductDeliveryVerdict verdict,
        CancellationToken cancellationToken)
    {
        if (governance is null || evidenceSetId is null || string.IsNullOrWhiteSpace(attemptId))
        {
            return;
        }

        try
        {
            var linked = await governance.LinkEvidenceAsync(
                new Harness.Persistence.Abstractions.Governance.GovernanceReceiptEvidenceLinkCommand(
                    tenantId, attemptId, evidenceSetId, commitSha, verdict.Summary(), clock.UtcNow),
                cancellationToken);
            LogReceiptLinked(logger, projectId, linked, evidenceSetId);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogReceiptNotLinked(logger, projectId, exception);
        }
    }

    private static void LogReceiptLinked(ILogger? logger, string projectId, int linked, string evidenceSetId)
    {
        if (logger is not null)
        {
            ReceiptLinked(logger, projectId, linked, evidenceSetId, null);
        }
    }

    private static void LogReceiptNotLinked(ILogger? logger, string projectId, Exception exception)
    {
        if (logger is not null)
        {
            ReceiptNotLinked(logger, projectId, exception);
        }
    }

    private static readonly Action<ILogger, string, int, string, Exception?> ReceiptLinked =
        LoggerMessage.Define<string, int, string>(
            LogLevel.Information,
            new EventId(5, nameof(ReceiptLinked)),
            "Projeto {ProjectId}: {Linked} recibo(s) passaram a apontar para o conjunto de " +
            "evidências {EvidenceSetId}.");

    private static readonly Action<ILogger, string, Exception?> ReceiptNotLinked =
        LoggerMessage.Define<string>(
            LogLevel.Error,
            new EventId(6, nameof(ReceiptNotLinked)),
            "Não foi possível ligar os recibos do projeto {ProjectId} ao conjunto de evidências; a " +
            "decisão do portão seguiu, mas a trilha de auditoria ficou interrompida no recibo.");

    /// <summary>
    /// Resolve e PERSISTE o perfil efetivo do projeto. Idempotente por conteúdo: resolver duas
    /// vezes a mesma coisa não cria versão nova, e mudar uma decisão cria.
    /// </summary>
    public async Task<ProjectEffectiveProfileSaveResult> MaterializeAsync(
        string tenantId,
        string projectId,
        string demandText,
        IReadOnlyList<ProfileDirective> directives,
        IReadOnlyList<string> activeAdrs,
        string resolvedBy,
        CancellationToken cancellationToken)
    {
        var profile = EffectiveProfileResolver.Resolve(
            new EffectiveProfileInputs(projectId, demandText, directives, activeAdrs));

        return await profiles.SaveAsync(
            new ProjectEffectiveProfileSaveCommand(
                tenantId,
                projectId,
                profile.Fingerprint(),
                profile.BaselineVersion,
                profile.Modality.ToString(),
                profile.ToJson(),
                clock.UtcNow,
                resolvedBy),
            cancellationToken);
    }

    private static void LogLegacyProjectExempt(ILogger? logger, string projectId)
    {
        if (logger is not null)
        {
            LegacyExempt(logger, projectId, null);
        }
    }

    private static void LogProductVerdict(
        ILogger? logger, string projectId, int version, string verdict, string evidence)
    {
        if (logger is not null)
        {
            ProductVerdict(logger, projectId, version, verdict, evidence, null);
        }
    }

    private static void LogProfileMaterialized(
        ILogger? logger, string projectId, int version, string modality)
    {
        if (logger is not null)
        {
            ProfileMaterialized(logger, projectId, version, modality, null);
        }
    }

    private static readonly Action<ILogger, string, int, string, Exception?> ProfileMaterialized =
        LoggerMessage.Define<string, int, string>(
            LogLevel.Information,
            new EventId(3, nameof(ProfileMaterialized)),
            "Perfil efetivo do projeto {ProjectId} materializado na v{ProfileVersion} " +
            "(modalidade {Modality}).");

    private static void LogEvidenceNotPersisted(ILogger? logger, string projectId, Exception exception)
    {
        if (logger is not null)
        {
            EvidenceNotPersisted(logger, projectId, exception);
        }
    }

    private static readonly Action<ILogger, string, Exception?> EvidenceNotPersisted =
        LoggerMessage.Define<string>(
            LogLevel.Error,
            new EventId(4, nameof(EvidenceNotPersisted)),
            "Não foi possível gravar o conjunto de evidências do projeto {ProjectId}; a decisão do " +
            "portão seguiu, mas a prova não ficou auditável.");

    private static readonly Action<ILogger, string, Exception?> LegacyExempt =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(1, nameof(LegacyExempt)),
            "Projeto {ProjectId} não tem perfil efetivo persistido: o portão do produto NÃO incide " +
            "sobre ele (compatibilidade com projetos anteriores ao baseline de produto).");

    private static readonly Action<ILogger, string, int, string, string, Exception?> ProductVerdict =
        LoggerMessage.Define<string, int, string, string>(
            LogLevel.Information,
            new EventId(2, nameof(ProductVerdict)),
            "Portão do produto para {ProjectId} (perfil v{ProfileVersion}): {Verdict}; evidências: {Evidence}");
}
