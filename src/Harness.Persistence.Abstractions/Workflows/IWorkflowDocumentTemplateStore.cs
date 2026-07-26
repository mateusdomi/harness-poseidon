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

public sealed record WorkflowDocumentTemplateRecord(
    string Code,
    string Name,
    string Phase,
    string TargetCardType,
    string RequiredFieldsJson);
