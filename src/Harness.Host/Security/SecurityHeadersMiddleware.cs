namespace Harness.Host.Security;

/// <summary>
/// Política de cabeçalhos de segurança aplicada a toda resposta HTTP. É estática e
/// configurável por <see cref="SecurityHeadersOptions"/> e nunca carrega segredos.
/// Não sobrescreve um cabeçalho que já tenha sido definido por outra camada.
/// </summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next, SecurityHeadersOptions options)
{
    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Este é o primeiro middleware do pipeline: definir os cabeçalhos aqui, antes
        // de `next`, garante que eles acompanhem toda resposta (SPA estática, API, hub)
        // e não sejam removidos por camadas posteriores.
        if (options.Enabled)
        {
            Apply(context);
        }

        return next(context);
    }

    private void Apply(HttpContext context)
    {
        var headers = context.Response.Headers;
        Set(headers, "X-Content-Type-Options", "nosniff");
        Set(headers, "X-Frame-Options", "DENY");
        Set(headers, "Referrer-Policy", "no-referrer");
        Set(headers, "Permissions-Policy", "camera=(), microphone=(), geolocation=(), payment=(), usb=()");
        Set(headers, "Cross-Origin-Opener-Policy", "same-origin");
        Set(headers, "Cross-Origin-Resource-Policy", "same-origin");
        Set(headers, "Content-Security-Policy", options.ResolvedContentSecurityPolicy);

        // HSTS só faz sentido sobre TLS; o modo pessoal em loopback HTTP não o recebe.
        if (context.Request.IsHttps)
        {
            Set(headers, "Strict-Transport-Security", "max-age=31536000; includeSubDomains");
        }
    }

    private static void Set(IHeaderDictionary headers, string name, string value)
    {
        if (!headers.ContainsKey(name))
        {
            headers[name] = value;
        }
    }
}

/// <summary>
/// Configuração da política de cabeçalhos. Bind de <c>Harness:Security:Headers</c>.
/// A CSP padrão é compatível com o bundle servido (scripts/estilos externos,
/// sem inline script) e mantém <c>frame-ancestors 'none'</c> e <c>object-src 'none'</c>.
/// </summary>
public sealed record SecurityHeadersOptions
{
    public const string DefaultContentSecurityPolicy =
        "default-src 'self'; " +
        "script-src 'self'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data: https:; " +
        "font-src 'self' data:; " +
        "connect-src 'self'; " +
        "frame-ancestors 'none'; " +
        "base-uri 'self'; " +
        "form-action 'self'; " +
        "object-src 'none'";

    public bool Enabled { get; init; } = true;

    /// <summary>Sobrescreve a CSP padrão sem recompilar; deve permanecer restritiva.</summary>
    public string? ContentSecurityPolicy { get; init; }

    public string ResolvedContentSecurityPolicy =>
        string.IsNullOrWhiteSpace(ContentSecurityPolicy)
            ? DefaultContentSecurityPolicy
            : ContentSecurityPolicy;
}
