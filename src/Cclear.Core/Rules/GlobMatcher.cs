using System;
using System.Text;
using System.Text.RegularExpressions;

namespace Cclear.Core.Rules;

/// <summary>
/// 极简 glob → 正则转换与匹配（大小写不敏感，'/' 与 '\\' 等价）。
/// 语义：* 匹配单段内任意字符；** 跨段匹配任意；? 匹配单字符。
/// </summary>
public static class GlobMatcher
{
    public static bool IsMatch(string pattern, string input)
    {
        var regex = ToRegex(pattern);
        var normalized = input.Replace('\\', '/');
        return regex.IsMatch(normalized);
    }

    public static Regex ToRegex(string pattern)
    {
        var normalized = pattern.Replace('\\', '/');
        var sb = new StringBuilder(normalized.Length * 2 + 4);
        sb.Append('^');
        for (int i = 0; i < normalized.Length; i++)
        {
            char c = normalized[i];
            if (c == '*')
            {
                if (i + 1 < normalized.Length && normalized[i + 1] == '*')
                {
                    sb.Append(".*");
                    i++;
                    if (i + 1 < normalized.Length && normalized[i + 1] == '/')
                    {
                        i++; // "**/" 可匹配零层
                    }
                }
                else
                {
                    sb.Append("[^/]*");
                }
            }
            else if (c == '?')
            {
                sb.Append("[^/]");
            }
            else
            {
                sb.Append(Regex.Escape(c.ToString()));
            }
        }
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }

    /// <summary>把无分隔符的模式（如 "*.log"）提升为任意深度匹配；"*" 视为全部。</summary>
    public static string NormalizeToAnyDepth(string pattern)
    {
        if (!pattern.Contains('/') && !pattern.Contains('\\'))
        {
            return "**/" + pattern;
        }
        return pattern;
    }
}
