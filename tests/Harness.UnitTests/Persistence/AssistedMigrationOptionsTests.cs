using Harness.Persistence.Migration;

namespace Harness.UnitTests.Persistence;

public sealed class AssistedMigrationOptionsTests
{
    [Fact]
    public void RequiresExplicitConfirmation()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            AssistedMigrationOptions.Parse(["--sqlite", "personal.db"]));

        Assert.Contains("--confirm", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AcceptsOnlySourcePathAndConfirmation()
    {
        var options = AssistedMigrationOptions.Parse(
            ["--sqlite", "personal.db", "--confirm"]);

        Assert.Equal(Path.GetFullPath("personal.db"), options.SqliteDatabasePath);
        Assert.True(options.Confirmed);
        Assert.Throws<ArgumentException>(() =>
            AssistedMigrationOptions.Parse(
                ["--sqlite", "personal.db", "--connection", "forbidden", "--confirm"]));
    }
}
