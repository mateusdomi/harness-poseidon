using System.Security.Cryptography;
using System.Text.Json;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace Harness.Modules.Licensing.Domain;

public sealed record LicenseDocument(
    string LicenseId,
    string Product,
    string Licensee,
    string? DeviceFingerprint,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    int GraceDays,
    IReadOnlyList<string> Entitlements,
    int Version);

public sealed record SignedLicenseDocument(LicenseDocument Document, string Signature);

public sealed record LicenseRevocationList(
    IReadOnlyList<string> RevokedLicenseIds,
    DateTimeOffset IssuedAt);

public sealed record SignedLicenseRevocationList(LicenseRevocationList List, string Signature);

public enum SignedLicenseState
{
    Active,
    GracePeriod,
    Expired,
    Revoked,
}

/// <summary>
/// Assinatura Ed25519 de documentos de licença. O payload canônico é o JSON
/// camelCase do record na ordem de declaração — determinístico por construção.
/// A validação é 100% offline: somente a chave pública configurada é necessária.
/// </summary>
public static class LicenseSigning
{
    private static readonly JsonSerializerOptions CanonicalJson = new(JsonSerializerDefaults.Web);

    public static (string PublicKey, string PrivateKey) GenerateKeyPair()
    {
        var seed = new byte[32];
        RandomNumberGenerator.Fill(seed);
        var privateKey = new Ed25519PrivateKeyParameters(seed, 0);
        var publicKey = privateKey.GeneratePublicKey();
        return (
            Convert.ToBase64String(publicKey.GetEncoded()),
            Convert.ToBase64String(privateKey.GetEncoded()));
    }

    public static string Sign<TPayload>(TPayload payload, string privateKeyBase64)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentException.ThrowIfNullOrWhiteSpace(privateKeyBase64);
        var key = new Ed25519PrivateKeyParameters(Convert.FromBase64String(privateKeyBase64), 0);
        var message = JsonSerializer.SerializeToUtf8Bytes(payload, CanonicalJson);
        var signer = new Ed25519Signer();
        signer.Init(forSigning: true, key);
        signer.BlockUpdate(message, 0, message.Length);
        return Convert.ToBase64String(signer.GenerateSignature());
    }

    public static bool Verify<TPayload>(TPayload payload, string signatureBase64, string publicKeyBase64)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentException.ThrowIfNullOrWhiteSpace(signatureBase64);
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKeyBase64);
        byte[] signature;
        byte[] publicKeyBytes;
        try
        {
            signature = Convert.FromBase64String(signatureBase64);
            publicKeyBytes = Convert.FromBase64String(publicKeyBase64);
        }
        catch (FormatException)
        {
            return false;
        }

        if (publicKeyBytes.Length != Ed25519PublicKeyParameters.KeySize)
        {
            return false;
        }

        var key = new Ed25519PublicKeyParameters(publicKeyBytes, 0);
        var message = JsonSerializer.SerializeToUtf8Bytes(payload, CanonicalJson);
        var verifier = new Ed25519Signer();
        verifier.Init(forSigning: false, key);
        verifier.BlockUpdate(message, 0, message.Length);
        return verifier.VerifySignature(signature);
    }
}

public static class LicenseDocumentValidator
{
    public const string ExpectedProduct = "Poseidon";

    public static void Validate(LicenseDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.LicenseId.Length != 26)
        {
            throw new ArgumentException("O ID da licença deve ser um ULID canônico.", nameof(document));
        }

        if (!string.Equals(document.Product, ExpectedProduct, StringComparison.Ordinal))
        {
            throw new ArgumentException("O documento de licença pertence a outro produto.", nameof(document));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(document.Licensee);
        if (document.ExpiresAt <= document.IssuedAt)
        {
            throw new ArgumentException("A expiração deve ser posterior à emissão.", nameof(document));
        }

        if (document.GraceDays is < 0 or > 90)
        {
            throw new ArgumentException("O período de grace deve estar entre 0 e 90 dias.", nameof(document));
        }

        if (document.Entitlements is not { Count: > 0 } ||
            document.Entitlements.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("A licença exige ao menos um entitlement não vazio.", nameof(document));
        }

        if (document.Version < 1)
        {
            throw new ArgumentException("A versão do documento deve ser positiva.", nameof(document));
        }
    }

    public static SignedLicenseState DeriveState(
        LicenseDocument document,
        bool revoked,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (revoked)
        {
            return SignedLicenseState.Revoked;
        }

        if (now <= document.ExpiresAt)
        {
            return SignedLicenseState.Active;
        }

        return now <= document.ExpiresAt.AddDays(document.GraceDays)
            ? SignedLicenseState.GracePeriod
            : SignedLicenseState.Expired;
    }
}
