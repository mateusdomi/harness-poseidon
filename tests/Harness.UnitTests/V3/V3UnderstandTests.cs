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
    public void OpenQuestionPolicyDoesNotAskDeadlineOrLocalRepositoryByDefault()
    {
        var questions = V3OpenQuestionPolicy.RequiredQuestions("01K00000000000000000000000", null, null);

        Assert.Empty(questions);
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

        Assert.Equal("READY_TO_START", result.State.LifecycleState);
        Assert.Contains("Gestão de riscos corporativos", result.State.Requirements);
        Assert.Contains("Fluxo de aprovação por perfis", result.State.Requirements);
        Assert.Empty(result.OpenQuestions);
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
                null,
                null,
                null,
                null,
                "Oracle",
                "PRIMARY_REQUIREMENTS",
                "ITRC definido",
                "PRIMARY_REQUIREMENTS",
                26),
            [Coverage(complete: true)]);

        Assert.Empty(questions);
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
    public void PrimaryRequirementFactsExtractExplicitFrontendBackendAndDatabaseForStack()
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
                "Frontend: React + TypeScript. Backend: .NET 8. Banco de dados: Oracle."),
        ]);
        var project = Project(description: "Projeto sem stack no cadastro.", technologies: []);

        var stack = V3StackResolver.Resolve(project, state: null, [], facts);

        Assert.Equal("React + TypeScript", facts.Frontend);
        Assert.Equal(".NET 8", facts.Backend);
        Assert.Equal("Oracle", facts.Database);
        Assert.Equal("React + TypeScript", stack.Frontend);
        Assert.Equal(".NET 8", stack.Backend);
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

    [Theory]
    [InlineData("quero algo simples")]
    [InlineData("algo similar ao sistema antigo")]
    [InlineData("aproximadamente isso")]
    [InlineData("não pode iniciar")]
    [InlineData("não inicie ainda")]
    [InlineData("pode me dizer se está pronto para iniciar?")]
    public void AuthorizationRejectsSubstringAndQuestionFalsePositives(string response)
    {
        Assert.False(V3AuthorizationPolicy.IsAuthorized(response));
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
    public void MissionCompilerIncludesProjectBrandingWhenDeclared()
    {
        var now = DateTimeOffset.UnixEpoch;
        var context = Context(deadline: now.AddDays(10), repository: "/tmp/prisma") with
        {
            Brand = new V3ProjectBrandContext(
                "https://example.test/logo.png",
                "#123456",
                "#654321",
                "Inter"),
        };
        var recommended = new V3RecommendedExecutor("worker-codex-project", "AVAILABLE", "AVAILABLE + WRITE_CAPABLE + role compatible.");

        var mission = V3MissionCompiler.CompileBuildMission(context, recommended, null, now);

        Assert.Contains("BRANDING / PROVIDED VISUAL IDENTITY", mission.MissionText, StringComparison.Ordinal);
        Assert.Contains("https://example.test/logo.png", mission.MissionText, StringComparison.Ordinal);
        Assert.Contains("#123456", mission.MissionText, StringComparison.Ordinal);
        Assert.Contains("#654321", mission.MissionText, StringComparison.Ordinal);
        Assert.Contains("Inter", mission.MissionText, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildAndValidationMissionsMaterializeReadableContextForFutureExecutors()
    {
        var now = DateTimeOffset.UnixEpoch;
        var repo = Path.Combine(_directory, "product-repo");
        Directory.CreateDirectory(repo);
        var requirements = Path.Combine(_directory, "GoldenRun_Ocorrencias_Operacionais.md");
        var frontend = Path.Combine(_directory, "occurrence-hub-main.zip");
        File.WriteAllText(requirements, OccurrencesRequirements());
        File.WriteAllText(frontend, "zip-fixture");
        var context = Context(deadline: null, repository: repo) with
        {
            Artifacts =
            [
                new V3ArtifactReference(
                    "artifact-requirements",
                    "GoldenRun_Ocorrencias_Operacionais.md",
                    "text/markdown",
                    "requirements_source",
                    "solicitation_attachment",
                    requirements,
                    "REQSHA",
                    "accepted"),
                new V3ArtifactReference(
                    "artifact-frontend",
                    "occurrence-hub-main.zip",
                    "application/zip",
                    "provided_frontend",
                    "solicitation_attachment",
                    frontend,
                    "ZIPSHA",
                    "accepted"),
            ],
            State = Context(deadline: null, repository: repo).State! with
            {
                AcceptanceCriteria = [.. Enumerable.Range(1, 15).Select(index => $"Critério de aceite {index}")],
            },
            AcceptanceCriteria = [.. Enumerable.Range(1, 15).Select(index => $"Critério de aceite {index}")],
            Repository = repo,
            OpenQuestions = [],
            CurrentLifecycleState = "READY_TO_START",
        };
        var recommended = new V3RecommendedExecutor("worker-codex-project", "AVAILABLE", "AVAILABLE + WRITE_CAPABLE + role compatible.");

        var build = V3MissionCompiler.CompileBuildMission(context, recommended, null, now);
        var validation = V3MissionCompiler.CompileValidationMission(context, null, recommended, now);

        Assert.All(build.ArtifactReferences, artifact => Assert.True(File.Exists(artifact.ReadablePath), artifact.ReadablePath));
        Assert.All(build.KnowledgeReferences, reference => Assert.True(File.Exists(reference.ReadablePath), reference.Path));
        Assert.Contains(build.ArtifactReferences, artifact => artifact.Role == "requirements_source");
        Assert.Contains(build.ArtifactReferences, artifact => artifact.Role == "provided_frontend");
        Assert.Equal(15, build.MissionText.Split("Critério de aceite ", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("governance/rules/git.md", build.MissionText, StringComparison.Ordinal);
        Assert.DoesNotContain("cardActions", build.MissionText, StringComparison.Ordinal);
        Assert.Contains("POSEIDON_MISSION_COMPLETE", build.MissionText, StringComparison.Ordinal);
        Assert.All(validation.ArtifactReferences, artifact => Assert.True(File.Exists(artifact.ReadablePath), artifact.ReadablePath));
        Assert.Contains(validation.KnowledgeReferences, reference =>
            reference.Path == "docs/product/checklist-auto-auditoria-ia.md" &&
            File.Exists(reference.ReadablePath));
        Assert.Contains("BROWSER-FIRST POLICY", validation.MissionText, StringComparison.Ordinal);
        Assert.Contains("POSEIDON_VALIDATION_COMPLETE", validation.MissionText, StringComparison.Ordinal);
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

    [Fact]
    public void PlatformMaintenancePreviewRequiresPlatformMaintainerRole()
    {
        var selected = V3ExecutorPreview.Select(
        [
            Account("worker-project", [AgentRoles.ProjectExecutor], priority: 100),
            Account("worker-platform", [AgentRoles.PlatformMaintainer], priority: 10),
        ], AgentRoles.PlatformMaintainer);

        Assert.Equal("worker-platform", selected.AccountAlias);
        Assert.Equal("AVAILABLE", selected.Status);
    }

    [Fact]
    public void PlatformMaintenanceRuntimeSelectorDoesNotFallbackToProjectExecutor()
    {
        var selected = V3BuildExecutorSelector.Select(
        [
            Account("worker-project", [AgentRoles.ProjectExecutor], priority: 100),
        ], requiredRole: AgentRoles.PlatformMaintainer);

        Assert.Null(selected.Account);
        Assert.Contains("No authenticated", selected.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void PlatformMaintenanceMissionCarriesSafePoseidonRepairBoundary()
    {
        var now = DateTimeOffset.UnixEpoch;
        var repository = Path.Combine(_directory, "poseidon-repo");
        Directory.CreateDirectory(repository);
        var executor = new V3RecommendedExecutor("worker-platform", "AVAILABLE", "AVAILABLE + WRITE_CAPABLE + role compatible.");

        var mission = V3MissionCompiler.CompilePlatformMaintenanceMission(
            "project-platform",
            "Poseidon",
            repository,
            new V3CompilePlatformMaintenanceMissionRequest(
                "A tela de agentes mistura conta runtime com pessoa pública.",
                "Health PASS; executor Codex auth pending.",
                "Abrir /agents e verificar duplicidade de Bruna."),
            executor,
            now);

        Assert.Equal("PLATFORM_MAINTENANCE", mission.MissionType);
        Assert.Equal(AgentRoles.PlatformMaintainer, mission.TargetExecutorCapability);
        Assert.Equal(repository, mission.Repository);
        Assert.Contains("Trabalhe somente no repositório Poseidon", mission.MissionText, StringComparison.Ordinal);
        Assert.Contains("Não altere Prisma ou Indicadores", mission.MissionText, StringComparison.Ordinal);
        Assert.Contains("Não reintroduza Council", mission.MissionText, StringComparison.Ordinal);
        Assert.Contains("POSEIDON_MISSION_COMPLETE", mission.MissionText, StringComparison.Ordinal);
    }

    [Fact]
    public void SimpleCrudReceivesSimpleSolutionStrategyWithoutArchitectureApproval()
    {
        var now = DateTimeOffset.UnixEpoch;
        var context = Context(deadline: null, repository: "/tmp/equipment") with
        {
            PrimaryRequirementsCoverage =
            [
                CoverageWithText("""
                Sistema web simples para controlar empréstimo de equipamentos.
                Usuários registram equipamentos, empréstimos e devoluções.
                Persistir os dados e mostrar listagem com filtros simples.
                """),
            ],
        };

        var result = V3UnderstandAnalyzer.Analyze(context, new V3UnderstandAnalyzeRequest
        {
            OriginalIntent = "Controlar empréstimo de equipamentos.",
        }, now);

        Assert.Equal("SIMPLE", result.State.SolutionStrategy?.Complexity);
        Assert.False(result.State.SolutionStrategy?.ArchitectureApprovalRequired);
        Assert.Contains(result.State.SolutionStrategy!.ImportantTradeoffs, item =>
            item.Contains("Evitar camadas", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(result.OpenQuestions);
    }

    [Fact]
    public void ComplexIntegrationReceivesSolutionStrategyWithoutInventingNewPhase()
    {
        var now = DateTimeOffset.UnixEpoch;
        var context = Context(deadline: null, repository: "/tmp/integration") with
        {
            PrimaryRequirementsCoverage =
            [
                CoverageWithText("""
                Portal web com API, banco, integração com ERP, API de terceiro,
                processamento assíncrono em fila, SSO corporativo, alto volume e requisito de alta disponibilidade.
                Todos os fornecedores e protocolos já estão mandatados no documento.
                """),
            ],
        };

        var result = V3UnderstandAnalyzer.Analyze(context, new V3UnderstandAnalyzeRequest
        {
            OriginalIntent = "Integrar operação web com ERP e API externa.",
        }, now);

        Assert.Equal("COMPLEX", result.State.SolutionStrategy?.Complexity);
        Assert.Contains("ERP", result.State.SolutionStrategy!.IntegrationPoints);
        Assert.Contains("API externa/terceiro", result.State.SolutionStrategy.IntegrationPoints);
        Assert.Contains("Processamento assíncrono", result.State.SolutionStrategy.KeyComponents);
        Assert.False(result.State.SolutionStrategy.ArchitectureApprovalRequired);
        Assert.Empty(result.OpenQuestions);
    }

    [Fact]
    public void MaterialUnresolvedArchitectureDecisionBlocksUnderstandInsideUnderstand()
    {
        var now = DateTimeOffset.UnixEpoch;
        var context = Context(deadline: null, repository: "/tmp/material-decision") with
        {
            PrimaryRequirementsCoverage =
            [
                CoverageWithText("""
                Sistema de integração crítica com ERP e API externa.
                A topologia cloud ou on-prem está a definir infraestrutura.
                O fornecedor a definir impacta o contrato externo.
                """),
            ],
        };

        var result = V3UnderstandAnalyzer.Analyze(context, new V3UnderstandAnalyzeRequest
        {
            OriginalIntent = "Integração crítica com decisão material pendente.",
        }, now);

        Assert.True(result.State.SolutionStrategy?.ArchitectureApprovalRequired);
        Assert.Equal("AWAITING_INPUT", result.State.LifecycleState);
        Assert.Contains(result.OpenQuestions, question =>
            question.Reason == "solution_strategy.architecture_approval_required");
    }

    [Fact]
    public void CriticalCalculationHighlightsDeterministicDomainTests()
    {
        var now = DateTimeOffset.UnixEpoch;
        var context = Context(deadline: null, repository: "/tmp/calculation") with
        {
            PrimaryRequirementsCoverage =
            [
                CoverageWithText("""
                Sistema para calcular faixas de comissão por tabela de referência.
                Fórmula possui arredondamento específico e casos de borda críticos.
                """),
            ],
        };

        var result = V3UnderstandAnalyzer.Analyze(context, new V3UnderstandAnalyzeRequest
        {
            OriginalIntent = "Calcular comissões por tabela de referência.",
        }, now);

        Assert.Contains(result.State.SolutionStrategy!.TechnicalRisks, risk =>
            risk.Contains("testes determinísticos", StringComparison.OrdinalIgnoreCase));
        Assert.False(result.State.SolutionStrategy.ArchitectureApprovalRequired);
    }

    [Fact]
    public void KnowledgeSelectorKeepsSimpleCrudSmall()
    {
        var stack = new V3EffectiveStackContract(
            "Frontend conforme requisitos",
            "Backend conforme baseline Poseidon",
            "Database conforme requisitos",
            "Clean Architecture / modular full-stack",
            "Playwright + unit/integration tests",
            ["baseline"]);
        var strategy = new V3SolutionStrategy(
            "SIMPLE",
            "Implementar direto.",
            ["Fluxo principal do produto"],
            [],
            "Persistência somente se exigida.",
            [],
            [],
            [],
            ["Evitar cerimônia."],
            [],
            false);

        var refs = V3KnowledgeSelector.Select(stack, strategy);

        Assert.Contains(refs, item => item.Path == "docs/product/definition-of-done.md");
        Assert.Contains(refs, item => item.Path == "docs/product/baseline.md");
        Assert.DoesNotContain(refs, item => item.Path == "docs/product/oracle-data-standards.md");
        Assert.DoesNotContain(refs, item => item.Path == "docs/product/provided-artifacts.md");
        Assert.DoesNotContain(refs, item => item.Path == "docs/product/authentication-standards.md");
    }

    [Fact]
    public void KnowledgeSelectorExpandsForComplexIntegration()
    {
        var stack = new V3EffectiveStackContract(
            "React + TypeScript",
            ".NET 8",
            "Oracle",
            "Clean Architecture / modular full-stack",
            "Playwright + unit/integration tests",
            ["requirements"]);
        var strategy = new V3SolutionStrategy(
            "COMPLEX",
            "Integração complexa.",
            ["Frontend web", "API/backend", "Persistência relacional", "Autenticação/autorização", "Integração: ERP"],
            ["ERP"],
            "Persistência real.",
            ["Autorização server-side."],
            ["Health externo."],
            ["Contrato externo."],
            [],
            [],
            false);

        var refs = V3KnowledgeSelector.Select(stack, strategy);

        Assert.Contains(refs, item => item.Path == "docs/product/frontend-standards.md");
        Assert.Contains(refs, item => item.Path == "docs/product/backend-standards.md");
        Assert.Contains(refs, item => item.Path == "docs/product/data-standards.md");
        Assert.Contains(refs, item => item.Path == "docs/product/oracle-data-standards.md");
        Assert.Contains(refs, item => item.Path == "docs/product/security-and-operability.md");
        Assert.Contains(refs, item => item.Path == "docs/product/authentication-standards.md");
        Assert.Contains(refs, item => item.Path == "docs/product/qa-standards.md");
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
                "React + TypeScript + Vite",
                "PRIMARY_REQUIREMENTS",
                ".NET",
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

    private static V3SourceCoverage CoverageWithText(string text) =>
        new(
            "artifact-1",
            "requirements.md",
            "requirements_source",
            1,
            1,
            100,
            true,
            "full-text-read",
            text.Length,
            text);

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

    private static string OccurrencesRequirements() =>
        """
        # Sistema de Gestão de Ocorrências Operacionais

        Criar um sistema web para registrar, acompanhar e encerrar ocorrências operacionais internas.

        Critérios de aceite:
        1. Login válido funciona.
        2. Login inválido mostra mensagem adequada.
        3. Administrador acessa gestão de usuários.
        4. Operador não acessa gestão de usuários.
        5. É possível criar ocorrência.
        6. Ocorrência criada aparece na listagem.
        7. Alteração persiste.
        8. Filtros funcionam.
        9. Dashboard reflete dados reais.
        10. Histórico registra alterações.
        11. Administrador consegue encerrar ocorrência.
        12. Operador não autorizado não consegue encerrar ocorrência de outro usuário.
        13. Logout funciona.
        14. Aplicação funciona em desktop.
        15. Aplicação funciona em mobile.
        """;

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
