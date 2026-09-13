using System;
using System.IO;
using Cclear.Core.Cloud;
using Xunit;

namespace Cclear.Core.Tests;

public class CloudPlaceholderTests
{
    [Fact]
    public void IsPlaceholder_DetectsRecallOnDataAccess()
    {
        Assert.True(CloudPlaceholderStatistics.IsPlaceholder((FileAttributes)0x400000));
        Assert.True(CloudPlaceholderStatistics.IsPlaceholder((FileAttributes)0x400000 | FileAttributes.Archive));
        Assert.False(CloudPlaceholderStatistics.IsPlaceholder(FileAttributes.Archive));
        Assert.False(CloudPlaceholderStatistics.IsPlaceholder(FileAttributes.Hidden | FileAttributes.System));
        Assert.False(CloudPlaceholderStatistics.IsPlaceholder((FileAttributes)0x40000)); // RECALL_ON_OPEN 不算（与 V1 扫描器口径一致，仅 0x400000）
    }

    [Fact]
    public void Scan_NormalFilesNotCounted()
    {
        var dir = Path.Combine(Path.GetTempPath(), "c-clear-cloud-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "a.txt"), "hello");
            File.WriteAllText(Path.Combine(dir, "b.txt"), "world");
            var stats = CloudPlaceholderStatistics.Scan(dir, CancellationToken.None);
            Assert.Equal(2, stats.TotalFiles);
            Assert.Equal(0, stats.PlaceholderFiles);
            Assert.Equal(0, stats.LogicalBytes);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void DiscoverRoots_NoThrow_OnAnyMachine()
    {
        var roots = CloudPlaceholderStatistics.DiscoverRoots();
        Assert.NotNull(roots);
    }
}
