using System;
using System.Collections.Generic;
using System.IO;

namespace Cclear.Core.Rules;

/// <summary>当前生效的规则集与来源描述。</summary>
public sealed record ActiveRules(IReadOnlyList<CleanupRule> Rules, string Source);

/// <summary>规则来源解析：在线规则包优先（有效且非空），否则回退内置包。</summary>
public static class RulesResolver
{
    public static ActiveRules LoadActive()
    {
        try
        {
            var path = RulesUpdater.InstalledPackPath;
            if (File.Exists(path))
            {
                var pack = RulesPackFile.Load(path);
                if (pack.Rules.Count > 0)
                {
                    return new ActiveRules(pack.Rules, $"在线规则包 v{pack.PackVersion}");
                }
            }
        }
        catch (Exception)
        {
            // 在线包损坏/被篡改 → 静默回退内置包（V2 计划 F1 安全要求）
        }
        return new ActiveRules(RulePackLoader.LoadEmbedded(), "内置规则");
    }
}
