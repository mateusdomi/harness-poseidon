using Harness.Modules.Workflows.Application;
using Harness.Persistence.Abstractions.Workflows;

namespace Harness.Host.Workflows;

/// <summary>
/// Resolve o MODO DE OPERAÇÃO efetivo de um projeto a partir de UMA fonte, com precedência
/// explícita.
///
/// Havia duas fontes para a mesma decisão e elas divergiam na prática:
///   * <c>workflow_bindings.operation_mode</c> — o que o dono altera pela tela
///     (<c>SetOperationModeAsync</c>, com aceite de risco registrado) e o que a esteira de fases
///     (<see cref="WorkflowPhaseDriver"/>) já lia;
///   * <c>projects.operation_mode</c> — escrito UMA vez, na criação do projeto, e nunca mais.
///     Nenhum caminho de escrita o atualiza depois.
///
/// Quem lesse o campo do projeto veria "autonomous" para sempre, mesmo depois de o dono ter posto
/// o projeto em manual na tela — o modo mudava na interface e o laço de fundo continuava
/// despachando. A precedência aqui é, portanto: <b>o vínculo manda</b>; o campo do projeto só
/// responde enquanto não existe vínculo, porque aí ele é a única declaração que existe.
/// </summary>
internal static class ProjectOperationModeResolver
{
    /// <summary>
    /// Modo efetivo do projeto. Falhas de leitura do catálogo NÃO são engolidas em silêncio:
    /// propagam para quem chamou decidir — um erro de banco não pode ser lido como "autônomo".
    /// </summary>
    public static async Task<ProjectOperationMode> ResolveAsync(
        IWorkflowCatalogStore catalog,
        string tenantId,
        string projectId,
        string? projectOperationMode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        // Um projeto tem NO MÁXIMO um vínculo: `LinkTemplateAsync` recusa o segundo com
        // `WorkflowBindingAlreadyExistsException`. Por isso `limit: 1` e o primeiro item — não é
        // "pegar qualquer um de vários", é ler o único que pode existir.
        var bindings = await catalog.ListBindingsAsync(tenantId, projectId, null, 1, cancellationToken);
        return bindings.Count > 0
            ? PhaseGatePolicy.ParseMode(bindings[0].OperationMode)
            : PhaseGatePolicy.ParseMode(projectOperationMode);
    }
}
