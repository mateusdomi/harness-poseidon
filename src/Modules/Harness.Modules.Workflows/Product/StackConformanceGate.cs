namespace Harness.Modules.Workflows.Product;

/// <summary>
/// Veredito de conformidade de STACK: a árvore entregue é feita da tecnologia que o perfil
/// efetivo decidiu?
/// </summary>
public sealed record StackConformanceVerdict(
    bool Satisfied,
    IReadOnlyList<ProductEvidence> Violations)
{
    /// <summary>Resumo estável para log, rationale de review e auditoria.</summary>
    public string Summary() => Satisfied
        ? "stack_conformant"
        : "stack_nonconformant:" + string.Join(
            ',', Violations.Select(violation => violation.Kind.ToString().ToLowerInvariant()));
}

/// <summary>
/// O gate que impede a lição mais cara da avaliação TrensRJ de se repetir: o intake resolveu
/// React + .NET + Oracle com proveniência, a execução entregou Node com dados em memória, e
/// QUATRO reviews independentes aprovaram — porque todos olhavam o diff, e nenhum confrontava a
/// árvore entregue com o perfil efetivo.
///
/// Este gate é o subconjunto de EXISTÊNCIA E FORMA do <see cref="ProductDeliveryGate"/> — os
/// fatos que se constatam olhando a árvore, sem executar nada (custo ~zero, sem tokens). Build,
/// integração e E2E continuam sendo responsabilidade da validação de produto ao fim do objetivo;
/// aqui o objetivo é matar o desvio de stack no PRIMEIRO merge, não no terceiro dia.
///
/// PURO e Default-FAIL: evidência exigida ausente reprova igual a evidência reprovada.
/// </summary>
public static class StackConformanceGate
{
    /// <summary>
    /// Os fatos de existência/forma cobrados no merge. Deliberadamente NÃO inclui os kinds de
    /// execução (build, testes, E2E, persistência verificada): cobrá-los a cada merge exigiria
    /// rodar o produto dentro do review — esse é o trabalho do Product Validator, no fim do
    /// objetivo.
    /// </summary>
    private static readonly ProductEvidenceKind[] FormKinds =
    [
        ProductEvidenceKind.BackendPresent,
        ProductEvidenceKind.FrontendPresent,
        ProductEvidenceKind.ApiPresent,
        ProductEvidenceKind.DatabaseMigrationValidated,
        ProductEvidenceKind.DataAccessDeclared,
    ];

    public static StackConformanceVerdict Evaluate(
        ProjectEffectiveProfile profile,
        IReadOnlyList<ProductEvidence> observed)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(observed);

        var required = ProductDeliveryRequirements.For(profile)
            .Where(FormKinds.Contains)
            .ToArray();

        var violations = new List<ProductEvidence>();
        foreach (var kind in required)
        {
            var evidence = observed.FirstOrDefault(item => item.Kind == kind && !item.Satisfied)
                ?? observed.FirstOrDefault(item => item.Kind == kind);
            if (evidence is null)
            {
                violations.Add(new ProductEvidence(
                    kind, false,
                    $"O perfil efetivo exige {kind} e a inspeção não produziu constatação alguma."));
                continue;
            }

            if (!evidence.Satisfied)
            {
                violations.Add(evidence);
            }
        }

        return new StackConformanceVerdict(violations.Count == 0, violations);
    }
}
