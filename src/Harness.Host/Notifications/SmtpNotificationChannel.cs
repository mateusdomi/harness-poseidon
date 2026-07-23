using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Mail;
using Harness.Modules.Agents.Application.Accounts;
using Harness.SharedKernel.Security;

namespace Harness.Host.Notifications;

/// <summary>
/// Uma notificação já renderizada, pronta para entrega por um canal externo. O destinatário
/// é um endereço concreto (resolvido pelo gateway a partir de referência opaca) — nunca é
/// literal em configuração, código ou log. Assunto e corpo são texto humano.
/// </summary>
public sealed record RenderedNotification(string Recipient, string Subject, string Body);

/// <summary>
/// Resultado tipado de uma tentativa de entrega. Falhas carregam apenas um MOTIVO curto e
/// não sensível (jamais o endereço, host ou credencial): a superfície de erro nunca vaza
/// segredo. <see cref="Skipped"/> distingue "canal não configurado" (no-op) de falha real.
/// </summary>
public sealed record NotificationDeliveryResult(bool Delivered, bool Skipped, string? FailureReason)
{
    public static NotificationDeliveryResult Ok() => new(Delivered: true, Skipped: false, FailureReason: null);

    public static NotificationDeliveryResult SkippedUnconfigured() =>
        new(Delivered: false, Skipped: true, FailureReason: "channel_unconfigured");

    public static NotificationDeliveryResult Failed(string reason) =>
        new(Delivered: false, Skipped: false, FailureReason: reason);
}

/// <summary>
/// Abstração de um canal externo de notificação. O gateway roteia uma notificação renderizada
/// para o canal cujo <see cref="Channel"/> corresponde ao destino pedido ("email"). Espelha o
/// padrão dos adaptadores Telegram/Teams: configuração vem de ambiente/cofre e a
/// indisponibilidade é um no-op explícito, nunca uma exceção não tratada.
/// </summary>
public interface IExternalNotificationChannel
{
    /// <summary>Nome lógico do canal usado no roteamento (ex.: "email").</summary>
    string Channel { get; }

    /// <summary>Verdadeiro apenas quando o deploy forneceu todas as referências de segredo.</summary>
    bool IsConfigured { get; }

    Task<NotificationDeliveryResult> SendAsync(RenderedNotification notification, CancellationToken cancellationToken);
}

/// <summary>
/// Envelope de transporte SMTP já resolvido para valores concretos, existente apenas na pilha
/// durante o envio. Nunca é persistido, logado nem serializado. A senha é o único segredo e
/// vive aqui só o tempo de uma chamada a <see cref="ISmtpTransport.SendAsync"/>.
/// </summary>
public sealed record SmtpEnvelope(
    string Host,
    int Port,
    bool UseTls,
    string? Username,
    string? Password,
    string FromAddress,
    string ToAddress,
    string Subject,
    string Body);

/// <summary>
/// Costura de transporte SMTP. A implementação de produção usa <see cref="SmtpClient"/> +
/// <see cref="MailMessage"/> da BCL; os testes injetam um fake que captura o envelope sem
/// abrir socket algum.
/// </summary>
public interface ISmtpTransport
{
    Task SendAsync(SmtpEnvelope envelope, CancellationToken cancellationToken);
}

/// <summary>
/// Transporte SMTP real sobre a BCL. TLS é solicitado por padrão (<c>EnableSsl</c>). Nenhuma
/// credencial é logada: em falha, o chamador registra apenas o TIPO da exceção.
/// </summary>
public sealed class SystemNetSmtpTransport : ISmtpTransport
{
    public async Task SendAsync(SmtpEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        using var message = new MailMessage(envelope.FromAddress, envelope.ToAddress)
        {
            Subject = envelope.Subject,
            Body = envelope.Body,
            IsBodyHtml = false,
        };
        using var client = new SmtpClient(envelope.Host, envelope.Port)
        {
            EnableSsl = envelope.UseTls,
            DeliveryMethod = SmtpDeliveryMethod.Network,
        };
        if (!string.IsNullOrEmpty(envelope.Username))
        {
            client.Credentials = new NetworkCredential(envelope.Username, envelope.Password ?? string.Empty);
        }

        await client.SendMailAsync(message, cancellationToken);
    }
}

