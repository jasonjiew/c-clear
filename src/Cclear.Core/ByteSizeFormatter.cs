using System;
using System.Globalization;

namespace Cclear.Core;

/// <summary>字节数人性化显示（UI 与报告共用）。</summary>
public static class ByteSizeFormatter
{
    private const double Kb = 1024;
    private const double Mb = Kb * 1024;
    private const double Gb = Mb * 1024;
    private const double Tb = Gb * 1024;

    public static string Format(long bytes)
    {
        if (bytes < 0)
        {
            return "-" + Format(-bytes);
        }
        return FormatCore(bytes);
    }

    private static string FormatCore(long bytes)
    {
        double b = bytes;
        return b switch
        {
            < Kb => string.Create(CultureInfo.CurrentCulture, $"{bytes} B"),
            < Mb => string.Create(CultureInfo.CurrentCulture, $"{b / Kb:0.#} KB"),
            < Gb => string.Create(CultureInfo.CurrentCulture, $"{b / Mb:0.0} MB"),
            < Tb => string.Create(CultureInfo.CurrentCulture, $"{b / Gb:0.00} GB"),
            _ => string.Create(CultureInfo.CurrentCulture, $"{b / Tb:0.00} TB"),
        };
    }
}
