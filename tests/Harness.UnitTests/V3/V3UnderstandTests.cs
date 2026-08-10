using Harness.Host.V3;
using Harness.Modules.Agents.Application.Accounts;
using Harness.Modules.Agents.Contracts;
using Harness.Persistence.Abstractions.Projects;

namespace Harness.UnitTests.V3;

public sealed class V3UnderstandTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"poseidon-v3-understand-{Guid.NewGuid():N}");

    [Fact]
    public void OpenQuestionPolicyRequiresOnlyDeadlineAndRepositoryBeforeAuthorization()
    {
        var questions = V3OpenQuestionPolicy.RequiredQuestions("01K00000000000000000000000", null, null);

        Assert.Equal(["deadline", "repository"], questions.Select(question => question.QuestionId));
    }

    [Fact]
    public void AnalyzeInfersLowRiskCapabilitiesButKeepsRequiredQuestions()
    {
        var now = DateTimeOffset.UnixEpoch;
        var context = Context(deadline: null, repository: null);

        var result = V3UnderstandAnalyzer.Analyze(context, new V3UnderstandAnalyzeRequest
        {
            OriginalIntent = "Sistema de riscos com aprovações, auditoria e dashboard.",
            Assumptions = ["React fornecido será preservado quando existir."],
        }, now);

        Assert.Equal("AWAITING_INPUT", result.State.LifecycleState);
        Assert.Contains("Gestão de riscos corporativos", result.State.Requirements);
        Assert.Contains("Fluxo de aprovação por perfis", result.State.Requirements);
        Assert.Equal(["deadline", "repository"], result.OpenQuestions.Select(question => question.QuestionId));
    }

    [Fact]
    public void AnalyzeAcceptsPersistedProjectRepositoryAsRepositoryDecision()
    {
        var now = DateTimeOffset.UnixEpoch;
        var context = Context(deadline: now.AddDays(10), repository: null) with
        {
            Repository = "/Users/mateus/.harness-poseidon/repositories/tenant/generated-project",
        };

        var result = V3UnderstandAnalyzer.Analyze(context, new V3UnderstandAnalyzeRequest(), now);

        Assert.Equal("READY_TO_START", result.State.LifecycleState);
        Assert.Empty(result.OpenQuestions);
        Assert.Equal("/Users/mateus/.harness-poseidon/repositories/tenant/generated-project", result.State.Repository);
    }

    [Fact]
    public void PrimaryRequirementFactsSuppressDeadlineQuestion()
    {
        var deadline = new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.Zero);
        var questions = V3OpenQuestionPolicy.RequiredQuestions(
            "01K00000000000000000000000",
            null,
            null,
            new V3RequirementSourceFacts(
                deadline,
                "PRIMARY_REQUIREMENTS:artifact-1:deadline",
                "Autenticação própria, sem SSO",
                "PRIMARY_REQUIREMENTS",
                "Notificações por e-mail/digest",
                "PRIMARY_REQUIREMENTS",
                "Oracle",
                "PRIMARY_REQUIREMENTS",
                "ITRC definido",
                "PRIMARY_REQUIREMENTS",
                26),
            [Coverage(complete: true)]);

        Assert.Equal(["repository"], questions.Select(question => question.QuestionId));
    }

    [Fact]
    public void PrimaryRequirementFactsExtractDatabaseForEffectiveStack()
    {
        var facts = V3RequirementFactsExtractor.Extract(
        [
            new V3SourceCoverage(
                "artifact-1",
                "requirements.md",
                "requirements_source",
                1,
                1,
                100,
                true,
                "full-text-read",
                120,
                "O backend deve usar Oracle como banco de dados local de desenvolvimento."),
        ]);
        var project = Project(description: "Projeto blind sem stack manual.", technologies: []);

        var stack = V3StackResolver.Resolve(project, state: null, [], facts);

        Assert.Equal("Oracle", facts.Database);
        Assert.Equal("PRIMARY_REQUIREMENTS", facts.DatabaseProvenance);
        Assert.Equal("Oracle", stack.Database);
    }

    [Fact]
    public void IncompletePrimaryRequirementsKeepUnderstandReadingWithoutQuestions()
    {
        var now = DateTimeOffset.UnixEpoch;
        var context = Context(deadline: null, repository: null) with
        {
            PrimaryRequirementsCoverage = [Coverage(complete: false)],
        };

        var result = V3UnderstandAnalyzer.Analyze(context, new V3UnderstandAnalyzeRequest(), now);

        Assert.Equal("UNDERSTANDING", result.State.LifecycleState);
        Assert.Equal("READING_PRIMARY_REQUIREMENTS", result.State.Status);
        Assert.Empty(result.OpenQuestions);
    }

    [Theory]
    [InlineData("sim")]
    [InlineData("pode iniciar")]
    [InlineData("autorizado")]
    [InlineData("comece")]
    public void AuthorizationAcceptsNaturalLanguageApproval(string response)
    {
        Assert.True(V3AuthorizationPolicy.IsAuthorized(response));
    }

    [Fact]
    public void MissionCompilerPersistsArtifactAndKnowledgeReferences()
    {
        var now = DateTimeOffset.UnixEpoch;
        var store = new V3UnderstandStore(_directory);
        var context = Context(deadline: now.AddDays(10), repository: "/tmp/prisma");
        var recommended = new V3RecommendedExecutor("worker-codex-project", "AVAILABLE", "AVAILABLE + WRITE_CAPABLE + role compatible.");

        var mission = V3MissionCompiler.CompileBuildMission(context, recommended, null, now);
        store.WriteMission(mission);
        var saved = store.ReadMission(mission.MissionId);

        Assert.NotNull(saved);
        Assert.Equal("COMPILED", saved!.Status);
        Assert.Contains(saved.ArtifactReferences, artifact => artifact.ArtifactId == "artifact-1");
        Assert.Contains(saved.KnowledgeReferences, reference => reference.Path == "docs/product/frontend-standards.md");
        Assert.Contains(saved.KnowledgeReferences, reference => reference.Path == "docs/product/oracle-data-standards.md");
        Assert.Contains(saved.PrimaryRequirementsCoverage, source => source.Complete);
        Assert.Contains("PrimaryRequirementsCoverage: 100%", saved.MissionText, StringComparison.Ordinal);
        Assert.Contains("leia integralmente", saved.MissionText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AUTONOMY CONTRACT", saved.MissionText, StringComparison.Ordinal);
        Assert.Contains("DEFINITION OF DONE", saved.MissionText, StringComparison.Ordinal);
    }

    [Fact]
    public void ExecutorPreviewChoosesUniqueAvailableWriteCapableAccount()
    {
        var selected = V3ExecutorPreview.Select(
        [
            Account("chief", [AgentRoles.ChiefOrchestrator], priority: 10),
            Account("worker-low", [AgentRoles.ProjectExecutor], priority: 10),
            Account("worker-high", [AgentRoles.ProjectExecutor, AgentRoles.FrontendSpecialist], priority: 90),
            Account("worker-disabled", [AgentRoles.ProjectExecutor], AgentAccountState.Disabled, priority: 100),
        ]);

        Assert.Equal("worker-high", selected.AccountAlias);
        Assert.Equal("AVAILABLE", selected.Status);
    }

    private static V3ProjectContextResponse Context(DateTimeOffset? deadline, string? repository) =>
        new(
            "01K00000000000000000000000",
            "V3-UNDERSTAND-PRISMA-REPLAY",
            "Sistema de riscos corporativos com React, .NET e Oracle.",
            [
                new V3ArtifactReference(
                    "artifact-1",
                    "Prisma_Especificacao_Tecnica_MVP.md",
                    "text/markdown",
                    "requirements",
                    "solicitation_attachment",
                    "tenant/artifact",
                    "ABC123",
                    "accepted"),
                new V3ArtifactReference(
                    "artifact-2",
                    "frontend-react.zip",
                    "application/zip",
                    "provided_frontend",
                    "solicitation_attachment",
                    "tenant/frontend",
                    "DEF456",
                    "accepted"),
            ],
            [],
            [],
            new V3ProjectUnderstandState(
                "01K00000000000000000000000",
                "AUTHORIZED",
                "BUILDING",
                "Sistema de riscos corporativos com React, .NET e Oracle.",
                "Construir o Prisma.",
                "Construir o Prisma.",
                ["GRC", "Diretor", "Gerente", "Coordenador", "Ponto Focal"],
                ["Gestão de riscos corporativos", "Fluxo de aprovação por perfis"],
                ["Login por perfis funciona", "Risco completo persiste"],
                [],
                ["Preservar frontend fornecido."],
                [],
                true,
                [],
                new V3EffectiveStackContract(
                    "React + TypeScript + Vite",
                    ".NET",
                    "Oracle",
                    "Clean Architecture / modular full-stack",
                    "Playwright + unit/integration tests",
                    ["restrição explícita do usuário/documento"]),
                deadline,
                repository,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch),
            "Construir o Prisma.",
            "Construir o Prisma.",
            ["Gestão de riscos corporativos", "Fluxo de aprovação por perfis"],
            ["Login por perfis funciona", "Risco completo persiste"],
            [],
            ["Preservar frontend fornecido."],
            [Coverage(complete: true)],
            new V3RequirementSourceFacts(
                deadline,
                deadline is null ? null : "PRIMARY_REQUIREMENTS:artifact-1:deadline",
                "Autenticação própria, sem SSO",
                "PRIMARY_REQUIREMENTS",
                "Notificações por e-mail/digest",
                "PRIMARY_REQUIREMENTS",
                "Oracle",
                "PRIMARY_REQUIREMENTS",
                "ITRC definido",
                "PRIMARY_REQUIREMENTS",
                26),
            V3OpenQuestionPolicy.RequiredQuestions("01K00000000000000000000000", deadline, repository),
            new V3EffectiveStackContract(
                "React + TypeScript + Vite",
                ".NET",
                "Oracle",
                "Clean Architecture / modular full-stack",
                "Playwright + unit/integration tests",
                ["restrição explícita do usuário/documento"]),
            deadline,
            repository,
            "host-api-and-frontend; database container only when stack requires it",
            "existing notification channels",
            new V3ExecutionCapacityResponse(DateTimeOffset.UnixEpoch, 1, 1, 1, 1, []),
            [],
            deadline is null || repository is null ? "AWAITING_INPUT" : "BUILDING");

    private static V3SourceCoverage Coverage(bool complete) =>
        new(
            "artifact-1",
            "Prisma_Especificacao_Tecnica_MVP.md",
            "requirements_source",
            complete ? 18 : 18,
            complete ? 18 : 3,
            complete ? 100 : 17,
            complete,
            complete ? "full-text-read" : "partial",
            33790,
            null);

    private static AgentAccountContract Account(
        string alias,
        IReadOnlyList<string> roles,
        AgentAccountState state = AgentAccountState.Available,
        int priority = 100) =>
        new(
            alias,
            "openai",
            ExecutorCatalog.Codex,
            $"keychain://poseidon/{alias}",
            $"confighome://{alias}",
            roles,
            [.. roles.SelectMany(AgentRoles.PathScopesFor).Distinct(StringComparer.Ordinal)],
            state,
            state == AgentAccountState.Available ? AgentAccountHealth.Healthy : AgentAccountHealth.Unknown,
            1,
            0,
            null,
            null,
            null,
            null,
            null,
            priority);

    private static ProjectRecord Project(string description, IReadOnlyList<string> technologies) =>
        new(
            "tenant",
            "01K00000000000000000000000",
            "01K00000000000000000000001",
            "V3 Understand",
            "V3UNDERSTAND",
            description,
            "active",
            "normal",
            null,
            "local",
            "main",
            technologies,
            new ProjectBrandRecord(null, null, null, null),
            [],
            1,
            "chief",
            "live",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            1);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
