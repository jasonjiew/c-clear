using Xunit;

namespace Cclear.Core.Tests;

public class SmokeTests
{
    [Fact]
    public void TestProject_CanReferenceCore()
    {
        // W0 冒烟：测试工程能引用并运行 Core 类型
        var coreAssembly = typeof(Cclear.Core.Marker).Assembly;
        Assert.Equal("Cclear.Core", coreAssembly.GetName().Name);
    }
}
