using System.Net.Mail;

namespace Harness.Modules.Identity.Domain;

public sealed record LocalProfile(
    string Id,
    string DisplayName,
    string? Email,
    string? AvatarUrl,
    string Locale,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActiveAt,
    long Version)
{
    public static LocalProfile Create(
        string id,
        string displayName,
        string? email,
        string? avatarUrl,
        string locale,
        DateTimeOffset occurredAt) =>
        new(
            Required(id, 26, nameof(id)),
            Required(displayName, 200, nameof(displayName)),
            NormalizeEmail(email),
            Optional(avatarUrl, 2_048, nameof(avatarUrl)),
            Required(locale, 20, nameof(locale)),
            RequireUtc(occurredAt),
            occurredAt,
            1);

    public LocalProfile Update(
        string displayName,
        string? email,
        string? avatarUrl,
        string locale,
        DateTimeOffset occurredAt) =>
        this with
        {
            DisplayName = Required(displayName, 200, nameof(displayName)),
            Email = NormalizeEmail(email),
            AvatarUrl = Optional(avatarUrl, 2_048, nameof(avatarUrl)),
            Locale = Required(locale, 20, nameof(locale)),
            LastActiveAt = RequireUtc(occurredAt),
            Version = checked(Version + 1),
        };

    private static string Required(string value, int maximumLength, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim();
        if (normalized.Length > maximumLength)
        {
            throw new ArgumentException($"Value exceeds {maximumLength} characters.", parameterName);
        }

        return normalized;
    }

    private static string? Optional(string? value, int maximumLength, string parameterName)
    {
        if (value is null)
        {
            return null;
        }

        var normalized = value.Trim();
        if (normalized.Length == 0)
        {
            return null;
        }

        if (normalized.Length > maximumLength)
        {
            throw new ArgumentException($"Value exceeds {maximumLength} characters.", parameterName);
        }

        return normalized;
    }

    private static string? NormalizeEmail(string? value)
    {
        var normalized = Optional(value, 320, nameof(value));
        if (normalized is null)
        {
            return null;
        }

        try
        {
            return new MailAddress(normalized).Address;
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("Email is invalid.", nameof(value), exception);
        }
    }

    private static DateTimeOffset RequireUtc(DateTimeOffset value)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Timestamp must be UTC.");
        }

        return value;
    }
}
