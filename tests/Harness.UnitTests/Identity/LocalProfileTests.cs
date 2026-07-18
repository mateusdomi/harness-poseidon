using Harness.Modules.Identity.Application;
using Harness.Modules.Identity.Contracts;

namespace Harness.UnitTests.Identity;

public sealed class LocalProfileTests
{
    private static readonly DateTimeOffset Initial =
        new(2026, 7, 18, 19, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CreateNormalizesAndValidatesProfile()
    {
        var profile = LocalProfileApplicationService.Create(
            "01ARZ3NDEKTSV4RRFFQ69G5FAV",
            new CreateProfileRequest("  Mateus  ", "mateus@example.com", null, "pt-BR"),
            Initial);

        Assert.Equal("Mateus", profile.DisplayName);
        Assert.Equal("mateus@example.com", profile.Email);
        Assert.Equal("pt-BR", profile.Locale);
        Assert.Equal(1, profile.Version);
        Assert.Throws<ArgumentException>(() => LocalProfileApplicationService.Create(
            profile.Id,
            new CreateProfileRequest("Mateus", "invalid", null, "pt-BR"),
            Initial));
    }

    [Fact]
    public void PatchDistinguishesMissingAndExplicitNullAndRejectsUnknownFields()
    {
        var profile = LocalProfileApplicationService.Create(
            "01ARZ3NDEKTSV4RRFFQ69G5FAV",
            new CreateProfileRequest("Mateus", "mateus@example.com", "avatar.png", "pt-BR"),
            Initial);
        var updated = LocalProfileApplicationService.Patch(
            profile,
            new UpdateProfileRequest { DisplayName = "Novo Nome", Email = null },
            Initial.AddMinutes(1));

        Assert.Equal("Novo Nome", updated.DisplayName);
        Assert.Null(updated.Email);
        Assert.Equal("avatar.png", updated.AvatarUrl);
        Assert.Equal(2, updated.Version);

        Assert.Throws<ArgumentException>(() => LocalProfileApplicationService.Patch(
            profile,
            new UpdateProfileRequest(),
            Initial.AddMinutes(1)));
    }
}
