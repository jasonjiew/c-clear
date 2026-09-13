using System;
using Cclear.Core.AutoClean;
using Cclear.Core.Rules;
using Xunit;

namespace Cclear.Core.Tests;

public class AutoCleanTests
{
    [Fact]
    public void SelectAutoRules_OnlySafe_AndNoShellAction()
    {
        var rules = RulePackLoader.LoadEmbedded();
        var selected = AutoCleanRunner.SelectAutoRules(rules);

        Assert.NotEmpty(selected);
        Assert.All(selected, r =>
        {
            Assert.Equal(SafetyLevel.Safe, r.Level);
            Assert.False(r.ShellAction);
        });
        // 红线：回收站清空（Shell 动作/Caution）绝不允许自动执行
        Assert.DoesNotContain(selected, r => r.Id == "recycle-bin");
        // 自动清理范围是内置 Safe 规则的真子集（存在非 Safe 规则被排除）
        Assert.True(selected.Count < rules.Count);
    }

    [Fact]
    public void SelectAutoRules_EmptyInput_EmptyOutput()
    {
        Assert.Empty(AutoCleanRunner.SelectAutoRules(Array.Empty<CleanupRule>()));
    }
}
