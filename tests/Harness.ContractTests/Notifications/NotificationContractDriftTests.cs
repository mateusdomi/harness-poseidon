using System.Text.Json;

namespace Harness.ContractTests.Notifications;

public sealed class NotificationContractDriftTests
{
    [Theory]
    [InlineData("notifications", "NotificationContract", "notificationSchema", "Notification", "id,profileId,severity,category,title,body,groupKey,dedupeCount,status,link,createdAt,readAt")]
    [InlineData("settings", "SettingsContract", "settingsSchema", "Settings", "id,profileId,theme,language,notificationsEnabled,mutedCategories,workingDirectory,unsafeModeAcceptedAt,updatedAt")]
    public void OpenApiMatchesFrontendSystemContracts(string route, string schema, string marker, string type, string csv)
    {
        var root = FindRepositoryRoot(); using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "docs", "contracts", "openapi.json")));
        var paths = document.RootElement.GetProperty("paths"); Assert.True(paths.GetProperty($"/api/v1/{route}").TryGetProperty("get", out _));
        var item = paths.GetProperty($"/api/v1/{route}/{{id}}"); Assert.True(item.TryGetProperty("get", out _));
        var fields = csv.Split(','); var actual = document.RootElement.GetProperty("components").GetProperty("schemas").GetProperty(schema).GetProperty("properties").EnumerateObject().Select(x => x.Name).Order(StringComparer.Ordinal).ToArray(); Assert.Equal(fields.Order(StringComparer.Ordinal), actual);
        var source = File.ReadAllText(Path.Combine(root, "frontend", "src", "api", "contracts", "system.ts")); var start = source.IndexOf($"export const {marker}", StringComparison.Ordinal); var end = source.IndexOf($"export type {type}", start, StringComparison.Ordinal); Assert.True(start >= 0 && end > start); var block = source[start..end]; Assert.All(fields, field => Assert.Contains($"{field}:", block, StringComparison.Ordinal));
        if (route == "notifications") { Assert.True(paths.GetProperty("/api/v1/notifications/read").TryGetProperty("post", out _)); Assert.True(paths.GetProperty("/api/v1/notifications/mute").TryGetProperty("post", out _)); Assert.True(paths.GetProperty("/api/v1/notifications").TryGetProperty("post", out _)); }
        else Assert.True(item.TryGetProperty("patch", out _));
    }
    private static string FindRepositoryRoot() { var directory = new DirectoryInfo(AppContext.BaseDirectory); while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Harness.sln"))) directory = directory.Parent; return directory?.FullName ?? throw new DirectoryNotFoundException(); }
}