/// <summary>
/// Resolve uma referência OPACA de segredo (`env://`, `secret://`, `keychain://`) para o valor
/// concreto NO MOMENTO do envio. O valor resolvido nunca é persistido nem cacheado neste tipo.
/// O deploy pode substituir a implementação por uma que fale com o cofre da plataforma.
/// </summary>
public interface ISecretReferenceResolver
{
    /// <summary>Retorna o valor da referência, ou <c>null</c> se não puder ser resolvida.</summary>
    string? Resolve(string reference);
}

/// <summary>
/// Resolvedor padrão: resolve <c>env://NOME</c> a partir de variáveis de ambiente do processo
/// (o mecanismo de injeção de segredo do deploy). Esquemas de cofre (`secret://`, `keychain://`)
/// exigem um resolvedor específico da plataforma e retornam <c>null</c> aqui — o que faz o canal
/// se comportar como não configurado em vez de vazar ou adivinhar um segredo.
/// </summary>
public sealed class EnvironmentSecretReferenceResolver : ISecretReferenceResolver
{
    public string? Resolve(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        var separator = reference.IndexOf("://", StringComparison.Ordinal);
        if (separator <= 0)
        {
            return null;
        }

        var scheme = reference[..separator];
        var locator = reference[(separator + 3)..];
        if (locator.Length == 0)
        {
            return null;
        }

        return string.Equals(scheme, "env", StringComparison.Ordinal)
            ? Environment.GetEnvironmentVariable(locator)
            : null;
    }
}

/// <summary>
/// Configuração do canal de e-mail SMTP. TODOS os valores sensíveis são REFERÊNCIAS OPACAS
/// (`env://`, `secret://`, `keychain://`) resolvidas em runtime — nunca literais em repositório,
/// configuração, log ou evidência. O canal nasce DESLIGADO: sem <see cref="Enabled"/> e sem as
/// referências obrigatórias ele é um no-op, e nada muda no comportamento existente.
///
/// Chaveado por convenção em <c>Harness:Notifications:Smtp</c>.
/// </summary>
public sealed record SmtpNotificationOptions
{
    /// <summary>Chave-mestra: precisa ser true E as referências presentes para o canal existir.</summary>
    public bool Enabled { get; init; }

    /// <summary>Referência opaca para o host do relay (ex.: `env://SMTP_HOST`).</summary>
    public string? HostReference { get; init; }

    /// <summary>Referência opaca para a porta do relay (ex.: `env://SMTP_PORT`).</summary>
    public string? PortReference { get; init; }

    /// <summary>Referência opaca para o usuário de autenticação do relay (opcional).</summary>
    public string? UsernameReference { get; init; }

    /// <summary>Referência opaca para a senha/credencial do relay.</summary>
    public string? PasswordReference { get; init; }

    /// <summary>Referência opaca para o endereço remetente (`From`).</summary>
    public string? FromAddressReference { get; init; }

    /// <summary>TLS solicitado por padrão; só pode ser desligado explicitamente.</summary>
    public bool UseTls { get; init; } = true;

    /// <summary>Porta usada se a referência não resolver um inteiro válido (submissão TLS).</summary>
    public int DefaultPort { get; init; } = 587;

    /// <summary>
    /// Configurado quando ligado E com as referências mínimas presentes. Presença de referência
    /// não implica que o segredo resolve — a resolução real acontece no envio.
    /// </summary>
    public bool IsConfigured =>
        Enabled &&
        !string.IsNullOrWhiteSpace(HostReference) &&
        !string.IsNullOrWhiteSpace(PasswordReference) &&
        !string.IsNullOrWhiteSpace(FromAddressReference);
}

