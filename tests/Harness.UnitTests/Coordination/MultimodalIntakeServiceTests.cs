using Harness.Modules.Coordination.Application;

namespace Harness.UnitTests.Coordination;

public sealed class MultimodalIntakeServiceTests
{
    [Fact]
    public async Task ProcessAttachmentAsyncValidatesSha256AndMimeTypeForImages()
    {
        var service = new MultimodalIntakeService();
        var content = System.Text.Encoding.UTF8.GetBytes("fake png image data");

        var result = await service.ProcessAttachmentAsync(
            tenantId: "tenant-1",
            resourceId: "solicitation-100",
            fileName: "architecture_diagram.png",
            contentType: "image/png",
            content: content);

        Assert.NotNull(result);
        Assert.Equal("tenant-1", result.TenantId);
        Assert.Equal("solicitation-100", result.ResourceId);
        Assert.True(result.IsAllowedType);
        Assert.Equal("passed", result.SecurityScanStatus);
        Assert.NotEmpty(result.Sha256Hash);
        Assert.Equal(content.Length, result.SizeBytes);
    }

    [Fact]
    public async Task ProcessAttachmentAsyncRejectsOverSizedAttachments()
    {
        var service = new MultimodalIntakeService();
        var content = new byte[1024];

        await Assert.ThrowsAsync<ArgumentException>(() => service.ProcessAttachmentAsync(
            tenantId: "tenant-1",
            resourceId: "solicitation-100",
            fileName: "huge_file.bin",
            contentType: "application/octet-stream",
            content: content,
            maxSizeBytes: 512));
    }
}
