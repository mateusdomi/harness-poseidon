using System.Collections.Concurrent;
using Harness.Host.Notifications;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harness.IntegrationTests.Notifications;

/// <summary>
/// Prova o adaptador de e-mail SMTP contra um transporte FAKE (nenhum servidor SMTP real):
/// o envelope capturado carrega assunto/corpo/destinatário e solicita TLS; segredos nunca
/// aparecem na superfície capturada nem em log; canal não configurado é no-op; falha de relay
/// vira resultado tipado. Espelha o padrão dos testes fake de Telegram/Teams.
/// </summary>
public sealed class SmtpNotificationChannelTests
{
    private const string HostSecret = "smtp.relay.internal";
    private const string FromSecret = "chief@poseidon.invalid";
    private const string RelayPassword = "unit-test-relay-password";
    private const string Recipient = "human@poseidon.invalid";

    private static SmtpNotificationOptions ConfiguredOptions() => new()
    {
        Enabled = true,
        HostReference = "env://TEST_SMTP_HOST",
        PortReference = "env://TEST_SMTP_PORT",
        UsernameReference = "env://TEST_SMTP_USER",
        PasswordReference = "env://TEST_SMTP_PASSWORD",
        FromAddressReference = "env://TEST_SMTP_FROM",
        UseTls = true,
    };

    private static FakeSecretResolver ConfiguredSecrets() => new(new Dictionary<string, string?>
    {
        ["env://TEST_SMTP_HOST"] = HostSecret,
        ["env://TEST_SMTP_PORT"] = "2525",
        ["env://TEST_SMTP_USER"] = "relay-user",
        ["env://TEST_SMTP_PASSWORD"] = RelayPassword,
        ["env://TEST_SMTP_FROM"] = FromSecret,
        ["env://TEST_RECIPIENT"] = Recipient,
    });

    [Fact]
    public async Task ConfiguredChannelSendsViaFakeTransportAndCapturesRenderedMessage()
    {
        var transport = new FakeSmtpTransport();
        var channel = new SmtpNotificationChannel(
            ConfiguredOptions(),
            transport,
            ConfiguredSecrets(),
            NullLogger<SmtpNotificationChannel>.Instance);

        Assert.True(channel.IsConfigured);
        Assert.Equal("email", channel.Channel);

        var result = await channel.SendAsync(
            new RenderedNotification(Recipient, "Demanda bloqueada", "O Chefe precisa de revisão humana."),
            CancellationToken.None);

        Assert.True(result.Delivered);
        Assert.False(result.Skipped);
        Assert.Null(result.FailureReason);

        var sent = Assert.Single(transport.Sent);
        Assert.Equal(HostSecret, sent.Host);
        Assert.Equal(2525, sent.Port);
        Assert.True(sent.UseTls);
        Assert.Equal(FromSecret, sent.FromAddress);
        Assert.Equal(Recipient, sent.ToAddress);
        Assert.Equal("Demanda bloqueada", sent.Subject);
        Assert.Equal("O Chefe precisa de revisão humana.", sent.Body);
    }

    [Fact]
    public async Task UnconfiguredChannelIsNoOpAndNeverTouchesTransport()
    {
        var transport = new FakeSmtpTransport();
        var channel = new SmtpNotificationChannel(
            new SmtpNotificationOptions(),
            transport,
            new FakeSecretResolver(new Dictionary<string, string?>()),
            NullLogger<SmtpNotificationChannel>.Instance);

        Assert.False(channel.IsConfigured);

        var result = await channel.SendAsync(
            new RenderedNotification(Recipient, "s", "b"),
            CancellationToken.None);

        Assert.False(result.Delivered);
        Assert.True(result.Skipped);
        Assert.Equal("channel_unconfigured", result.FailureReason);
        Assert.Empty(transport.Sent);
    }

    [Fact]
    public async Task UnresolvedRelaySecretYieldsTypedFailureWithoutRevealingReference()
    {
        var transport = new FakeSmtpTransport();
        // Password reference present but the secret store cannot resolve it at send time.
        var secrets = new FakeSecretResolver(new Dictionary<string, string?>
        {
            ["env://TEST_SMTP_HOST"] = HostSecret,
            ["env://TEST_SMTP_FROM"] = FromSecret,
            ["env://TEST_SMTP_PASSWORD"] = null,
        });
        var channel = new SmtpNotificationChannel(
            ConfiguredOptions(),
            transport,
            secrets,
            NullLogger<SmtpNotificationChannel>.Instance);

        var result = await channel.SendAsync(
            new RenderedNotification(Recipient, "s", "b"),
            CancellationToken.None);

        Assert.False(result.Delivered);
        Assert.Equal("relay_secret_unresolved", result.FailureReason);
        Assert.Empty(transport.Sent);
    }

