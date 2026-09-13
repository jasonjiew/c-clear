using System;
using Cclear.Core.DeepClean;
using Xunit;

namespace Cclear.Core.Tests;

public class DeepCleanTests
{
    [Fact]
    public void ParseComponentStoreGb_EnglishOutput()
    {
        var output = "The operation completed successfully.\r\nActual Size of Component Store : 5.23 GBs";
        var size = DeepCleanService.ParseComponentStoreGb(output);
        Assert.NotNull(size);
        Assert.Equal(5.23, size.Value, 2);
    }

    [Fact]
    public void ParseComponentStoreGb_ChineseOutput()
    {
        var output = "操作已成功完成。\r\n组件存储的实际大小 : 1830 MB";
        var size = DeepCleanService.ParseComponentStoreGb(output);
        Assert.NotNull(size);
        Assert.Equal(1830.0 / 1024, size.Value, 2);
    }

    [Fact]
    public void ParseComponentStoreGb_NoMatch_ReturnsNull()
    {
        Assert.Null(DeepCleanService.ParseComponentStoreGb("无关输出"));
    }

    [Fact]
    public void ParseShadowStorageUsedGb_Chinese()
    {
        var output = "已用空间: 12.4 GB (5%)";
        var size = DeepCleanService.ParseShadowStorageUsedGb(output);
        Assert.NotNull(size);
        Assert.Equal(12.4, size.Value, 1);
    }

    [Fact]
    public void ParseShadowStorageUsedGb_English()
    {
        var output = "Used Space: 512 MB";
        var size = DeepCleanService.ParseShadowStorageUsedGb(output);
        Assert.NotNull(size);
        Assert.Equal(0.5, size.Value, 1);
    }

    [Fact]
    public void CleanDismOutput_StripsBackspaceAndEmptyLines()
    {
        var cleaned = DeepCleanService.CleanDismOutput("\b\b50.0%\r\n\b\r\n完成。\r\n");
        Assert.Equal("50.0%\r\n完成。", cleaned);
    }

    [Fact]
    public void GetHiberfilBytes_DoesNotThrow()
    {
        // 只验证可调用（存在与否取决于机器配置）
        var bytes = DeepCleanService.GetHiberfilBytes();
        Assert.True(bytes is null || bytes.Value > 0);
    }
}
