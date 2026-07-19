using Harness.Modules.Licensing.Domain;

namespace Harness.UnitTests.Licensing;

public sealed class SignedLicenseTests
{
    private static LicenseDocument Document(DateTimeOffset issuedAt) => new(
        "01ARZ3NDEKTSV4RRFFQ69G5FAV",
        "Poseidon",
        "Mateus Software Architect",
        "mac-fingerprint",
        issuedAt,
        issuedAt.AddYears(1),
        30,
        ["backend", "frontend", "agents"],
        1);

    [Fact]
    public void SignAndVerifyRoundTripsWithGeneratedKeyPair()
    {
        var (publicKey, privateKey) = LicenseSigning.GenerateKeyPair();
        var document = Document(DateTimeOffset.Parse("2026-01-01T00:00:00Z", null));
        var signature = LicenseSigning.Sign(document, privateKey);
        Assert.True(LicenseSigning.Verify(document, signature, publicKey));
    }

    [Fact]
    public void TamperedDocumentAndWrongKeyFailVerification()
    {
        var (publicKey, privateKey) = LicenseSigning.GenerateKeyPair();
        var (otherPublicKey, _) = LicenseSigning.GenerateKeyPair();
        var document = Document(DateTimeOffset.Parse("2026-01-01T00:00:00Z", null));
        var signature = LicenseSigning.Sign(document, privateKey);
        Assert.False(LicenseSigning.Verify(
            document with { Licensee = "Outro Usuário" }, signature, publicKey));
        Assert.False(LicenseSigning.Verify(document, signature, otherPublicKey));
        Assert.False(LicenseSigning.Verify(document, "assinatura-invalida!!", publicKey));
        Assert.False(LicenseSigning.Verify(document, signature, "chave-invalida!!"));
    }

    [Fact]
    public void RevocationListSignatureIsIndependentOfLicenseDocuments()
    {
        var (publicKey, privateKey) = LicenseSigning.GenerateKeyPair();
        var list = new LicenseRevocationList(
            ["01ARZ3NDEKTSV4RRFFQ69G5FAV"],
            DateTimeOffset.Parse("2026-02-01T00:00:00Z", null));
        var signature = LicenseSigning.Sign(list, privateKey);
        Assert.True(LicenseSigning.Verify(list, signature, publicKey));
        Assert.False(LicenseSigning.Verify(
            list with { RevokedLicenseIds = ["01ARZ3NDEKTSV4RRFFQ69G5FAW"] },
            signature,
            publicKey));
    }

    [Fact]
    public void ValidatorEnforcesClosedDocumentRules()
    {
        var issuedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z", null);
        LicenseDocumentValidator.Validate(Document(issuedAt));
        Assert.Throws<ArgumentException>(() => LicenseDocumentValidator.Validate(
            Document(issuedAt) with { LicenseId = "curto" }));
        Assert.Throws<ArgumentException>(() => LicenseDocumentValidator.Validate(
            Document(issuedAt) with { Product = "OutroProduto" }));
        Assert.Throws<ArgumentException>(() => LicenseDocumentValidator.Validate(
            Document(issuedAt) with { ExpiresAt = issuedAt }));
        Assert.Throws<ArgumentException>(() => LicenseDocumentValidator.Validate(
            Document(issuedAt) with { GraceDays = 91 }));
        Assert.Throws<ArgumentException>(() => LicenseDocumentValidator.Validate(
            Document(issuedAt) with { Entitlements = [] }));
    }

    [Fact]
    public void StateDerivationCoversActiveGraceExpiredAndRevoked()
    {
        var issuedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z", null);
        var document = Document(issuedAt);
        Assert.Equal(
            SignedLicenseState.Active,
            LicenseDocumentValidator.DeriveState(document, false, issuedAt.AddMonths(6)));
        Assert.Equal(
            SignedLicenseState.GracePeriod,
            LicenseDocumentValidator.DeriveState(document, false, issuedAt.AddYears(1).AddDays(10)));
        Assert.Equal(
            SignedLicenseState.Expired,
            LicenseDocumentValidator.DeriveState(document, false, issuedAt.AddYears(1).AddDays(45)));
        Assert.Equal(
            SignedLicenseState.Revoked,
            LicenseDocumentValidator.DeriveState(document, true, issuedAt));
    }
}
