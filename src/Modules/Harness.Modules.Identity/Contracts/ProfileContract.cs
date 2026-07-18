using System.Text.Json.Serialization;

namespace Harness.Modules.Identity.Contracts;

public sealed record ProfileContract(
    string Id,
    string DisplayName,
    string? Email,
    string? AvatarUrl,
    string Locale,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActiveAt,
    long Version);

public sealed record CreateProfileRequest(
    string DisplayName,
    string? Email,
    string? AvatarUrl,
    string Locale);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class UpdateProfileRequest
{
    private string? _displayName;
    private string? _email;
    private string? _avatarUrl;
    private string? _locale;

    public string? DisplayName
    {
        get => _displayName;
        init
        {
            _displayName = value;
            DisplayNameSpecified = true;
        }
    }

    public string? Email
    {
        get => _email;
        init
        {
            _email = value;
            EmailSpecified = true;
        }
    }

    public string? AvatarUrl
    {
        get => _avatarUrl;
        init
        {
            _avatarUrl = value;
            AvatarUrlSpecified = true;
        }
    }

    public string? Locale
    {
        get => _locale;
        init
        {
            _locale = value;
            LocaleSpecified = true;
        }
    }

    [JsonIgnore]
    public bool DisplayNameSpecified { get; private set; }

    [JsonIgnore]
    public bool EmailSpecified { get; private set; }

    [JsonIgnore]
    public bool AvatarUrlSpecified { get; private set; }

    [JsonIgnore]
    public bool LocaleSpecified { get; private set; }
}