/// <summary>
/// Adaptador do canal "email": resolve o relay a partir de referências opacas no momento do
/// envio, monta o envelope efêmero e delega ao <see cref="ISmtpTransport"/>. Nenhum endereço,
/// host, usuário ou senha entra em log — em falha, registra apenas o tipo do erro. Espelha o
/// padrão dos adaptadores Telegram/Teams (opções com `Enabled`, isolamento de falha transitória).
/// </summary>
public sealed partial class SmtpNotificationChannel(
    SmtpNotificationOptions options,
    ISmtpTransport transport,
    ISecretReferenceResolver secrets,
    ILogger<SmtpNotificationChannel> logger) : IExternalNotificationChannel
{
    public string Channel => "email";

    public bool IsConfigured => options.IsConfigured;

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "A entrega de notificação deve devolver um resultado tipado por falha de relay em vez de propagar a exceção do provedor, isolando o chamador (gateway/Chief).")]
    public async Task<NotificationDeliveryResult> SendAsync(
        RenderedNotification notification,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (!options.IsConfigured)
        {
            return NotificationDeliveryResult.SkippedUnconfigured();
        }

        if (string.IsNullOrWhiteSpace(notification.Recipient))
        {
            return NotificationDeliveryResult.Failed("recipient_missing");
        }

        var host = secrets.Resolve(options.HostReference!);
        var from = secrets.Resolve(options.FromAddressReference!);
        var password = secrets.Resolve(options.PasswordReference!);
        if (string.IsNullOrWhiteSpace(host) ||
            string.IsNullOrWhiteSpace(from) ||
            string.IsNullOrWhiteSpace(password))
        {
            // Referência presente mas não resolvível (segredo ausente no deploy): trata como
            // não configurado, sem revelar qual referência falhou.
            return NotificationDeliveryResult.Failed("relay_secret_unresolved");
        }

        var username = string.IsNullOrWhiteSpace(options.UsernameReference)
            ? null
            : secrets.Resolve(options.UsernameReference);
        var port = ResolvePort(options.PortReference is null ? null : secrets.Resolve(options.PortReference));

        var envelope = new SmtpEnvelope(
            host,
            port,
            options.UseTls,
            username,
            password,
            from,
            notification.Recipient,
            notification.Subject,
            notification.Body);

        try
        {
            await transport.SendAsync(envelope, cancellationToken);
            LogDelivered(logger, Channel);
            return NotificationDeliveryResult.Ok();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogSendFailure(logger, exception.GetType().Name);
            return NotificationDeliveryResult.Failed(exception.GetType().Name);
        }
    }

    private int ResolvePort(string? resolved) =>
        int.TryParse(resolved, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) &&
        value is > 0 and <= 65535
            ? value
            : options.DefaultPort;

    [LoggerMessage(
        EventId = 5301,
        Level = LogLevel.Information,
        Message = "Notificação entregue pelo canal {Channel}.")]
    private static partial void LogDelivered(ILogger logger, string channel);

    [LoggerMessage(
        EventId = 5302,
        Level = LogLevel.Warning,
        Message = "Falha ao entregar notificação por e-mail SMTP: {ErrorType}.")]
    private static partial void LogSendFailure(ILogger logger, string errorType);
}

/// <summary>
/// Validação da configuração do canal SMTP contra o vazamento de segredo: cada referência
/// precisa ser opaca (`env://`, `secret://`, `keychain://`) — um literal (endereço, host, senha)
/// é recusado, reutilizando a allowlist de esquemas do padrão de contas de agente.
/// </summary>
public static class SmtpNotificationOptionsValidator
{
    public static void EnsureOpaqueReferences(SmtpNotificationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled)
        {
            return;
        }

        EnsureOpaque(options.HostReference, nameof(options.HostReference));
        EnsureOpaque(options.PortReference, nameof(options.PortReference), optional: true);
        EnsureOpaque(options.UsernameReference, nameof(options.UsernameReference), optional: true);
        EnsureOpaque(options.PasswordReference, nameof(options.PasswordReference));
        EnsureOpaque(options.FromAddressReference, nameof(options.FromAddressReference));
    }

    private static void EnsureOpaque(string? reference, string name, bool optional = false)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            if (optional)
            {
                return;
            }

            throw new ArgumentException($"A referência de segredo '{name}' é obrigatória quando o canal SMTP está habilitado.", nameof(reference));
        }

        if (SecretTextProtector.ContainsSecret(reference))
        {
            throw new ArgumentException($"A referência de segredo '{name}' não pode conter um segredo literal.", nameof(reference));
        }

        // Reutiliza a allowlist canônica de esquemas opacos (`keychain`/`secret`/`env`),
        // normalizando a falha para ArgumentException na fronteira de configuração do Host.
        try
        {
            AgentAccountRegistry.ValidateCredentialReference(reference);
        }
        catch (AgentAccountValidationException exception)
        {
            throw new ArgumentException(
                $"A referência de segredo '{name}' precisa ser opaca (env://, secret:// ou keychain://).",
                nameof(reference),
                exception);
        }
    }
}
