namespace Harness.Persistence.Abstractions.Workflows;

/// <summary>
/// Catálogo GLOBAL (dado do produto) dos templates de documento/card do playbook (§7),
/// semeado pela migration 0082: id estável, nome, fase da esteira, tipo de card alvo e campos
/// obrigatórios. O conteúdo integral de cada template é derivado na primeira execução real de
/// projeto — aqui vive somente a estrutura canônica. Somente leitura em runtime: o catálogo
/// muda por migração, nunca por endpoint.
/// </summary>
public interface IWorkflowDocumentTemplateStore
{
    Task<IReadOnlyList<WorkflowDocumentTemplateRecord>> ListAsync(
        CancellationToken cancellationToken = default);
}

/// <param name="MetricFormatsJson">
/// Mapa campo → formato exigido, para os campos que carregam MÉTRICA. Vazio (<c>{}</c>) quando o
/// template não tem nenhuma. "Lead time" ora em horas, ora em dias, ora em "rápido" não é métrica:
/// é opinião com número.
/// </param>
/// <param name="Guidance">
/// O que o documento precisa PROVAR. Não é prosa de exemplo — que o agente copiaria — e sim o
/// critério que separa o documento pronto do documento preenchido.
/// </param>
public sealed record WorkflowDocumentTemplateRecord(
    string Code,
    string Name,
    string Phase,
    string TargetCardType,
    string RequiredFieldsJson,
    string MetricFormatsJson,
    string Guidance);
