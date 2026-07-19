using Harness.Modules.Workflows.Contracts;

namespace Harness.Host.Workflows;

public sealed record CanonicalWorkflowTemplate(
    string Key,
    string Name,
    string Description,
    IReadOnlyList<string> Phases,
    IReadOnlyDictionary<string, IReadOnlyList<string>> GatesByPhase)
{
    public CreateWorkflowTemplateRequest ToRequest() => new(
        Name,
        Description,
        Phases,
        GatesByPhase,
        "Template canônico semeado pela plataforma.");
}

public static class CanonicalWorkflowTemplates
{
    private static CanonicalWorkflowTemplate Build(
        string key,
        string name,
        string description,
        IReadOnlyList<string> phases,
        params string[] gatedPhases) => new(
        key,
        name,
        description,
        phases,
        gatedPhases.ToDictionary(
            phase => phase,
            phase => (IReadOnlyList<string>)[$"Aprovação de {phase}"],
            StringComparer.Ordinal));

    public static IReadOnlyList<CanonicalWorkflowTemplate> All { get; } =
    [
        Build(
            "delivery-standard",
            "Entrega padrão (Triagem → Sustentação)",
            "Fluxo canônico completo: triagem da solicitação, análise de requisitos, arquitetura, " +
            "implementação em fatias verificáveis, verificação independente, homologação humana e sustentação.",
            ["Triagem", "Análise", "Arquitetura", "Implementação", "Verificação", "Homologação", "Sustentação"],
            "Análise", "Arquitetura", "Implementação", "Verificação", "Homologação"),
        Build(
            "variant-new-project",
            "Variante: projeto novo",
            "Nascimento de produto: descoberta de escopo, fundação técnica, incrementos de MVP, " +
            "verificação e homologação antes da sustentação.",
            ["Triagem", "Descoberta", "Fundação", "Incrementos", "Verificação", "Homologação", "Sustentação"],
            "Fundação", "Incrementos", "Verificação", "Homologação"),
        Build(
            "variant-bug",
            "Variante: correção de bug",
            "Correção dirigida por reprodução: triagem, reprodução determinística, correção mínima, " +
            "verificação de regressão e sustentação.",
            ["Triagem", "Reprodução", "Correção", "Verificação de regressão", "Sustentação"],
            "Reprodução", "Correção", "Verificação de regressão"),
        Build(
            "variant-evolution",
            "Variante: evolução de funcionalidade",
            "Evolução sobre comportamento existente: análise de impacto, implementação compatível, " +
            "verificação e homologação.",
            ["Triagem", "Análise de impacto", "Implementação", "Verificação", "Homologação", "Sustentação"],
            "Análise de impacto", "Implementação", "Verificação", "Homologação"),
        Build(
            "variant-legacy",
            "Variante: sistema legado",
            "Intervenção em legado: caracterização do comportamento atual, rede de testes de segurança, " +
            "mudança incremental protegida e verificação.",
            ["Triagem", "Caracterização", "Rede de testes", "Mudança protegida", "Verificação", "Sustentação"],
            "Caracterização", "Rede de testes", "Mudança protegida", "Verificação"),
        Build(
            "variant-reverse-engineering",
            "Variante: engenharia reversa",
            "Reconstrução de conhecimento: descoberta de artefatos, mapeamento de comportamento, " +
            "documentação viva e validação com stakeholders.",
            ["Triagem", "Descoberta", "Mapeamento", "Documentação", "Validação", "Sustentação"],
            "Mapeamento", "Documentação", "Validação"),
    ];
}
