using System.Globalization;
using System.Text;

namespace Harness.Modules.Workflows.Product;

/// <summary>Entrada da resolução: o que o usuário disse e o que já foi decidido sobre o projeto.</summary>
public sealed record EffectiveProfileInputs(
    string ProjectId,
    string DemandText,
    IReadOnlyList<ProfileDirective> Directives,
    IReadOnlyList<string>? ActiveAdrs = null);

/// <summary>
/// Resolve o perfil efetivo de um projeto — PURO, determinístico e sem I/O.
///
/// A regra que este resolvedor existe para impor: <b>ausência de especificação do usuário é
/// aplicação do baseline, nunca omissão de parte do produto.</b> O incidente que originou este
/// trabalho foi exatamente o contrário: o usuário não informou stack, e a fábrica entregou um
/// endpoint sem interface porque "não foi especificado".
/// </summary>
public static class EffectiveProfileResolver
{
    public const string AreaModality = "modality";
    public const string AreaFrontendRequired = "frontend.required";
    public const string AreaFrontendFramework = "frontend.framework";
    public const string AreaFrontendLanguage = "frontend.language";
    public const string AreaFrontendBuild = "frontend.build";
    public const string AreaFrontendDesignSystem = "frontend.designSystem";
    public const string AreaBackendFramework = "backend.framework";
    public const string AreaBackendRuntime = "backend.runtime";
    public const string AreaBackendLanguage = "backend.language";
    public const string AreaArchitectureStyle = "architecture.style";
    public const string AreaDatabase = "data.database";
    public const string AreaMigrationStrategy = "data.migrationStrategy";
    public const string AreaApiProtocol = "api.protocol";
    public const string AreaOpenApiRequired = "api.openApiRequired";
    public const string AreaAuthentication = "security.authentication";
    public const string AreaAuthorization = "security.authorization";
    public const string AreaHosting = "operation.hosting";
    public const string AreaObservability = "operation.observability";

    public static ProjectEffectiveProfile Resolve(EffectiveProfileInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        var directives = (inputs.Directives ?? [])
            .Where(directive => !string.IsNullOrWhiteSpace(directive.Area))
            .ToArray();

        var modality = ResolveModality(inputs.DemandText, directives, out var modalitySource);
        var defaults = BaselineDefaults(modality);
        var overrides = new List<ProfileOverride>();
        var sources = new List<string>
        {
            $"baseline:{ProjectEffectiveProfile.CurrentBaselineVersion}",
            modalitySource,
        };

        string? Effective(string area)
        {
            var declared = Winner(directives, area);
            var fallback = defaults.GetValueOrDefault(area);
            if (declared is null)
            {
                return fallback;
            }

            // Override de UMA dimensão não abandona o baseline nas outras: o registro é por área,
            // e só a área declarada deixa de herdar.
            if (!string.Equals(declared.Value, fallback, StringComparison.OrdinalIgnoreCase))
            {
                overrides.Add(new ProfileOverride(
                    area,
                    fallback ?? "(sem default)",
                    declared.Value,
                    declared.Authority,
                    declared.Reason,
                    declared.AdrId));
            }

            sources.Add($"{area}:{declared.Authority.ToString().ToLowerInvariant()}");
            return declared.Value;
        }

        var frontendRequired = ParseBool(Effective(AreaFrontendRequired), modality == ProductModality.Web);
        var apiProtocol = Effective(AreaApiProtocol);
        var openApiRequired = ParseBool(
            Effective(AreaOpenApiRequired),
            !string.IsNullOrWhiteSpace(apiProtocol));

        return new ProjectEffectiveProfile(
            inputs.ProjectId,
            ProjectEffectiveProfile.CurrentBaselineVersion,
            modality,
            new BackendProfile(
                Required: modality is not ProductModality.Library,
                Runtime: Effective(AreaBackendRuntime),
                Framework: Effective(AreaBackendFramework),
                Language: Effective(AreaBackendLanguage),
                ArchitectureStyle: Effective(AreaArchitectureStyle)),
            new FrontendProfile(
                Required: frontendRequired,
                Framework: frontendRequired ? Effective(AreaFrontendFramework) : null,
                Language: frontendRequired ? Effective(AreaFrontendLanguage) : null,
                BuildSystem: frontendRequired ? Effective(AreaFrontendBuild) : null,
                DesignSystem: Effective(AreaFrontendDesignSystem)),
            new DataProfile(
                Required: modality is ProductModality.Web or ProductModality.ApiOnly
                    or ProductModality.Worker or ProductModality.Desktop or ProductModality.Mobile,
                Database: Effective(AreaDatabase),
                MigrationStrategy: Effective(AreaMigrationStrategy),
                ApprovedTechnologies: []),
            new ApiProfile(
                Required: modality is ProductModality.Web or ProductModality.ApiOnly
                    or ProductModality.Mobile,
                Protocol: apiProtocol,
                OpenApiRequired: openApiRequired,
                Versioning: null),
            new SecurityProfile(
                Authentication: Effective(AreaAuthentication),
                Authorization: Effective(AreaAuthorization),
                Constraints: []),
            new OperationProfile(
                Hosting: Effective(AreaHosting),
                Observability: Effective(AreaObservability),
                Requirements: []),
            overrides,
            inputs.ActiveAdrs ?? [],
            directives
                .Where(directive => directive.Authority is ProfileAuthority.Regulatory
                    or ProfileAuthority.OrganizationConstraint)
                .Select(directive => $"{directive.Area}={directive.Value} ({directive.Reason})")
                .ToArray(),
            sources);
    }

