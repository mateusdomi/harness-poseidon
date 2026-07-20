using Harness.Persistence.Abstractions.Identity;

namespace Harness.Host.Profiles;

/// <summary>
/// No modo pessoal (single-user, loopback, sem OIDC/multiusuário) não há muro de login:
/// o cookie de sessão só era gravado na criação do perfil, então um navegador novo (sem
/// cookie) ficava travado — não conseguia logar nem recriar (409). Este middleware adota
/// automaticamente o único perfil local existente como sessão da requisição e grava o
/// cookie para as requisições seguintes. Registrado apenas quando NÃO é multiusuário/OIDC,
/// portanto o modo servidor e o OIDC continuam exigindo autenticação real.
/// </summary>
public sealed class PersonalProfileSessionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ILocalProfileStore profiles)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(profiles);

        var hasSession = LocalProfileSession.TryGetProfileId(context.Request, out var profileId) &&
                         await profiles.GetAsync(profileId, context.RequestAborted) is not null;
        if (!hasSession)
        {
            var existing = await profiles.ListAsync(context.RequestAborted);
            if (existing.Count > 0)
            {
                // Adoção determinística do perfil mais antigo (ULID crescente) quando houver.
                var adopted = existing.OrderBy(profile => profile.Id, StringComparer.Ordinal).First();
                context.Items[LocalProfileSession.ItemKey] = adopted.Id;
                context.Response.Cookies.Append(
                    LocalProfileSession.CookieName,
                    adopted.Id,
                    new CookieOptions
                    {
                        HttpOnly = true,
                        IsEssential = true,
                        SameSite = SameSiteMode.Strict,
                        Secure = context.Request.IsHttps,
                        Path = "/",
                    });
            }
        }

        await next(context);
    }
}
