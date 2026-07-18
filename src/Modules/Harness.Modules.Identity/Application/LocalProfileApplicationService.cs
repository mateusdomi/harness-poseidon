using Harness.Modules.Identity.Contracts;
using Harness.Modules.Identity.Domain;

namespace Harness.Modules.Identity.Application;

public static class LocalProfileApplicationService
{
    public static ProfileContract Create(
        string profileId,
        CreateProfileRequest request,
        DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ToContract(LocalProfile.Create(
            profileId,
            request.DisplayName,
            request.Email,
            request.AvatarUrl,
            request.Locale,
            occurredAt));
    }

    public static ProfileContract Patch(
        ProfileContract current,
        UpdateProfileRequest patch,
        DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (!patch.DisplayNameSpecified && !patch.EmailSpecified &&
            !patch.AvatarUrlSpecified && !patch.LocaleSpecified)
        {
            throw new ArgumentException("Profile patch cannot be empty.", nameof(patch));
        }

        var profile = new LocalProfile(
            current.Id,
            current.DisplayName,
            current.Email,
            current.AvatarUrl,
            current.Locale,
            current.CreatedAt,
            current.LastActiveAt,
            current.Version);
        return ToContract(profile.Update(
            patch.DisplayNameSpecified ? patch.DisplayName! : current.DisplayName,
            patch.EmailSpecified ? patch.Email : current.Email,
            patch.AvatarUrlSpecified ? patch.AvatarUrl : current.AvatarUrl,
            patch.LocaleSpecified ? patch.Locale! : current.Locale,
            occurredAt));
    }

    private static ProfileContract ToContract(LocalProfile profile) =>
        new(
            profile.Id,
            profile.DisplayName,
            profile.Email,
            profile.AvatarUrl,
            profile.Locale,
            profile.CreatedAt,
            profile.LastActiveAt,
            profile.Version);
}
