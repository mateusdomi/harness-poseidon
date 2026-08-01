using Harness.Host.Agents;
using Harness.Modules.Governance.Context;

namespace Harness.UnitTests.Agents;

/// <summary>
/// Fase 2A.3 — as nove especialidades do playbook deixaram de ser rótulos.
///
/// Elas viviam como três colunas no catálogo de especialidades: chave, nome e uma linha. Um agente
/// despachado como "qa" recebia, como definição inteira do papel, a frase "Qualidade é cultura" —
/// bonita e inútil diante de um card concreto. Pior: <c>ContextSegmentKind.Persona</c> existia no
/// enum desde o começo e nunca teve produtor, então nenhuma persona chegava ao prompt.
/// </summary>
public sealed class PlaybookPersonaDepthTests
{
    private static readonly string[] PlaybookKeys =
    [
        "playbook-product-owner", "playbook-arquiteto", "playbook-tech-lead", "playbook-qa",
        "playbook-devops", "playbook-sre-sustentacao", "playbook-security", "playbook-dba-dados",
        "playbook-dev-executor",
    ];

    [Fact]
    public void TheNinePlaybookSpecialtiesExistAsDeepDefinitions()
    {
        var byKey = CanonicalAgentDefinitions.All
            .ToDictionary(seed => seed.Content.Key, StringComparer.Ordinal);

        foreach (var key in PlaybookKeys)
        {
            Assert.True(byKey.ContainsKey(key), $"A persona '{key}' do playbook não existe.");
            var content = byKey[key].Content;

            // Uma linha não é persona. Cada campo abaixo responde a uma pergunta que o agente faz
            // diante do card e que o rótulo não respondia.
            Assert.False(string.IsNullOrWhiteSpace(content.Persona), $"{key}: sem mentalidade.");
            Assert.False(string.IsNullOrWhiteSpace(content.Mission), $"{key}: sem missão.");
            Assert.True(content.OperatingPrinciples.Count >= 3, $"{key}: princípios rasos.");
            Assert.True(content.Deliverables.Count >= 2, $"{key}: sem entregáveis.");
            Assert.True(content.QualityCriteria.Count >= 2, $"{key}: sem critérios de qualidade.");
            Assert.NotEmpty(content.Limitations);
        }
    }

    [Fact]
    public void EveryPlaybookPersonaDeclaresWhenToActivateAndWhenNotTo()
    {
        var byKey = CanonicalAgentDefinitions.All
            .ToDictionary(seed => seed.Content.Key, StringComparer.Ordinal);

        foreach (var key in PlaybookKeys)
        {
            var content = byKey[key].Content;

            // Sem critério de acionamento, escolher especialista é semelhança de nome — e o card
            // de banco cai no back-end genérico porque "parece parecido".
            Assert.True(
                content.ActivationCriteria is { Count: >= 2 },
                $"{key}: não diz QUANDO ser acionada.");

            // A fronteira negativa (B11) é o que impede a persona de ser chamada para tudo.
            Assert.True(
                content.NonActivationCriteria is { Count: >= 2 },
                $"{key}: não diz quando NÃO ser acionada.");
        }
    }

    [Fact]
    public void EveryPlaybookPersonaDeclaresWhatItMayAndMayNotTouch()
    {
        var byKey = CanonicalAgentDefinitions.All
            .ToDictionary(seed => seed.Content.Key, StringComparer.Ordinal);

        foreach (var key in PlaybookKeys)
        {
            var content = byKey[key].Content;
            Assert.True(content.AllowedScopes is { Count: > 0 }, $"{key}: sem escopo permitido.");
            Assert.True(content.DeniedScopes is { Count: > 0 }, $"{key}: sem escopo negado.");

            // Governança nunca é tocada por persona de projeto: é a regra que as restringe.
            Assert.Contains(
                content.DeniedScopes!,
                scope => scope.StartsWith("governance/", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void ThePersonaReachesThePromptAsAMandatorySegment()
    {
        // O gate do 2A.3: a persona certa, com o conteúdo novo, dentro do bundle.
        var architect = CanonicalAgentDefinitions.All
            .Single(seed => seed.Content.Key == "playbook-arquiteto").Content;

        var bundle = new ContextBundleBuilder(FindRepositoryRoot()).Build(
            Request() with
            {
                Persona = new ContextPersonaSlice(
                    architect.Key, architect.Name, architect.Persona, architect.Mission,
                    architect.OperatingPrinciples, architect.Deliverables, architect.Limitations),
            });

        var segment = Assert.Single(
            bundle.Segments,
            item => item.Kind == ContextSegmentKind.Persona);

        Assert.Equal("persona:playbook-arquiteto", segment.SourceId);

        // OBRIGATÓRIA: o orçamento de tokens corta qualquer outra coisa antes de cortar quem o
        // agente está sendo.
        Assert.True(segment.Mandatory);

        // O conteúdo NOVO está lá — não o rótulo de uma linha.
        Assert.Contains("Trade-offs", segment.Content, StringComparison.Ordinal);
        Assert.Contains("consequência NEGATIVA", segment.Content, StringComparison.Ordinal);
        Assert.Contains("Onde você para", segment.Content, StringComparison.Ordinal);
        Assert.Contains("não escreve código de produção", bundle.RenderedContext.ToLowerInvariant(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutAResolvedPersonaTheBundleSimplyHasNoPersonaSegment()
    {
        // Ausência de persona não inventa uma genérica: o segmento some, e quem lê o bundle vê
        // que faltou — em vez de acreditar num especialista que ninguém escolheu.
        var bundle = new ContextBundleBuilder(FindRepositoryRoot()).Build(Request());

        Assert.DoesNotContain(bundle.Segments, item => item.Kind == ContextSegmentKind.Persona);
    }

    private static ContextBundleRequest Request() => new(
        "tenant", "project", "task", "attempt", "agent", "poseidon", "fake",
        "agent-run", "execution", "backend-specialist", "medium", ["src/Harness.Host"], "{}",
        ["criterion"], ["read"], [], ["stop on failure"], 12000);

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