    [Fact]
    public async Task RelaySendFailureIsSurfacedAsTypedResultAndNotLeakingSecrets()
    {
        var transport = new FakeSmtpTransport { FailWith = new InvalidOperationException(RelayPassword) };
        var channel = new SmtpNotificationChannel(
            ConfiguredOptions(),
            transport,
            ConfiguredSecrets(),
            NullLogger<SmtpNotificationChannel>.Instance);

        var result = await channel.SendAsync(
            new RenderedNotification(Recipient, "s", "b"),
            CancellationToken.None);

        Assert.False(result.Delivered);
        Assert.False(result.Skipped);
        // Only the exception TYPE surfaces, never the message (which here held the password).
        Assert.Equal(nameof(InvalidOperationException), result.FailureReason);
        Assert.DoesNotContain(RelayPassword, result.FailureReason);
    }

    [Fact]
    public async Task GatewayRoutesEmailDispatchResolvingRecipientReference()
    {
        var transport = new FakeSmtpTransport();
        var secrets = ConfiguredSecrets();
        var channel = new SmtpNotificationChannel(
            ConfiguredOptions(),
            transport,
            secrets,
            NullLogger<SmtpNotificationChannel>.Instance);
        var gateway = new ExternalNotificationGateway(
            [channel],
            secrets,
            NullLogger<ExternalNotificationGateway>.Instance);

        Assert.Contains("email", gateway.ConfiguredChannels);

        var result = await gateway.DispatchAsync(
            new NotificationDispatch("email", "env://TEST_RECIPIENT", "Concluído", "A demanda foi concluída."),
            CancellationToken.None);

        Assert.True(result.Delivered);
        var sent = Assert.Single(transport.Sent);
        Assert.Equal(Recipient, sent.ToAddress);
        Assert.Equal("Concluído", sent.Subject);
    }

    [Fact]
    public async Task GatewayIsNoOpWhenChannelIsUnknownOrUnconfigured()
    {
        var gateway = new ExternalNotificationGateway(
            [],
            new FakeSecretResolver(new Dictionary<string, string?>()),
            NullLogger<ExternalNotificationGateway>.Instance);

        var result = await gateway.DispatchAsync(
            new NotificationDispatch("email", "env://TEST_RECIPIENT", "s", "b"),
            CancellationToken.None);

        Assert.False(result.Delivered);
        Assert.True(result.Skipped);
        Assert.Empty(gateway.ConfiguredChannels);
    }

    [Fact]
    public void ValidatorRejectsLiteralReferencesWhenEnabled()
    {
        var withLiteral = new SmtpNotificationOptions
        {
            Enabled = true,
            HostReference = "smtp.relay.internal", // literal, not opaque
            PasswordReference = "env://TEST_SMTP_PASSWORD",
            FromAddressReference = "env://TEST_SMTP_FROM",
        };

        Assert.Throws<ArgumentException>(() =>
            SmtpNotificationOptionsValidator.EnsureOpaqueReferences(withLiteral));

        // Disabled options are never validated (the channel is simply off).
        var disabled = new SmtpNotificationOptions { HostReference = "smtp.relay.internal" };
        SmtpNotificationOptionsValidator.EnsureOpaqueReferences(disabled);
    }

    [Fact]
    public void EnvironmentResolverResolvesOnlyEnvSchemeAndIgnoresVaultSchemes()
    {
        var variable = $"HARNESS_SMTP_TEST_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(variable, "resolved-value");
        try
        {
            var resolver = new EnvironmentSecretReferenceResolver();
            Assert.Equal("resolved-value", resolver.Resolve($"env://{variable}"));
            Assert.Null(resolver.Resolve("secret://poseidon/smtp"));
            Assert.Null(resolver.Resolve("keychain://poseidon/smtp"));
            Assert.Null(resolver.Resolve("not-a-reference"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    private sealed class FakeSmtpTransport : ISmtpTransport
    {
        public ConcurrentBag<SmtpEnvelope> Sent { get; } = [];

        public Exception? FailWith { get; init; }

        public Task SendAsync(SmtpEnvelope envelope, CancellationToken cancellationToken)
        {
            if (FailWith is not null)
            {
                throw FailWith;
            }

            Sent.Add(envelope);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSecretResolver(IReadOnlyDictionary<string, string?> values) : ISecretReferenceResolver
    {
        public string? Resolve(string reference) =>
            values.TryGetValue(reference, out var value) ? value : null;
    }
}
