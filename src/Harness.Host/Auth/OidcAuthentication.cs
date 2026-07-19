using System.Data.Common;
using System.Security.Claims;
using Harness.Host.Profiles;
using Harness.Persistence.Abstractions.Identity;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;

namespace Harness.Host.Auth;

/// <summary>
/// Configuração fechada do modo OIDC. Quando habilitado, toda a API e o hub de
/// eventos exigem bearer token do provedor configurado; perfis locais são
/// provisionados automaticamente a partir do subject externo (primeiro usuário
/// autenticado vira admin do tenant compartilhado, os demais aderem como member).
/// </summary>
public sealed record OidcSettings(
    bool Enabled,
    string Authority,
    string Audience,
    bool RequireHttpsMetadata)
{
    public static OidcSettings From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var enabled = string.Equals(
            configuration["Harness:Auth:Oidc:Enabled"], "true", StringComparison.OrdinalIgnoreCase);
        if (!enabled)
        {
            return new OidcSettings(false, string.Empty, string.Empty, true);
        }

        var authority = configuration["Harness:Auth:Oidc:Authority"];
        var audience = configuration["Harness:Auth:Oidc:Audience"];
        if (string.IsNullOrWhiteSpace(authority) || string.IsNullOrWhiteSpace(audience))
        {
            throw new InvalidOperationException(
                "OIDC mode requires Harness:Auth:Oidc:Authority and Harness:Auth:Oidc:Audience.");
        }

        return new OidcSettings(
            true,
            authority,
            audience,
            !string.Equals(
                configuration["Harness:Auth:Oidc:RequireHttpsMetadata"],
                "false",
                StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Provisiona o perfil local de um subject OIDC: reutiliza o vínculo existente,
/// senão cria (bootstrap admin ou adesão member) com as mesmas garantias de
/// corrida do endpoint de perfis — lock de bootstrap no store, retry como adesão
/// e re-leitura em violação de unicidade do subject.
/// </summary>
public sealed class OidcProfileProvisioner(ILocalProfileStore store, IClock clock)
{
    public async Task<LocalProfileRecord?> EnsureProfileAsync(
        string externalSubject,
        string displayName,
        string? email,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalSubject);
        var existing = await store.GetByExternalSubjectAsync(externalSubject, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var occurredAt = clock.UtcNow;
        var profiles = await store.ListAsync(cancellationToken);
        var joinExistingTenant = profiles.Count > 0;
        var tenantId = joinExistingTenant
            ? profiles[0].TenantId
            : UlidValue.New(occurredAt).ToString();
        try
        {
            var result = await store.CreateAsync(
                new LocalProfileCreateCommand(
                    tenantId, "Personal", UlidValue.New(occurredAt).ToString(),
                    string.IsNullOrWhiteSpace(displayName) ? externalSubject : displayName,
                    email, null, "pt-BR", occurredAt, joinExistingTenant, externalSubject),
                cancellationToken);
            if (result.Status is LocalProfileMutationStatus.AlreadyExists && !joinExistingTenant)
            {
                // Corrida de bootstrap: outro subject venceu; adere ao tenant dele.
                var winners = await store.ListAsync(cancellationToken);
                result = await store.CreateAsync(
                    new LocalProfileCreateCommand(
                        winners[0].TenantId, "Personal", UlidValue.New(occurredAt).ToString(),
                        string.IsNullOrWhiteSpace(displayName) ? externalSubject : displayName,
                        email, null, "pt-BR", occurredAt, true, externalSubject),
                    cancellationToken);
            }

            return result.Profile;
        }
        catch (DbException)
        {
            // Corrida do mesmo subject em requisições concorrentes: o índice único
            // de external_subject garante um vencedor; os demais reutilizam.
            return await store.GetByExternalSubjectAsync(externalSubject, cancellationToken);
        }
    }
}

/// <summary>
/// Gate do modo OIDC: exige principal autenticado em /api e /hubs, resolve o
/// perfil local do subject e o publica em HttpContext.Items para a sessão.
/// </summary>
public sealed class OidcSessionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, OidcProfileProvisioner provisioner)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(provisioner);
        var path = context.Request.Path;
        if (!path.StartsWithSegments("/api", StringComparison.Ordinal) &&
            !path.StartsWithSegments("/hubs", StringComparison.Ordinal))
        {
            await next(context);
            return;
        }

        if (context.User.Identity?.IsAuthenticated != true)
        {
            await Unauthorized(context, "A valid bearer token is required in OIDC mode.");
            return;
        }

        var subject = context.User.FindFirstValue("oid")
            ?? context.User.FindFirstValue("sub")
            ?? context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(subject))
        {
            await Unauthorized(context, "The bearer token carries no usable subject claim.");
            return;
        }

        var displayName = context.User.FindFirstValue("name")
            ?? context.User.FindFirstValue("preferred_username")
            ?? subject;
        var profile = await provisioner.EnsureProfileAsync(
            subject,
            displayName,
            context.User.FindFirstValue("email"),
            context.RequestAborted);
        if (profile is null)
        {
            await Unauthorized(context, "The authenticated subject could not be provisioned.");
            return;
        }

        context.Items[LocalProfileSession.ItemKey] = profile.Id;
        await next(context);
    }

    private static Task Unauthorized(HttpContext context, string detail)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Results.Problem(statusCode: 401, title: "oidc_unauthorized", detail: detail)
            .ExecuteAsync(context);
    }
}