    /// <summary>
    /// A diretiva vencedora de uma área: maior autoridade primeiro; empatando, a última declarada.
    /// Preferência do agente não entra porque não existe como autoridade.
    /// </summary>
    private static ProfileDirective? Winner(IReadOnlyList<ProfileDirective> directives, string area) =>
        directives
            .Where(directive => string.Equals(directive.Area, area, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(directive => (int)directive.Authority)
            .LastOrDefault(directive => directive.Authority ==
                directives
                    .Where(candidate => string.Equals(candidate.Area, area, StringComparison.OrdinalIgnoreCase))
                    .Max(candidate => candidate.Authority));

    private static ProductModality ResolveModality(
        string? demandText,
        IReadOnlyList<ProfileDirective> directives,
        out string source)
    {
        var declared = Winner(directives, AreaModality);
        if (declared is not null && TryParseModality(declared.Value, out var explicitModality))
        {
            source = $"modality:{declared.Authority.ToString().ToLowerInvariant()}";
            return explicitModality;
        }

        var inferred = ProductModalityInference.Infer(demandText);
        source = $"modality:inferred";
        return inferred;
    }

    public static bool TryParseModality(string? value, out ProductModality modality)
    {
        modality = ProductModality.Unspecified;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        switch (Normalize(value))
        {
            case "web": modality = ProductModality.Web; return true;
            case "apionly" or "api-only" or "api": modality = ProductModality.ApiOnly; return true;
            case "worker" or "service" or "servico": modality = ProductModality.Worker; return true;
            case "desktop" or "exe": modality = ProductModality.Desktop; return true;
            case "mobile" or "app": modality = ProductModality.Mobile; return true;
            case "library" or "biblioteca" or "sdk": modality = ProductModality.Library; return true;
            default: return false;
        }
    }

    private static bool ParseBool(string? value, bool fallback) =>
        value is null
            ? fallback
            : Normalize(value) switch
            {
                "true" or "sim" or "yes" or "required" or "obrigatorio" => true,
                "false" or "nao" or "no" or "optional" or "opcional" => false,
                _ => fallback,
            };

    /// <summary>
    /// Os defaults do baseline por modalidade. Uma modalidade sem interface humana não herda
    /// framework de frontend; uma biblioteca não herda banco. O default é proporcional ao pedido.
    /// </summary>
    private static Dictionary<string, string> BaselineDefaults(ProductModality modality)
    {
        var defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [AreaBackendRuntime] = ".NET 8",
            [AreaBackendFramework] = "ASP.NET Core 8",
            [AreaBackendLanguage] = "C#",
            [AreaArchitectureStyle] = "Modular Monolith + Clean Architecture",
        };

        if (modality is ProductModality.Web or ProductModality.ApiOnly or ProductModality.Mobile)
        {
            defaults[AreaApiProtocol] = "REST";
            defaults[AreaOpenApiRequired] = "true";
        }

        if (modality is ProductModality.Web or ProductModality.ApiOnly or ProductModality.Worker
            or ProductModality.Desktop or ProductModality.Mobile)
        {
            defaults[AreaDatabase] = "SQL Server";
            defaults[AreaMigrationStrategy] = "EF Core migrations versionadas";
        }

        if (modality is ProductModality.Web)
        {
            defaults[AreaFrontendRequired] = "true";
            defaults[AreaFrontendFramework] = "React";
            defaults[AreaFrontendLanguage] = "TypeScript";
            defaults[AreaFrontendBuild] = "Vite";
        }
        else if (modality is ProductModality.Desktop or ProductModality.Mobile)
        {
            // Há interface, mas o baseline não presume qual: desktop e mobile dependem da
            // plataforma alvo, que a Arquitetura decide. Exigir a interface sem fingir conhecer
            // o framework é mais honesto que inventar um default.
            defaults[AreaFrontendRequired] = "true";
        }
        else
        {
            defaults[AreaFrontendRequired] = "false";
        }

        return defaults;
    }

    private static string Normalize(string value)
    {
        var decomposed = value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark &&
                character is not ' ' and not '_')
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }
}
