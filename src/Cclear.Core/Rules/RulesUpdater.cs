using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Cclear.Core.Rules;

/// <summary>在线规则包检查结果。</summary>
public sealed record RulesUpdateCheck(
    bool UpdateAvailable,
    int OnlinePackVersion,
    int InstalledPackVersion,
    string? AssetUrl,
    string? AssetName,
    string? Error);

/// <summary>
/// 在线规则包更新（V2 F1）：GitHub Releases latest → rules-pack-vN.json 资产。
/// 安全：仅接受 HTTPS 下载地址；下载后 RulesPackFile.Load 强制 SHA256 校验；
/// 失败/无网/校验不过由调用方静默回退内置包。红线上界不变：每条规则仍受
/// Cleaner 层硬编码黑名单约束。
/// </summary>
public static class RulesUpdater
{
    public const string LatestReleaseApi = "https://api.github.com/repos/wangjie0721666-web/c-clear/releases/latest";

    private static readonly Regex AssetNamePattern =
        new("^rules-pack-v([0-9]+)\\.json$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string RulesDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "C-Clear", "rules");

    public static string InstalledPackPath => Path.Combine(RulesDirectory, "rules-pack.json");

    /// <summary>已安装在线包的版本；未安装/无效返回 0。</summary>
    public static int GetInstalledPackVersion()
    {
        try
        {
            if (!File.Exists(InstalledPackPath))
            {
                return 0;
            }
            return RulesPackFile.Load(InstalledPackPath).PackVersion;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>检查 GitHub Releases 上的最新规则包（网络失败抛异常，由调用方处理）。</summary>
    public static async Task<RulesUpdateCheck> CheckLatestAsync(CancellationToken cancellationToken)
    {
        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromSeconds(10);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("C-Clear-RulesUpdater");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        using var response = await http.GetAsync(LatestReleaseApi, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = System.Text.Json.JsonDocument.Parse(json);

        int? onlineVersion = null;
        string? assetUrl = null;
        string? assetName = null;
        if (doc.RootElement.TryGetProperty("assets", out var assets) && assets.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                if (!asset.TryGetProperty("name", out var nameElement))
                {
                    continue;
                }
                var match = AssetNamePattern.Match(nameElement.GetString() ?? "");
                if (!match.Success)
                {
                    continue;
                }
                var version = int.Parse(match.Groups[1].Value);
                if (!asset.TryGetProperty("browser_download_url", out var urlElement))
                {
                    continue;
                }
                var url = urlElement.GetString() ?? "";
                if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    continue; // 红线：只接受 HTTPS
                }
                if (onlineVersion is null || version > onlineVersion)
                {
                    onlineVersion = version;
                    assetUrl = url;
                    assetName = nameElement.GetString();
                }
            }
        }

        var installed = GetInstalledPackVersion();
        if (onlineVersion is null)
        {
            return new RulesUpdateCheck(false, 0, installed, null, null, "发布页没有规则包资产");
        }
        return new RulesUpdateCheck(onlineVersion.Value > installed, onlineVersion.Value, installed, assetUrl, assetName, null);
    }

    /// <summary>下载并安装规则包（SHA256 校验 + 版本回退保护），成功返回已装包。</summary>
    public static async Task<RulesPack> DownloadAndInstallAsync(
        string assetUrl, int minimumPackVersion, CancellationToken cancellationToken)
    {
        if (!assetUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            throw new RulePackException("规则包下载地址必须是 HTTPS");
        }
        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromSeconds(30);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("C-Clear-RulesUpdater");

        var bytes = await http.GetByteArrayAsync(assetUrl, cancellationToken);
        var tempPath = InstalledPackPath + ".downloading";
        Directory.CreateDirectory(RulesDirectory);
        await File.WriteAllBytesAsync(tempPath, bytes, cancellationToken);
        try
        {
            var pack = RulesPackFile.Load(tempPath); // SHA256 + schema 校验
            if (pack.PackVersion < minimumPackVersion)
            {
                throw new RulePackException($"规则包版本回退（在线 v{pack.PackVersion} < 预期 v{minimumPackVersion}）");
            }
            File.Copy(tempPath, InstalledPackPath, overwrite: true);
            return pack;
        }
        finally
        {
            File.Delete(tempPath);
        }
    }
}
