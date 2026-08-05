using System.Text.RegularExpressions;
using Harness.Modules.Agents.Application.Execution;
using Harness.Modules.Workflows.Product.Graph;
using Harness.SharedKernel.Graph;

namespace Harness.UnitTests.Graph;

/// <summary>
/// Onda 4.3 — o dry-run do Prisma: a especificação REAL (v3.0) vira grafo ANTES de o projeto
/// rodar. Um nó Requirement por critério de aceite do §16 (T1..T26), o fato humano do ambiente
/// (banco corporativo Oracle 19c) e as restrições travadas (🔒) da metodologia como Constraint.
///
/// O que isto compra: no dia em que a Bruna despachar o primeiro card do Prisma, a cobertura do
/// §16 já é uma PERGUNTA RESPONDÍVEL (quantos critérios têm card vivo que os implementa?) — e
/// hoje a resposta correta é 0/26, porque o projeto está pausado. O relatório derivado vive em
/// docs/architecture/preflight/prisma-graph-dry-run.md.
/// </summary>
public sealed partial class PrismaDryRunTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);
    private const string PrismaProjectId = "01KZ7ZNTXRRQMMA9G1TQ2H5BFW";

    [GeneratedRegex(@"- \*\*T(\d+)\*\*\s*(.+)")]
    private static partial Regex CriterionPattern();

    private static readonly Lazy<string> Spec = new(() => File.ReadAllText(
        Path.Combine(
            FindRepositoryRoot(),
            "docs", "architecture", "preflight", "prisma-especificacao-mvp-v3.0.md")));

    [Fact]
    public void OsVinteESeisCriteriosDoParagrafo16ViramNosDeRequisito()
    {
        var snapshot = BuildDryRunSnapshot();
        var projection = ProjectGraphProjector.Project(snapshot, Now);

        var criteria = projection.Nodes
            .Where(node => node.Type == GraphNodeType.Requirement)
            .ToArray();
        Assert.Equal(26, criteria.Length);
        Assert.Contains(criteria, node =>
            node.CanonicalSourceId == "prisma-t14" &&
            node.Title.Contains("ITRC = 50,0%", StringComparison.Ordinal));
    }

    [Fact]
    public void ACoberturaDoParagrafo16EUmaPerguntaRespondivelEHojeEZero()
    {
        var snapshot = BuildDryRunSnapshot();
        var projection = ProjectGraphProjector.Project(snapshot, Now);

        // Cobertura = critérios com card VIVO que os implementa. O Prisma está pausado, sem
        // cards: a resposta honesta é 0/26 — e é o grafo quem a dá, não uma planilha.
        var covered = projection.Nodes
            .Where(node => node.Type == GraphNodeType.Requirement)
            .Count(requirement => projection.Edges.Any(edge =>
                edge.RelationType == GraphRelationType.Implements &&
                string.Equals(edge.ToNodeId, requirement.Id, StringComparison.Ordinal)));

        Assert.Equal(0, covered);
    }

    [Fact]
    public void OsCriteriosDeCalculoSaoRestringidosPelaMetodologiaTravada()
    {
        var snapshot = BuildDryRunSnapshot();
        var projection = ProjectGraphProjector.Project(snapshot, Now);

        // T1..T8 (criticidade/residual) são constrained_by a metodologia 🔒 (§5); T9..T15 pela
        // fórmula do ITRC 🔒 (§6). Uma mudança nessas seções da spec propagaria para os
        // critérios — exatamente o mecanismo provado nas Provas A e Eval 1.
        var methodology = GraphNode.DeterministicId(GraphNodeType.Constraint, "prisma-metodologia-secao5");
        var itrc = GraphNode.DeterministicId(GraphNodeType.Constraint, "prisma-itrc-secao6");
        var impactedByMethodology = ImpactAnalysisService.Analyze(
            projection.Nodes, projection.Edges, methodology);
        var impactedByItrc = ImpactAnalysisService.Analyze(
            projection.Nodes, projection.Edges, itrc);

        Assert.Equal(8, impactedByMethodology.Count);
        Assert.Equal(7, impactedByItrc.Count);

        // E o fato humano do ambiente (Oracle 19c) existe como nó — quando os cards de
        // persistência nascerem constrained_by ele, a troca de banco propaga (Eval 1).
        Assert.Contains(projection.Nodes, node =>
            node.Type == GraphNodeType.HumanFact &&
            node.Title.Contains("Oracle", StringComparison.Ordinal));
    }

    /// <summary>
    /// O snapshot do dry-run, derivado da spec REAL: §16 lido pelo mesmo secionador que serve a
    /// Bruna (Onda 0.7) — se a spec mudar, este teste muda junto ou reprova.
    /// </summary>
    private static GraphSourceSnapshot BuildDryRunSnapshot()
    {
        var sections = AttachmentSectionizer.Split(Spec.Value);
        var section16 = AttachmentSectionizer.Find(sections, "16");
        Assert.NotNull(section16);

        var items = new List<GraphSourceItem>
        {
            new(GraphNodeType.HumanFact, "prisma-oracle-19c", "org_environment", 1,
                "O banco corporativo da TrensRJ é Oracle 19c"),
            new(GraphNodeType.Constraint, "prisma-metodologia-secao5", "spec_section", 1,
                "Metodologia de avaliação travada (spec §5, 🔒)"),
            new(GraphNodeType.Constraint, "prisma-itrc-secao6", "spec_section", 1,
                "Fórmula e imutabilidade do ITRC travadas (spec §6, 🔒)"),
        };
        var links = new List<GraphSourceLink>();

        foreach (Match match in CriterionPattern().Matches(section16.Content))
        {
            var number = int.Parse(
                match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            var sourceId = $"prisma-t{number.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            items.Add(new GraphSourceItem(
                GraphNodeType.Requirement, sourceId, "acceptance_criterion", 1,
                $"T{number.ToString(System.Globalization.CultureInfo.InvariantCulture)} — {match.Groups[2].Value.Trim()}"));

            // T1..T8: metodologia (§5). T9..T15: ITRC (§6). Os demais: cadeia/perfis/auditoria,
            // sem constraint travada.
            if (number <= 8)
            {
                links.Add(new GraphSourceLink(
                    GraphRelationType.ConstrainedBy,
                    GraphNodeType.Requirement, sourceId,
                    GraphNodeType.Constraint, "prisma-metodologia-secao5"));
            }
            else if (number <= 15)
            {
                links.Add(new GraphSourceLink(
                    GraphRelationType.ConstrainedBy,
                    GraphNodeType.Requirement, sourceId,
                    GraphNodeType.Constraint, "prisma-itrc-secao6"));
            }
        }

        return new GraphSourceSnapshot(PrismaProjectId, items, links);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "governance", "manifest.yaml")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
