using Harness.Modules.Workflows.Contracts;

namespace Harness.Host.Workflows;

public sealed record CanonicalWorkflowTemplate(
    string Key,
    string Name,
    string Description,
    IReadOnlyList<string> Phases,
    IReadOnlyDictionary<string, IReadOnlyList<string>> GatesByPhase,
    IReadOnlyDictionary<string, IReadOnlyList<string>> DocumentsByPhase)
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
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> NoDocuments =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

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
            StringComparer.Ordinal),
        NoDocuments);

    /// <summary>
    /// A chave canônica do template recomendado (o "Software Delivery Standard"): o fluxo de
    /// entrega padrão completo, pré-selecionado na criação de um projeto para não bloquear o
    /// caminho dourado do Chief em um workflow ausente.
    /// </summary>
    public const string RecommendedKey = "delivery-standard";

    /// <summary>
    /// DEL-07 — a chave canônica do workflow de ENTREGA TÉCNICA de 11 fases (Recebimento → Revisão de
    /// benefícios), com portões (gates) e documentos obrigatórios por fase. Reusa o módulo de Workflows
    /// (GP-09): é um template canônico publicado, não um novo motor.
    /// </summary>
    public const string TechnicalDeliveryKey = "delivery-technical";

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
        TechnicalDelivery(),
    ];

    /// <summary>
    /// O template recomendado, pré-selecionado na criação do projeto quando nenhum override é
    /// informado.
    /// </summary>
    public static CanonicalWorkflowTemplate Recommended { get; } =
        All.Single(template => template.Key == RecommendedKey);

    /// <summary>DEL-07 — o workflow de entrega técnica de 11 fases.</summary>
    public static CanonicalWorkflowTemplate TechnicalDeliveryTemplate { get; } =
        All.Single(template => template.Key == TechnicalDeliveryKey);

    // DEL-07 — as 11 fases da entrega técnica, com portões e documentos obrigatórios por fase. Cada
    // documento vira um objetivo de fase de tipo 'document' (semeado pelo WorkflowTemplateSeeder); cada
    // portão vira um objetivo/gate 'gate'. Todas as fases carregam ao menos um documento obrigatório e
    // as fases de decisão/prontidão carregam um portão de aprovação.
    private static CanonicalWorkflowTemplate TechnicalDelivery()
    {
        var phases = new[]
        {
            "Recebimento", "Baseline", "Planejamento", "Execução acompanhada", "Prontidão homolog",
            "Homologação", "Prontidão prod", "Produção", "Estabilização", "Encerramento",
            "Revisão de benefícios",
        };

        var documents = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["Recebimento"] = ["Registro da solicitação", "Critérios de aceite"],
            ["Baseline"] = ["Baseline técnica", "Mapa de dependências"],
            ["Planejamento"] = ["Plano de entrega", "Plano de marcos"],
            ["Execução acompanhada"] = ["Registro de execução", "Log de decisões"],
            ["Prontidão homolog"] = ["Checklist de prontidão de homologação"],
            ["Homologação"] = ["Relatório de homologação"],
            ["Prontidão prod"] = ["Checklist de prontidão de produção", "Plano de rollback"],
            ["Produção"] = ["Runbook operacional", "Registro de implantação"],
            ["Estabilização"] = ["Relatório de estabilização"],
            ["Encerramento"] = ["Dossiê de encerramento"],
            ["Revisão de benefícios"] = ["Relatório de benefícios"],
        };

        var gatedPhases = new[]
        {
            "Baseline", "Planejamento", "Prontidão homolog", "Homologação", "Prontidão prod",
            "Produção", "Encerramento", "Revisão de benefícios",
        };

        var gates = gatedPhases.ToDictionary(
            phase => phase,
            phase => (IReadOnlyList<string>)[$"Aprovação de {phase}"],
            StringComparer.Ordinal);

        return new CanonicalWorkflowTemplate(
            TechnicalDeliveryKey,
            "Entrega técnica (Recebimento → Revisão de benefícios)",
            "Fluxo canônico de entrega técnica em 11 fases, com portões (gates) e documentos " +
            "obrigatórios por fase: recebimento, baseline, planejamento, execução acompanhada, " +
            "prontidão de homologação, homologação, prontidão de produção, produção, estabilização, " +
            "encerramento e revisão de benefícios.",
            phases,
            gates,
            documents);
    }
}
