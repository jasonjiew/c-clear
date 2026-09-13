namespace Cclear.Core.Rules;

/// <summary>规则安全级别。NEVER 是硬编码黑名单，不进规则体系。</summary>
public enum SafetyLevel
{
    Safe,
    Caution,
    Manual,
}
